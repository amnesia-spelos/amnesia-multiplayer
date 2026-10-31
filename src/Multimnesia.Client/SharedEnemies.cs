using System.Threading.Channels;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

// One local game Session. Map presence comes from player Poses; enemy reports never count as player presence.
public sealed class SharedEnemies
{
    private readonly ISessionOperations _sessions;
    private readonly Func<string, CancellationToken, ValueTask> _writeLine;
    private readonly Task<ProtocolNegotiation> _negotiation;
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    // List-shaped for the players carried by the Multiplayer Relay (ADR 0008).
    private readonly List<string> _otherMaps = [];
    private string? _localMap;
    private int _arrival;
    private readonly Dictionary<int, LanMessage.EnemyPose> _pending = [];
    private readonly HashSet<int> _driven = [];
    private readonly Queue<string> _commands = [];
    // This game's shareddeath Commands, numbered for this game Session. The Session Host's episodes share the numbers.
    private int _episodes;
    // The last episode the Session Host sent, for a Joining Player's death reports.
    private int _receivedEpisode;
    // A Joining Player's death after the open episode reached it, answered once that episode completes.
    private LanMessage.PlayerDeath? _laterDeath;
    // From an episode until this game's checkpoint completes it.
    private bool _dying;
    // This game's checkpoint registration on the current Map Visit, once the game reported it.
    private CheckPoint? _checkPoint;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });

    public SharedEnemies(ISessionOperations sessions, Func<string, CancellationToken, ValueTask> writeLine,
        Task<ProtocolNegotiation> negotiation, CancellationToken token)
    {
        _sessions = sessions;
        _writeLine = writeLine;
        _negotiation = negotiation;
        _token = token;
        sessions.OtherPlayerPresenceChanged += HandlePresenceChanged;
        HandlePresenceChanged();
        _ = WorkAsync();
    }

    private bool Granted => _negotiation.IsCompletedSuccessfully && _negotiation.Result.GrantsEnemies;

    private void HandlePresenceChanged()
    {
        lock (_gate)
        {
            if (_arrival != _sessions.OtherPlayerArrival)
            {
                _arrival = _sessions.OtherPlayerArrival;
                ReleaseLocked();
                _otherMaps.Clear();
            }
        }
        _wake.Writer.TryWrite(true);
    }

    public void HandleReceivedPose(LanMessage.Pose pose)
    {
        lock (_gate)
        {
            if (pose.Map != _localMap) ReleaseLocked();
            _otherMaps.Clear();
            if (_arrival != 0) _otherMaps.Add(pose.Map);
        }
        _wake.Writer.TryWrite(true);
    }

    public Task HandleLocalGameEventAsync(GameEvent gameEvent)
    {
        LanMessage.EnemyEvent? enemyEvent = null;
        LanMessage.DeathMessage? death = null;
        lock (_gate)
        {
            if (GameInteractionProtocol.LocalMapOf(gameEvent) is { } map)
            {
                if (gameEvent is GameEvent.MapEntered || map != _localMap)
                {
                    // The game already released all copies at the new Map Visit, including same-map reloads.
                    _pending.Clear();
                    _driven.Clear();
                    _commands.Clear();
                    _checkPoint = null;
                    _dying = false;
                    _laterDeath = null;
                }
                _localMap = map;
            }
            // A Shared Death resets the enemies the Session Host simulates, so their samples until then would only
            // reactivate copies that the Joining Player's own checkpoint resets.
            if (gameEvent is GameEvent.EnemyPoseReported enemy && !_dying && SharesLocked(enemy.Pose.Map))
                _sessions.SendEnemyPose(enemy.Pose);
            if (gameEvent is GameEvent.EnemyAttackReported attacked && !_dying && SharesLocked(attacked.Attack.Map))
                enemyEvent = attacked.Attack;
            // Only the Session Host's game decides a disappearance; a Joining Player's own report is never relayed.
            if (gameEvent is GameEvent.EnemyDisappeared disappeared && !_dying && SharesLocked(disappeared.Disappearance.Map))
                enemyEvent = disappeared.Disappearance;
            // Only the simulating game's Kaernk eats its own food; a Joining Player's copy never reports a bite.
            if (gameEvent is GameEvent.EnemyAte ate && !_dying && SharesLocked(ate.Bite.Map))
                enemyEvent = ate.Bite;
            // A broken door stays broken through a checkpoint, so every game breaks its own copy, even during an episode.
            if (gameEvent is GameEvent.EnemyDoorBroke broke && SharesLocked(broke.DoorBreak.Map))
                enemyEvent = broke.DoorBreak;
            // Not killed, or already past its checkpoint: no checkpoint completes the episode.
            if (gameEvent is GameEvent.Responded { Keyword: "shareddeath", Outcome: not "ok" }) death = EndEpisodeLocked();
            // The game reports its checkpoint again once a death completed it. A coordinated death deactivated each copy, and
            // only driving it again reactivates one, so a released copy stays inactive until the next Enemy Pose drives it.
            if (gameEvent is GameEvent.CheckPointReported reported)
            {
                _checkPoint = reported.CheckPoint;
                if (_dying) ReleaseLocked();
                death = EndEpisodeLocked();
            }
            if (gameEvent is GameEvent.PlayerDied { CheckPoint.Callback: true } died) death = HandleOwnDeathLocked(died);
        }
        _wake.Writer.TryWrite(true);
        if (death is not null) return _sessions.SendDeathMessageAsync(death, _token);
        return enemyEvent is null ? Task.CompletedTask : _sessions.SendEnemyEventAsync(enemyEvent, _token);
    }

    // A death runs a callback that may change maps or puzzles, so it kills every player on the map, and each game passes the
    // Session Host's count to its own callback. A Joining Player's game waits for the episode the Session Host decides.
    private LanMessage.DeathMessage? HandleOwnDeathLocked(GameEvent.PlayerDied died)
    {
        var checkPoint = died.CheckPoint;
        if (_sessions.IsJoined)
            return DrivesLocked(checkPoint.Map) ? new LanMessage.PlayerDeath(checkPoint.Map, died.Death, _receivedEpisode) : null;
        if (!SharesLocked(checkPoint.Map) || _dying) return null;
        var episode = BeginEpisodeLocked(checkPoint);
        _commands.Enqueue(GameInteractionProtocol.SharedDeath(_episodes, episode with { Death = died.Death }));
        return episode;
    }

    // The Session Host's next episode, killing the Joining Player unless it is told which reported death it answers.
    private LanMessage.SharedDeath BeginEpisodeLocked(CheckPoint checkPoint)
    {
        _dying = true;
        return new LanMessage.SharedDeath(checkPoint.Map, ++_episodes, 0, checkPoint.Count, checkPoint.Name);
    }

    private LanMessage.SharedDeath? EndEpisodeLocked()
    {
        _dying = false;
        var later = _laterDeath;
        _laterDeath = null;
        return later is null ? null : HandleJoiningPlayerDeathLocked(later);
    }

    public Task HandleReceivedDeathMessageAsync(LanMessage.DeathMessage message)
    {
        LanMessage.SharedDeath? answer = null;
        lock (_gate)
        {
            if (message is LanMessage.PlayerDeath death) answer = HandleJoiningPlayerDeathLocked(death);
            // The game rejects a duplicate, stale, or other-map episode itself, and never kills a player not in play.
            if (message is LanMessage.SharedDeath received) _receivedEpisode = received.Episode;
            if (message is LanMessage.SharedDeath episode && DrivesLocked(episode.Map))
            {
                _dying = true;
                _pending.Clear();
                _commands.Enqueue(GameInteractionProtocol.SharedDeath(++_episodes, episode));
            }
        }
        _wake.Writer.TryWrite(true);
        return answer is null ? Task.CompletedTask : _sessions.SendDeathMessageAsync(answer, _token);
    }

    // The Joining Player's game reports only a death that runs a callback. Its episode kills the Session Host even when the
    // Session Host's own checkpoint differs, so nobody is stranded; each game then keeps its own count for a mismatch.
    // A report sent before the open episode reached the Joining Player is a death that episode joined.
    private LanMessage.SharedDeath? HandleJoiningPlayerDeathLocked(LanMessage.PlayerDeath death)
    {
        if (_dying && death.AfterEpisode == _episodes) _laterDeath = death;
        if (!SharesLocked(death.Map) || _dying || _checkPoint is not { } checkPoint || checkPoint.Map != death.Map) return null;
        var episode = BeginEpisodeLocked(checkPoint);
        _commands.Enqueue(GameInteractionProtocol.SharedDeath(_episodes, episode));
        return episode with { Death = death.Death };
    }

    // The Session Host simulates this map's enemies for the other players on it.
    private bool SharesLocked(string map) =>
        Granted && !_sessions.IsJoined && _arrival != 0 && map == _localMap && _otherMaps.Contains(map);

    public void HandleReceivedEnemyPose(LanMessage.EnemyPose pose)
    {
        lock (_gate)
        {
            if (_dying || !DrivesLocked(pose.Map)) return;
            _pending[pose.EntityId] = pose;
        }
        _wake.Writer.TryWrite(true);
    }

    // The game tests the attack against its own player from the driven copy; it answers not-found for an enemy it does not drive.
    public void HandleReceivedEnemyAttack(LanMessage.EnemyAttack attack) =>
        DeliverAfterReceivedPoses(attack.Map, attack.EntityId, GameInteractionProtocol.EnemyAttack(attack));

    // The game plays the full disappearance on its own enemy once, driven or not yet driven, and ignores that enemy's Enemy
    // Poses sampled no later than it, so late poses cannot bring it back; a later one means the Session Host's game
    // activated it again. The game also rejects a duplicate or stale delivery itself.
    public void HandleReceivedEnemyDisappearance(LanMessage.EnemyDisappearance disappearance) =>
        DeliverAfterReceivedPoses(disappearance.Map, disappearance.EntityId, GameInteractionProtocol.EnemyDisappear(disappearance));

    // The game shows the eat splash where the driven copy is shown and pauses its movement splashes; it answers not-found
    // for an enemy it does not drive.
    public void HandleReceivedEnemyBite(LanMessage.EnemyBite bite) =>
        DeliverAfterReceivedPoses(bite.Map, bite.EntityId, GameInteractionProtocol.EnemyEat(bite));

    // An enemy event acts on the copy as posed by every sample received before it. A Shared Death resets the copies, so
    // none is delivered until this game's checkpoint completes the episode.
    private void DeliverAfterReceivedPoses(string map, int entityId, string command)
    {
        lock (_gate)
        {
            if (_dying || !DrivesLocked(map)) return;
            if (_pending.Remove(entityId, out var pose)) EnqueuePoseLocked(pose);
            _commands.Enqueue(command);
        }
        _wake.Writer.TryWrite(true);
    }

    // The game breaks its own copy of the door, which ends any Hold on it in every game. A checkpoint does not mend it, so
    // the break applies during an episode too.
    public void HandleReceivedEnemyDoorBreak(LanMessage.EnemyDoorBreak doorBreak)
    {
        lock (_gate)
        {
            if (!DrivesLocked(doorBreak.Map)) return;
            _commands.Enqueue(GameInteractionProtocol.EnemyDoorBreak(doorBreak));
        }
        _wake.Writer.TryWrite(true);
    }

    // A Joining Player drives the Session Host's enemies on the map both are on.
    private bool DrivesLocked(string map) =>
        Granted && _sessions.IsJoined && _arrival != 0 && map == _localMap && (_otherMaps.Count == 0 || _otherMaps.Contains(map));

    private void ReleaseLocked()
    {
        if (_localMap is not null)
            foreach (var id in _driven) _commands.Enqueue(GameInteractionProtocol.EnemyRelease(id, _localMap));
        _driven.Clear();
        _pending.Clear();
    }

    private string? NextCommand()
    {
        lock (_gate)
        {
            if (_commands.TryDequeue(out var command)) return command;
            if (_pending.Count == 0) return null;
            var (id, pose) = _pending.First();
            _pending.Remove(id);
            EnqueuePoseLocked(pose);
            return _commands.Dequeue();
        }
    }

    private void EnqueuePoseLocked(LanMessage.EnemyPose pose)
    {
        if (_driven.Add(pose.EntityId)) _commands.Enqueue(GameInteractionProtocol.EnemyDrive(pose.EntityId, pose.Map));
        _commands.Enqueue(GameInteractionProtocol.EnemyPose(pose));
    }

    private async Task WorkAsync()
    {
        try
        {
            if (!(await _negotiation.WaitAsync(_token)).GrantsEnemies) return;
            var subscribed = false;
            while (await _wake.Reader.WaitToReadAsync(_token))
            {
                _wake.Reader.TryRead(out _);
                var subscribe = !_sessions.IsJoined && _sessions.OtherPlayerArrival != 0;
                if (subscribed != subscribe)
                {
                    subscribed = subscribe;
                    await _writeLine(subscribe ? $"enemyposes subscribe {SharedPose.LocalPoseRateHz}" : "enemyposes unsubscribe", _token);
                }
                while (NextCommand() is { } command) await _writeLine(command, _token);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
