using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum StartCustomStoryExchangeOutcome { Starting, NotFound, Invalid, NotInMainMenu, Unrecognized, TimedOut, AlreadyPending }

public sealed class LocalGameCommands(
    Func<string, CancellationToken, ValueTask> writeLine,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private TaskCompletionSource<StartCustomStoryExchangeOutcome>? _pendingStart;

    public async Task<StartCustomStoryExchangeOutcome> StartCustomStoryAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!CustomStoryIdentifier.IsValid(identifier))
            throw new ArgumentException("Invalid Custom Story Identifier.", nameof(identifier));

        var pending = new TaskCompletionSource<StartCustomStoryExchangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pendingStart, pending, null) is not null)
            return StartCustomStoryExchangeOutcome.AlreadyPending;

        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await writeLine(GameInteractionProtocol.StartCustomStory(identifier), cancellationToken);
            var deadline = delay(ResponseTimeout, deadlineCancellation.Token);
            if (await Task.WhenAny(pending.Task, deadline) == pending.Task) return await pending.Task;
            await deadline;
            return StartCustomStoryExchangeOutcome.TimedOut;
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
        StartCustomStoryExchangeOutcome? outcome = gameEvent switch
        {
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.Starting } => StartCustomStoryExchangeOutcome.Starting,
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.NotFound } => StartCustomStoryExchangeOutcome.NotFound,
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.Invalid } => StartCustomStoryExchangeOutcome.Invalid,
            GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.NotInMainMenu } => StartCustomStoryExchangeOutcome.NotInMainMenu,
            GameEvent.StartCustomStoryResponded or GameEvent.UnknownCommandWarned => StartCustomStoryExchangeOutcome.Unrecognized,
            _ => null
        };
        if (outcome is { } value) Volatile.Read(ref _pendingStart)?.TrySetResult(value);
    }
}
