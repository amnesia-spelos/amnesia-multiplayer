using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class SharedStoryStartTests
{
    private readonly FakeSessionOperations _sessions = new();
    private readonly List<string> _displayed = [];
    private readonly List<Story> _startedLocally = [];
    private static readonly Story MpTestCs = new Story.Custom("mp-test-cs");
    private static readonly Story MainStory = new Story.Main();
    private StartStoryExchangeOutcome _localOutcome = StartStoryExchangeOutcome.Starting;
    private TaskCompletionSource<StartStoryExchangeOutcome>? _localStart;
    private const string OwnStartWarning = "Only the Session Host's Custom Story starts are shared.";

    private SharedStoryStart Create() => new(
        _sessions,
        (story, _) => { _startedLocally.Add(story); return _localStart?.Task ?? Task.FromResult(_localOutcome); },
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
        await Create().HandleHostStartAsync(MpTestCs, TestContext.Current.CancellationToken);

        Assert.Equal([MpTestCs], _startedLocally);
        Assert.Equal([("mp-test-cs", SharedStoryStartOutcome.Started)], _sessions.SentOutcomes);
        Assert.Equal(["The host started Custom Story mp-test-cs."], _displayed);
    }

    [Theory]
    [InlineData(StartStoryExchangeOutcome.NotFound, SharedStoryStartOutcome.NotFound,
        "Custom Story mp-test-cs could not start for the Joining Player: it is not installed.")]
    [InlineData(StartStoryExchangeOutcome.Invalid, SharedStoryStartOutcome.Invalid,
        "Custom Story mp-test-cs could not start for the Joining Player: the installed Custom Story is invalid.")]
    [InlineData(StartStoryExchangeOutcome.NotInMainMenu, SharedStoryStartOutcome.NotInMainMenu,
        "Custom Story mp-test-cs could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(StartStoryExchangeOutcome.AlreadyPending, SharedStoryStartOutcome.NotInMainMenu,
        "Custom Story mp-test-cs could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(StartStoryExchangeOutcome.TimedOut, SharedStoryStartOutcome.Unavailable,
        "Custom Story mp-test-cs could not start for the Joining Player: their game does not support Custom Story starts or did not respond.")]
    [InlineData(StartStoryExchangeOutcome.Unrecognized, SharedStoryStartOutcome.Unavailable,
        "Custom Story mp-test-cs could not start for the Joining Player: their game does not support Custom Story starts or did not respond.")]
    public async Task Joining_Player_reports_a_failed_start_to_the_Session_Host_and_sees_why(
        StartStoryExchangeOutcome local, SharedStoryStartOutcome reported, string feedback)
    {
        _localOutcome = local;

        await Create().HandleHostStartAsync(MpTestCs, TestContext.Current.CancellationToken);

        Assert.Equal([("mp-test-cs", reported)], _sessions.SentOutcomes);
        Assert.Equal([feedback], _displayed);
    }

    [Theory]
    [InlineData(SharedStoryStartOutcome.NotFound,
        "Custom Story mp-test-cs could not start for the Joining Player: it is not installed.")]
    [InlineData(SharedStoryStartOutcome.Invalid,
        "Custom Story mp-test-cs could not start for the Joining Player: the installed Custom Story is invalid.")]
    [InlineData(SharedStoryStartOutcome.NotInMainMenu,
        "Custom Story mp-test-cs could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(SharedStoryStartOutcome.Unavailable,
        "Custom Story mp-test-cs could not start for the Joining Player: their game does not support Custom Story starts or did not respond.")]
    public async Task Session_Host_sees_why_a_start_failed_for_the_Joining_Player(
        SharedStoryStartOutcome outcome, string feedback)
    {
        await Create().HandleOutcomeAsync(MpTestCs, outcome);

        Assert.Equal([feedback], _displayed);
    }

    [Fact]
    public async Task Session_Host_sees_nothing_further_when_the_start_succeeded()
    {
        await Create().HandleOutcomeAsync(MpTestCs, SharedStoryStartOutcome.Started);

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
        var hostStart = sharedStart.HandleHostStartAsync(MpTestCs, TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(
            new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.NotInMainMenu), TestContext.Current.CancellationToken);
        _localStart.SetResult(StartStoryExchangeOutcome.NotInMainMenu);
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

    private const string OwnMainStoryStartWarning = "Only the Session Host's Main Story starts are shared.";

    [Fact]
    public async Task Session_Host_relays_a_local_Main_Story_start_at_once_with_SYSTEM_feedback()
    {
        _sessions.JoiningPlayerAdmitted = true;

        await Create().HandleLocalGameEventAsync(new GameEvent.MainStoryStarted(), TestContext.Current.CancellationToken);

        Assert.Equal(1, _sessions.SentMainStoryStarts);
        Assert.Empty(_sessions.SentStarts);
        Assert.Equal(["Starting the Main Story for the other player."], _displayed);
        Assert.Empty(_startedLocally);
    }

    [Fact]
    public async Task A_local_Main_Story_start_without_an_admitted_Joining_Player_is_silent()
    {
        await Create().HandleLocalGameEventAsync(new GameEvent.MainStoryStarted(), TestContext.Current.CancellationToken);

        Assert.Empty(_displayed);
    }

    [Fact]
    public async Task Joining_Player_starts_the_host_Main_Story_and_reports_started()
    {
        await Create().HandleHostStartAsync(MainStory, TestContext.Current.CancellationToken);

        Assert.Equal([MainStory], _startedLocally);
        Assert.Equal([SharedStoryStartOutcome.Started], _sessions.SentMainStoryOutcomes);
        Assert.Empty(_sessions.SentOutcomes);
        Assert.Equal(["The host started the Main Story."], _displayed);
    }

    [Theory]
    [InlineData(StartStoryExchangeOutcome.NotInMainMenu, SharedStoryStartOutcome.NotInMainMenu,
        "The Main Story could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(StartStoryExchangeOutcome.AlreadyPending, SharedStoryStartOutcome.NotInMainMenu,
        "The Main Story could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(StartStoryExchangeOutcome.TimedOut, SharedStoryStartOutcome.Unavailable,
        "The Main Story could not start for the Joining Player: their game does not support Main Story starts or did not respond.")]
    [InlineData(StartStoryExchangeOutcome.Unrecognized, SharedStoryStartOutcome.Unavailable,
        "The Main Story could not start for the Joining Player: their game does not support Main Story starts or did not respond.")]
    public async Task Joining_Player_reports_a_failed_Main_Story_start_to_the_Session_Host_and_sees_why(
        StartStoryExchangeOutcome local, SharedStoryStartOutcome reported, string feedback)
    {
        _localOutcome = local;

        await Create().HandleHostStartAsync(MainStory, TestContext.Current.CancellationToken);

        Assert.Equal([reported], _sessions.SentMainStoryOutcomes);
        Assert.Equal([feedback], _displayed);
    }

    [Theory]
    [InlineData(SharedStoryStartOutcome.NotInMainMenu,
        "The Main Story could not start for the Joining Player: their game is not in the main menu.")]
    [InlineData(SharedStoryStartOutcome.Unavailable,
        "The Main Story could not start for the Joining Player: their game does not support Main Story starts or did not respond.")]
    public async Task Session_Host_sees_why_a_Main_Story_start_failed_for_the_Joining_Player(SharedStoryStartOutcome outcome, string feedback)
    {
        await Create().HandleOutcomeAsync(MainStory, outcome);

        Assert.Equal([feedback], _displayed);
    }

    [Fact]
    public async Task Session_Host_sees_nothing_further_when_the_Main_Story_start_succeeded()
    {
        await Create().HandleOutcomeAsync(MainStory, SharedStoryStartOutcome.Started);

        Assert.Empty(_displayed);
    }

    [Fact]
    public async Task Joining_Player_own_Main_Story_start_is_kept_local_with_a_warning()
    {
        _sessions.IsJoined = true;

        await Create().HandleLocalGameEventAsync(new GameEvent.MainStoryStarted(), TestContext.Current.CancellationToken);

        Assert.Equal([OwnMainStoryStartWarning], _displayed);
        Assert.Equal(0, _sessions.SentMainStoryStarts);
    }

    [Fact]
    public async Task Joining_Player_reproducing_the_host_Main_Story_start_neither_relays_it_nor_warns()
    {
        _sessions.IsJoined = true;
        var sharedStart = Create();
        _localStart = new();

        var exchange = sharedStart.HandleHostStartAsync(MainStory, TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(
            new GameEvent.StartMainStoryResponded(StartMainStoryOutcome.Starting), TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(new GameEvent.MainStoryStarted(), TestContext.Current.CancellationToken);
        _localStart.SetResult(StartStoryExchangeOutcome.Starting);
        await exchange;

        Assert.Equal(["The host started the Main Story."], _displayed);
        Assert.Equal(0, _sessions.SentMainStoryStarts);
    }

    [Fact]
    public async Task A_Main_Story_start_after_reproducing_a_Custom_Story_start_is_the_Joining_Player_own()
    {
        _sessions.IsJoined = true;
        var sharedStart = Create();
        _localStart = new();

        var exchange = sharedStart.HandleHostStartAsync(MpTestCs, TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(
            new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting), TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(new GameEvent.MainStoryStarted(), TestContext.Current.CancellationToken);
        _localStart.SetResult(StartStoryExchangeOutcome.Starting);
        await exchange;

        Assert.Equal([OwnMainStoryStartWarning, "The host started Custom Story mp-test-cs."], _displayed);
    }

    private async Task ReproduceHostStartAsync(SharedStoryStart sharedStart, string hostStart, string reportedStart)
    {
        _localStart = new();
        var exchange = sharedStart.HandleHostStartAsync(new Story.Custom(hostStart), TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(
            new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting), TestContext.Current.CancellationToken);
        await sharedStart.HandleLocalGameEventAsync(new GameEvent.CustomStoryStarted(reportedStart), TestContext.Current.CancellationToken);
        _localStart.SetResult(StartStoryExchangeOutcome.Starting);
        await exchange;
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged { add { } remove { } }
        public int OtherPlayerArrival => 0;
        public void SendEnemyPose(LanMessage.EnemyPose pose) { }
        public Task SendEnemyEventAsync(LanMessage.EnemyEvent message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendDeathMessageAsync(LanMessage.DeathMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SendPose(LanMessage.Pose pose) { }
        public void SendBodies(LanMessage.Bodies bodies) { }
        public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendWorldMessageAsync(LanMessage.WorldMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool JoiningPlayerAdmitted { get; set; }
        public bool IsJoined { get; set; }
        public List<string> SentStarts { get; } = [];
        public List<(string, SharedStoryStartOutcome)> SentOutcomes { get; } = [];
        public int SentMainStoryStarts { get; private set; }
        public List<SharedStoryStartOutcome> SentMainStoryOutcomes { get; } = [];

        public Task<bool> SendMainStoryStartedAsync(CancellationToken cancellationToken)
        {
            SentMainStoryStarts++;
            return Task.FromResult(JoiningPlayerAdmitted);
        }

        public Task SendMainStoryStartOutcomeAsync(SharedStoryStartOutcome outcome, CancellationToken cancellationToken)
        {
            SentMainStoryOutcomes.Add(outcome);
            return Task.CompletedTask;
        }
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
            string identifier, SharedStoryStartOutcome outcome, CancellationToken cancellationToken)
        {
            SentOutcomes.Add((identifier, outcome));
            return Task.CompletedTask;
        }
    }
}
