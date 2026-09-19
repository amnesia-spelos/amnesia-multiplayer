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
        new(sessions, game.WriteLineAsync, game.DisplaySystemAsync, game.Log, Task.FromResult(negotiation ?? Granted),
            TestContext.Current.CancellationToken);

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
        Assert.Empty(_game.Notices);
        Assert.Empty(_game.Logged);
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

    [Fact]
    public async Task The_Joining_Player_removes_the_Avatar_when_the_Session_Host_ends_the_Multiplayer_Session()
    {
        var port = FreePort();
        var joiningGame = new RecordingGame();
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        CreateSharedPose(joining, joiningGame);
        var hostPeer = new GamePeerOrchestrator(host);
        await hostPeer.HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        await new GamePeerOrchestrator(joining).HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await joiningGame.WaitForLinesAsync(2);

        await hostPeer.HandleAsync(new ChatEntry("Host", "/leave"), TestContext.Current.CancellationToken);

        await joiningGame.WaitForLinesAsync(4);
        Assert.Equal([Create, Subscribe, Remove, Unsubscribe], joiningGame.Lines);
    }

    [Fact]
    public async Task The_Session_Host_removes_the_Avatar_when_the_connection_is_lost_and_creates_a_fresh_one_for_a_replacement()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        await using var replacement = new TcpSessionOperations(new SessionNetworkOptions { Port = port });
        CreateSharedPose(host, hostGame);
        await host.HostAsync(TestContext.Current.CancellationToken);
        using (await AdmitRawPeerAsync(port)) await hostGame.WaitForLinesAsync(2);

        await hostGame.WaitForLinesAsync(4);
        Assert.True((await replacement.JoinAsync("127.0.0.1", TestContext.Current.CancellationToken)).Success);

        await hostGame.WaitForLinesAsync(6);
        Assert.Equal([Create, Subscribe, Remove, Unsubscribe, Create, Subscribe], hostGame.Lines);
    }

    [Fact]
    public async Task The_Session_Host_removes_the_Avatar_when_the_Joining_Player_falls_silent()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        await using var host = new TcpSessionOperations(new SessionNetworkOptions
        {
            Port = port, HeartbeatInterval = TimeSpan.FromMilliseconds(20), HeartbeatTimeout = TimeSpan.FromMilliseconds(100)
        });
        CreateSharedPose(host, hostGame);
        await host.HostAsync(TestContext.Current.CancellationToken);

        using var silent = await AdmitRawPeerAsync(port);

        await hostGame.WaitForLinesAsync(4);
        Assert.Equal([Create, Subscribe, Remove, Unsubscribe], hostGame.Lines);
    }

    [Fact]
    public async Task A_player_replaced_before_their_Avatar_was_removed_still_gets_a_fresh_Avatar()
    {
        CreateSharedPose();
        var release = _game.HoldWrites();
        _sessions.SetPresent(true);
        await _game.WaitForHeldWriteAsync();

        _sessions.SetPresent(false);
        _sessions.SetPresent(true);
        release();

        await _game.WaitForLinesAsync(4);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Create, Subscribe, Remove, Create], _game.Lines);
    }

    // A new local game Session has no Avatars, like the game after a reconnect.
    [Fact]
    public async Task A_new_local_game_Session_recreates_the_Avatar_for_an_other_player_still_present_once_granted()
    {
        _sessions.SetPresent(true);
        var negotiation = new TaskCompletionSource<ProtocolNegotiation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedPose = new SharedPose(_sessions, _game.WriteLineAsync, _game.DisplaySystemAsync, _game.Log, negotiation.Task,
            TestContext.Current.CancellationToken);
        sharedPose.HandleReceivedPose(Remote);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(_game.Lines);

        negotiation.SetResult(Granted);

        await _game.WaitForLinesAsync(3);
        Assert.Equal([Create, Subscribe, RemoteLine], _game.Lines);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("exists")]
    public async Task An_avatarcreate_that_succeeded_or_found_the_Avatar_existing_is_logged_as_created(string outcome)
    {
        var sharedPose = CreateSharedPose();

        await sharedPose.HandleResponseAsync(new GameEvent.Responded("avatarcreate", outcome, ["partner"]));

        Assert.Equal(
            [(ConnectionLogSeverity.Information, LocalGameEventName.AvatarCreated, $"RESPONSE avatarcreate {outcome} partner")],
            _game.Logged);
        Assert.Empty(_game.Notices);
    }

    [Fact]
    public async Task A_missing_Avatar_model_is_explained_once_and_the_Shared_Pose_continues()
    {
        var sharedPose = CreateSharedPose();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(2);

        await sharedPose.HandleResponseAsync(new GameEvent.Responded("avatarcreate", "model-not-found", ["partner"]));
        await sharedPose.HandleResponseAsync(new GameEvent.Responded("avatarcreate", "model-not-found", ["partner"]));
        sharedPose.HandleReceivedPose(Remote);
        sharedPose.HandleLocalPose(Local);

        Assert.Equal([SharedPose.AvatarModelMissingNotice], _game.Notices);
        Assert.All(_game.Logged, entry => Assert.Equal(
            (ConnectionLogSeverity.Warning, LocalGameEventName.SharedPoseCommandFailed, "RESPONSE avatarcreate model-not-found partner"), entry));
        await _game.WaitForLinesAsync(3);
        Assert.Equal(RemoteLine, _game.Lines[2]);
        Assert.Single(_sessions.SentPoses);
    }

    [Theory]
    [InlineData("avatarcreate", "limit", new[] { "partner" })]
    [InlineData("avatarcreate", "invalid", new string[0])]
    [InlineData("avatarpose", "not-found", new[] { "partner" })]
    [InlineData("avatarpose", "invalid", new[] { "partner" })]
    [InlineData("avatarpose", "invalid", new string[0])]
    [InlineData("avatarremove", "not-found", new[] { "partner" })]
    [InlineData("localpose", "invalid", new string[0])]
    public async Task Other_Shared_Pose_failures_are_logged_and_never_shown_in_chat(string keyword, string outcome, string[] fields)
    {
        var sharedPose = CreateSharedPose();

        await sharedPose.HandleResponseAsync(new GameEvent.Responded(keyword, outcome, fields));

        var line = string.Join(' ', ["RESPONSE", keyword, outcome, .. fields]);
        Assert.Equal([(ConnectionLogSeverity.Warning, LocalGameEventName.SharedPoseCommandFailed, line)], _game.Logged);
        Assert.Empty(_game.Notices);
    }

    [Theory]
    [InlineData("avatarremove", "ok", new[] { "partner" })]
    [InlineData("localpose", "ok", new[] { "subscribe", "30" })]
    [InlineData("localpose", "ok", new[] { "unsubscribe" })]
    [InlineData("protocol", "ok", new[] { "2" })]
    public async Task Other_Responses_are_neither_logged_nor_shown(string keyword, string outcome, string[] fields)
    {
        var sharedPose = CreateSharedPose();

        await sharedPose.HandleResponseAsync(new GameEvent.Responded(keyword, outcome, fields));

        Assert.Empty(_game.Logged);
        Assert.Empty(_game.Notices);
    }

    private static async Task<TcpClient> AdmitRawPeerAsync(int port)
    {
        var peer = new TcpClient(AddressFamily.InterNetwork);
        await peer.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await LanProtocol.WriteAsync(peer.GetStream(),
            new LanMessage.JoinRequest(LanProtocol.CurrentVersion, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.IsType<LanMessage.AdmissionAccepted>(await LanProtocol.ReadAsync(peer.GetStream(), TestContext.Current.CancellationToken));
        return peer;
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

        private readonly List<string> _notices = [];
        private readonly List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> _log = [];

        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public List<string> Notices { get { lock (_notices) return [.. _notices]; } }
        public List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> Logged { get { lock (_log) return [.. _log]; } }

        public ValueTask DisplaySystemAsync(string message)
        {
            lock (_notices) _notices.Add(message);
            return ValueTask.CompletedTask;
        }

        public void Log(ConnectionLogSeverity severity, LocalGameEventName gameEvent, string line)
        {
            lock (_log) _log.Add((severity, gameEvent, line));
        }

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
        private int _arrivalCount;
        private int _currentArrival;
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged;
        public bool IsJoined => false;
        public int OtherPlayerArrival => Volatile.Read(ref _currentArrival);
        public List<LanMessage.Pose> SentPoses { get; } = [];

        // Becoming present again is a new arrival.
        public void SetPresent(bool present)
        {
            Volatile.Write(ref _currentArrival, present ? Interlocked.Increment(ref _arrivalCount) : 0);
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
