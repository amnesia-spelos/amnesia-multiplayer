using Multimnesia.Client;

var options = GamePeerConfiguration.Load(args, "appsettings.json");
var errors = options.Validate();
var logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
var startedAt = DateTime.Now;
using var log = GamePeerLog.Open(
    logsDirectory,
    startedAt,
    errors.Count == 0 ? options.ParsedLogLevel : RelaySeverity.Information,
    Console.Out,
    Console.Error);
if (errors.Count > 0)
{
    foreach (var error in errors) log.Write(RelaySeverity.Error, $"Configuration error: {error}");
    return 2;
}
log.Write(RelaySeverity.Information, $"{GamePeerVersion.DisplayName} starting.");
await using var traffic = TrafficRecorder.Open(
    logsDirectory, startedAt, options.ParsedTrafficOptions, warning => log.Write(RelaySeverity.Warning, warning));
if (traffic.FilePath is not null)
{
    var features = (traffic.IsStatisticsOn, traffic.IsTraceOn) switch
    {
        (true, true) => "Traffic Statistics and the Traffic Trace are",
        (true, false) => "Traffic Statistics are",
        _ => "The Traffic Trace is"
    };
    log.Write(RelaySeverity.Information, $"{features} on, written to {traffic.FilePath}");
}

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
            receiveCustomStoryStartOutcome: callbacks.ReceiveCustomStoryStartOutcome,
            receiveMainStoryStarted: callbacks.ReceiveMainStoryStarted,
            receiveMainStoryStartOutcome: callbacks.ReceiveMainStoryStartOutcome,
            receivePose: callbacks.ReceivePose,
            receiveHoldMessage: callbacks.ReceiveHoldMessage,
            receiveBodies: callbacks.ReceiveBodies,
            receiveWorldMessage: callbacks.ReceiveWorldMessage,
            receiveEnemyPose: callbacks.ReceiveEnemyPose,
            receiveEnemyEvent: callbacks.ReceiveEnemyEvent,
            receiveDeathMessage: callbacks.ReceiveDeathMessage,
            traffic: traffic),
        entry =>
        {
            if (entry.Event == LocalGameEventName.Connected)
                log.Write(entry.Severity, $"Connected to local game on port {options.GamePort}. {entry.Line}");
            else
                log.Write(entry.Severity, LocalGameLogEntry.Format(entry));
        },
        connectedNotice: Interlocked.Exchange(ref isFirstConnection, false) ? GamePeerVersion.ConnectedNotice : null,
        trafficRecorder: traffic,
        chatColor: options.ParsedChatColor),
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
