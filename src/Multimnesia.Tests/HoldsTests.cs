using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class HoldsTests
{
    private const string Map = "custom_stories/mp-test-cs/maps/holds room.map";
    private const string Subscribe = "reportedbodies subscribe 30";
    private const string Unsubscribe = "reportedbodies unsubscribe";
    private const string Drive = $"entitydrive 12 {Map}";
    private const string Release = $"entityrelease 12 {Map}";
    private static readonly ProtocolNegotiation Granted = new("ok", 2, ["avatars", "localpose", "interactions"]);
    private static readonly BodyState Carried = new(1.25, -2.5, 3.75, 0, 0, 0, 1, 0.5, 0, -1, 0, 90, 0);
    private const string CarriedText = "1.2500 -2.5000 3.7500 0.0000 0.0000 0.0000 1.0000 0.5000 0.0000 -1.0000 0.0000 90.0000 0.0000";
    private static readonly GameEvent Grab = new GameEvent.InteractionStarted(12, 3, Map);
    private static readonly GameEvent Throw = new GameEvent.InteractionEnded(12, 3, InteractionEnding.Thrown, Map);
    private static readonly GameEvent Rest = new GameEvent.ReportSettled(12, Map);
    private static readonly LanMessage.Claim ClaimOf12 = new(Map, 12, ClaimReason.Interact);

    private readonly FakeSessionOperations _sessions = new();
    private readonly RecordingGame _game = new();

    private Holds CreateHolds(ProtocolNegotiation? negotiation = null) => CreateHolds(_sessions, _game, negotiation);

    private static Holds CreateHolds(ISessionOperations sessions, RecordingGame game, ProtocolNegotiation? negotiation = null) =>
        new(sessions, game.WriteLineAsync, game.Log, Task.FromResult(negotiation ?? Granted), TestContext.Current.CancellationToken);

    private static GameEvent.ReportedBodies Report(ulong timeMs, params BodyEntry[] entries) => new(timeMs, entries, Map);

    private async Task<Holds> CreatePresentHoldsAsync()
    {
        var holds = CreateHolds();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(1);
        return holds;
    }

    [Fact]
    public async Task The_other_player_becoming_present_subscribes_to_the_reported_bodies_and_leaving_ends_it()
    {
        CreateHolds();

        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(1);
        _sessions.SetPresent(false);

        await _game.WaitForLinesAsync(2);
        Assert.Equal([Subscribe, Unsubscribe], _game.Lines);
    }

    [Theory]
    [InlineData("ok", 2, new[] { "avatars", "localpose" })]
    [InlineData("unknown-command", null, new string[0])]
    public async Task Nothing_is_written_or_sent_unless_the_local_game_granted_interactions(string outcome, int? version, string[] capabilities)
    {
        var holds = CreateHolds(new ProtocolNegotiation(outcome, version, capabilities));

        _sessions.SetPresent(true);
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_game.Lines);
        Assert.Empty(_sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
    }

    [Fact]
    public async Task A_local_interaction_start_Claims_the_entity_and_shares_the_interaction()
    {
        var holds = await CreatePresentHoldsAsync();

        await holds.HandleLocalGameEventAsync(Grab);

        Assert.Equal([ClaimOf12, new LanMessage.Interaction(Map, 12, 3, true)], _sessions.SentHoldMessages);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldClaimed && entry.Line.Contains("12"));
    }

    [Fact]
    public async Task Nothing_is_Claimed_while_the_other_player_is_not_present()
    {
        var holds = CreateHolds();

        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));

        Assert.Empty(_sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
    }

    [Fact]
    public async Task Only_the_bodies_of_entities_the_local_player_Holds_are_streamed()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Report(999, new BodyEntry(12, 3, Carried)));

        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried), new BodyEntry(40, 0, Carried)));
        await holds.HandleLocalGameEventAsync(new GameEvent.ReportedBodies(1033, [new(12, 3, Carried)], "maps/other.map"));

        Assert.Equal([new LanMessage.Bodies(1000, Map, [new(12, 3, Carried)])], _sessions.SentBodies);
    }

    [Fact]
    public async Task The_Hold_outlasts_the_interaction_while_the_entity_is_Settling_and_ends_when_it_settles()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        await holds.HandleLocalGameEventAsync(Rest);
        await holds.HandleLocalGameEventAsync(Report(1033, new BodyEntry(12, 3, Carried)));

        Assert.Equal(
            [ClaimOf12, new LanMessage.Interaction(Map, 12, 3, true), new LanMessage.Interaction(Map, 12, 3, false), new LanMessage.Settled(Map, 12)],
            _sessions.SentHoldMessages);
        Assert.Equal([new LanMessage.Bodies(1000, Map, [new(12, 3, Carried)])], _sessions.SentBodies);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldEnded && entry.Line.Contains("settled"));
    }

    [Fact]
    public async Task Grabbing_a_Settling_entity_again_continues_the_Hold_and_a_settled_one_is_Claimed_anew()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Throw);

        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(Rest);
        await holds.HandleLocalGameEventAsync(Grab);

        LanMessage.HoldMessage start = new LanMessage.Interaction(Map, 12, 3, true), end = new LanMessage.Interaction(Map, 12, 3, false);
        Assert.Equal([ClaimOf12, start, end, start, end, new LanMessage.Settled(Map, 12), ClaimOf12, start], _sessions.SentHoldMessages);
    }

    [Fact]
    public async Task Events_about_entities_the_local_player_does_not_Hold_are_not_shared()
    {
        var holds = await CreatePresentHoldsAsync();

        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(Rest);

        Assert.Empty(_sessions.SentHoldMessages);
    }

    [Fact]
    public async Task The_other_players_Claim_makes_the_entity_a_Peer_Driven_Entity_and_their_interaction_is_mirrored()
    {
        var holds = await CreatePresentHoldsAsync();

        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, false));

        await _game.WaitForLinesAsync(4);
        Assert.Equal([Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entityinteracting 12 0 {Map}"], _game.Lines);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldClaimed && entry.Line.Contains("other player"));
    }

    [Fact]
    public async Task Received_bodies_drive_only_the_entities_the_other_player_Holds()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedBodies(new(999, Map, [new(12, 3, Carried)]));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(2);
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried), new(40, 0, Carried)]));

        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Subscribe, Drive, $"entitybodies 1000 1 12 3 {CarriedText} {Map}"], _game.Lines);
    }

    [Fact]
    public async Task Received_bodies_are_latest_wins_while_a_write_to_the_local_game_is_pending()
    {
        var holds = await CreatePresentHoldsAsync();
        var release = _game.HoldWrites();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForHeldWriteAsync();

        for (ulong time = 1; time <= 100; time++) holds.HandleReceivedBodies(new(time, Map, [new(12, 3, Carried)]));
        release();

        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Subscribe, Drive, $"entitybodies 100 1 12 3 {CarriedText} {Map}"], _game.Lines);
    }

    [Fact]
    public async Task The_other_players_entity_settling_returns_it_to_local_physics_and_ends_their_Hold()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);

        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive, Release], _game.Lines);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldEnded && entry.Line.Contains("settled"));
    }

    [Fact]
    public async Task Messages_about_entities_the_other_player_does_not_Hold_are_not_applied()
    {
        var holds = await CreatePresentHoldsAsync();

        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));
        holds.HandleReceivedHoldMessage(new LanMessage.Claim("maps/other.map", 12, ClaimReason.Interact));
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        await _game.WaitForLinesAsync(2);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, "entitydrive 12 maps/other.map"], _game.Lines);
    }

    [Fact]
    public async Task Two_Game_Peers_see_each_others_grab_and_throw_until_it_settles()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        var joiningGame = new RecordingGame();
        Holds hostHolds = null!;
        Holds joiningHolds = null!;
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { hostHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { hostHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { joiningHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { joiningHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        hostHolds = CreateHolds(host, hostGame);
        joiningHolds = CreateHolds(joining, joiningGame);
        await new GamePeerOrchestrator(host).HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        await new GamePeerOrchestrator(joining).HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await hostGame.WaitForLinesAsync(1);
        await joiningGame.WaitForLinesAsync(1);

        // The Joining Player grabs and throws prop 12; the Multiplayer Relay's arbiter accepts the Claim.
        await joiningHolds.HandleLocalGameEventAsync(Grab);
        await joiningHolds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        await hostGame.WaitForLinesAsync(4);
        await joiningHolds.HandleLocalGameEventAsync(Throw);
        await joiningHolds.HandleLocalGameEventAsync(Rest);
        await hostGame.WaitForLinesAsync(6);

        Assert.Equal(
            [Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entitybodies 1000 1 12 3 {CarriedText} {Map}",
                $"entityinteracting 12 0 {Map}", Release],
            hostGame.Lines);

        // Once it settled, the Session Host can take it, through the same arbiter.
        await hostHolds.HandleLocalGameEventAsync(Grab);
        await joiningGame.WaitForLinesAsync(3);
        Assert.Equal([Subscribe, Drive, $"entityinteracting 12 1 {Map}"], joiningGame.Lines);
    }

    [Theory]
    [InlineData("entitydrive", "not-found", new[] { "12" })]
    [InlineData("entitydrive", "wrong-map", new[] { "12" })]
    [InlineData("entitybodies", "not-found", new[] { "12" })]
    [InlineData("entityinteracting", "invalid", new string[0])]
    [InlineData("entityrelease", "not-granted", new string[0])]
    [InlineData("reportedbodies", "invalid", new string[0])]
    public void Failed_Peer_Driven_Entity_Commands_are_logged(string keyword, string outcome, string[] fields)
    {
        var holds = CreateHolds();

        holds.HandleResponse(new GameEvent.Responded(keyword, outcome, fields));

        var logged = Assert.Single(_game.Logged);
        Assert.Equal(ConnectionLogSeverity.Warning, logged.Severity);
        Assert.Equal(LocalGameEventName.HoldCommandFailed, logged.Event);
        Assert.Equal(string.Join(' ', ["RESPONSE", keyword, outcome, .. fields]), logged.Line);
    }

    [Theory]
    [InlineData("entitydrive", "ok", new[] { "12" })]
    [InlineData("reportedbodies", "ok", new[] { "subscribe", "30" })]
    [InlineData("avatarpose", "not-found", new[] { "partner" })]
    public void Other_Responses_are_not_logged(string keyword, string outcome, string[] fields)
    {
        var holds = CreateHolds();

        holds.HandleResponse(new GameEvent.Responded(keyword, outcome, fields));

        Assert.Empty(_game.Logged);
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
        private readonly List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> _log = [];
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _hold;

        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> Logged { get { lock (_log) return [.. _log]; } }

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
        private readonly List<LanMessage.HoldMessage> _sentHoldMessages = [];
        private readonly List<LanMessage.Bodies> _sentBodies = [];
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged;
        public bool IsJoined => false;
        public int OtherPlayerArrival => Volatile.Read(ref _currentArrival);
        public List<LanMessage.HoldMessage> SentHoldMessages { get { lock (_sentHoldMessages) return [.. _sentHoldMessages]; } }
        public List<LanMessage.Bodies> SentBodies { get { lock (_sentBodies) return [.. _sentBodies]; } }

        // Becoming present again is a new arrival.
        public void SetPresent(bool present)
        {
            Volatile.Write(ref _currentArrival, present ? Interlocked.Increment(ref _arrivalCount) : 0);
            OtherPlayerPresenceChanged?.Invoke();
        }

        public void SendBodies(LanMessage.Bodies bodies) { lock (_sentBodies) _sentBodies.Add(bodies); }

        public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken)
        {
            lock (_sentHoldMessages) _sentHoldMessages.Add(message);
            return Task.CompletedTask;
        }

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
