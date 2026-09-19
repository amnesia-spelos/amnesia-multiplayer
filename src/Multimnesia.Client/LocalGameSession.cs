using System.Text.Json;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum LocalGameEventName { Connected, UnrecognizedGameReply }

public sealed record LocalGameLogEntry(
    DateTimeOffset Timestamp,
    ConnectionLogSeverity Severity,
    LocalGameEventName Event,
    string? Line = null)
{
    public static void ConsoleSink(LocalGameLogEntry entry) => Console.WriteLine(JsonSerializer.Serialize(new
    {
        timestamp = entry.Timestamp,
        severity = entry.Severity.ToString(),
        @event = entry.Event.ToString(),
        line = entry.Line
    }));
}

// What a Multiplayer Session reports to this game Session.
public sealed record SessionCallbacks(
    Func<string, ValueTask> DisplaySystem,
    Func<ChatEntry, ValueTask> ReceiveChat,
    Func<string, ValueTask> ReceiveCustomStoryStarted,
    Func<string, SharedCustomStoryStartOutcome, ValueTask> ReceiveCustomStoryStartOutcome);

// One Game Interaction Protocol Session with the local game: from its greeting until the connection is lost.
public sealed class LocalGameSession(
    Stream gameStream,
    Func<SessionCallbacks, ISessionOperations> createSessions,
    Action<LocalGameLogEntry> log)
{
    // Returns when cancelled; throws IOException when the local game connection is lost.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var writer = GameInteractionProtocol.CreateWriter(gameStream);
        using var reader = GameInteractionProtocol.CreateReader(gameStream);
        var welcome = await reader.ReadLineAsync(cancellationToken)
            ?? throw new IOException("The local game closed the Game Interaction Protocol session during startup.");
        Log(ConnectionLogSeverity.Information, LocalGameEventName.Connected, welcome);

        var writeGate = new SemaphoreSlim(1, 1);
        async ValueTask WriteLineAsync(string line, CancellationToken token)
        {
            await writeGate.WaitAsync(token);
            try { await writer.WriteLineAsync(line.AsMemory(), token); }
            finally { writeGate.Release(); }
        }
        ValueTask DisplayAsync(ChatEntry entry) => WriteLineAsync(GameInteractionProtocol.Display(entry), cancellationToken);
        ValueTask DisplaySystemAsync(string message) => DisplayAsync(new ChatEntry("SYSTEM", message));
        var localGameCommands = new LocalGameCommands(WriteLineAsync, Task.Delay);
        SharedCustomStoryStart sharedStart = null!;

        var sessions = createSessions(new SessionCallbacks(
            DisplaySystemAsync,
            DisplayAsync,
            // Not awaited: the exchange with the local game must not stall reading Heartbeats from the Session Host.
            identifier =>
            {
                _ = RunIgnoringDisconnectAsync(sharedStart.HandleHostStartAsync(identifier, cancellationToken));
                return ValueTask.CompletedTask;
            },
            // Not awaited: the Session Host's game may be frozen loading, and writing to it must not stall reading Heartbeats.
            (identifier, outcome) =>
            {
                _ = RunIgnoringDisconnectAsync(sharedStart.HandleOutcomeAsync(identifier, outcome));
                return ValueTask.CompletedTask;
            }));
        try
        {
            var orchestrator = new GamePeerOrchestrator(sessions);
            sharedStart = new SharedCustomStoryStart(sessions, localGameCommands.StartCustomStoryAsync, DisplaySystemAsync);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken)
                    ?? throw new IOException("The local game closed the Game Interaction Protocol session.");
                var gameEvent = GameInteractionProtocol.ParseEvent(line);
                if (GameInteractionProtocol.IsUnrecognizedReply(gameEvent))
                    Log(ConnectionLogSeverity.Warning, LocalGameEventName.UnrecognizedGameReply, line);

                if (gameEvent is GameEvent.ChatSubmitted chat) _ = RunIgnoringDisconnectAsync(HandleCommandAsync(orchestrator, chat.Entry));
                // Not awaited, but it records the event before its first await, so events are observed in the order the game sent them.
                _ = RunIgnoringDisconnectAsync(sharedStart.HandleLocalGameEventAsync(gameEvent, cancellationToken));
                localGameCommands.Dispatch(gameEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (sessions is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }

        async Task HandleCommandAsync(GamePeerOrchestrator orchestrator, ChatEntry entry)
        {
            var feedback = await orchestrator.HandleAsync(entry, cancellationToken);
            if (feedback is not null) await DisplaySystemAsync(feedback.Message);
        }

        async Task RunIgnoringDisconnectAsync(Task operation)
        {
            try { await operation; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private void Log(ConnectionLogSeverity severity, LocalGameEventName eventName, string? line = null) =>
        log(new(DateTimeOffset.UtcNow, severity, eventName, line));
}
