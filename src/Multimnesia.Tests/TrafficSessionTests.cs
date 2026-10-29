using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

// Traffic Statistics and the Traffic Trace over real loopback connections between Game Peers.
public sealed class TrafficSessionTests
{
    private const string Secret = "the password is swordfish";
    private static readonly LanMessage.Pose Pose = new(1, 0, 1, 2, 3, 0, 0, false, false, "maps/secret_cellar.map");
    private static readonly LanMessage.Claim Claim = new("maps/secret_cellar.map", 7, ClaimReason.Interact);

    private static TrafficRecorder StartRecorder(Stream output, bool trace = true) =>
        TrafficRecorder.Start(output, new TrafficOptions
        {
            Statistics = true,
            Trace = trace,
            StatisticsInterval = TimeSpan.FromMilliseconds(50)
        }, _ => { });

    private static async Task<int> FramedSizeAsync(LanMessage message) =>
        await LanProtocol.WriteAsync(new MemoryStream(), message, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Statistics_count_each_LAN_message_kind_and_its_framed_bytes_on_both_Game_Peers()
    {
        var port = FreePort();
        var hostOutput = new MemoryStream();
        var joiningOutput = new MemoryStream();
        var hostTraffic = StartRecorder(hostOutput);
        var joiningTraffic = StartRecorder(joiningOutput);
        var chats = 0;
        var poses = 0;
        var holds = 0;
        await using (var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            receiveChat: _ => { Interlocked.Increment(ref chats); return ValueTask.CompletedTask; },
            receivePose: _ => { Interlocked.Increment(ref poses); return ValueTask.CompletedTask; },
            receiveHoldMessage: _ => { Interlocked.Increment(ref holds); return ValueTask.CompletedTask; },
            traffic: hostTraffic))
        await using (var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port }, traffic: joiningTraffic))
        {
            await host.HostAsync(TestContext.Current.CancellationToken);
            Assert.True((await joining.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken)).Success);

            await joining.SendChatAsync(new ChatEntry("Bob", Secret), TestContext.Current.CancellationToken);
            await Task.Delay(80, TestContext.Current.CancellationToken);
            await joining.SendChatAsync(new ChatEntry("Bob", "again"), TestContext.Current.CancellationToken);
            await joining.SendHoldMessageAsync(Claim, TestContext.Current.CancellationToken);
            joining.SendPose(Pose);
            await WaitUntilAsync(() => Volatile.Read(ref chats) == 2 && Volatile.Read(ref holds) == 1 && Volatile.Read(ref poses) == 1);
        }
        await hostTraffic.DisposeAsync();
        await joiningTraffic.DisposeAsync();

        var chatBytes = await FramedSizeAsync(new LanMessage.ChatEntry("Bob", Secret)) + await FramedSizeAsync(new LanMessage.ChatEntry("Bob", "again"));
        var hostFile = TrafficFile.Parse(hostOutput);
        var joiningFile = TrafficFile.Parse(joiningOutput);
        var hostConnection = LanConnectionId(hostFile);
        var joiningConnection = LanConnectionId(joiningFile);
        Assert.Equal(new TrafficCounts(2, chatBytes), hostFile.StatisticsTotal(hostConnection, "inbound", "chat-entry"));
        Assert.Equal(new TrafficCounts(2, chatBytes), joiningFile.StatisticsTotal(joiningConnection, "outbound", "chat-entry"));
        Assert.Equal(new TrafficCounts(1, await FramedSizeAsync(Claim)), hostFile.StatisticsTotal(hostConnection, "inbound", "claim"));
        Assert.Equal(new TrafficCounts(1, await FramedSizeAsync(Pose)), hostFile.StatisticsTotal(hostConnection, "inbound", "pose"));
        Assert.Equal(new TrafficCounts(1, await FramedSizeAsync(Pose)), joiningFile.StatisticsTotal(joiningConnection, "outbound", "pose"));
        var joinRequestBytes = await FramedSizeAsync(new LanMessage.JoinRequest(LanProtocol.CurrentVersion, Guid.Empty));
        Assert.Equal(new TrafficCounts(1, joinRequestBytes), hostFile.StatisticsTotal(hostConnection, "inbound", "join-request"));
        Assert.Equal(new TrafficCounts(1, joinRequestBytes), joiningFile.StatisticsTotal(joiningConnection, "outbound", "join-request"));
        Assert.Equal(1, joiningFile.StatisticsTotal(joiningConnection, "inbound", "admission-accepted").Count);
        Assert.True(hostFile.Statistics(hostConnection).Count >= 2);

        foreach (var (file, connection) in new[] { (hostFile, hostConnection), (joiningFile, joiningConnection) })
        {
            var summary = Assert.Single(file.Records("summary"), record => record.GetProperty("connection").GetInt32() == connection);
            foreach (var direction in new[] { "inbound", "outbound" })
                foreach (var kind in summary.GetProperty(direction).EnumerateObject().Select(property => property.Name))
                    Assert.Equal(TrafficFile.Counts(summary, direction, kind), file.StatisticsTotal(connection, direction, kind));
            Assert.Equal(["opened", "admitted", "closed"], file.Records("connection")
                .Where(record => record.GetProperty("connection").GetInt32() == connection)
                .Select(record => record.GetProperty("event").GetString()));
            Assert.Contains(file.Records("trace"), record => record.GetProperty("kind").GetString() == "chat-entry");
            Assert.DoesNotContain("swordfish", file.Text);
            Assert.DoesNotContain("secret_cellar", file.Text);
        }
    }

    [Fact]
    public async Task The_Session_Host_reports_each_Joining_Player_separately_by_peer_correlation_id()
    {
        var port = FreePort();
        var output = new MemoryStream();
        var traffic = StartRecorder(output, trace: false);
        var hostNotices = new List<string>();
        Guid firstPeer, secondPeer;
        await using (var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            message => { lock (hostNotices) hostNotices.Add(message); return ValueTask.CompletedTask; },
            traffic: traffic))
        {
            await host.HostAsync(TestContext.Current.CancellationToken);
            await using (var first = new TcpSessionOperations(new SessionNetworkOptions { Port = port }))
            {
                Assert.True((await first.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken)).Success);
                firstPeer = first.PeerCorrelationId;
                await first.SendChatAsync(new ChatEntry("First", "one"), TestContext.Current.CancellationToken);
                await first.LeaveAsync(GamePeerState.Joined, TestContext.Current.CancellationToken);
                await WaitUntilAsync(() => { lock (hostNotices) return hostNotices.Contains("A player left."); });
            }
            await using var second = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
            Assert.True((await second.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken)).Success);
            secondPeer = second.PeerCorrelationId;
            await second.SendChatAsync(new ChatEntry("Second", "two"), TestContext.Current.CancellationToken);
            await second.SendChatAsync(new ChatEntry("Second", "three"), TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        await traffic.DisposeAsync();

        var file = TrafficFile.Parse(output);
        long ChatsFrom(Guid peer) => file.Records("statistics")
            .Where(record => record.TryGetProperty("peerCorrelationId", out var id) && id.GetGuid() == peer)
            .Sum(record => TrafficFile.Counts(record, "inbound", "chat-entry").Count);
        Assert.Equal(1, ChatsFrom(firstPeer));
        Assert.Equal(2, ChatsFrom(secondPeer));
        Assert.Equal(2, file.Records("summary").Count);
    }

    [Fact]
    public async Task Messages_keep_flowing_while_the_traffic_file_writer_is_stalled()
    {
        var port = FreePort();
        var output = new StallingStream();
        var traffic = TrafficRecorder.Start(output, new TrafficOptions { Statistics = true, Trace = true, TraceCapacity = 4 }, _ => { });
        var received = new List<string>();
        await using (var host = new TcpSessionOperations(
            new SessionNetworkOptions { Port = port },
            receiveChat: entry => { lock (received) received.Add(entry.Message); return ValueTask.CompletedTask; },
            traffic: traffic))
        await using (var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port }))
        {
            await host.HostAsync(TestContext.Current.CancellationToken);
            Assert.True((await joining.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken)).Success);
            await output.Stalled.WaitAsync(TestContext.Current.CancellationToken);

            for (var i = 0; i < 20; i++)
                await joining.SendChatAsync(new ChatEntry("Bob", $"message {i}"), TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => { lock (received) return received.Count == 20; });
        }
        output.Release();
        await traffic.DisposeAsync();

        var summary = Assert.Single(TrafficFile.Parse(output.Written).Records("summary"));
        Assert.True(summary.GetProperty("traceDropped").GetInt64() > 0);
        Assert.Equal(20, TrafficFile.Counts(summary, "inbound", "chat-entry").Count);
    }

    private static int LanConnectionId(TrafficFile file) =>
        file.Records("connection").First(record => record.GetProperty("channel").GetString() == "lan").GetProperty("connection").GetInt32();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
