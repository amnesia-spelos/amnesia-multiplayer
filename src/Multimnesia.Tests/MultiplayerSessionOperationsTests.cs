using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class MultiplayerSessionOperationsTests
{
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
}
