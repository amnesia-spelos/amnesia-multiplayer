using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Multimnesia.Client;

var configuration = new ConfigurationManager();
configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
configuration.AddCommandLine(args);
var options = configuration.GetSection(GamePeerOptions.SectionName).Get<GamePeerOptions>() ?? new GamePeerOptions();
var errors = options.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors) Console.Error.WriteLine($"Configuration error: {error}");
    return 2;
}
var logLevel = options.ParsedLogLevel;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };

var localGameLosses = 0;
while (!cancellation.IsCancellationRequested)
{
    await using var gameStream = await ConnectToLocalGameAsync(options, cancellation.Token);
    if (gameStream is null) break;
    await using var writer = GameInteractionProtocol.CreateWriter(gameStream);
    using var reader = GameInteractionProtocol.CreateReader(gameStream);
    var welcome = await reader.ReadLineAsync(cancellation.Token);
    if (welcome is null)
    {
        Console.Error.WriteLine("Local-game connection failed: the game closed the Game Interaction Protocol session during startup.");
        localGameLosses++;
        await DelayBeforeLocalGameReconnectAsync(localGameLosses, cancellation.Token);
        continue;
    }
    Console.WriteLine($"Connected to local game at {options.GameHost}:{options.GamePort}. {welcome}");

    var writeGate = new SemaphoreSlim(1, 1);
    async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try { await writer.WriteLineAsync(line.AsMemory(), cancellationToken); }
        finally { writeGate.Release(); }
    }
    ValueTask DisplayAsync(ChatEntry entry) => WriteLineAsync(GameInteractionProtocol.Display(entry), cancellation.Token);
    ValueTask DisplaySystemAsync(string message) => DisplayAsync(new ChatEntry("SYSTEM", message));
    var localGameCommands = new LocalGameCommands(WriteLineAsync, Task.Delay);
    SharedCustomStoryStart sharedStart = null!;

    await using var sessions = new TcpSessionOperations(
        new SessionNetworkOptions
        {
            Port = options.RelayPort,
            JoinTimeout = TimeSpan.FromSeconds(options.JoinTimeoutSeconds)
        },
        DisplaySystemAsync,
        receiveChat: DisplayAsync,
        log: RelayLog.ConsoleSinkAt(logLevel),
        // Not awaited: the exchange with the local game must not stall reading Heartbeats from the Session Host.
        receiveCustomStoryStarted: identifier =>
        {
            _ = RunIgnoringDisconnectAsync(sharedStart.HandleHostStartAsync(identifier, cancellation.Token));
            return ValueTask.CompletedTask;
        });
    var orchestrator = new GamePeerOrchestrator(sessions);
    sharedStart = new SharedCustomStoryStart(sessions, localGameCommands.StartCustomStoryAsync, DisplaySystemAsync);

    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellation.Token);
            if (line is null) throw new IOException("The local game closed the Game Interaction Protocol session.");
            var gameEvent = GameInteractionProtocol.ParseEvent(line);
            if (GameInteractionProtocol.IsUnrecognizedReply(gameEvent))
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    severity = "Warning",
                    @event = "UnrecognizedGameReply",
                    line
                }));
            DispatchGameEvent(gameEvent);
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    catch (IOException exception)
    {
        Console.Error.WriteLine($"Local-game connection was lost: {exception.Message}");
        localGameLosses++;
        await DelayBeforeLocalGameReconnectAsync(localGameLosses, cancellation.Token);
    }

    void DispatchGameEvent(GameEvent gameEvent)
    {
        if (gameEvent is GameEvent.ChatSubmitted chat) _ = RunIgnoringDisconnectAsync(HandleCommandAsync(chat.Entry));
        if (gameEvent is GameEvent.CustomStoryStarted started)
            _ = RunIgnoringDisconnectAsync(sharedStart.HandleLocalStartAsync(started.Identifier, cancellation.Token));
        localGameCommands.Dispatch(gameEvent);
    }

    async Task HandleCommandAsync(ChatEntry entry)
    {
        var feedback = await orchestrator.HandleAsync(entry, cancellation.Token);
        if (feedback is not null) await DisplaySystemAsync(feedback.Message);
    }

    async Task RunIgnoringDisconnectAsync(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) { }
    }
}
return 0;

static async Task DelayBeforeLocalGameReconnectAsync(int attempt, CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(LocalGameReconnectPolicy.DelayForAttempt(
            attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
}

static Task<Stream?> ConnectToLocalGameAsync(GamePeerOptions options, CancellationToken cancellationToken)
{
    var connector = new LocalGameConnector(
        async token =>
        {
            var game = new TcpClient();
            try { await game.ConnectAsync(options.GameHost, options.GamePort, token); return game.GetStream(); }
            catch { game.Dispose(); throw; }
        },
        Task.Delay,
        status => Console.WriteLine(JsonSerializer.Serialize(new
        {
            timestamp = status.Timestamp,
            severity = status.Severity.ToString(),
            @event = status.Event.ToString(),
            status.Attempt
        })),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30));
    return connector.ConnectAsync(cancellationToken);
}
