using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class MultiplayerSessionOperationsTests
{
    [Fact]
    public async Task Joining_Player_can_leave_and_a_replacement_can_join()
    {
        var port = FreePort();
        var hostNotices = new List<string>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            message => { lock (hostNotices) hostNotices.Add(message); return ValueTask.CompletedTask; });
        await using var first = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var replacement = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);
        Assert.True((await first.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken)).Success);

        await first.LeaveAsync(GamePeerState.Joined, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => { lock (hostNotices) return hostNotices.Contains("A player left."); });
        var result = await replacement.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Session_Host_leave_returns_Joining_Player_to_Local_with_exact_feedback()
    {
        var port = FreePort();
        var joiningNotices = new List<string>();
        await using var hostOperations = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var joiningOperations = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            message => { lock (joiningNotices) joiningNotices.Add(message); return ValueTask.CompletedTask; });
        var joining = new GamePeerOrchestrator(joiningOperations);
        await hostOperations.HostAsync(TestContext.Current.CancellationToken);
        await joining.HandleAsync(new ChatEntry("Player", "/join 127.0.0.1"), TestContext.Current.CancellationToken);

        await hostOperations.LeaveAsync(GamePeerState.Hosting, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => joining.State == GamePeerState.Local);

        lock (joiningNotices) Assert.Contains("The host ended the Multiplayer Session.", joiningNotices);
    }

    [Fact]
    public async Task Abrupt_Joining_Player_loss_keeps_hosting_and_allows_replacement()
    {
        var port = FreePort();
        var hostNotices = new List<string>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port, HeartbeatInterval = TimeSpan.FromMilliseconds(20), HeartbeatTimeout = TimeSpan.FromMilliseconds(100) },
            message => { lock (hostNotices) hostNotices.Add(message); return ValueTask.CompletedTask; });
        await using var replacement = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);
        using var first = new TcpClient(AddressFamily.InterNetwork);
        await first.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await LanProtocol.WriteAsync(first.GetStream(),
            new LanMessage.JoinRequest(LanProtocol.CurrentVersion, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.IsType<LanMessage.AdmissionAccepted>(await LanProtocol.ReadAsync(first.GetStream(), TestContext.Current.CancellationToken));

        first.Dispose();
        await WaitUntilAsync(() => { lock (hostNotices) return hostNotices.Contains("A player disconnected."); });
        var result = await replacement.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Silent_Session_Host_loss_is_detected_by_heartbeat_timeout()
    {
        var port = FreePort();
        var notices = new List<string>();
        using var silentHost = new TcpListener(IPAddress.Loopback, port);
        silentHost.Start();
        var hostTask = Task.Run(async () =>
        {
            using var connection = await silentHost.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var request = await LanProtocol.ReadAsync(connection.GetStream(), TestContext.Current.CancellationToken);
            Assert.IsType<LanMessage.JoinRequest>(request);
            await LanProtocol.WriteAsync(connection.GetStream(),
                new LanMessage.AdmissionAccepted(LanProtocol.CurrentVersion, Guid.NewGuid()), TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await using var operations = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port, HeartbeatInterval = TimeSpan.FromMilliseconds(20), HeartbeatTimeout = TimeSpan.FromMilliseconds(100) },
            message => { lock (notices) notices.Add(message); return ValueTask.CompletedTask; });
        var joining = new GamePeerOrchestrator(operations);

        await joining.HandleAsync(new ChatEntry("Player", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => joining.State == GamePeerState.Local);

        lock (notices) Assert.Contains("Connection to the host was lost.", notices);
        await hostTask;
    }

    [Fact]
    public async Task Graceful_disposal_signals_departure_before_shutdown()
    {
        var port = FreePort();
        var hostNotices = new List<string>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            message => { lock (hostNotices) hostNotices.Add(message); return ValueTask.CompletedTask; });
        var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);
        await joining.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);

        await joining.DisposeAsync();
        await WaitUntilAsync(() => { lock (hostNotices) return hostNotices.Contains("A player left."); });
    }
    [Fact]
    public async Task Ordinary_chat_is_delivered_bidirectionally_without_echoing_the_sender()
    {
        var port = FreePort();
        await using var hostGame = new MemoryStream();
        await using var joiningGame = new MemoryStream();
        await using var hostWriter = GameInteractionProtocol.CreateWriter(hostGame);
        await using var joiningWriter = GameInteractionProtocol.CreateWriter(joiningGame);
        await using var hostOperations = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            receiveChat: entry => new ValueTask(hostWriter.WriteLineAsync(GameInteractionProtocol.Display(entry))));
        await using var joinOperations = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            receiveChat: entry => new ValueTask(joiningWriter.WriteLineAsync(GameInteractionProtocol.Display(entry))));
        var host = new GamePeerOrchestrator(hostOperations);
        var joining = new GamePeerOrchestrator(joinOperations);
        await host.HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        await joining.HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);

        await host.HandleAsync(new ChatEntry("Žofie 👩‍🚀", "ahoj: světe 👋"), TestContext.Current.CancellationToken);
        await joining.HandleAsync(new ChatEntry("René", "nazdar"), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => hostGame.Length > 0 && joiningGame.Length > 0);

        Assert.Equal("chat:René:nazdar\n", Encoding.UTF8.GetString(hostGame.ToArray()));
        Assert.Equal("chat:Žofie 👩‍🚀:ahoj: světe 👋\n", Encoding.UTF8.GetString(joiningGame.ToArray()));
    }

    [Fact]
    public async Task Invalid_remote_chat_is_discarded_with_only_generic_SYSTEM_feedback()
    {
        var port = FreePort();
        var notices = new List<string>();
        var received = new List<ChatEntry>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            notice => { notices.Add(notice); return ValueTask.CompletedTask; },
            receiveChat: entry => { received.Add(entry); return ValueTask.CompletedTask; });
        await host.HostAsync(TestContext.Current.CancellationToken);
        using var offender = new TcpClient(AddressFamily.InterNetwork);
        await offender.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        var stream = offender.GetStream();
        await LanProtocol.WriteAsync(stream,
            new LanMessage.JoinRequest(LanProtocol.CurrentVersion, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.IsType<LanMessage.AdmissionAccepted>(await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));

        await WriteRawFrameAsync(stream,
            "{\"type\":\"chat-entry\",\"author\":\"Mallory\",\"message\":\"private-invalid-content\\n\"}");
        await WaitUntilAsync(() => notices.Contains("The remote Game Peer violated the Multiplayer Session protocol."));

        Assert.Empty(received);
        Assert.DoesNotContain(notices, notice => notice.Contains("private-invalid-content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_host_chat_returns_the_Joining_Player_to_Local()
    {
        var port = FreePort();
        using var relay = new TcpListener(IPAddress.Loopback, port);
        relay.Start();
        var relayTask = Task.Run(async () =>
        {
            using var connection = await relay.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = connection.GetStream();
            Assert.IsType<LanMessage.JoinRequest>(await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
            await LanProtocol.WriteAsync(stream,
                new LanMessage.AdmissionAccepted(LanProtocol.CurrentVersion, Guid.NewGuid()),
                TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            await WriteRawFrameAsync(stream,
                "{\"type\":\"chat-entry\",\"author\":\"Host\",\"message\":\"bad\\nchat\"}");
        }, TestContext.Current.CancellationToken);
        await using var operations = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        var joining = new GamePeerOrchestrator(operations);

        await joining.HandleAsync(new ChatEntry("Player", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        Assert.Equal(GamePeerState.Joined, joining.State);
        await WaitUntilAsync(() => joining.State == GamePeerState.Local);

        await relayTask;
    }

    [Fact]
    public async Task Commands_drive_real_operations_through_hosting_and_joined_states()
    {
        var port = FreePort();
        await using var hostOperations = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var joinOperations = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        var host = new GamePeerOrchestrator(hostOperations);
        var joining = new GamePeerOrchestrator(joinOperations);

        var hostFeedback = await host.HandleAsync(new ChatEntry("Player", "/host"), TestContext.Current.CancellationToken);
        var joinFeedback = await joining.HandleAsync(new ChatEntry("Player", "/join 127.0.0.1"), TestContext.Current.CancellationToken);

        Assert.Equal(GamePeerState.Hosting, host.State);
        Assert.StartsWith("Hosting Multiplayer Session at ", hostFeedback?.Message);
        Assert.Equal(GamePeerState.Joined, joining.State);
        Assert.Equal("Joined the Multiplayer Session.", joinFeedback?.Message);
    }

    [Fact]
    public async Task Host_and_join_negotiate_and_report_admission()
    {
        var port = FreePort();
        var hostNotices = new List<string>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port }, notice => { hostNotices.Add(notice); return ValueTask.CompletedTask; });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port });

        var hosted = await host.HostAsync(TestContext.Current.CancellationToken);
        var joined = await joining.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => hostNotices.Contains("A player joined."));

        Assert.True(hosted.Success);
        Assert.Contains($":{port}", hosted.Feedback);
        Assert.True(joined.Success);
        Assert.Equal(new[] { "A player is attempting to join.", "A player joined." }, hostNotices);
        Assert.NotEqual(Guid.Empty, host.SessionCorrelationId);
        Assert.NotEqual(Guid.Empty, joining.PeerCorrelationId);
    }

    [Fact]
    public async Task Incompatible_protocol_versions_are_explicitly_rejected()
    {
        var port = FreePort();
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port, ProtocolVersion = 99 });
        await host.HostAsync(TestContext.Current.CancellationToken);

        var result = await joining.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("The Multiplayer Session uses an incompatible protocol version.", result.Feedback);
    }

    [Fact]
    public async Task Exactly_one_simultaneous_join_is_admitted()
    {
        var port = FreePort();
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var first = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var second = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(
            first.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken),
            second.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken));

        Assert.Single(results, result => result.Success);
        Assert.Single(results, result => result.Feedback == "The Multiplayer Session is full.");
    }

    [Fact]
    public async Task IPv6_only_resolution_is_rejected_without_connecting()
    {
        await using var operations = new TcpSessionOperations(
            new SessionNetworkOptions(), resolver: _ => Task.FromResult<IPAddress[]>([IPAddress.IPv6Loopback]));

        var result = await operations.JoinAsync("v6-only", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("The host did not resolve to an IPv4 address.", result.Feedback);
    }

    [Fact]
    public async Task Resolved_IPv4_addresses_are_tried_sequentially()
    {
        var port = FreePort();
        using var relay = new TcpListener(IPAddress.Loopback, port);
        relay.Start();
        var relayTask = Task.Run(async () =>
        {
            using var connection = await relay.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = connection.GetStream();
            Assert.IsType<LanMessage.JoinRequest>(await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
            await LanProtocol.WriteAsync(stream,
                new LanMessage.AdmissionAccepted(LanProtocol.CurrentVersion, Guid.NewGuid()),
                TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await using var operations = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            resolver: _ => Task.FromResult<IPAddress[]>([IPAddress.Parse("127.0.0.2"), IPAddress.Loopback]));

        var result = await operations.JoinAsync("multi-address", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        await relayTask;
    }

    [Fact]
    public async Task One_deadline_bounds_a_stalled_negotiation()
    {
        var port = FreePort();
        using var relay = new TcpListener(IPAddress.Loopback, port);
        relay.Start();
        var relayTask = Task.Run(async () =>
        {
            using var connection = await relay.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await using var operations = new TcpSessionOperations(new SessionNetworkOptions
        {
            Port = port,
            JoinTimeout = TimeSpan.FromMilliseconds(100)
        });

        var result = await operations.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);

        Assert.Equal("Joining the Multiplayer Session timed out.", result.Feedback);
        await relayTask;
    }

    [Fact]
    public async Task A_stalled_handshake_does_not_reserve_the_joining_player_slot()
    {
        var port = FreePort();
        await using var host = new TcpSessionOperations(new SessionNetworkOptions
        {
            Port = port,
            HandshakeTimeout = TimeSpan.FromMilliseconds(50)
        });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);
        using var stalled = new TcpClient(AddressFamily.InterNetwork);
        await stalled.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var result = await joining.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Bind_failure_returns_a_useful_system_message()
    {
        var port = FreePort();
        using var occupied = new TcpListener(IPAddress.Any, port);
        occupied.Start();
        await using var operations = new TcpSessionOperations(new SessionNetworkOptions { Port = port });

        var result = await operations.HostAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.StartsWith("Hosting failed:", result.Feedback);
    }

    [Fact]
    public async Task Handshake_attempts_beyond_the_concurrency_cap_are_rejected_immediately()
    {
        var port = FreePort();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port, HandshakeTimeout = TimeSpan.FromSeconds(5) });
        await host.HostAsync(TestContext.Current.CancellationToken);

        var stalled = new List<TcpClient>();
        try
        {
            for (var i = 0; i < TcpSessionOperations.MaxConcurrentHandshakes; i++)
            {
                var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
                stalled.Add(client);
            }

            using var overflow = new TcpClient(AddressFamily.InterNetwork);
            await overflow.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            var buffer = new byte[1];
            await WaitUntilAsync(() =>
            {
                try { return overflow.Client.Poll(0, SelectMode.SelectRead) && overflow.Client.Receive(buffer, SocketFlags.Peek) == 0; }
                catch (SocketException) { return true; }
            });
        }
        finally { foreach (var client in stalled) client.Dispose(); }
    }

    [Fact]
    public async Task An_admitted_peer_flooding_valid_messages_is_disconnected_without_ending_the_session()
    {
        var port = FreePort();
        var hostNotices = new List<string>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            message => { lock (hostNotices) hostNotices.Add(message); return ValueTask.CompletedTask; });
        await using var replacement = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);

        using var offender = new TcpClient(AddressFamily.InterNetwork);
        await offender.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        var stream = offender.GetStream();
        await LanProtocol.WriteAsync(stream,
            new LanMessage.JoinRequest(LanProtocol.CurrentVersion, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.IsType<LanMessage.AdmissionAccepted>(await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));

        try
        {
            for (var i = 0; i < TcpSessionOperations.MaxInboundMessagesPerWindow + 5; i++)
                await LanProtocol.WriteAsync(stream, new LanMessage.Heartbeat(), TestContext.Current.CancellationToken);
        }
        catch (IOException) { }

        await WaitUntilAsync(() => { lock (hostNotices) return hostNotices.Any(notice => notice.Contains("rate limit", StringComparison.Ordinal)); });
        var result = await replacement.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        lock (hostNotices) Assert.DoesNotContain(hostNotices, notice => notice.Contains("heartbeat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Structured_logs_capture_role_and_correlation_without_leaking_endpoints_above_debug()
    {
        var port = FreePort();
        var log = new List<RelayLogEntry>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port }, log: entry => { lock (log) log.Add(entry); });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);
        await joining.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => { lock (log) return log.Any(entry => entry.Event == RelayEventName.PeerAdmitted); });

        await joining.LeaveAsync(GamePeerState.Joined, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => { lock (log) return log.Any(entry => entry.Event == RelayEventName.PeerDisconnected); });

        List<RelayLogEntry> snapshot;
        lock (log) snapshot = [.. log];
        Assert.Contains(snapshot, entry =>
            entry.Event == RelayEventName.HandshakeAttempted && entry.Severity == RelaySeverity.Debug && entry.Endpoint is not null);
        var admitted = Assert.Single(snapshot, entry => entry.Event == RelayEventName.PeerAdmitted);
        Assert.Equal(RelayRole.Host, admitted.Role);
        Assert.NotEqual(RelaySeverity.Debug, admitted.Severity);
        Assert.Null(admitted.Endpoint);
        Assert.NotNull(admitted.SessionCorrelationId);
        Assert.Equal(joining.PeerCorrelationId, admitted.PeerCorrelationId);
        var disconnected = Assert.Single(snapshot, entry => entry.Event == RelayEventName.PeerDisconnected);
        Assert.Equal(RelayFailureCategory.None, disconnected.Failure);
        Assert.Equal(joining.PeerCorrelationId, disconnected.PeerCorrelationId);
    }

    [Fact]
    public async Task Concurrent_Joining_Players_are_each_logged_under_their_own_correlation_identifier()
    {
        var port = FreePort();
        var log = new List<RelayLogEntry>();
        await using var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port }, log: entry => { lock (log) log.Add(entry); });
        await using var first = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var second = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await host.HostAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(
            first.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken),
            second.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => { lock (log) return log.Count(entry => entry.Event == RelayEventName.HandshakeRejected) >= 1; });

        List<RelayLogEntry> snapshot;
        lock (log) snapshot = [.. log];
        var admittedIds = new Guid?[] { first.PeerCorrelationId, second.PeerCorrelationId };
        Assert.Contains(snapshot, entry => entry.Event == RelayEventName.PeerAdmitted && admittedIds.Contains(entry.PeerCorrelationId));
        Assert.Contains(snapshot, entry => entry.Event == RelayEventName.HandshakeRejected
            && entry.Failure == RelayFailureCategory.Capacity && admittedIds.Contains(entry.PeerCorrelationId));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task WriteRawFrameAsync(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, TestContext.Current.CancellationToken);
        await stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
    }
}
