using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
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
}

public sealed class TcpSessionOperations : ISessionOperations, IAsyncDisposable
{
    private sealed record OutboundMessage(LanMessage Message, TaskCompletionSource? Sent = null);
    private const int OutboundChatCapacity = 64;
    public const int MaxConcurrentHandshakes = 8;
    public const int MaxInboundMessagesPerWindow = 40;
    private static readonly TimeSpan InboundRateWindow = TimeSpan.FromSeconds(1);
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
    private readonly SemaphoreSlim _handshakeSlots = new(MaxConcurrentHandshakes, MaxConcurrentHandshakes);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private TcpClient? _joinedConnection;
    private TcpClient? _admittedConnection;
    private Channel<OutboundMessage>? _joinedOutbound;
    private Channel<OutboundMessage>? _admittedOutbound;
    private Task? _acceptLoop;

    public TcpSessionOperations(
        SessionNetworkOptions options,
        Func<string, ValueTask>? notice = null,
        Func<string, Task<IPAddress[]>>? resolver = null,
        Func<ChatEntry, ValueTask>? receiveChat = null,
        Action<RelayLogEntry>? log = null,
        Func<string, ValueTask>? receiveCustomStoryStarted = null,
        Func<string, SharedCustomStoryStartOutcome, ValueTask>? receiveCustomStoryStartOutcome = null)
    {
        _options = options;
        _notice = notice ?? (_ => ValueTask.CompletedTask);
        _resolver = resolver ?? Dns.GetHostAddressesAsync;
        _receiveChat = receiveChat ?? (_ => ValueTask.CompletedTask);
        _log = log ?? RelayLog.ConsoleSink;
        _receiveCustomStoryStarted = receiveCustomStoryStarted ?? (_ => ValueTask.CompletedTask);
        _receiveCustomStoryStartOutcome = receiveCustomStoryStartOutcome ?? ((_, _) => ValueTask.CompletedTask);
    }

    public Guid SessionCorrelationId { get; private set; }
    public Guid PeerCorrelationId { get; private set; }
    public event Action? MultiplayerSessionEnded;

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
            var addresses = UsablePrivateAddresses().Select(address => $"{address}:{_options.Port}").ToArray();
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
            var connection = new TcpClient(AddressFamily.InterNetwork);
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
                        }
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
        Channel<OutboundMessage>? outbound;
        TcpClient? connection;
        lock (_gate)
        {
            outbound = state == GamePeerState.Hosting ? _admittedOutbound : _joinedOutbound;
            connection = state == GamePeerState.Hosting ? _admittedConnection : _joinedConnection;
        }
        if (outbound is not null && connection is not null)
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (outbound.Writer.TryWrite(new(new LanMessage.Departure(), sent)))
                await sent.Task.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
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
    }

    public async Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken)
    {
        Channel<OutboundMessage>? outbound;
        TcpClient? connection;
        lock (_gate)
        {
            outbound = _joinedOutbound ?? _admittedOutbound;
            connection = _joinedConnection ?? _admittedConnection;
        }
        if (outbound is null || connection is null) return;
        if (outbound.Writer.TryWrite(new(new LanMessage.ChatEntry(entry.Author, entry.Message)))) return;

        connection.Dispose();
        await _notice("The remote Game Peer could not keep up with chat traffic.");
    }

    public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken)
    {
        Channel<OutboundMessage>? outbound;
        lock (_gate) outbound = _admittedOutbound;
        if (outbound is null || !outbound.Writer.TryWrite(new(new LanMessage.CustomStoryStarted(identifier))))
            return Task.FromResult(false);

        Log(RelaySeverity.Information, RelayEventName.CustomStoryStartSent, RelayRole.Host, "Admitted->Admitted",
            customStoryIdentifier: identifier);
        return Task.FromResult(true);
    }

    public Task SendCustomStoryStartOutcomeAsync(
        string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken)
    {
        Channel<OutboundMessage>? outbound;
        lock (_gate) outbound = _joinedOutbound;
        if (outbound is not null && outbound.Writer.TryWrite(new(new LanMessage.CustomStoryStartOutcome(identifier, outcome))))
            Log(RelaySeverity.Information, RelayEventName.CustomStoryStartOutcomeSent, RelayRole.Joining, "Admitted->Admitted",
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
                var connection = await listener.AcceptTcpClientAsync(cancellationToken);
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
                lock (_gate) _admittedOutbound = StartOutbound(connection, cancellationToken);
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

    private Channel<OutboundMessage> StartOutbound(TcpClient connection, CancellationToken cancellationToken)
    {
        var outbound = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(OutboundChatCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _ = SendOutboundAsync(connection, outbound.Reader, cancellationToken);
        _ = SendHeartbeatsAsync(outbound.Writer, cancellationToken);
        return outbound;
    }

    private static async Task SendOutboundAsync(
        TcpClient connection, ChannelReader<OutboundMessage> outbound, CancellationToken cancellationToken)
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

    private async Task SendHeartbeatsAsync(ChannelWriter<OutboundMessage> outbound, CancellationToken cancellationToken)
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
        var windowStart = DateTime.UtcNow;
        var windowCount = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await LanProtocol.ReadAsync(connection.GetStream(), cancellationToken)
                    .AsTask().WaitAsync(_options.HeartbeatTimeout, cancellationToken);

                var now = DateTime.UtcNow;
                if (now - windowStart >= InboundRateWindow) { windowStart = now; windowCount = 0; }
                if (++windowCount > MaxInboundMessagesPerWindow)
                {
                    failure = RelayFailureCategory.Flood;
                    await _notice(isHost ? "A player was disconnected for exceeding the Multiplayer Session's message rate limit." : RateLimited);
                    return;
                }

                switch (message)
                {
                    case LanMessage.ChatEntry chat:
                        await _receiveChat(new ChatEntry(chat.Author, chat.Message));
                        break;
                    case LanMessage.CustomStoryStarted started when !isHost:
                        Log(RelaySeverity.Information, RelayEventName.CustomStoryStartReceived, role, "Admitted->Admitted",
                            customStoryIdentifier: started.Identifier);
                        await _receiveCustomStoryStarted(started.Identifier);
                        break;
                    case LanMessage.CustomStoryStartOutcome reported when isHost:
                        Log(RelaySeverity.Information, RelayEventName.CustomStoryStartOutcomeReceived, role, "Admitted->Admitted",
                            peerCorrelationId: peerCorrelationId, customStoryIdentifier: reported.Identifier,
                            customStoryStartOutcome: reported.Outcome);
                        await _receiveCustomStoryStartOutcome(reported.Identifier, reported.Outcome);
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
        }
    }

    private void Stop()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        _listener?.Stop();
        _joinedConnection?.Dispose();
        _joinedOutbound?.Writer.TryComplete();
        lock (_gate)
        {
            _admittedConnection?.Dispose();
            _admittedConnection = null;
            _admittedOutbound?.Writer.TryComplete();
        }
    }

    private void ClearAdmitted(TcpClient? connection)
    {
        if (connection is not null && !ReferenceEquals(_admittedConnection, connection)) return;
        _admittedConnection?.Dispose();
        _admittedConnection = null;
        _admittedOutbound?.Writer.TryComplete();
        _admittedOutbound = null;
    }

    private void ClearJoined(TcpClient? connection)
    {
        if (connection is not null && !ReferenceEquals(_joinedConnection, connection)) return;
        _joinedConnection?.Dispose();
        _joinedConnection = null;
        _joinedOutbound?.Writer.TryComplete();
        _joinedOutbound = null;
    }

    private static IEnumerable<IPAddress> UsablePrivateAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal)
            .Where(IsPrivate);

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; } catch (Exception exception) when (exception is OperationCanceledException or SocketException) { }
    }
}
