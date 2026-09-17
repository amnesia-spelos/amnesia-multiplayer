namespace Multimnesia.Client;

public sealed class UnavailableSessionOperations : ISessionOperations
{
    private static readonly SessionOperationResult Unavailable =
        SessionOperationResult.Failed("Multiplayer connectivity is not available yet.");

    public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => Task.FromResult(Unavailable);
    public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => Task.CompletedTask;
}
