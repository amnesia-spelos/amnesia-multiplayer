using Microsoft.Extensions.Configuration;
using Multimnesia.Client;

var configuration = new ConfigurationManager();
configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
configuration.AddCommandLine(args);
var options = configuration.GetSection(GamePeerOptions.SectionName).Get<GamePeerOptions>() ?? new GamePeerOptions();
var errors = options.Validate();
using var log = GamePeerLog.Open(
    Path.Combine(AppContext.BaseDirectory, "logs"),
    DateTime.Now,
    errors.Count == 0 ? options.ParsedLogLevel : RelaySeverity.Information,
    Console.Out,
    Console.Error);
if (errors.Count > 0)
{
    foreach (var error in errors) log.Write(RelaySeverity.Error, $"Configuration error: {error}");
    return 2;
}
log.Write(RelaySeverity.Information, $"{GamePeerVersion.DisplayName} starting.");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };

var isFirstConnection = true;
await LocalGameSession.RunReconnectingAsync(
    token => ConnectToLocalGameAsync(options, log, token),
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
            log: entry => log.Write(entry.Severity, RelayLog.Format(entry)),
            receiveCustomStoryStarted: callbacks.ReceiveCustomStoryStarted,
            receiveCustomStoryStartOutcome: callbacks.ReceiveCustomStoryStartOutcome),
        entry =>
        {
            if (entry.Event == LocalGameEventName.Connected)
                log.Write(entry.Severity, $"Connected to local game on port {options.GamePort}. {entry.Line}");
            else
                log.Write(entry.Severity, LocalGameLogEntry.Format(entry));
        },
        connectedNotice: Interlocked.Exchange(ref isFirstConnection, false) ? GamePeerVersion.ConnectedNotice : null),
    (attempt, token) => Task.Delay(LocalGameReconnectPolicy.DelayForAttempt(
        attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), token),
    exception => log.Write(RelaySeverity.Error, $"Local-game connection was lost: {exception.Message}"),
    cancellation.Token);
return 0;

static Task<Stream?> ConnectToLocalGameAsync(GamePeerOptions options, GamePeerLog log, CancellationToken cancellationToken)
{
    var connector = new LocalGameConnector(
        token => LocalGameSocket.ConnectAsync(options.GameHost, options.GamePort, token),
        Task.Delay,
        status => log.Write(status.Severity, GameConnectionStatus.Format(status)),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30));
    return connector.ConnectAsync(cancellationToken);
}
