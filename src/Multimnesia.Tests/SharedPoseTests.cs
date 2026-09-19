using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class SharedPoseTests
{
    private const string Create = "avatarcreate partner";
    private const string Subscribe = "localpose subscribe 30";
    private const string Remove = "avatarremove partner";
    private const string Unsubscribe = "localpose unsubscribe";
    private static readonly ProtocolNegotiation Granted = new("ok", 2, ["avatars", "localpose"]);
    private static readonly LocalPose Local = new(1000, 1, 1.25, 2.5, -3.75, 90, -45, false, "custom_stories/mp-test-cs/maps/start.map");
    private static readonly LanMessage.Pose Remote = new(
        123456, 3, 1.25, -2.5, 3.75, 90, -45, true, "custom_stories/My Story: Part 2/maps/cellar one.map");
    private const string RemoteLine =
        "avatarpose partner 123456 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 custom_stories/My Story: Part 2/maps/cellar one.map";

    private readonly FakeSessionOperations _sessions = new();
    private readonly RecordingGame _game = new();

    private SharedPose CreateSharedPose(ProtocolNegotiation? negotiation = null) => CreateSharedPose(_sessions, _game, negotiation);

    private static SharedPose CreateSharedPose(ISessionOperations sessions, RecordingGame game, ProtocolNegotiation? negotiation = null) =>
        new(sessions, game.WriteLineAsync, Task.FromResult(negotiation ?? Granted), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_other_player_becoming_present_creates_the_Avatar_and_subscribes_to_the_local_Pose()
    {
        CreateSharedPose();

        _sessions.SetPresent(true);

        await _game.WaitForLinesAsync(2);
        Assert.Equal([Create, Subscribe], _game.Lines);
    }

    [Fact]
    public async Task Nothing_is_written_while_the_other_player_is_not_present()
    {
        var sharedPose = CreateSharedPose();

        _sessions.SetPresent(false);
        sharedPose.HandleLocalPose(Local);
        sharedPose.HandleReceivedPose(Remote);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_game.Lines);
    }

    [Theory]
    [InlineData("ok", 2, new[] { "avatars" })]
    [InlineData("ok", 2, new[] { "localpose" })]
    [InlineData("unknown-command", null, new string[0])]
    public async Task Nothing_is_written_unless_the_local_game_granted_both_Capabilities(string outcome, int? version, string[] capabilities)
    {
        var sharedPose = CreateSharedPose(new ProtocolNegotiation(outcome, version, capabilities));

        _sessions.SetPresent(true);
        sharedPose.HandleLocalPose(Local);
        sharedPose.HandleReceivedPose(Remote);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_game.Lines);
        Assert.Empty(_sessions.SentPoses);
    }

    [Fact]
    public async Task Each_local_Pose_is_sent_to_the_other_player()
    {
        var sharedPose = CreateSharedPose();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(2);

        sharedPose.HandleLocalPose(Local);
        sharedPose.HandleLocalPose(Local with { TimeMs = 1033 });

        Assert.Equal(
            [
                new LanMessage.Pose(1000, 1, 1.25, 2.5, -3.75, 90, -45, false, "custom_stories/mp-test-cs/maps/start.map"),
                new LanMessage.Pose(1033, 1, 1.25, 2.5, -3.75, 90, -45, false, "custom_stories/mp-test-cs/maps/start.map")
            ],
            _sessions.SentPoses);
    }

    [Fact]
    public async Task A_received_Pose_is_written_as_avatarpose_formatted_by_this_Game_Peer_whatever_the_locale()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs-CZ");
        try
        {
            var sharedPose = CreateSharedPose();
            _sessions.SetPresent(true);
            await _game.WaitForLinesAsync(2);
            sharedPose.HandleLocalPose(Local);

            sharedPose.HandleReceivedPose(Remote);

            await _game.WaitForLinesAsync(3);
            Assert.Equal(RemoteLine, _game.Lines[2]);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task Received_Poses_are_latest_wins_while_a_write_to_the_local_game_is_pending()
    {
        var sharedPose = CreateSharedPose();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(2);
        sharedPose.HandleLocalPose(Local);
        var release = _game.HoldWrites();

        for (ulong time = 1; time <= 100; time++) sharedPose.HandleReceivedPose(Remote with { TimeMs = time });
        await _game.WaitForHeldWriteAsync();
        release();
        await _game.WaitForLinesAsync(4);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(4, _game.Lines.Count);
        Assert.StartsWith("avatarpose partner 100 ", _game.Lines[3]);
    }

    // A still player in a menu, or with the game in the background, reports no Pose of their own (manual Whisper test).
    [Fact]
    public async Task Received_Poses_are_written_while_the_local_game_reports_no_Pose_of_its_own()
    {
        var sharedPose = CreateSharedPose();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(2);

        for (ulong time = 1; time <= 5; time++)
        {
            sharedPose.HandleReceivedPose(Remote with { TimeMs = time });
            await _game.WaitForLinesAsync(2 + (int)time);
        }

        Assert.Equal(
            ["avatarpose partner 1 ", "avatarpose partner 2 ", "avatarpose partner 3 ", "avatarpose partner 4 ", "avatarpose partner 5 "],
            _game.Lines[2..].Select(line => line[..(line.IndexOf(' ', "avatarpose partner ".Length) + 1)]));
    }

    [Fact]
    public async Task Received_Poses_wait_for_the_local_game_to_answer_a_ping_once_a_second_of_them_is_unconfirmed()
    {
        var sharedPose = CreateSharedPose();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(2);

        for (ulong time = 1; time <= 40; time++) sharedPose.HandleReceivedPose(Remote with { TimeMs = time });
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var written = _game.Lines[2..];
        Assert.Equal(SharedPose.MaxUnconfirmedPoses, written.Count(line => line.StartsWith("avatarpose ", StringComparison.Ordinal)));
        Assert.Equal("ping", Assert.Single(written, line => !line.StartsWith("avatarpose ", StringComparison.Ordinal)));
        Assert.Equal("ping", written[SharedPose.PingEveryPoses]);

        sharedPose.HandlePong();
        await _game.WaitForLinesAsync(2 + written.Count + 1);

        Assert.StartsWith("avatarpose partner 40 ", _game.Lines[2 + written.Count]);
    }

    [Fact]
    public async Task The_other_player_leaving_removes_the_Avatar_and_ends_the_subscription()
    {
        var sharedPose = CreateSharedPose();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(2);

        _sessions.SetPresent(false);
        await _game.WaitForLinesAsync(4);
        sharedPose.HandleLocalPose(Local);
        sharedPose.HandleReceivedPose(Remote);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Create, Subscribe, Remove, Unsubscribe], _game.Lines);
    }

    [Fact]
    public async Task Two_Game_Peers_see_each_other_until_the_Joining_Player_leaves()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        var joiningGame = new RecordingGame();
        SharedPose hostPose = null!;
        SharedPose joiningPose = null!;
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receivePose: pose => { hostPose.HandleReceivedPose(pose); return ValueTask.CompletedTask; });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receivePose: pose => { joiningPose.HandleReceivedPose(pose); return ValueTask.CompletedTask; });
        hostPose = CreateSharedPose(host, hostGame);
        joiningPose = CreateSharedPose(joining, joiningGame);
        var hostPeer = new GamePeerOrchestrator(host);
        var joiningPeer = new GamePeerOrchestrator(joining);

        await hostPeer.HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        Assert.Empty(hostGame.Lines);
        await joiningPeer.HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await hostGame.WaitForLinesAsync(2);
        await joiningGame.WaitForLinesAsync(2);
        Assert.Equal([Create, Subscribe], hostGame.Lines);
        Assert.Equal([Create, Subscribe], joiningGame.Lines);

        hostPose.HandleLocalPose(Local);
        joiningPose.HandleLocalPose(Local with { X = 7 });
        await hostGame.WaitForLinesAsync(3);
        await joiningGame.WaitForLinesAsync(3);
        Assert.StartsWith("avatarpose partner 1000 1 7.0000 ", hostGame.Lines[2]);
        Assert.StartsWith("avatarpose partner 1000 1 1.2500 ", joiningGame.Lines[2]);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var (hostLines, joiningLines) = (hostGame.Lines.Count, joiningGame.Lines.Count);

        await joiningPeer.HandleAsync(new ChatEntry("Joiner", "/leave"), TestContext.Current.CancellationToken);
        await hostGame.WaitForLinesAsync(hostLines + 2);
        await joiningGame.WaitForLinesAsync(joiningLines + 2);
        Assert.Equal([Remove, Unsubscribe], hostGame.Lines[hostLines..]);
        Assert.Equal([Remove, Unsubscribe], joiningGame.Lines[joiningLines..]);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingGame
    {
        private readonly List<string> _lines = [];
        private TaskCompletionSource? _hold;
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (_lines) _lines.Add(line);
            if (Volatile.Read(ref _hold) is { } hold)
            {
                _held.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken);
            }
        }

        // The next write is recorded but does not complete until released.
        public Action HoldWrites()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _hold, hold);
            return () => { Volatile.Write(ref _hold, null); hold.TrySetResult(); };
        }

        public Task WaitForHeldWriteAsync() => _held.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public async Task WaitForLinesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Lines.Count < count) await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        private bool _present;
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged;
        public bool IsJoined => false;
        public bool IsOtherPlayerPresent => Volatile.Read(ref _present);
        public List<LanMessage.Pose> SentPoses { get; } = [];

        public void SetPresent(bool present)
        {
            Volatile.Write(ref _present, present);
            OtherPlayerPresenceChanged?.Invoke();
        }

        public void SendPose(LanMessage.Pose pose) { lock (SentPoses) SentPoses.Add(pose); }
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
