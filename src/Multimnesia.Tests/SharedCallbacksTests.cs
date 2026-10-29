using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class SharedCallbacksTests
{
    private const string Cellar = "custom_stories/mp-test-cs/maps/cellar one.map";
    private const string Study = "custom_stories/mp-test-cs/maps/study.map";
    private static readonly ProtocolNegotiation Granted = new("ok", 2, ["avatars", "localpose", "interactions", "sharedcallbacks"]);
    private static readonly LanMessage.PlayerCollide EnteredArea = new(Cellar, 30, Entered: true);
    private static readonly LanMessage.UseItem UsedKey = new(Cellar, 12, "key_study");
    private static readonly LanMessage.ButtonState PressedButton = new(Study, 5, SwitchedOn: true);
    private static readonly LanMessage.LampState LitCandle = new(Study, 6, Lit: true);
    private static readonly LanMessage.Interact OpenedDrawer = new(Study, 7);

    private readonly FakeSessionOperations _sessions = new();
    private readonly RecordingGame _game = new();

    private SharedCallbacks CreateSharedCallbacks(ProtocolNegotiation? negotiation = null) =>
        new(_sessions, _game.WriteLineAsync, _game.Log, Task.FromResult(negotiation ?? Granted), TestContext.Current.CancellationToken);

    private static GameEvent Entered(string map) => new GameEvent.MapEntered(map);

    [Fact]
    public async Task Each_change_the_local_game_reports_is_sent_to_the_other_players_once()
    {
        var sharedCallbacks = CreateSharedCallbacks();

        foreach (var change in new LanMessage.WorldMessage[] { EnteredArea, UsedKey, PressedButton, LitCandle })
            await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.WorldChanged(change));

        Assert.Equal([EnteredArea, UsedKey, PressedButton, LitCandle], _sessions.SentWorldMessages);
        Assert.Empty(_game.Lines);
    }

    // Only the Session Host's game simulates Shared Enemies; a Joining Player's own enemy disabling itself stays local.
    [Fact]
    public async Task Only_the_Session_Host_sends_an_enemy_disabling_itself()
    {
        var disabled = new LanMessage.AutoDisabled(Cellar, 139);
        _sessions.IsJoined = true;
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.WorldChanged(disabled));
        Assert.Empty(_sessions.SentWorldMessages);

        _sessions.IsJoined = false;
        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.WorldChanged(disabled));
        Assert.Equal([disabled], _sessions.SentWorldMessages);
    }

    [Fact]
    public async Task A_change_received_for_the_local_games_map_is_applied_with_one_Command()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));

        sharedCallbacks.HandleReceivedWorldMessage(UsedKey);

        await _game.WaitForLinesAsync(1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([$"sharedcallback useitem 12 key_study {Cellar}"], _game.Lines);
    }

    [Fact]
    public async Task Changes_for_another_map_wait_until_the_local_game_enters_it_and_then_apply_in_arrival_order()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));

        sharedCallbacks.HandleReceivedWorldMessage(PressedButton);
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);
        sharedCallbacks.HandleReceivedWorldMessage(LitCandle);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(_game.Lines);

        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));
        sharedCallbacks.HandleReceivedWorldMessage(new LanMessage.Interact(Study, 8));

        await _game.WaitForLinesAsync(4);
        Assert.Equal(
            [$"buttonstate 5 1 {Study}", $"sharedcallback interact 7 {Study}", $"lampstate 6 1 {Study}", $"sharedcallback interact 8 {Study}"],
            _game.Lines);
    }

    [Fact]
    public async Task Pickups_combines_and_flashbacks_are_sent_applied_and_kept_like_the_other_kinds()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        var pickedUpKey = new LanMessage.Pickup(Cellar, 42);
        var combinedDrill = new LanMessage.Combine(Cellar, "hollow_needle_1", "stone_hammer_1");
        var flashback = new LanMessage.Flashback(Study, 17);
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));

        foreach (var change in new LanMessage.WorldMessage[] { pickedUpKey, combinedDrill })
            await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.WorldChanged(change));
        sharedCallbacks.HandleReceivedWorldMessage(flashback);
        sharedCallbacks.HandleReceivedWorldMessage(pickedUpKey);
        sharedCallbacks.HandleReceivedWorldMessage(combinedDrill);
        await _game.WaitForLinesAsync(2);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([pickedUpKey, combinedDrill], _sessions.SentWorldMessages);
        Assert.Equal([$"sharedcallback pickup 42 {Cellar}", $"sharedcallback combine hollow_needle_1 stone_hammer_1 {Cellar}"], _game.Lines);

        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await _game.WaitForLinesAsync(3);
        Assert.Equal($"sharedcallback flashback 17 {Study}", _game.Lines[2]);
    }

    [Fact]
    public async Task Sticky_attaches_are_sent_applied_and_kept_like_the_other_kinds()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        var stuckPipe = new LanMessage.StickyAttach(Cellar, 21, 30, 7);
        var stuckJar = new LanMessage.StickyAttach(Study, 22, 31, 0);
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));

        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.WorldChanged(stuckPipe));
        sharedCallbacks.HandleReceivedWorldMessage(stuckJar);
        sharedCallbacks.HandleReceivedWorldMessage(stuckPipe);
        await _game.WaitForLinesAsync(1);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([stuckPipe], _sessions.SentWorldMessages);
        Assert.Equal([$"sharedcallback stickyattach 21 30 7 {Cellar}"], _game.Lines);

        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await _game.WaitForLinesAsync(2);
        Assert.Equal($"sharedcallback stickyattach 22 31 0 {Study}", _game.Lines[1]);
    }

    [Fact]
    public async Task Each_answer_to_a_sticky_attach_Command_except_wrong_map_reports_whether_it_attached()
    {
        List<(LanMessage.StickyAttach, bool)> answers = [];
        var sharedCallbacks = new SharedCallbacks(
            _sessions, _game.WriteLineAsync, _game.Log, Task.FromResult(Granted), TestContext.Current.CancellationToken,
            (attach, attached) => answers.Add((attach, attached)));
        var stuckPipe = new LanMessage.StickyAttach(Cellar, 21, 30, 7);
        var stuckJar = new LanMessage.StickyAttach(Cellar, 22, 31, 0);
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));
        foreach (var change in new LanMessage.WorldMessage[] { stuckPipe, stuckJar, PressedButton with { Map = Cellar }, stuckPipe })
            sharedCallbacks.HandleReceivedWorldMessage(change);
        await _game.WaitForLinesAsync(4);

        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "ok", []));
        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "not-found", []));
        sharedCallbacks.HandleResponse(new GameEvent.Responded("buttonstate", "not-found", []));
        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "wrong-map", []));

        Assert.Equal([(stuckPipe, true), (stuckJar, false)], answers);
    }

    [Fact]
    public async Task Changes_kept_for_a_map_are_applied_only_once()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);

        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await _game.WaitForLinesAsync(1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([$"sharedcallback interact 7 {Study}"], _game.Lines);
    }

    [Fact]
    public async Task The_local_Pose_also_tells_the_map_the_local_game_is_on()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);

        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.LocalPoseReported(new(1000, 0, 0, 0, 0, 0, 0, false, false, 0, Study)));

        await _game.WaitForLinesAsync(1);
        Assert.Equal([$"sharedcallback interact 7 {Study}"], _game.Lines);
    }

    [Fact]
    public async Task A_Shared_Custom_Story_Start_received_by_the_Joining_Player_discards_every_kept_change()
    {
        _sessions.IsJoined = true;
        var sharedCallbacks = CreateSharedCallbacks();
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);
        sharedCallbacks.HandleReceivedWorldMessage(EnteredArea);

        sharedCallbacks.HandleReceivedCustomStoryStart();
        sharedCallbacks.HandleReceivedWorldMessage(LitCandle);
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await _game.WaitForLinesAsync(1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([$"lampstate 6 1 {Study}"], _game.Lines);
    }

    [Fact]
    public async Task The_Session_Hosts_Custom_Story_start_discards_every_kept_change()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);

        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("mp-test-cs"));
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(_game.Lines);
    }

    [Fact]
    public async Task A_Joining_Players_own_Custom_Story_start_keeps_the_changes()
    {
        _sessions.IsJoined = true;
        var sharedCallbacks = CreateSharedCallbacks();
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);

        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("mp-test-cs"));
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await _game.WaitForLinesAsync(1);
        Assert.Equal([$"sharedcallback interact 7 {Study}"], _game.Lines);
    }

    [Fact]
    public async Task A_change_from_before_a_Shared_Custom_Story_Start_is_not_kept_when_the_new_story_turns_it_away()
    {
        _sessions.IsJoined = true;
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);
        await _game.WaitForLinesAsync(1);

        sharedCallbacks.HandleReceivedCustomStoryStart();
        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "wrong-map", []));
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_game.Lines);
    }

    [Fact]
    public async Task Nothing_found_in_the_local_game_is_logged_as_one_line()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));
        sharedCallbacks.HandleReceivedWorldMessage(UsedKey);
        await _game.WaitForLinesAsync(1);

        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "not-found", []));

        var logged = Assert.Single(_game.Logged);
        Assert.Equal(ConnectionLogSeverity.Information, logged.Severity);
        Assert.Equal(LocalGameEventName.SharedCallbackNotFound, logged.Event);
        Assert.Contains("12", logged.Line);
    }

    [Theory]
    [InlineData("sharedcallback", "ok", false)]
    [InlineData("sharedcallback", "invalid", true)]
    [InlineData("buttonstate", "wrong-type", true)]
    [InlineData("lampstate", "not-granted", true)]
    public async Task Other_failures_are_logged_as_warnings(string keyword, string outcome, bool logged)
    {
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));
        sharedCallbacks.HandleReceivedWorldMessage(EnteredArea);
        await _game.WaitForLinesAsync(1);

        sharedCallbacks.HandleResponse(new GameEvent.Responded(keyword, outcome, []));
        sharedCallbacks.HandleResponse(new GameEvent.Responded("entitydrive", "not-found", ["12"]));

        Assert.Equal(logged ? [(ConnectionLogSeverity.Warning, LocalGameEventName.SharedCallbackFailed)] : [],
            _game.Logged.Select(entry => (entry.Severity, entry.Event)));
    }

    [Fact]
    public async Task A_change_that_reached_the_local_game_after_it_left_the_map_waits_for_its_next_visit()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);
        await _game.WaitForLinesAsync(1);
        // A later change for the same map arrives while the turned-away one is still unanswered.
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));
        sharedCallbacks.HandleReceivedWorldMessage(LitCandle);

        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "wrong-map", []));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_game.Lines);
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));

        await _game.WaitForLinesAsync(3);
        Assert.Equal([$"sharedcallback interact 7 {Study}", $"sharedcallback interact 7 {Study}", $"lampstate 6 1 {Study}"], _game.Lines);
    }

    [Fact]
    public async Task A_change_turned_away_before_the_local_game_returned_to_its_map_is_applied_at_once()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);
        await _game.WaitForLinesAsync(1);

        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));
        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "wrong-map", []));

        await _game.WaitForLinesAsync(2);
        Assert.Equal([$"sharedcallback interact 7 {Study}", $"sharedcallback interact 7 {Study}"], _game.Lines);
    }

    [Fact]
    public async Task A_change_turned_away_on_the_map_the_Game_Peer_believes_current_is_not_retried_in_a_loop()
    {
        var sharedCallbacks = CreateSharedCallbacks();
        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Study));
        sharedCallbacks.HandleReceivedWorldMessage(OpenedDrawer);
        await _game.WaitForLinesAsync(1);

        sharedCallbacks.HandleResponse(new GameEvent.Responded("sharedcallback", "wrong-map", []));
        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.LocalPoseReported(new(1000, 0, 0, 0, 0, 0, 0, false, false, 0, Study)));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_game.Lines);
    }

    [Theory]
    [InlineData("ok", 2, new[] { "avatars", "localpose", "interactions" })]
    [InlineData("unknown-command", null, new string[0])]
    public async Task Nothing_is_sent_or_written_unless_the_local_game_granted_Shared_Callbacks(
        string outcome, int? version, string[] capabilities)
    {
        var sharedCallbacks = CreateSharedCallbacks(new ProtocolNegotiation(outcome, version, capabilities));

        await sharedCallbacks.HandleLocalGameEventAsync(Entered(Cellar));
        await sharedCallbacks.HandleLocalGameEventAsync(new GameEvent.WorldChanged(EnteredArea));
        sharedCallbacks.HandleReceivedWorldMessage(UsedKey);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_sessions.SentWorldMessages);
        Assert.Empty(_game.Lines);
    }

    private sealed class RecordingGame
    {
        private readonly List<string> _lines = [];
        private readonly List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> _log = [];

        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> Logged { get { lock (_log) return [.. _log]; } }

        public void Log(ConnectionLogSeverity severity, LocalGameEventName gameEvent, string line)
        {
            lock (_log) _log.Add((severity, gameEvent, line));
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (_lines) _lines.Add(line);
            return ValueTask.CompletedTask;
        }

        public async Task WaitForLinesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Lines.Count < count) await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        private readonly List<LanMessage.WorldMessage> _sentWorldMessages = [];
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged { add { } remove { } }
        public bool IsJoined { get; set; }
        public int OtherPlayerArrival => 1;
        public List<LanMessage.WorldMessage> SentWorldMessages { get { lock (_sentWorldMessages) return [.. _sentWorldMessages]; } }

        public Task SendWorldMessageAsync(LanMessage.WorldMessage message, CancellationToken cancellationToken)
        {
            lock (_sentWorldMessages) _sentWorldMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SendBodies(LanMessage.Bodies bodies) { }
        public void SendEnemyPose(LanMessage.EnemyPose pose) { }
        public Task SendEnemyEventAsync(LanMessage.EnemyEvent message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendDeathMessageAsync(LanMessage.DeathMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SendPose(LanMessage.Pose pose) { }
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
