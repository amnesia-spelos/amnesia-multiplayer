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
        LanMessage.EnemyAttack? attack = null;
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
                }
                _localMap = map;
            }
            if (gameEvent is GameEvent.EnemyPoseReported reported && SharesLocked(reported.Pose.Map))
                _sessions.SendEnemyPose(reported.Pose);
            if (gameEvent is GameEvent.EnemyAttackReported attacked && SharesLocked(attacked.Attack.Map))
                attack = attacked.Attack;
        }
        return attack is null ? Task.CompletedTask : _sessions.SendEnemyAttackAsync(attack, _token);
    }

    // The Session Host simulates this map's enemies for the other players on it.
    private bool SharesLocked(string map) =>
        Granted && !_sessions.IsJoined && _arrival != 0 && map == _localMap && _otherMaps.Contains(map);

    public void HandleReceivedEnemyPose(LanMessage.EnemyPose pose)
    {
        lock (_gate)
        {
            if (!DrivesLocked(pose.Map)) return;
            _pending[pose.EntityId] = pose;
        }
        _wake.Writer.TryWrite(true);
    }

    // The game tests the attack against its own player from the driven copy; it answers not-found for an enemy it does not drive.
    public void HandleReceivedEnemyAttack(LanMessage.EnemyAttack attack)
    {
        lock (_gate)
        {
            if (!DrivesLocked(attack.Map)) return;
            // The attack is tested against the copy as posed by every sample received before it.
            if (_pending.Remove(attack.EntityId, out var pose)) EnqueuePoseLocked(pose);
            _commands.Enqueue(GameInteractionProtocol.EnemyAttack(attack));
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
