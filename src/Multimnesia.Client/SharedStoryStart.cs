using Multimnesia.Contracts;

namespace Multimnesia.Client;

// A story the Session Host can start for every player: the Main Story, or an installed Custom Story.
public abstract record Story
{
    public sealed record Main : Story;
    public sealed record Custom(string Identifier) : Story;
}

// The Shared Custom Story Start and the Shared Main Story Start.
public sealed class SharedStoryStart(
    ISessionOperations sessions,
    Func<Story, CancellationToken, Task<StartStoryExchangeOutcome>> startLocalStory,
    Func<string, ValueTask> displaySystem)
{
    // Set while this Game Peer's start for the Session Host awaits the local game's Response.
    private Story? _hostStartAwaitingResponse;
    // Read and written only from the local game's event loop, which reads the Response before the start it announces.
    private Story? _reproducedStart;

    // Must be called in the order the local game reported the events. A start is relayed as soon as the game reports
    // it, so both games load together.
    public async Task HandleLocalGameEventAsync(GameEvent gameEvent, CancellationToken cancellationToken)
    {
        switch (gameEvent)
        {
            case GameEvent.StartCustomStoryResponded responded:
                HandleResponse(responded.Outcome == StartCustomStoryOutcome.Starting);
                break;
            case GameEvent.StartMainStoryResponded responded:
                HandleResponse(responded.Outcome == StartMainStoryOutcome.Starting);
                break;
            case GameEvent.CustomStoryStarted started:
                await HandleLocalStartAsync(new Story.Custom(started.Identifier), cancellationToken);
                break;
            case GameEvent.MainStoryStarted:
                await HandleLocalStartAsync(new Story.Main(), cancellationToken);
                break;
        }
    }

    private void HandleResponse(bool starting)
    {
        var story = Interlocked.Exchange(ref _hostStartAwaitingResponse, null);
        _reproducedStart = starting ? story : null;
    }

    private async Task HandleLocalStartAsync(Story story, CancellationToken cancellationToken)
    {
        var reproduced = story.Equals(_reproducedStart);
        _reproducedStart = null;
        if (reproduced) return;
        if (sessions.IsJoined)
            await displaySystem($"Only the Session Host's {Kind(story)} starts are shared.");
        else if (await SendStartAsync(story, cancellationToken))
            await displaySystem($"Starting {Name(story)} for the other player.");
    }

    private Task<bool> SendStartAsync(Story story, CancellationToken cancellationToken) => story switch
    {
        Story.Custom custom => sessions.SendCustomStoryStartedAsync(custom.Identifier, cancellationToken),
        _ => sessions.SendMainStoryStartedAsync(cancellationToken)
    };

    // Joining Player: the Session Host started a story.
    public async Task HandleHostStartAsync(Story story, CancellationToken cancellationToken)
    {
        // A start already awaiting its Response keeps the claim; the local game rejects this one as already pending.
        var claimedResponse = Interlocked.CompareExchange(ref _hostStartAwaitingResponse, story, null) is null;
        StartStoryExchangeOutcome exchangeOutcome;
        try { exchangeOutcome = await startLocalStory(story, cancellationToken); }
        finally { if (claimedResponse) Interlocked.CompareExchange(ref _hostStartAwaitingResponse, null, story); }

        var outcome = exchangeOutcome switch
        {
            StartStoryExchangeOutcome.Starting => SharedStoryStartOutcome.Started,
            // A Main Story is never missing or invalid, so the game that says so does not understand the start.
            StartStoryExchangeOutcome.NotFound when story is Story.Custom => SharedStoryStartOutcome.NotFound,
            StartStoryExchangeOutcome.Invalid when story is Story.Custom => SharedStoryStartOutcome.Invalid,
            StartStoryExchangeOutcome.NotInMainMenu or StartStoryExchangeOutcome.AlreadyPending =>
                SharedStoryStartOutcome.NotInMainMenu,
            _ => SharedStoryStartOutcome.Unavailable
        };
        await (story switch
        {
            Story.Custom custom => sessions.SendCustomStoryStartOutcomeAsync(custom.Identifier, outcome, cancellationToken),
            _ => sessions.SendMainStoryStartOutcomeAsync(outcome, cancellationToken)
        });
        await displaySystem(outcome == SharedStoryStartOutcome.Started
            ? $"The host started {Name(story)}."
            : FailureFeedback(story, outcome));
    }

    // Session Host: the Joining Player reported how its start went.
    public async Task HandleOutcomeAsync(Story story, SharedStoryStartOutcome outcome)
    {
        if (outcome != SharedStoryStartOutcome.Started)
            await displaySystem(FailureFeedback(story, outcome));
    }

    private static string Kind(Story story) => story is Story.Custom ? "Custom Story" : "Main Story";

    private static string Name(Story story) => story is Story.Custom custom ? $"Custom Story {custom.Identifier}" : "the Main Story";

    private static string FailureFeedback(Story story, SharedStoryStartOutcome outcome) =>
        $"{(story is Story.Custom custom ? $"Custom Story {custom.Identifier}" : "The Main Story")} could not start for the Joining Player: " +
        outcome switch
        {
            SharedStoryStartOutcome.NotFound => "it is not installed.",
            SharedStoryStartOutcome.Invalid => "the installed Custom Story is invalid.",
            SharedStoryStartOutcome.NotInMainMenu => "their game is not in the main menu.",
            _ => $"their game does not support {Kind(story)} starts or did not respond."
        };
}
