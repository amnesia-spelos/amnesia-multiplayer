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

await LocalGameSession.RunReconnectingAsync(
    token => ConnectToLocalGameAsync(options, token),
    gameStream => new LocalGameSession(
        gameStream,
        callbacks => new TcpSessionOperations(
            new SessionNetworkOptions
            {
                Port = options.RelayPort,
                JoinTimeout = TimeSpan.FromSeconds(options.JoinTimeoutSeconds)
            },
            callbacks.DisplaySystem,
            receiveChat: callbacks.ReceiveChat,
            log: RelayLog.ConsoleSinkAt(logLevel),
            receiveCustomStoryStarted: callbacks.ReceiveCustomStoryStarted,
            receiveCustomStoryStartOutcome: callbacks.ReceiveCustomStoryStartOutcome),
        entry =>
        {
            if (entry.Event == LocalGameEventName.Connected)
                Console.WriteLine($"Connected to local game at {options.GameHost}:{options.GamePort}. {entry.Line}");
            else
                LocalGameLogEntry.ConsoleSink(entry);
        }),
    (attempt, token) => Task.Delay(LocalGameReconnectPolicy.DelayForAttempt(
        attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), token),
    exception => Console.Error.WriteLine($"Local-game connection was lost: {exception.Message}"),
    cancellation.Token);
return 0;

static Task<Stream?> ConnectToLocalGameAsync(GamePeerOptions options, CancellationToken cancellationToken)
{
    var connector = new LocalGameConnector(
        token => LocalGameSocket.ConnectAsync(options.GameHost, options.GamePort, token),
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
