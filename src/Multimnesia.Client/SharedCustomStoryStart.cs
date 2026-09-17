using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed class SharedCustomStoryStart(
    ISessionOperations sessions,
    Func<string, CancellationToken, Task<StartCustomStoryExchangeOutcome>> startLocalCustomStory,
    Func<string, ValueTask> displaySystem)
{
    // Session Host: the local game reported a fresh Custom Story start.
    public async Task HandleLocalStartAsync(string identifier, CancellationToken cancellationToken)
    {
        if (await sessions.SendCustomStoryStartedAsync(identifier, cancellationToken))
            await displaySystem($"Starting Custom Story {identifier} for the other player.");
    }

    // Joining Player: the Session Host started a Custom Story.
    public async Task HandleHostStartAsync(string identifier, CancellationToken cancellationToken)
    {
        var outcome = await startLocalCustomStory(identifier, cancellationToken) switch
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
