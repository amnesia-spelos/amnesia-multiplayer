using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed class SessionNetworkOptions
{
    public int Port { get; init; } = 5000;
    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int ProtocolVersion { get; init; } = LanProtocol.CurrentVersion;
    // Carried with each Chat Entry this player sends; null leaves the Chat Author name in the game default color.
    public ChatColor? ChatColor { get; init; }
}

public sealed class TcpSessionOperations : ISessionOperations, IAsyncDisposable
{
    private const int OutboundChatCapacity = 64;
    public const int MaxConcurrentHandshakes = 8;
    public const int MaxInboundMessagesPerWindow = 40;
    private static readonly TimeSpan InboundRateWindow = TimeSpan.FromSeconds(1);
    // Poses have their own allowance: 30 per second, but a brief network stall releases them in one burst.
    public const int MaxInboundPoseBurst = 600;
    public const int InboundPosesPerSecond = 60;
    // Bodies have an allowance of their own like Poses, so carrying a prop never spends the session message budget.
    public const int MaxInboundBodiesBurst = 600;
    public const int InboundBodiesPerSecond = 60;
    // Hold messages have their own allowance: a cave-in Claims and settles up to 32 entities at once, but never for long.
    public const int MaxInboundHoldBurst = 96;
    public const int InboundHoldsPerSecond = 20;
    // One Session Host reports all enemies, independently of the number of players. Headroom for 64 active enemies
    // at up to 60 Hz, including a second of queued samples released after a network stall. Custom maps beyond this
    // envelope need a larger allowance; the mp-test-cs encounter uses one.
    public const int EnemyStreams = 64;
    public const int MaxInboundEnemyPoseBurst = EnemyStreams * 60;
    public const int InboundEnemyPosesPerSecond = EnemyStreams * 60;
    // The Session Host and three Joining Players (ADR 0007).
    public const int MaxPlayers = 4;
    private const string Incompatible = "The Multiplayer Session uses an incompatible protocol version.";
    private const string Full = "The Multiplayer Session is full.";
    private const string RateLimited = "You were disconnected for exceeding the Multiplayer Session's message rate limit.";
    private readonly SessionNetworkOptions _options;
    private readonly Func<string, ValueTask> _notice;
    private readonly Func<ChatEntry, ValueTask> _receiveChat;
    private readonly Func<string, Task<IPAddress[]>> _resolver;
    private readonly Action<RelayLogEntry> _log;
    private readonly Func<string, ValueTask> _receiveCustomStoryStarted;
    private readonly Func<string, SharedCustomStoryStartOutcome, ValueTask> _receiveCustomStoryStartOutcome;
    private readonly Func<LanMessage.EnemyPose, ValueTask> _receiveEnemyPose;
    private readonly Func<LanMessage.EnemyEvent, ValueTask> _receiveEnemyEvent;
    private readonly Func<LanMessage.DeathMessage, ValueTask> _receiveDeathMessage;
    private readonly Func<LanMessage.Pose, ValueTask> _receivePose;
    private readonly Func<LanMessage.HoldMessage, ValueTask> _receiveHoldMessage;
    private readonly Func<LanMessage.Bodies, ValueTask> _receiveBodies;
    private readonly Func<LanMessage.WorldMessage, ValueTask> _receiveWorldMessage;
    private readonly SemaphoreSlim _handshakeSlots = new(MaxConcurrentHandshakes, MaxConcurrentHandshakes);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private TcpClient? _joinedConnection;
    private TcpClient? _admittedConnection;
    private LanOutbound? _joinedOutbound;
    private LanOutbound? _admittedOutbound;
    private Task? _acceptLoop;
    // Counts the other players who became present; guarded by _gate.
    private int _arrivals;

    public TcpSessionOperations(
        SessionNetworkOptions options,
        Func<string, ValueTask>? notice = null,
        Func<string, Task<IPAddress[]>>? resolver = null,
        Func<ChatEntry, ValueTask>? receiveChat = null,
        Action<RelayLogEntry>? log = null,
        Func<string, ValueTask>? receiveCustomStoryStarted = null,
        Func<string, SharedCustomStoryStartOutcome, ValueTask>? receiveCustomStoryStartOutcome = null,
        Func<LanMessage.Pose, ValueTask>? receivePose = null,
        Func<LanMessage.HoldMessage, ValueTask>? receiveHoldMessage = null,
        Func<LanMessage.Bodies, ValueTask>? receiveBodies = null,
        Func<LanMessage.WorldMessage, ValueTask>? receiveWorldMessage = null,
        Func<LanMessage.EnemyPose, ValueTask>? receiveEnemyPose = null,
        Func<LanMessage.EnemyEvent, ValueTask>? receiveEnemyEvent = null,
        Func<LanMessage.DeathMessage, ValueTask>? receiveDeathMessage = null)
    {
        _options = options;
        _notice = notice ?? (_ => ValueTask.CompletedTask);
        _resolver = resolver ?? Dns.GetHostAddressesAsync;
        _receiveChat = receiveChat ?? (_ => ValueTask.CompletedTask);
        _log = log ?? (_ => { });
        _receiveCustomStoryStarted = receiveCustomStoryStarted ?? (_ => ValueTask.CompletedTask);
        _receiveCustomStoryStartOutcome = receiveCustomStoryStartOutcome ?? ((_, _) => ValueTask.CompletedTask);
        _receiveEnemyPose = receiveEnemyPose ?? (_ => ValueTask.CompletedTask);
        _receiveEnemyEvent = receiveEnemyEvent ?? (_ => ValueTask.CompletedTask);
        _receiveDeathMessage = receiveDeathMessage ?? (_ => ValueTask.CompletedTask);
        _receivePose = receivePose ?? (_ => ValueTask.CompletedTask);
        _receiveHoldMessage = receiveHoldMessage ?? (_ => ValueTask.CompletedTask);
        _receiveBodies = receiveBodies ?? (_ => ValueTask.CompletedTask);
        _receiveWorldMessage = receiveWorldMessage ?? (_ => ValueTask.CompletedTask);
    }

    public Guid SessionCorrelationId { get; private set; }
    public Guid PeerCorrelationId { get; private set; }
    public event Action? MultiplayerSessionEnded;
    // Raised after the other player may have become present or gone; read OtherPlayerArrival for the current state.
    public event Action? OtherPlayerPresenceChanged;

    public bool IsJoined { get { lock (_gate) return _joinedConnection is not null; } }

    public bool IsOtherPlayerPresent => OtherPlayerArrival != 0;

    // Hosting with an admitted Joining Player, or joined to a Session Host.
    public int OtherPlayerArrival
    {
        get { lock (_gate) return _admittedOutbound is not null || _joinedOutbound is not null ? _arrivals : 0; }
    }

    public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, _options.Port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            listener.Start();
            _listener = listener;
            SessionCorrelationId = Guid.NewGuid();
            _acceptLoop = AcceptLoopAsync(listener, _lifetime.Token);
            var addresses = AdvertisedHostAddresses().Select(address => $"{address}:{_options.Port}").ToArray();
            var destinations = addresses.Length == 0 ? $"port {_options.Port}" : string.Join(", ", addresses);
            return Task.FromResult(SessionOperationResult.SucceededWith($"Hosting Multiplayer Session at {destinations}."));
        }
        catch (Exception exception) when (exception is SocketException or ArgumentOutOfRangeException)
        {
            return Task.FromResult(SessionOperationResult.Failed($"Hosting failed: {exception.Message}"));
        }
    }

    public async Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(_options.JoinTimeout);
        IPAddress[] addresses;
        try { addresses = await _resolver(destination).WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return SessionOperationResult.Failed("Joining the Multiplayer Session timed out."); }
        catch (OperationCanceledException)
        { return SessionOperationResult.Failed("Joining cancelled."); }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        { return SessionOperationResult.Failed($"Could not resolve the destination: {exception.Message}"); }

        var eligible = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
        if (eligible.Length == 0) return SessionOperationResult.Failed("The host did not resolve to an IPv4 address.");

        Exception? lastFailure = null;
        foreach (var address in eligible)
        {
            var connection = LanSocket.CreateOutbound();
            try
            {
                await connection.ConnectAsync(address, _options.Port, deadline.Token);
                PeerCorrelationId = Guid.NewGuid();
                Log(RelaySeverity.Debug, RelayEventName.HandshakeAttempted, RelayRole.Joining, "None->Attempting",
                    endpoint: new IPEndPoint(address, _options.Port));
                var stream = connection.GetStream();
                await LanProtocol.WriteAsync(stream, new LanMessage.JoinRequest(_options.ProtocolVersion, PeerCorrelationId), deadline.Token);
                var response = await LanProtocol.ReadAsync(stream, deadline.Token);
                switch (response)
                {
                    case LanMessage.AdmissionAccepted accepted when accepted.ProtocolVersion == _options.ProtocolVersion:
                        SessionCorrelationId = accepted.SessionCorrelationId;
                        lock (_gate)
                        {
                            _joinedConnection = connection;
                            _joinedOutbound = StartOutbound(connection, _lifetime.Token);
                            _arrivals++;
                        }
                        OtherPlayerPresenceChanged?.Invoke();
                        Log(RelaySeverity.Information, RelayEventName.PeerAdmitted, RelayRole.Joining, "Attempting->Admitted");
                        _ = ReceiveChatAsync(connection, isHost: false, _lifetime.Token);
                        return SessionOperationResult.SucceededWith("Joined the Multiplayer Session.");
                    case LanMessage.AdmissionRejected rejected:
                        Log(RelaySeverity.Information, RelayEventName.HandshakeRejected, RelayRole.Joining, "Attempting->None",
                            RelayFailureCategory.Protocol);
                        connection.Dispose();
                        return SessionOperationResult.Failed(rejected.Reason);
                    default:
                        throw new LanProtocolException("Unexpected LAN message during negotiation.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log(RelaySeverity.Information, RelayEventName.HandshakeRejected, RelayRole.Joining, "Attempting->None",
                    RelayFailureCategory.Timeout);
                connection.Dispose();
                return SessionOperationResult.Failed("Joining the Multiplayer Session timed out.");
            }
            catch (OperationCanceledException)
            {
                connection.Dispose();
                return SessionOperationResult.Failed("Joining cancelled.");
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                Log(RelaySeverity.Information, RelayEventName.HandshakeRejected, RelayRole.Joining, "Attempting->None",
                    RelayFailureCategory.Io);
                connection.Dispose();
                lastFailure = exception;
            }
        }
        return SessionOperationResult.Failed($"Could not join the Multiplayer Session: {lastFailure?.Message ?? "connection failed"}");
    }

    public async Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken)
    {
        LanOutbound? outbound;
        TcpClient? connection;
        lock (_gate)
        {
            outbound = state == GamePeerState.Hosting ? _admittedOutbound : _joinedOutbound;
            connection = state == GamePeerState.Hosting ? _admittedConnection : _joinedConnection;
        }
        try
        {
            if (outbound is not null && connection is not null)
            {
                var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (outbound.TryWrite(new(new LanMessage.Departure(), sent)))
                    await sent.Task.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        finally
        {
            if (state == GamePeerState.Hosting)
            {
                _listener?.Stop();
                _listener = null;
                lock (_gate) ClearAdmitted(connection);
            }
            else
            {
                connection?.Dispose();
                lock (_gate) ClearJoined(connection);
            }
            OtherPlayerPresenceChanged?.Invoke();
        }
    }

    // Latest-wins; dropped without another player. Validated here too, so an invalid local Pose never reaches the
    // outbound frames, where it would fail the write and end the Multiplayer Session.
    public void SendPose(LanMessage.Pose pose)
    {
        if (!LanProtocol.IsValid(pose)) return;
        LanOutbound? outbound;
        lock (_gate) outbound = _joinedOutbound ?? _admittedOutbound;
        outbound?.OfferPose(pose);
    }

    public void SendEnemyPose(LanMessage.EnemyPose pose)
    {
        if (!LanProtocol.IsValid(pose)) return;
        lock (_gate) _admittedOutbound?.OfferEnemyPose(pose);
    }

    // Ordered like a Hold message, and validated here like an Enemy Pose. Only the Session Host sends one.
    public Task SendEnemyEventAsync(LanMessage.EnemyEvent message, CancellationToken cancellationToken) => message switch
    {
        LanMessage.EnemyAttack attack when LanProtocol.IsValid(attack) => SendOrderedAsync(attack, hostOnly: true),
        LanMessage.EnemyDoorBreak doorBreak when LanProtocol.IsValid(doorBreak) => SendOrderedAsync(doorBreak, hostOnly: true),
        _ => Task.CompletedTask
    };

    // Ordered like an enemy attack: a Joining Player sends only its own deaths, and the Session Host only the episodes.
    public Task SendDeathMessageAsync(LanMessage.DeathMessage message, CancellationToken cancellationToken) => message switch
    {
        LanMessage.PlayerDeath death when LanProtocol.IsValid(death) => SendOrderedAsync(death, joiningOnly: true),
        LanMessage.SharedDeath death when LanProtocol.IsValid(death) => SendOrderedAsync(death, hostOnly: true),
        _ => Task.CompletedTask
    };

    // Latest-wins like the Pose, and validated here for the same reason.
    public void SendBodies(LanMessage.Bodies bodies)
    {
        if (!LanProtocol.IsValid(bodies)) return;
        LanOutbound? outbound;
        lock (_gate) outbound = _joinedOutbound ?? _admittedOutbound;
        outbound?.OfferBodies(bodies);
    }

    // Ordered and exactly once, like chat: a Hold message that cannot be queued ends the Multiplayer Session.
    public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken) =>
        SendOrderedAsync(message);

    // A world message that cannot be queued ends the Multiplayer Session the same way.
    public Task SendWorldMessageAsync(LanMessage.WorldMessage message, CancellationToken cancellationToken) =>
        SendOrderedAsync(message);

    private async Task SendOrderedAsync(LanMessage message, bool hostOnly = false, bool joiningOnly = false)
    {
        LanOutbound? outbound;
        TcpClient? connection;
        lock (_gate)
        {
            outbound = hostOnly ? _admittedOutbound : joiningOnly ? _joinedOutbound : _joinedOutbound ?? _admittedOutbound;
            connection = hostOnly ? _admittedConnection : joiningOnly ? _joinedConnection : _joinedConnection ?? _admittedConnection;
        }
        if (outbound is null || connection is null) return;
        if (outbound.TryWrite(new(message))) return;

        connection.Dispose();
        await _notice("The remote Game Peer could not keep up with interaction traffic.");
    }

    public async Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken)
    {
        LanOutbound? outbound;
        TcpClient? connection;
        lock (_gate)
        {
            outbound = _joinedOutbound ?? _admittedOutbound;
            connection = _joinedConnection ?? _admittedConnection;
        }
        if (outbound is null || connection is null) return;
        if (outbound.TryWrite(new(new LanMessage.ChatEntry(entry.Author, entry.Message, _options.ChatColor?.Hex)))) return;

        connection.Dispose();
        await _notice("The remote Game Peer could not keep up with chat traffic.");
    }

    public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken)
    {
        LanOutbound? outbound;
        lock (_gate) outbound = _admittedOutbound;
        if (outbound is null || !outbound.TryWrite(new(new LanMessage.CustomStoryStarted(identifier))))
            return Task.FromResult(false);

        Log(RelaySeverity.Information, RelayEventName.CustomStoryStartSent, RelayRole.Host, "Admitted->Admitted",
            customStoryIdentifier: identifier);
        return Task.FromResult(true);
    }

    public Task SendCustomStoryStartOutcomeAsync(
        string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken)
    {
        LanOutbound? outbound;
        lock (_gate) outbound = _joinedOutbound;
        if (outbound is not null && outbound.TryWrite(new(new LanMessage.CustomStoryStartOutcome(identifier, outcome))))
            Log(OutcomeSeverity(outcome), RelayEventName.CustomStoryStartOutcomeSent, RelayRole.Joining, "Admitted->Admitted",
                customStoryIdentifier: identifier, customStoryStartOutcome: outcome);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        var state = _listener is not null ? GamePeerState.Hosting : _joinedConnection is not null ? GamePeerState.Joined : GamePeerState.Local;
        if (state != GamePeerState.Local)
        {
            using var departure = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try { await LeaveAsync(state, departure.Token); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        }
        Stop();
        if (_acceptLoop is not null) await IgnoreCancellationAsync(_acceptLoop);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await LanSocket.AcceptAsync(listener, cancellationToken);
                if (!_handshakeSlots.Wait(0))
                {
                    Log(RelaySeverity.Warning, RelayEventName.HandshakeRejected, RelayRole.Host, "Attempting->None",
                        RelayFailureCategory.Capacity, RemoteEndpointOf(connection));
                    connection.Dispose();
                    continue;
                }
                _ = HandleAttemptAsync(connection, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested || !ReferenceEquals(_listener, listener)) { }
    }

    private async Task HandleAttemptAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAttemptCoreAsync(connection, cancellationToken);
        }
        finally { _handshakeSlots.Release(); }
    }

    private async Task HandleAttemptCoreAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        Log(RelaySeverity.Debug, RelayEventName.HandshakeAttempted, RelayRole.Host, "None->Attempting",
            endpoint: RemoteEndpointOf(connection));
        await _notice("A player is attempting to join.");
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_options.HandshakeTimeout);
        try
        {
            var stream = connection.GetStream();
            var request = await LanProtocol.ReadAsync(stream, handshake.Token);
            if (request is not LanMessage.JoinRequest join)
                throw new LanProtocolException("Expected a join request.");
            if (join.ProtocolVersion != _options.ProtocolVersion)
            {
                Log(RelaySeverity.Information, RelayEventName.HandshakeRejected, RelayRole.Host, "Attempting->None",
                    RelayFailureCategory.Protocol, peerCorrelationId: join.PeerCorrelationId);
                await LanProtocol.WriteAsync(stream, new LanMessage.AdmissionRejected(Incompatible), handshake.Token);
                connection.Dispose();
                return;
            }

            var admitted = false;
            lock (_gate)
            {
                if (_admittedConnection is null)
                {
                    _admittedConnection = connection;
                    admitted = true;
                }
                else connection = null!;
            }
            if (connection is null)
            {
                Log(RelaySeverity.Information, RelayEventName.HandshakeRejected, RelayRole.Host, "Attempting->None",
                    RelayFailureCategory.Capacity, peerCorrelationId: join.PeerCorrelationId);
                await LanProtocol.WriteAsync(stream, new LanMessage.AdmissionRejected(Full), handshake.Token);
                stream.Dispose();
                return;
            }
            try
            {
                await LanProtocol.WriteAsync(stream,
                    new LanMessage.AdmissionAccepted(_options.ProtocolVersion, SessionCorrelationId), handshake.Token);
                lock (_gate)
                {
                    _admittedOutbound = StartOutbound(connection, cancellationToken);
                    _arrivals++;
                }
                OtherPlayerPresenceChanged?.Invoke();
                Log(RelaySeverity.Information, RelayEventName.PeerAdmitted, RelayRole.Host, "Attempting->Admitted",
                    peerCorrelationId: join.PeerCorrelationId);
                await _notice("A player joined.");
                await ReceiveChatAsync(connection, isHost: true, cancellationToken, join.PeerCorrelationId);
            }
            catch
            {
                if (admitted)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_admittedConnection, connection)) _admittedConnection = null;
                    }
                }
                throw;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            Log(RelaySeverity.Information, RelayEventName.HandshakeRejected, RelayRole.Host, "Attempting->None",
                exception is OperationCanceledException ? RelayFailureCategory.Timeout : RelayFailureCategory.Protocol);
            connection?.Dispose();
        }
    }

    private static IPEndPoint? RemoteEndpointOf(TcpClient connection)
    {
        try { return connection.Client.RemoteEndPoint as IPEndPoint; }
        catch (ObjectDisposedException) { return null; }
    }

    private void Log(
        RelaySeverity severity, RelayEventName @event, RelayRole role, string transition,
        RelayFailureCategory failure = RelayFailureCategory.None, IPEndPoint? endpoint = null,
        Guid? peerCorrelationId = null, string? customStoryIdentifier = null,
        SharedCustomStoryStartOutcome? customStoryStartOutcome = null) =>
        _log(new RelayLogEntry(
            DateTimeOffset.UtcNow, severity, @event, role, transition,
            SessionCorrelationId == Guid.Empty ? null : SessionCorrelationId,
            peerCorrelationId ?? (PeerCorrelationId == Guid.Empty ? null : PeerCorrelationId),
            failure, endpoint, customStoryIdentifier, customStoryStartOutcome));

    private static RelaySeverity OutcomeSeverity(SharedCustomStoryStartOutcome outcome) =>
        outcome == SharedCustomStoryStartOutcome.Started ? RelaySeverity.Information : RelaySeverity.Warning;

    private LanOutbound StartOutbound(TcpClient connection, CancellationToken cancellationToken)
    {
        var outbound = new LanOutbound(OutboundChatCapacity);
        _ = SendOutboundAsync(connection, outbound, cancellationToken);
        _ = SendHeartbeatsAsync(outbound, cancellationToken);
        return outbound;
    }

    private static async Task SendOutboundAsync(
        TcpClient connection, LanOutbound outbound, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in outbound.ReadAllAsync(cancellationToken))
            {
                await LanProtocol.WriteAsync(connection.GetStream(), entry.Message, cancellationToken);
                entry.Sent?.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { connection.Dispose(); }
    }

    private async Task SendHeartbeatsAsync(LanOutbound outbound, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken);
                if (!outbound.TryWrite(new(new LanMessage.Heartbeat()))) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReceiveChatAsync(
        TcpClient connection, bool isHost, CancellationToken cancellationToken, Guid? peerCorrelationId = null)
    {
        var role = isHost ? RelayRole.Host : RelayRole.Joining;
        var failure = RelayFailureCategory.None;
        // The Session Host applies one player's Sender Allowance to each Joining Player; a Joining Player's
        // connection to the Session Host carries every other player in the largest Multiplayer Session.
        var playersCarried = isHost ? 1 : MaxPlayers - 1;
        var windowStart = DateTime.UtcNow;
        var windowCount = 0;
        var poseAllowance = new InboundAllowance(playersCarried * MaxInboundPoseBurst, playersCarried * InboundPosesPerSecond, windowStart);
        var enemyAllowance = new InboundAllowance(MaxInboundEnemyPoseBurst, InboundEnemyPosesPerSecond, windowStart);
        var bodiesAllowance = new InboundAllowance(playersCarried * MaxInboundBodiesBurst, playersCarried * InboundBodiesPerSecond, windowStart);
        var holdAllowance = new InboundAllowance(playersCarried * MaxInboundHoldBurst, playersCarried * InboundHoldsPerSecond, windowStart);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await LanProtocol.ReadAsync(connection.GetStream(), cancellationToken)
                    .AsTask().WaitAsync(_options.HeartbeatTimeout, cancellationToken);

                var now = DateTime.UtcNow;
                bool withinAllowance;
                if (message is LanMessage.Pose) withinAllowance = poseAllowance.TryTake(now);
                else if (message is LanMessage.EnemyPose) withinAllowance = enemyAllowance.TryTake(now);
                else if (message is LanMessage.Bodies) withinAllowance = bodiesAllowance.TryTake(now);
                else if (message is LanMessage.HoldMessage) withinAllowance = holdAllowance.TryTake(now);
                else
                {
                    if (now - windowStart >= InboundRateWindow) { windowStart = now; windowCount = 0; }
                    withinAllowance = ++windowCount <= playersCarried * MaxInboundMessagesPerWindow;
                }
                if (!withinAllowance)
                {
                    failure = RelayFailureCategory.Flood;
                    await _notice(isHost ? "A player was disconnected for exceeding the Multiplayer Session's message rate limit." : RateLimited);
                    return;
                }

                switch (message)
                {
                    case LanMessage.ChatEntry chat:
                        await _receiveChat(new ChatEntry(
                            chat.Author, chat.Message, ChatColor.TryParse(chat.AuthorColor, out var authorColor) ? authorColor : null));
                        break;
                    case LanMessage.CustomStoryStarted started when !isHost:
                        Log(RelaySeverity.Information, RelayEventName.CustomStoryStartReceived, role, "Admitted->Admitted",
                            customStoryIdentifier: started.Identifier);
                        await _receiveCustomStoryStarted(started.Identifier);
                        break;
                    case LanMessage.CustomStoryStartOutcome reported when isHost:
                        Log(OutcomeSeverity(reported.Outcome), RelayEventName.CustomStoryStartOutcomeReceived, role, "Admitted->Admitted",
                            peerCorrelationId: peerCorrelationId, customStoryIdentifier: reported.Identifier,
                            customStoryStartOutcome: reported.Outcome);
                        await _receiveCustomStoryStartOutcome(reported.Identifier, reported.Outcome);
                        break;
                    case LanMessage.EnemyPose enemy when !isHost:
                        await _receiveEnemyPose(enemy);
                        break;
                    case LanMessage.EnemyEvent enemyEvent when !isHost:
                        await _receiveEnemyEvent(enemyEvent);
                        break;
                    case LanMessage.PlayerDeath death when isHost:
                        await _receiveDeathMessage(death);
                        break;
                    case LanMessage.SharedDeath death when !isHost:
                        await _receiveDeathMessage(death);
                        break;
                    case LanMessage.Pose pose:
                        await _receivePose(pose);
                        break;
                    case LanMessage.Bodies bodies:
                        await _receiveBodies(bodies);
                        break;
                    // Only the Multiplayer Relay's arbiter denies Claims.
                    case LanMessage.HoldMessage hold when hold is not LanMessage.ClaimDenied || !isHost:
                        await _receiveHoldMessage(hold);
                        break;
                    case LanMessage.WorldMessage world:
                        await _receiveWorldMessage(world);
                        break;
                    case LanMessage.Heartbeat:
                        break;
                    case LanMessage.Departure:
                        await _notice(isHost ? "A player left." : "The host ended the Multiplayer Session.");
                        return;
                    default:
                        throw new LanProtocolException("Unexpected LAN message after admission.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (LanProtocolException exception) when (exception.InnerException is EndOfStreamException)
        {
            failure = RelayFailureCategory.Io;
            await _notice(isHost ? "A player disconnected." : "Connection to the host was lost.");
        }
        catch (LanProtocolException)
        {
            failure = RelayFailureCategory.Protocol;
            await _notice("The remote Game Peer violated the Multiplayer Session protocol.");
        }
        catch (IOException)
        {
            failure = RelayFailureCategory.Io;
            await _notice(isHost ? "A player disconnected." : "Connection to the host was lost.");
        }
        catch (TimeoutException)
        {
            failure = RelayFailureCategory.Timeout;
            await _notice(isHost ? "A player disconnected." : "Connection to the host was lost.");
        }
        finally
        {
            Log(RelaySeverity.Information, RelayEventName.PeerDisconnected, role, "Admitted->None", failure,
                peerCorrelationId: peerCorrelationId);
            connection.Dispose();
            lock (_gate)
            {
                if (isHost && ReferenceEquals(_admittedConnection, connection))
                {
                    ClearAdmitted(connection);
                }
                if (!isHost && ReferenceEquals(_joinedConnection, connection))
                {
                    ClearJoined(connection);
                    MultiplayerSessionEnded?.Invoke();
                }
            }
            OtherPlayerPresenceChanged?.Invoke();
        }
    }

    private void Stop()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        _listener?.Stop();
        _joinedConnection?.Dispose();
        _joinedOutbound?.Complete();
        lock (_gate)
        {
            _admittedConnection?.Dispose();
            _admittedConnection = null;
            _admittedOutbound?.Complete();
        }
    }

    private void ClearAdmitted(TcpClient? connection)
    {
        if (connection is not null && !ReferenceEquals(_admittedConnection, connection)) return;
        _admittedConnection?.Dispose();
        _admittedConnection = null;
        _admittedOutbound?.Complete();
        _admittedOutbound = null;
    }

    private void ClearJoined(TcpClient? connection)
    {
        if (connection is not null && !ReferenceEquals(_joinedConnection, connection)) return;
        _joinedConnection?.Dispose();
        _joinedConnection = null;
        _joinedOutbound?.Complete();
        _joinedOutbound = null;
    }

    private static IEnumerable<IPAddress> AdvertisedHostAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal)
            .Where(IsAdvertisedHostAddress);

    // Private IPv4 ranges, plus 100.64.0.0/10, the shared address space Tailscale and NetBird assign tailnet addresses from.
    public static bool IsAdvertisedHostAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; } catch (Exception exception) when (exception is OperationCanceledException or SocketException) { }
    }
}
