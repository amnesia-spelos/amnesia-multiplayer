using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum StartStoryExchangeOutcome { Starting, NotFound, Invalid, NotInMainMenu, Unrecognized, TimedOut, AlreadyPending }

public sealed class LocalGameCommands(
    Func<string, CancellationToken, ValueTask> writeLine,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private TaskCompletionSource<StartStoryExchangeOutcome>? _pendingStart;

    public async Task<StartStoryExchangeOutcome> StartCustomStoryAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!CustomStoryIdentifier.IsValid(identifier))
            throw new ArgumentException("Invalid Custom Story Identifier.", nameof(identifier));
        return await StartAsync(GameInteractionProtocol.StartCustomStory(identifier), cancellationToken);
    }

    public Task<StartStoryExchangeOutcome> StartMainStoryAsync(CancellationToken cancellationToken) =>
        StartAsync(GameInteractionProtocol.StartMainStory, cancellationToken);

    // The game handles one story start at a time, so a Custom Story and a Main Story start share one exchange.
    private async Task<StartStoryExchangeOutcome> StartAsync(string command, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<StartStoryExchangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pendingStart, pending, null) is not null)
            return StartStoryExchangeOutcome.AlreadyPending;

        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await writeLine(command, cancellationToken);
            var deadline = delay(ResponseTimeout, deadlineCancellation.Token);
            if (await Task.WhenAny(pending.Task, deadline) == pending.Task) return await pending.Task;
            await deadline;
            return StartStoryExchangeOutcome.TimedOut;
        }
        finally
        {
            deadlineCancellation.Cancel();
            Interlocked.CompareExchange(ref _pendingStart, null, pending);
        }
    }

    // After the negotiation, which LocalGameSession answers itself, the Game Peer issues no other Command an older game
    // could reject, so an unknown-command warning answers the pending start.
    public void Dispatch(GameEvent gameEvent)
    {
        StartStoryExchangeOutcome? outcome = gameEvent switch
        {
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.Starting } => StartStoryExchangeOutcome.Starting,
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.NotFound } => StartStoryExchangeOutcome.NotFound,
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.Invalid } => StartStoryExchangeOutcome.Invalid,
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.NotInMainMenu } => StartStoryExchangeOutcome.NotInMainMenu,
            GameEvent.StartMainStoryResponded { Outcome: StartMainStoryOutcome.Starting } => StartStoryExchangeOutcome.Starting,
            GameEvent.StartMainStoryResponded { Outcome: StartMainStoryOutcome.NotInMainMenu } => StartStoryExchangeOutcome.NotInMainMenu,
            GameEvent.StartCustomStoryResponded or GameEvent.StartMainStoryResponded or GameEvent.UnknownCommandWarned =>
                StartStoryExchangeOutcome.Unrecognized,
            _ => null
        };
        if (outcome is { } value) Volatile.Read(ref _pendingStart)?.TrySetResult(value);
    }
}
