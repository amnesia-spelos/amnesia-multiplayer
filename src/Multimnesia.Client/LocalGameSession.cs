using System.Text.Encodings.Web;
using System.Text.Json;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum LocalGameEventName { Connected, ProtocolNegotiated, UnrecognizedGameReply }

public sealed record LocalGameLogEntry(
    DateTimeOffset Timestamp,
    ConnectionLogSeverity Severity,
    LocalGameEventName Event,
    string? Line = null,
    ProtocolNegotiation? Negotiation = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Format(LocalGameLogEntry entry) => JsonSerializer.Serialize(new
    {
        timestamp = entry.Timestamp,
        severity = entry.Severity.ToString(),
        @event = entry.Event.ToString(),
        line = entry.Line,
        outcome = entry.Negotiation?.Outcome,
        protocolVersion = entry.Negotiation?.Version,
        capabilities = entry.Negotiation?.Capabilities,
        sharedPose = entry.Negotiation?.GrantsSharedPose
    }, SerializerOptions);
}

// What a Multiplayer Session reports to this game Session.
public sealed record SessionCallbacks(
    Func<string, ValueTask> DisplaySystem,
    Func<ChatEntry, ValueTask> ReceiveChat,
    Func<string, ValueTask> ReceiveCustomStoryStarted,
    Func<string, SharedCustomStoryStartOutcome, ValueTask> ReceiveCustomStoryStartOutcome,
    Func<LanMessage.Pose, ValueTask> ReceivePose);

// One Game Interaction Protocol Session with the local game: from its greeting until the connection is lost.
public sealed class LocalGameSession(
    Stream gameStream,
    Func<SessionCallbacks, ISessionOperations> createSessions,
    Action<LocalGameLogEntry> log,
    string? connectedNotice = null)
{
    public const string AvatarsUnsupportedNotice = "Your game does not support Avatars; movement will not be shared.";

    // Completes when the local game answers the negotiation; every other write waits for it.
    private readonly TaskCompletionSource<ProtocolNegotiation> _negotiation = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Null until the local game answers the negotiation.
    public ProtocolNegotiation? Negotiation => _negotiation.Task.IsCompletedSuccessfully ? _negotiation.Task.Result : null;

    public bool IsSharedPoseAvailable => Negotiation?.GrantsSharedPose == true;

    // Runs one game Session per connection until cancelled or no connection can be made.
    public static async Task RunReconnectingAsync(
        Func<CancellationToken, Task<Stream?>> connect,
        Func<Stream, LocalGameSession> createSession,
        Func<int, CancellationToken, Task> delayAfterLoss,
        Action<IOException> reportLoss,
        CancellationToken cancellationToken)
    {
        var losses = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var gameStream = await connect(cancellationToken);
            if (gameStream is null) break;
            try
            {
                await createSession(gameStream).RunAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException exception)
            {
                reportLoss(exception);
                try { await delayAfterLoss(++losses, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
        }
    }

    // Returns when cancelled; throws IOException when the local game connection is lost.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var writer = GameInteractionProtocol.CreateWriter(gameStream);
        using var reader = GameInteractionProtocol.CreateReader(gameStream);
        var welcome = await reader.ReadLineAsync(cancellationToken)
            ?? throw new IOException("The local game closed the Game Interaction Protocol session during startup.");
        Log(ConnectionLogSeverity.Information, LocalGameEventName.Connected, welcome);

        var writeGate = new SemaphoreSlim(1, 1);
        // Writers queue on the gate first, so lines held back by the negotiation keep their order.
        async ValueTask WriteLineAsync(string line, CancellationToken token)
        {
            await writeGate.WaitAsync(token);
            try
            {
                await _negotiation.Task.WaitAsync(token);
                await writer.WriteLineAsync(line.AsMemory(), token);
            }
            finally { writeGate.Release(); }
        }
        ValueTask DisplayAsync(ChatEntry entry) => WriteLineAsync(GameInteractionProtocol.Display(entry), cancellationToken);
        ValueTask DisplaySystemAsync(string message) => DisplayAsync(new ChatEntry("SYSTEM", message));
        var localGameCommands = new LocalGameCommands(WriteLineAsync, Task.Delay);
        SharedCustomStoryStart sharedStart = null!;
        SharedPose sharedPose = null!;

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
            },
            // Never blocks: the Pose is written to the local game later, and only the newest one.
            pose =>
            {
                sharedPose.HandleReceivedPose(pose);
                return ValueTask.CompletedTask;
            }));
        try
        {
            var orchestrator = new GamePeerOrchestrator(sessions);
            sharedStart = new SharedCustomStoryStart(sessions, localGameCommands.StartCustomStoryAsync, DisplaySystemAsync);
            sharedPose = new SharedPose(sessions, WriteLineAsync, _negotiation.Task, cancellationToken);

            // Bypasses the gate: every other writer waits there until the negotiation is answered.
            await writer.WriteLineAsync(GameInteractionProtocol.NegotiateSharedPose.AsMemory(), cancellationToken);
            // Queued at the gate before the negotiation can settle, so it is the first chat line after it.
            if (connectedNotice is not null) _ = RunIgnoringDisconnectAsync(DisplaySystemAsync(connectedNotice).AsTask());
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken)
                    ?? throw new IOException("The local game closed the Game Interaction Protocol session.");
                var gameEvent = GameInteractionProtocol.ParseEvent(line);
                if (!_negotiation.Task.IsCompleted && TryNegotiate(gameEvent) is { } negotiation)
                {
                    Settle(negotiation);
                    continue;
                }
                if (GameInteractionProtocol.IsUnrecognizedReply(gameEvent))
                    Log(ConnectionLogSeverity.Warning, LocalGameEventName.UnrecognizedGameReply, line);

                if (gameEvent is GameEvent.ChatSubmitted chat) _ = RunIgnoringDisconnectAsync(HandleCommandAsync(orchestrator, chat.Entry));
                if (gameEvent is GameEvent.LocalPoseReported reported) sharedPose.HandleLocalPose(reported.Pose);
                if (gameEvent is GameEvent.Ponged) sharedPose.HandlePong();
                // Not awaited, but it records the event before its first await, so events are observed in the order the game sent them.
                _ = RunIgnoringDisconnectAsync(sharedStart.HandleLocalGameEventAsync(gameEvent, cancellationToken));
                localGameCommands.Dispatch(gameEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Writes still waiting for a negotiation that will never arrive fail like writes to a lost connection.
            _negotiation.TrySetException(new IOException("The local game Session ended before negotiating."));
            if (sessions is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }

        void Settle(ProtocolNegotiation negotiation)
        {
            Log(negotiation.GrantsSharedPose ? ConnectionLogSeverity.Information : ConnectionLogSeverity.Warning,
                LocalGameEventName.ProtocolNegotiated, negotiation: negotiation);
            _negotiation.TrySetResult(negotiation);
            if (!negotiation.GrantsSharedPose) _ = RunIgnoringDisconnectAsync(DisplaySystemAsync(AvatarsUnsupportedNotice).AsTask());
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

    // Nothing but the negotiation was written yet, so an unknown-command warning can only answer it.
    private static ProtocolNegotiation? TryNegotiate(GameEvent gameEvent) => gameEvent switch
    {
        GameEvent.Responded { Keyword: "protocol" } responded => ProtocolNegotiation.FromResponse(responded),
        GameEvent.UnknownCommandWarned => ProtocolNegotiation.UnknownCommand,
        _ => null
    };

    private void Log(
        ConnectionLogSeverity severity, LocalGameEventName eventName, string? line = null, ProtocolNegotiation? negotiation = null) =>
        log(new(DateTimeOffset.UtcNow, severity, eventName, line, negotiation));
}
