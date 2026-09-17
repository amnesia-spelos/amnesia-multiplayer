using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class SharedCustomStoryStartTests
{
    private readonly FakeSessionOperations _sessions = new();
    private readonly List<string> _displayed = [];
    private readonly List<string> _startedLocally = [];
    private StartCustomStoryExchangeOutcome _localOutcome = StartCustomStoryExchangeOutcome.Starting;

    private SharedCustomStoryStart Create() => new(
        _sessions,
        (identifier, _) => { _startedLocally.Add(identifier); return Task.FromResult(_localOutcome); },
        message => { _displayed.Add(message); return ValueTask.CompletedTask; });

    [Fact]
    public async Task Session_Host_relays_a_local_start_to_the_admitted_Joining_Player_with_SYSTEM_feedback()
    {
        _sessions.JoiningPlayerAdmitted = true;

        await Create().HandleLocalStartAsync("mp-test-cs", TestContext.Current.CancellationToken);

        Assert.Equal(["mp-test-cs"], _sessions.SentStarts);
        Assert.Equal(["Starting Custom Story mp-test-cs for the other player."], _displayed);
        Assert.Empty(_startedLocally);
    }

    [Fact]
    public async Task A_local_start_without_an_admitted_Joining_Player_is_silent()
    {
        _sessions.JoiningPlayerAdmitted = false;

        await Create().HandleLocalStartAsync("mp-test-cs", TestContext.Current.CancellationToken);

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

    private sealed class FakeSessionOperations : ISessionOperations
    {
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public bool JoiningPlayerAdmitted { get; set; }
        public List<string> SentStarts { get; } = [];
        public List<(string, SharedCustomStoryStartOutcome)> SentOutcomes { get; } = [];
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken)
        {
            if (JoiningPlayerAdmitted) SentStarts.Add(identifier);
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
