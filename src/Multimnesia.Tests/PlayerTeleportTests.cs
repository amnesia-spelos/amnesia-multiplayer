using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class PlayerTeleportTests
{
    private const string Map = "custom_stories/My Story: Part 2/maps/cellar one.map";
    private static readonly ProtocolNegotiation Granted = new("ok", 2, ["avatars", "localpose", "playerplace"]);
    private static readonly LocalPose Local = new(1000, 1, 0, 0, 0, 0, 0, false, false, Map);
    private static readonly LanMessage.Pose Other = new(123456, 3, 1.25, -2.5, 3.75, 90, -45, true, true, Map);
    private const string PlaceLine = "playerplace 1.2500 -2.5000 3.7500 90.0000 1 " + Map;

    private readonly FakeSessionOperations _sessions = new();
    private readonly List<string> _lines = [];
    // What the local game answers to each line written, in order.
    private string[] _answers = ["RESPONSE playerplace ok"];
    // The response deadline passes at once unless a test holds it back.
    private Func<TimeSpan, CancellationToken, Task> _delay = (_, _) => Task.CompletedTask;

    private PlayerTeleport CreateTeleport(ProtocolNegotiation? negotiation = null)
    {
        PlayerTeleport teleport = null!;
        teleport = new PlayerTeleport(
            _sessions,
            (line, _) =>
            {
                _lines.Add(line);
                foreach (var answer in _answers) teleport.HandleLocalGameEvent(GameInteractionProtocol.ParseEvent(answer));
                return ValueTask.CompletedTask;
            },
            Task.FromResult(negotiation ?? Granted),
            (timeout, token) => _delay(timeout, token));
        return teleport;
    }

    [Fact]
    public async Task Teleporting_with_the_other_player_on_the_same_map_places_the_local_player_at_their_latest_Pose()
    {
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleLocalGameEvent(new GameEvent.LocalPoseReported(Local));
        teleport.HandleReceivedPose(Other with { X = 100 });
        teleport.HandleReceivedPose(Other);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Equal([PlaceLine], _lines);
        Assert.Equal("Teleported to the other player.", feedback);
    }

    [Fact]
    public async Task Teleporting_after_the_other_player_left_writes_nothing()
    {
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleLocalGameEvent(new GameEvent.LocalPoseReported(Local));
        teleport.HandleReceivedPose(Other);
        _sessions.SetPresent(false);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_lines);
        Assert.Equal("The other player is not in the Multiplayer Session.", feedback);
    }

    [Fact]
    public async Task Teleporting_before_any_Pose_of_the_other_player_arrived_writes_nothing()
    {
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleLocalGameEvent(new GameEvent.LocalPoseReported(Local));

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_lines);
        Assert.Equal("The other player's position is not known yet.", feedback);
    }

    [Fact]
    public async Task A_replacement_player_is_not_teleported_to_by_the_Pose_of_the_player_who_left()
    {
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleLocalGameEvent(new GameEvent.LocalPoseReported(Local));
        teleport.HandleReceivedPose(Other);
        _sessions.SetPresent(false);
        _sessions.SetPresent(true);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_lines);
        Assert.Equal("The other player's position is not known yet.", feedback);
    }

    [Fact]
    public async Task Teleporting_to_the_other_player_on_another_map_writes_nothing()
    {
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleLocalGameEvent(new GameEvent.MapEntered("custom_stories/mystory/maps/map1.map"));
        teleport.HandleReceivedPose(Other);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_lines);
        Assert.Equal("The other player is on a different map.", feedback);
    }

    [Theory]
    [InlineData("ok", 2, new[] { "avatars", "localpose", "interactions", "sharedcallbacks" })]
    [InlineData("unknown-command", null, new string[0])]
    public async Task Teleporting_with_a_game_that_did_not_grant_playerplace_writes_nothing(
        string outcome, int? version, string[] capabilities)
    {
        var teleport = CreateTeleport(new(outcome, version, capabilities));
        _sessions.SetPresent(true);
        teleport.HandleLocalGameEvent(new GameEvent.LocalPoseReported(Local));
        teleport.HandleReceivedPose(Other);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_lines);
        Assert.Equal("Your game does not support /tp-to-peer; update it to teleport to the other player.", feedback);
    }

    [Theory]
    [InlineData("RESPONSE playerplace wrong-map", "The other player is on a different map.")]
    [InlineData("RESPONSE playerplace unavailable", "Your game refused the teleport: you are not in play.")]
    [InlineData("RESPONSE playerplace invalid", "Your game refused the teleport: the request was malformed.")]
    [InlineData("RESPONSE playerplace not-granted", "Your game does not support /tp-to-peer; update it to teleport to the other player.")]
    [InlineData("RESPONSE playerplace stuck", "Your game refused the teleport: stuck.")]
    public async Task A_refused_placement_shows_the_game_s_reason(string answer, string expected)
    {
        _answers = [answer];
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleReceivedPose(Other);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Equal([PlaceLine], _lines);
        Assert.Equal(expected, feedback);
    }

    [Fact]
    public async Task A_placement_the_game_never_answers_times_out()
    {
        _answers = [];
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleReceivedPose(Other);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Equal([PlaceLine], _lines);
        Assert.Equal("Your game did not answer the teleport.", feedback);
    }

    [Fact]
    public async Task Teleporting_again_while_the_game_has_not_answered_writes_nothing_more()
    {
        _answers = [];
        _delay = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleReceivedPose(Other);
        var first = teleport.TeleportAsync(TestContext.Current.CancellationToken);

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);
        teleport.HandleLocalGameEvent(GameInteractionProtocol.ParseEvent("RESPONSE playerplace ok"));

        Assert.Equal("A teleport is already in progress.", feedback);
        Assert.Equal("Teleported to the other player.", await first);
        Assert.Equal([PlaceLine], _lines);
    }

    [Fact]
    public async Task A_late_answer_to_a_timed_out_placement_does_not_answer_the_next_one()
    {
        _answers = [];
        var teleport = CreateTeleport();
        _sessions.SetPresent(true);
        teleport.HandleReceivedPose(Other);
        Assert.Equal("Your game did not answer the teleport.", await teleport.TeleportAsync(TestContext.Current.CancellationToken));
        _answers = ["RESPONSE playerplace ok", "RESPONSE playerplace unavailable"];

        var feedback = await teleport.TeleportAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Your game refused the teleport: you are not in play.", feedback);
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        private int _arrivalCount;
        private int _currentArrival;
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged;
        public bool IsJoined => false;
        public int OtherPlayerArrival => Volatile.Read(ref _currentArrival);

        // Becoming present again is a new arrival.
        public void SetPresent(bool present)
        {
            Volatile.Write(ref _currentArrival, present ? Interlocked.Increment(ref _arrivalCount) : 0);
            OtherPlayerPresenceChanged?.Invoke();
        }

        public void SendEnemyPose(LanMessage.EnemyPose pose) { }
        public Task SendEnemyAttackAsync(LanMessage.EnemyAttack attack, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SendPose(LanMessage.Pose pose) => throw new NotSupportedException();
        public void SendBodies(LanMessage.Bodies bodies) => throw new NotSupportedException();
        public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendWorldMessageAsync(LanMessage.WorldMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
