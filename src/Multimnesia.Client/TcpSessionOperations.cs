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
    public int ProtocolVersion { get; init; } = LanProtocol.CurrentVersion;
}

public sealed class TcpSessionOperations : ISessionOperations, IAsyncDisposable
{
    private const int OutboundChatCapacity = 64;
    private const string Incompatible = "The Multiplayer Session uses an incompatible protocol version.";
    private const string Full = "The Multiplayer Session is full.";
    private readonly SessionNetworkOptions _options;
    private readonly Func<string, ValueTask> _notice;
    private readonly Func<ChatEntry, ValueTask> _receiveChat;
    private readonly Func<string, Task<IPAddress[]>> _resolver;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private TcpClient? _joinedConnection;
    private TcpClient? _admittedConnection;
    private Channel<LanMessage.ChatEntry>? _joinedOutbound;
    private Channel<LanMessage.ChatEntry>? _admittedOutbound;
    private Task? _acceptLoop;

    public TcpSessionOperations(
        SessionNetworkOptions options,
        Func<string, ValueTask>? notice = null,
        Func<string, Task<IPAddress[]>>? resolver = null,
        Func<ChatEntry, ValueTask>? receiveChat = null)
    {
        _options = options;
        _notice = notice ?? (_ => ValueTask.CompletedTask);
        _resolver = resolver ?? Dns.GetHostAddressesAsync;
        _receiveChat = receiveChat ?? (_ => ValueTask.CompletedTask);
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
                        _ = ReceiveChatAsync(connection, isHost: false, _lifetime.Token);
                        return SessionOperationResult.SucceededWith("Joined the Multiplayer Session.");
                    case LanMessage.AdmissionRejected rejected:
                        connection.Dispose();
                        return SessionOperationResult.Failed(rejected.Reason);
                    default:
                        throw new LanProtocolException("Unexpected LAN message during negotiation.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                connection.Dispose();
                return SessionOperationResult.Failed("Joining the Multiplayer Session timed out.");
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                connection.Dispose();
                lastFailure = exception;
            }
        }
        return SessionOperationResult.Failed($"Could not join the Multiplayer Session: {lastFailure?.Message ?? "connection failed"}");
    }

    public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public async Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken)
    {
        Channel<LanMessage.ChatEntry>? outbound;
        TcpClient? connection;
        lock (_gate)
        {
            outbound = _joinedOutbound ?? _admittedOutbound;
            connection = _joinedConnection ?? _admittedConnection;
        }
        if (outbound is null || connection is null) return;
        if (outbound.Writer.TryWrite(new LanMessage.ChatEntry(entry.Author, entry.Message))) return;

        connection.Dispose();
        await _notice("The remote Game Peer could not keep up with chat traffic.");
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return _acceptLoop is null ? ValueTask.CompletedTask : new ValueTask(IgnoreCancellationAsync(_acceptLoop));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleAttemptAsync(connection, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleAttemptAsync(TcpClient connection, CancellationToken cancellationToken)
    {
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
                await LanProtocol.WriteAsync(stream, new LanMessage.AdmissionRejected(Full), handshake.Token);
                stream.Dispose();
                return;
            }
            try
            {
                await LanProtocol.WriteAsync(stream,
                    new LanMessage.AdmissionAccepted(_options.ProtocolVersion, SessionCorrelationId), handshake.Token);
                lock (_gate) _admittedOutbound = StartOutbound(connection, cancellationToken);
                await _notice("A player joined.");
                await ReceiveChatAsync(connection, isHost: true, cancellationToken);
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
            connection?.Dispose();
        }
    }

    private Channel<LanMessage.ChatEntry> StartOutbound(TcpClient connection, CancellationToken cancellationToken)
    {
        var outbound = Channel.CreateBounded<LanMessage.ChatEntry>(new BoundedChannelOptions(OutboundChatCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _ = SendOutboundAsync(connection, outbound.Reader, cancellationToken);
        return outbound;
    }

    private static async Task SendOutboundAsync(
        TcpClient connection, ChannelReader<LanMessage.ChatEntry> outbound, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in outbound.ReadAllAsync(cancellationToken))
                await LanProtocol.WriteAsync(connection.GetStream(), entry, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { connection.Dispose(); }
    }

    private async Task ReceiveChatAsync(TcpClient connection, bool isHost, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await LanProtocol.ReadAsync(connection.GetStream(), cancellationToken);
                if (message is not LanMessage.ChatEntry chat) throw new LanProtocolException("Unexpected LAN message after admission.");
                await _receiveChat(new ChatEntry(chat.Author, chat.Message));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (LanProtocolException)
        {
            await _notice("The remote Game Peer violated the Multiplayer Session protocol.");
        }
        catch (IOException) { }
        finally
        {
            connection.Dispose();
            lock (_gate)
            {
                if (isHost && ReferenceEquals(_admittedConnection, connection))
                {
                    _admittedConnection = null;
                    _admittedOutbound?.Writer.TryComplete();
                    _admittedOutbound = null;
                }
                if (!isHost && ReferenceEquals(_joinedConnection, connection))
                {
                    _joinedConnection = null;
                    _joinedOutbound?.Writer.TryComplete();
                    _joinedOutbound = null;
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
        try { await task; } catch (OperationCanceledException) { }
    }
}
