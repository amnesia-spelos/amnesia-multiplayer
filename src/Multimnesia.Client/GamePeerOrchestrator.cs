using System.Net;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum GamePeerState { Local, Hosting, Joining, Joined }

public abstract record PeerCommand
{
    public sealed record HostCommand : PeerCommand;
    public sealed record Join(string Destination) : PeerCommand;
    public sealed record LeaveCommand : PeerCommand;
    // Takes no player name while a Multiplayer Session has two players; `/tp-to-peer <name>` can name one later.
    public sealed record TeleportToPeerCommand : PeerCommand;

    public static PeerCommand Host { get; } = new HostCommand();
    public static PeerCommand Leave { get; } = new LeaveCommand();
    public static PeerCommand TeleportToPeer { get; } = new TeleportToPeerCommand();
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
        if (message == "/tp-to-peer") return new PeerInput.Command(PeerCommand.TeleportToPeer);
        if (message.StartsWith("/tp-to-peer ", StringComparison.Ordinal)) return new PeerInput.Rejected("Usage: /tp-to-peer");
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
    // Raised after the other player may have joined or gone, for either role; must not block.
    event Action? OtherPlayerPresenceChanged;
    bool IsJoined { get; }
    // Non-zero while a Session Host has an admitted Joining Player, or a Joining Player is joined to a Session Host.
    // It changes with each arrival, so a replacement Joining Player is told apart from the one who left.
    int OtherPlayerArrival { get; }
    Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken);
    Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken);
    Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken);
    Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken);
    Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken);
    Task SendCustomStoryStartOutcomeAsync(string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken);
    // Latest-wins: replaces any Pose not yet sent to the other player.
    void SendPose(LanMessage.Pose pose);
    void SendEnemyPose(LanMessage.EnemyPose pose);
    // Ordered and exactly once, on the same lane as Hold messages; only the Session Host sends one.
    Task SendEnemyAttackAsync(LanMessage.EnemyAttack attack, CancellationToken cancellationToken);
    // Latest-wins, beside the Pose: replaces any bodies not yet sent to the other player.
    void SendBodies(LanMessage.Bodies bodies);
    // Ordered and exactly once; never waits behind Poses or bodies.
    Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken);
    // Ordered and exactly once, on the same lane as Hold messages.
    Task SendWorldMessageAsync(LanMessage.WorldMessage message, CancellationToken cancellationToken);
}

public sealed class GamePeerOrchestrator
{
    private readonly ISessionOperations _operations;
    // Places the local player at the other player and returns the SYSTEM feedback; it writes only to the local game.
    private readonly Func<CancellationToken, Task<string>> _teleportToPeer;
    private int _multiplayerSessionEndVersion;
    private CancellationTokenSource? _joinCancellation;

    public GamePeerOrchestrator(
        ISessionOperations operations,
        GamePeerState initialState = GamePeerState.Local,
        Func<CancellationToken, Task<string>>? teleportToPeer = null)
    {
        _operations = operations;
        _teleportToPeer = teleportToPeer ?? (_ => Task.FromResult(PlayerTeleport.UnsupportedFeedback));
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
            case PeerInput.Command { Value: PeerCommand.TeleportToPeerCommand }:
                if (State is not (GamePeerState.Hosting or GamePeerState.Joined))
                    return SystemFeedback("You are not in a Multiplayer Session.");
                return SystemFeedback(await _teleportToPeer(cancellationToken));
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
