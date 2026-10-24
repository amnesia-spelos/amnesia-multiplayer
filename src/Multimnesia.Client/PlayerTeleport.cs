using Multimnesia.Contracts;

namespace Multimnesia.Client;

// `/tp-to-peer`: places the local player where another player stands, from their latest received Pose, so a player that a
// locked door or a closed wall left behind can follow. Local only: it writes a `playerplace` Command and sends nothing to the LAN.
public sealed class PlayerTeleport(
    ISessionOperations sessions,
    Func<string, CancellationToken, ValueTask> writeLine,
    Task<ProtocolNegotiation> negotiation,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    public const string TeleportedFeedback = "Teleported to the other player.";
    public const string NotPresentFeedback = "The other player is not in the Multiplayer Session.";
    public const string NoPoseFeedback = "The other player's position is not known yet.";
    public const string DifferentMapFeedback = "The other player is on a different map.";
    public const string TimedOutFeedback = "Your game did not answer the teleport.";
    public const string AlreadyPendingFeedback = "A teleport is already in progress.";
    public const string UnsupportedFeedback = "Your game does not support /tp-to-peer; update it to teleport to the other player.";

    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    // Guards the pending placement and the count of unanswered ones, so a late answer is never taken for a newer Command's.
    private readonly Lock _gate = new();
    private TaskCompletionSource<GameEvent.Responded>? _pendingPlace;
    // Placements written that timed out unanswered. The game answers in order, so their answers come first and are dropped.
    private int _unansweredPlaces;
    // The latest Pose of each other player, by the arrival it came in, so a replacement player is never placed at the Pose
    // of the player who left. A Multiplayer Session has one other player for now (ADR 0008).
    private readonly Dictionary<int, LanMessage.Pose> _otherPlayerPoses = [];
    // The map the local game last showed it is on, or null before it named one.
    private string? _localMap;

    // Latest-wins, like the Shared Pose.
    public void HandleReceivedPose(LanMessage.Pose pose)
    {
        var arrival = sessions.OtherPlayerArrival;
        lock (_otherPlayerPoses)
        {
            // Players who left keep no Pose.
            foreach (var departed in _otherPlayerPoses.Keys.Where(key => key != arrival).ToArray()) _otherPlayerPoses.Remove(departed);
            if (arrival != 0) _otherPlayerPoses[arrival] = pose;
        }
    }

    public void HandleLocalGameEvent(GameEvent gameEvent)
    {
        if (GameInteractionProtocol.LocalMapOf(gameEvent) is { } map) Volatile.Write(ref _localMap, map);
        if (gameEvent is not GameEvent.Responded { Keyword: "playerplace" } responded) return;
        lock (_gate)
        {
            if (_unansweredPlaces > 0) _unansweredPlaces--;
            else _pendingPlace?.TrySetResult(responded);
        }
    }

    // `/tp-to-peer` names no player while a Multiplayer Session has one other player. Returns the SYSTEM feedback.
    public async Task<string> TeleportAsync(CancellationToken cancellationToken)
    {
        if (!(await negotiation.WaitAsync(cancellationToken)).GrantsPlayerPlace) return UnsupportedFeedback;
        var arrival = sessions.OtherPlayerArrival;
        if (arrival == 0) return NotPresentFeedback;
        LanMessage.Pose? pose;
        lock (_otherPlayerPoses) _otherPlayerPoses.TryGetValue(arrival, out pose);
        if (pose is null) return NoPoseFeedback;
        // Following the other player through a map change is not part of this. Before the local game names its map, the
        // game itself refuses a Pose on another map.
        if (Volatile.Read(ref _localMap) is { } localMap && localMap != pose.Map) return DifferentMapFeedback;

        var pending = new TaskCompletionSource<GameEvent.Responded>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pendingPlace is not null) return AlreadyPendingFeedback;
            _pendingPlace = pending;
        }
        var written = false;
        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await writeLine(GameInteractionProtocol.PlayerPlace(pose), cancellationToken);
            written = true;
            var deadline = delay(ResponseTimeout, deadlineCancellation.Token);
            if (await Task.WhenAny(pending.Task, deadline) != pending.Task)
            {
                await deadline;
                return TimedOutFeedback;
            }
            return (await pending.Task).Outcome switch
            {
                "ok" => TeleportedFeedback,
                "wrong-map" => DifferentMapFeedback,
                "unavailable" => RefusedFeedback("you are not in play"),
                "invalid" => RefusedFeedback("the request was malformed"),
                "not-granted" => UnsupportedFeedback,
                var outcome => RefusedFeedback(outcome)
            };
        }
        finally
        {
            deadlineCancellation.Cancel();
            lock (_gate)
            {
                if (written && !pending.Task.IsCompleted) _unansweredPlaces++;
                _pendingPlace = null;
            }
        }
    }

    private static string RefusedFeedback(string reason) => $"Your game refused the teleport: {reason}.";
}
