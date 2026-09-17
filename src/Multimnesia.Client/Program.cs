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

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };

await using var gameStream = await ConnectToLocalGameAsync(options, cancellation.Token);
if (gameStream is null) return 0;
await using var writer = GameInteractionProtocol.CreateWriter(gameStream);
using var reader = GameInteractionProtocol.CreateReader(gameStream);
var welcome = await reader.ReadLineAsync(cancellation.Token);
if (welcome is null)
{
    Console.Error.WriteLine("Local-game connection failed: the game closed the Game Interaction Protocol session during startup.");
    return 1;
}
Console.WriteLine($"Connected to local game at {options.GameHost}:{options.GamePort}. {welcome}");

var writeGate = new SemaphoreSlim(1, 1);
async ValueTask DisplayAsync(ChatEntry entry)
{
    await writeGate.WaitAsync(cancellation.Token);
    try { await writer.WriteLineAsync(GameInteractionProtocol.Display(entry).AsMemory(), cancellation.Token); }
    finally { writeGate.Release(); }
}
ValueTask DisplaySystemAsync(string message) => DisplayAsync(new ChatEntry("SYSTEM", message));

await using var sessions = new TcpSessionOperations(
    new SessionNetworkOptions
    {
        Port = options.RelayPort,
        JoinTimeout = TimeSpan.FromSeconds(options.JoinTimeoutSeconds)
    },
    DisplaySystemAsync,
    receiveChat: DisplayAsync);
var orchestrator = new GamePeerOrchestrator(sessions);

try
{
    while (!cancellation.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(cancellation.Token);
        if (line is null) throw new IOException("The local game closed the Game Interaction Protocol session.");
        if (GameInteractionProtocol.ParseEvent(line) is not GameEvent.ChatSubmitted chat) continue;
        var feedback = await orchestrator.HandleAsync(chat.Entry, cancellation.Token);
        if (feedback is not null) await DisplaySystemAsync(feedback.Message);
    }
}
catch (OperationCanceledException) { }
catch (Exception exception)
{
    Console.Error.WriteLine($"Game Peer stopped because of an error: {exception.Message}");
    return 1;
}
return 0;

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
