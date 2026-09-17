using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed class SharedCustomStoryStart(
    ISessionOperations sessions,
    Func<string, CancellationToken, Task<StartCustomStoryExchangeOutcome>> startLocalCustomStory,
    Func<string, ValueTask> displaySystem)
{
    // Set while this Game Peer's startcustomstory for the Session Host awaits the local game's Response.
    private string? _hostStartAwaitingResponse;
    // Read and written only from the local game's event loop, which reads the Response before the start it announces.
    private string? _reproducedStart;

    // Must be called in the order the local game reported the events.
    public async Task HandleLocalGameEventAsync(GameEvent gameEvent, CancellationToken cancellationToken)
    {
        switch (gameEvent)
        {
            case GameEvent.StartCustomStoryResponded responded:
                var identifier = Interlocked.Exchange(ref _hostStartAwaitingResponse, null);
                _reproducedStart = responded.Outcome == StartCustomStoryOutcome.Starting ? identifier : null;
                break;
            case GameEvent.CustomStoryStarted started:
                var reproduced = _reproducedStart == started.Identifier;
                _reproducedStart = null;
                if (reproduced) return;
                if (sessions.IsJoined)
                    await displaySystem("Only the Session Host's Custom Story starts are shared.");
                else if (await sessions.SendCustomStoryStartedAsync(started.Identifier, cancellationToken))
                    await displaySystem($"Starting Custom Story {started.Identifier} for the other player.");
                break;
        }
    }

    // Joining Player: the Session Host started a Custom Story.
    public async Task HandleHostStartAsync(string identifier, CancellationToken cancellationToken)
    {
        // A start already awaiting its Response keeps the claim; the local game rejects this one as already pending.
        var claimedResponse = Interlocked.CompareExchange(ref _hostStartAwaitingResponse, identifier, null) is null;
        StartCustomStoryExchangeOutcome exchangeOutcome;
        try { exchangeOutcome = await startLocalCustomStory(identifier, cancellationToken); }
        finally { if (claimedResponse) Interlocked.CompareExchange(ref _hostStartAwaitingResponse, null, identifier); }

        var outcome = exchangeOutcome switch
        {
            StartCustomStoryExchangeOutcome.Starting => SharedCustomStoryStartOutcome.Started,
            StartCustomStoryExchangeOutcome.NotFound => SharedCustomStoryStartOutcome.NotFound,
            StartCustomStoryExchangeOutcome.Invalid => SharedCustomStoryStartOutcome.Invalid,
            StartCustomStoryExchangeOutcome.NotInMainMenu or StartCustomStoryExchangeOutcome.AlreadyPending =>
                SharedCustomStoryStartOutcome.NotInMainMenu,
            _ => SharedCustomStoryStartOutcome.Unavailable
        };
        await sessions.SendCustomStoryStartOutcomeAsync(identifier, outcome, cancellationToken);
        await displaySystem(outcome == SharedCustomStoryStartOutcome.Started
            ? $"The host started Custom Story {identifier}."
            : FailureFeedback(identifier, outcome));
    }

    // Session Host: the Joining Player reported how its start went.
    public async Task HandleOutcomeAsync(string identifier, SharedCustomStoryStartOutcome outcome)
    {
        if (outcome != SharedCustomStoryStartOutcome.Started)
            await displaySystem(FailureFeedback(identifier, outcome));
    }

    private static string FailureFeedback(string identifier, SharedCustomStoryStartOutcome outcome) =>
        $"Custom Story {identifier} could not start for the Joining Player: " + outcome switch
        {
            SharedCustomStoryStartOutcome.NotFound => "it is not installed.",
            SharedCustomStoryStartOutcome.Invalid => "the installed Custom Story is invalid.",
            SharedCustomStoryStartOutcome.NotInMainMenu => "their game is not in the main menu.",
            _ => "their game does not support Custom Story starts or did not respond."
        };
}
