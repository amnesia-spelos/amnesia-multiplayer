using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class LocalGameCommandsTests
{
    private readonly List<string> _written = [];
    private readonly List<TimeSpan> _deadlines = [];
    private readonly List<TaskCompletionSource> _pendingDeadlines = [];

    private LocalGameCommands CreateCommands() => new(
        (line, _) => { _written.Add(line); return ValueTask.CompletedTask; },
        (timeout, cancellationToken) =>
        {
            _deadlines.Add(timeout);
            var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingDeadlines.Add(deadline);
            return deadline.Task.WaitAsync(cancellationToken);
        });

    private void ExpireDeadline() => _pendingDeadlines[^1].SetResult();

    [Theory]
    [InlineData(StartCustomStoryOutcome.Starting, StartCustomStoryExchangeOutcome.Starting)]
    [InlineData(StartCustomStoryOutcome.NotFound, StartCustomStoryExchangeOutcome.NotFound)]
    [InlineData(StartCustomStoryOutcome.Invalid, StartCustomStoryExchangeOutcome.Invalid)]
    [InlineData(StartCustomStoryOutcome.NotInMainMenu, StartCustomStoryExchangeOutcome.NotInMainMenu)]
    [InlineData(StartCustomStoryOutcome.Unrecognized, StartCustomStoryExchangeOutcome.Unrecognized)]
    public async Task Start_custom_story_issues_the_command_and_yields_the_next_response(
        StartCustomStoryOutcome outcome, StartCustomStoryExchangeOutcome expected)
    {
        var commands = CreateCommands();

        var start = commands.StartCustomStoryAsync("mp-test-cs", TestContext.Current.CancellationToken);
        commands.Dispatch(new GameEvent.StartCustomStoryResponded(outcome));

        Assert.Equal(expected, await start);
        Assert.Equal(["startcustomstory:mp-test-cs"], _written);
    }

    [Fact]
    public async Task A_game_without_start_custom_story_yields_an_unrecognized_reply()
    {
        var commands = CreateCommands();

        var start = commands.StartCustomStoryAsync("mp-test-cs", TestContext.Current.CancellationToken);
        commands.Dispatch(new GameEvent.UnknownCommandWarned());

        Assert.Equal(StartCustomStoryExchangeOutcome.Unrecognized, await start);
    }

    [Fact]
    public async Task Start_custom_story_waits_five_seconds_by_default_and_reports_timeout()
    {
        var commands = CreateCommands();

        var start = commands.StartCustomStoryAsync("mp-test-cs", TestContext.Current.CancellationToken);
        ExpireDeadline();

        Assert.Equal(StartCustomStoryExchangeOutcome.TimedOut, await start);
        Assert.Equal([TimeSpan.FromSeconds(5)], _deadlines);
    }

    [Fact]
    public async Task A_response_after_timeout_is_not_matched_to_the_next_exchange()
    {
        var commands = CreateCommands();
        var timedOut = commands.StartCustomStoryAsync("first", TestContext.Current.CancellationToken);
        ExpireDeadline();
        await timedOut;
        commands.Dispatch(new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting));

        var next = commands.StartCustomStoryAsync("second", TestContext.Current.CancellationToken);

        Assert.False(next.IsCompleted);
        commands.Dispatch(new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.NotFound));
        Assert.Equal(StartCustomStoryExchangeOutcome.NotFound, await next);
    }

    [Fact]
    public async Task A_concurrent_start_is_reported_as_pending_without_contacting_the_game()
    {
        var commands = CreateCommands();
        var first = commands.StartCustomStoryAsync("first", TestContext.Current.CancellationToken);

        var second = await commands.StartCustomStoryAsync("second", TestContext.Current.CancellationToken);

        Assert.Equal(StartCustomStoryExchangeOutcome.AlreadyPending, second);
        Assert.Equal(["startcustomstory:first"], _written);
        commands.Dispatch(new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting));
        Assert.Equal(StartCustomStoryExchangeOutcome.Starting, await first);
    }

    [Fact]
    public async Task A_new_start_can_follow_a_completed_exchange()
    {
        var commands = CreateCommands();
        var first = commands.StartCustomStoryAsync("first", TestContext.Current.CancellationToken);
        commands.Dispatch(new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Invalid));
        await first;

        var second = commands.StartCustomStoryAsync("second", TestContext.Current.CancellationToken);
        commands.Dispatch(new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting));

        Assert.Equal(StartCustomStoryExchangeOutcome.Starting, await second);
        Assert.Equal(["startcustomstory:first", "startcustomstory:second"], _written);
    }

    [Fact]
    public async Task Other_events_do_not_complete_a_pending_start()
    {
        var commands = CreateCommands();
        var start = commands.StartCustomStoryAsync("mp-test-cs", TestContext.Current.CancellationToken);

        commands.Dispatch(new GameEvent.CustomStoryStarted("mp-test-cs"));
        commands.Dispatch(new GameEvent.ChatSubmitted(new ChatEntry("Alice", "hi")));
        commands.Dispatch(new GameEvent.Unknown("RESPONSE:chat:unavailable"));

        Assert.False(start.IsCompleted);
        ExpireDeadline();
        Assert.Equal(StartCustomStoryExchangeOutcome.TimedOut, await start);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nexec:Quit()")]
    [InlineData("a:b")]
    public async Task Invalid_identifiers_are_never_written_to_the_game(string identifier)
    {
        var commands = CreateCommands();

        await Assert.ThrowsAsync<ArgumentException>(() => commands.StartCustomStoryAsync(identifier, TestContext.Current.CancellationToken));

        Assert.Empty(_written);
    }

    [Fact]
    public async Task A_failed_write_releases_the_pending_exchange()
    {
        var fail = true;
        var commands = new LocalGameCommands(
            (_, _) => fail ? ValueTask.FromException(new IOException("closed")) : ValueTask.CompletedTask,
            (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));

        await Assert.ThrowsAsync<IOException>(() => commands.StartCustomStoryAsync("first", TestContext.Current.CancellationToken));
        fail = false;
        var next = commands.StartCustomStoryAsync("second", TestContext.Current.CancellationToken);
        commands.Dispatch(new GameEvent.StartCustomStoryResponded(StartCustomStoryOutcome.Starting));

        Assert.Equal(StartCustomStoryExchangeOutcome.Starting, await next);
    }
}
