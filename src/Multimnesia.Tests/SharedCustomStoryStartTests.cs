using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class SharedCustomStoryStartTests
{
    private readonly FakeSessionOperations _sessions = new();
    private readonly List<string> _displayed = [];
    private readonly List<string> _startedLocally = [];
    private StartCustomStoryExchangeOutcome _localOutcome = StartCustomStoryExchangeOutcome.Starting;
    private TaskCompletionSource<StartCustomStoryExchangeOutcome>? _localStart;
    private const string OwnStartWarning = "Only the Session Host's Custom Story starts are shared.";

    private SharedCustomStoryStart Create() => new(
        _sessions,
        (identifier, _) => { _startedLocally.Add(identifier); return _localStart?.Task ?? Task.FromResult(_localOutcome); },
        message => { _displayed.Add(message); return ValueTask.CompletedTask; });

    [Fact]
    public async Task Session_Host_relays_a_local_start_to_the_admitted_Joining_Player_with_SYSTEM_feedback()
    {
        _sessions.JoiningPlayerAdmitted = true;

        await Create().HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("mp-test-cs"), TestContext.Current.CancellationToken);

        Assert.Equal(["mp-test-cs"], _sessions.SentStarts);
        Assert.Equal(["Starting Custom Story mp-test-cs for the other player."], _displayed);
        Assert.Empty(_startedLocally);
    }

    [Fact]
    public async Task A_local_start_without_an_admitted_Joining_Player_is_silent()
    {
        _sessions.JoiningPlayerAdmitted = false;

        await Create().HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("mp-test-cs"), TestContext.Current.CancellationToken);

        Assert.Empty(_displayed);
    }

    [Fact]
    public async Task Joining_Player_starts_the_host_Custom_Story_and_reports_started()
    {
        await Create().HandleHostStartAsync("mp-test-cs", TestContext.Current.CancellationToken);

        Assert.Equal(["mp-test-cs"], _startedLocally);
        Assert.Equal([("mp-test-cs", SharedCustomStoryStartOutcome.Started)], _sessions.SentOutcomes);
        Assert.Equal(["The host started Custom Story mp-test-cs."], _displayed);
    }

    [Theory]
    [InlineData(StartCustomStoryExchangeOutcome.NotFound, SharedCustomStoryStartOutcome.NotFound,
        "Custom Story mp-test-cs could not start for the Joining Player: it is not installed.")]
    [InlineData(StartCustomStoryExchangeOutcome.Invalid, SharedCustomStoryStartOutcome.Invalid,
        "Custom Story mp-test-cs could not start for the Joining Player: the installed Custom Story is invalid.")]
    [InlineData(StartCustomStoryExchangeOutcome.NotInMainMenu, SharedCustomStoryStartOutcome.NotInMainMenu,
        "Custom Story mp-test-cs could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(StartCustomStoryExchangeOutcome.AlreadyPending, SharedCustomStoryStartOutcome.NotInMainMenu,
        "Custom Story mp-test-cs could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(StartCustomStoryExchangeOutcome.TimedOut, SharedCustomStoryStartOutcome.Unavailable,
        "Custom Story mp-test-cs could not start for the Joining Player: their game does not support Custom Story starts or did not respond.")]
    [InlineData(StartCustomStoryExchangeOutcome.Unrecognized, SharedCustomStoryStartOutcome.Unavailable,
        "Custom Story mp-test-cs could not start for the Joining Player: their game does not support Custom Story starts or did not respond.")]
    public async Task Joining_Player_reports_a_failed_start_to_the_Session_Host_and_sees_why(
        StartCustomStoryExchangeOutcome local, SharedCustomStoryStartOutcome reported, string feedback)
    {
        _localOutcome = local;

        await Create().HandleHostStartAsync("mp-test-cs", TestContext.Current.CancellationToken);

        Assert.Equal([("mp-test-cs", reported)], _sessions.SentOutcomes);
        Assert.Equal([feedback], _displayed);
    }

    [Theory]
    [InlineData(SharedCustomStoryStartOutcome.NotFound,
        "Custom Story mp-test-cs could not start for the Joining Player: it is not installed.")]
    [InlineData(SharedCustomStoryStartOutcome.Invalid,
        "Custom Story mp-test-cs could not start for the Joining Player: the installed Custom Story is invalid.")]
    [InlineData(SharedCustomStoryStartOutcome.NotInMainMenu,
        "Custom Story mp-test-cs could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(SharedCustomStoryStartOutcome.Unavailable,
        "Custom Story mp-test-cs could not start for the Joining Player: their game does not support Custom Story starts or did not respond.")]
    public async Task Session_Host_sees_why_a_start_failed_for_the_Joining_Player(
        SharedCustomStoryStartOutcome outcome, string feedback)
    {
        await Create().HandleOutcomeAsync("mp-test-cs", outcome);

        Assert.Equal([feedback], _displayed);
    }

    [Fact]
    public async Task Session_Host_sees_nothing_further_when_the_start_succeeded()
    {
        await Create().HandleOutcomeAsync("mp-test-cs", SharedCustomStoryStartOutcome.Started);

        Assert.Empty(_displayed);
    }

    [Fact]
    public async Task Joining_Player_reproducing_the_host_start_sees_no_warning()
    {
        _sessions.IsJoined = true;
        var sharedStart = Create();

        // The game's reply and start event are read before the exchange with the local game resumes.
        await ReproduceHostStartAsync(sharedStart, "mp-test-cs", reportedStart: "mp-test-cs");

        Assert.Equal(["The host started Custom Story mp-test-cs."], _displayed);
        Assert.Empty(_sessions.SentStarts);
    }

    [Fact]
    public async Task Joining_Player_own_start_without_a_pending_Shared_Custom_Story_Start_is_kept_local_with_a_warning()
    {
        _sessions.IsJoined = true;

        await Create().HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("my-own-cs"), TestContext.Current.CancellationToken);

        Assert.Equal([OwnStartWarning], _displayed);
        Assert.Empty(_sessions.SentStarts);
    }

    [Fact]
    public async Task Joining_Player_start_with_a_mismatched_identifier_is_their_own()
    {
        _sessions.IsJoined = true;

        await ReproduceHostStartAsync(Create(), "mp-test-cs", reportedStart: "my-own-cs");

        Assert.Equal([OwnStartWarning, "The host started Custom Story mp-test-cs."], _displayed);
        Assert.Empty(_sessions.SentStarts);
    }

    [Fact]
    public async Task Only_the_next_start_after_a_Shared_Custom_Story_Start_is_the_reproduced_one()
    {
        _sessions.IsJoined = true;
        var sharedStart = Create();
        await ReproduceHostStartAsync(sharedStart, "mp-test-cs", reportedStart: "mp-test-cs");
        _displayed.Clear();

        await sharedStart.HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("mp-test-cs"), TestContext.Current.CancellationToken);

        Assert.Equal([OwnStartWarning], _displayed);
    }

    [Fact]
    public async Task A_failed_Shared_Custom_Story_Start_leaves_no_reproduced_start_pending()
    {
        _sessions.IsJoined = true;
        var sharedStart = Create();
        _localStart = new();
        var hostStart = sharedStart.HandleHostStartAsync("mp-test-cs", TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(
            new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.NotInMainMenu), TestContext.Current.CancellationToken);
        _localStart.SetResult(StartCustomStoryExchangeOutcome.NotInMainMenu);
        await hostStart;
        _displayed.Clear();

        await sharedStart.HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("mp-test-cs"), TestContext.Current.CancellationToken);

        Assert.Equal([OwnStartWarning], _displayed);
    }

    [Fact]
    public async Task A_starting_reply_to_a_Command_the_Game_Peer_did_not_issue_for_the_host_is_not_a_reproduction()
    {
        _sessions.IsJoined = true;
        var sharedStart = Create();

        await sharedStart.HandleLocalGameEventAsync(
            new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting), TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted("mp-test-cs"), TestContext.Current.CancellationToken);

        Assert.Equal([OwnStartWarning], _displayed);
    }

    private async Task ReproduceHostStartAsync(SharedCustomStoryStart sharedStart, string hostStart, string reportedStart)
    {
        _localStart = new();
        var exchange = sharedStart.HandleHostStartAsync(hostStart, TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(
            new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting), TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted(reportedStart), TestContext.Current.CancellationToken);
        _localStart.SetResult(StartCustomStoryExchangeOutcome.Starting);
        await exchange;
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged { add { } remove { } }
        public int OtherPlayerArrival => 0;
        public void SendPose(LanMessage.Pose pose) { }
        public bool JoiningPlayerAdmitted { get; set; }
        public bool IsJoined { get; set; }
        public List<string> SentStarts { get; } = [];
        public List<(string, SharedCustomStoryStartOutcome)> SentOutcomes { get; } = [];
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken)
        {
            SentStarts.Add(identifier);
            return Task.FromResult(JoiningPlayerAdmitted);
        }

        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken)
        {
            SentOutcomes.Add((identifier, outcome));
            return Task.CompletedTask;
        }
    }
}
