using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed class UnavailableSessionOperations : ISessionOperations
{
    public event Action? MultiplayerSessionEnded { add { } remove { } }
    private static readonly SessionOperationResult Unavailable =
        SessionOperationResult.Failed("Multiplayer connectivity is not available yet.");

    public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task SendCustomStoryStartOutcomeAsync(
        string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;
}
