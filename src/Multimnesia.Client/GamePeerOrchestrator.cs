using System.Net;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum GamePeerState { Local, Hosting, Joining, Joined }

public abstract record PeerCommand
{
    public sealed record HostCommand : PeerCommand;
    public sealed record Join(string Destination) : PeerCommand;
    public sealed record LeaveCommand : PeerCommand;

    public static PeerCommand Host { get; } = new HostCommand();
    public static PeerCommand Leave { get; } = new LeaveCommand();
}

public abstract record PeerInput
{
    public sealed record OrdinaryChat : PeerInput;
    public sealed record Command(PeerCommand Value) : PeerInput;
    public sealed record Rejected(string Feedback) : PeerInput;
}

public static class PeerCommandParser
{
    public static PeerInput Parse(string message)
    {
        if (!message.StartsWith('/')) return new PeerInput.OrdinaryChat();
        if (message == "/host") return new PeerInput.Command(PeerCommand.Host);
        if (message.StartsWith("/host ", StringComparison.Ordinal)) return new PeerInput.Rejected("Usage: /host");
        if (message == "/leave") return new PeerInput.Command(PeerCommand.Leave);
        if (message.StartsWith("/leave ", StringComparison.Ordinal)) return new PeerInput.Rejected("Usage: /leave");
        if (message == "/join" || message.StartsWith("/join ", StringComparison.Ordinal))
        {
            const string prefix = "/join ";
            if (!message.StartsWith(prefix, StringComparison.Ordinal))
                return new PeerInput.Rejected("Usage: /join <hostname-or-IPv4>");
            var host = message[prefix.Length..];
            if (host.Length == 0 || host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                return new PeerInput.Rejected("Usage: /join <hostname-or-IPv4>");
            return new PeerInput.Command(new PeerCommand.Join(host));
        }
        return new PeerInput.Rejected("Unknown command");
    }
}

public readonly record struct SessionOperationResult(bool Success, string? Feedback)
{
    public static SessionOperationResult Succeeded { get; } = new(true, null);
    public static SessionOperationResult SucceededWith(string feedback) => new(true, feedback);
    public static SessionOperationResult Failed(string feedback) => new(false, feedback);
}

public interface ISessionOperations
{
    event Action? MultiplayerSessionEnded;
    bool IsJoined { get; }
    Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken);
    Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken);
    Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken);
    Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken);
    Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken);
    Task SendCustomStoryStartOutcomeAsync(string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken);
}

public sealed class GamePeerOrchestrator
{
    private readonly ISessionOperations _operations;
    private int _multiplayerSessionEndVersion;
    private CancellationTokenSource? _joinCancellation;

    public GamePeerOrchestrator(ISessionOperations operations, GamePeerState initialState = GamePeerState.Local)
    {
        _operations = operations;
        State = initialState;
        operations.MultiplayerSessionEnded += () =>
        {
            Interlocked.Increment(ref _multiplayerSessionEndVersion);
            State = GamePeerState.Local;
        };
    }

    public GamePeerState State { get; private set; }

    public async Task<ChatEntry?> HandleAsync(ChatEntry entry, CancellationToken cancellationToken = default)
    {
        switch (PeerCommandParser.Parse(entry.Message))
        {
            case PeerInput.OrdinaryChat:
                if (State is GamePeerState.Hosting or GamePeerState.Joined)
                    await _operations.SendChatAsync(entry, cancellationToken);
                return null;
            case PeerInput.Rejected rejected:
                return SystemFeedback(rejected.Feedback);
            case PeerInput.Command { Value: PeerCommand.LeaveCommand }:
                if (State == GamePeerState.Local) return SystemFeedback("You are not in a Multiplayer Session.");
                var leavingState = State;
                State = GamePeerState.Local;
                Interlocked.Exchange(ref _joinCancellation, null)?.Cancel();
                await _operations.LeaveAsync(leavingState, cancellationToken);
                return SystemFeedback(leavingState switch
                {
                    GamePeerState.Hosting => "Hosting stopped.",
                    GamePeerState.Joining => "Joining cancelled.",
                    _ => "You left the Multiplayer Session."
                });
            case PeerInput.Command when State != GamePeerState.Local:
                return SystemFeedback("Leave the current Multiplayer Session first.");
            case PeerInput.Command { Value: PeerCommand.HostCommand }:
                return Apply(await _operations.HostAsync(cancellationToken), GamePeerState.Hosting);
            case PeerInput.Command { Value: PeerCommand.Join join }:
                var sessionEndVersion = Volatile.Read(ref _multiplayerSessionEndVersion);
                State = GamePeerState.Joining;
                using (var joinCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    _joinCancellation = joinCancellation;
                    try
                    {
                        var joinResult = await _operations.JoinAsync(join.Destination, joinCancellation.Token);
                        if (State != GamePeerState.Joining) return null;
                        State = joinResult.Success && sessionEndVersion == Volatile.Read(ref _multiplayerSessionEndVersion)
                            ? GamePeerState.Joined
                            : GamePeerState.Local;
                        return joinResult.Feedback is null ? null : SystemFeedback(joinResult.Feedback);
                    }
                    finally { Interlocked.CompareExchange(ref _joinCancellation, null, joinCancellation); }
                }
            default:
                throw new InvalidOperationException("Unsupported Game Peer command.");
        }
    }

    private ChatEntry? Apply(SessionOperationResult result, GamePeerState successState)
    {
        if (result.Success) State = successState;
        return result.Feedback is null ? null : SystemFeedback(result.Feedback);
    }

    private static ChatEntry SystemFeedback(string message) => new("SYSTEM", message);
}
