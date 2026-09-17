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
        if (outcome == SharedCustomStoryStartOutcome.Started)
            await displaySystem($"The host started Custom Story {identifier}.");
    }
}
