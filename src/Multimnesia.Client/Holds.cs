using System.Collections.Concurrent;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

// Shares the players' Holds on map-placed entities (ADR 0004). A local interaction becomes a Claim, and the bodies the
// local player Holds stream to the other player. The other player's Claims make their entities Peer-Driven Entities in
// the local game, which follow their bodies until they settle. Every line written to the local game is composed here.
// Active only while the other player is present and the local game granted `interactions`.
// One per local game Session: the game releases its Peer-Driven Entities and subscription when that Session ends.
public sealed class Holds
{
    public const int ReportedBodiesRateHz = 30;

    private enum Holder { Local, Other }

    private readonly record struct Entity(string Map, int PropId)
    {
        public override string ToString() => $"entity {PropId} on {Map}";
    }

    private readonly ISessionOperations _sessions;
    private readonly Func<string, CancellationToken, ValueTask> _writeLine;
    private readonly Action<ConnectionLogSeverity, LocalGameEventName, string> _log;
    private readonly Task<ProtocolNegotiation> _negotiation;
    private readonly CancellationToken _cancellationToken;
    // Who Holds each entity. On the Session Host this table is the Multiplayer Relay's arbiter: every Claim, the Session
    // Host's own included, is ordered by a compare-and-set on it.
    private readonly Dictionary<Entity, Holder> _holds = [];
    // Lines for the local game, enqueued under the _holds lock so they follow the order the Holds changed in.
    private readonly ConcurrentQueue<string> _lines = new();
    private LanMessage.Bodies? _receivedBodies;
    // 1 while a worker is writing to the local game; only that worker reads or writes _subscribed.
    private int _working;
    private bool _subscribed;

    public Holds(
        ISessionOperations sessions,
        Func<string, CancellationToken, ValueTask> writeLine,
        Action<ConnectionLogSeverity, LocalGameEventName, string> log,
        Task<ProtocolNegotiation> negotiation,
        CancellationToken cancellationToken)
    {
        _sessions = sessions;
        _writeLine = writeLine;
        _log = log;
        _negotiation = negotiation;
        _cancellationToken = cancellationToken;
        sessions.OtherPlayerPresenceChanged += Update;
        // After a local game reconnect, the other player may already be present.
        Update();
    }

    private bool IsActive =>
        _negotiation.IsCompletedSuccessfully && _negotiation.Result.GrantsInteractions && _sessions.OtherPlayerArrival != 0;

    // Called for each `interactions` Event and `reportedbodies` State Update the local game sends. Never waits for the
    // other player: Hold messages are queued on the session lane, and bodies replace any not yet sent.
    public async Task HandleLocalGameEventAsync(GameEvent gameEvent)
    {
        if (!IsActive) return;
        switch (gameEvent)
        {
            case GameEvent.InteractionStarted started:
                var entity = new Entity(started.Map, started.EntityId);
                bool claimed;
                lock (_holds)
                {
                    // Grabbing an entity the local player Holds while it is Settling continues that Hold.
                    if (!TryTake(entity, Holder.Local, out claimed)) return;
                }
                if (claimed)
                {
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimed, $"The local player Claimed {entity}.");
                    await SendAsync(new LanMessage.Claim(entity.Map, entity.PropId, ClaimReason.Interact));
                }
                await SendAsync(new LanMessage.Interaction(entity.Map, entity.PropId, started.BodyId, true));
                break;
            case GameEvent.InteractionEnded ended when IsHeldBy(new(ended.Map, ended.EntityId), Holder.Local):
                await SendAsync(new LanMessage.Interaction(ended.Map, ended.EntityId, ended.BodyId, false));
                break;
            case GameEvent.ReportSettled settled when TryEnd(new(settled.Map, settled.EntityId), Holder.Local):
                _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldEnded,
                    $"The local player's Hold on {new Entity(settled.Map, settled.EntityId)} ended: settled.");
                await SendAsync(new LanMessage.Settled(settled.Map, settled.EntityId));
                break;
            case GameEvent.ReportedBodies reported:
                BodyEntry[] held;
                lock (_holds) held = [.. reported.Entries.Where(entry => IsHeldByLocked(new(reported.Map, entry.PropId), Holder.Local))];
                if (held.Length > 0) _sessions.SendBodies(new(reported.TimeMs, reported.Map, held));
                break;
        }
    }

    // Called for each Hold message from the other player. Never blocks: the local game is written to later, in order.
    public void HandleReceivedHoldMessage(LanMessage.HoldMessage message)
    {
        if (!IsActive) return;
        var entity = new Entity(message.Map, message.PropId);
        lock (_holds)
        {
            switch (message)
            {
                case LanMessage.Claim when TryTake(entity, Holder.Other, out _):
                    _lines.Enqueue(GameInteractionProtocol.EntityDrive(entity.PropId, entity.Map));
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimed, $"The other player Claimed {entity}.");
                    break;
                case LanMessage.Interaction interaction when IsHeldByLocked(entity, Holder.Other):
                    _lines.Enqueue(GameInteractionProtocol.EntityInteracting(entity.PropId, interaction.Active, entity.Map));
                    break;
                case LanMessage.Settled when IsHeldByLocked(entity, Holder.Other) && _holds.Remove(entity):
                    _lines.Enqueue(GameInteractionProtocol.EntityRelease(entity.PropId, entity.Map));
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldEnded,
                        $"The other player's Hold on {entity} ended: settled.");
                    break;
                default:
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldMessageIgnored, $"Ignored {message}.");
                    return;
            }
        }
        Update();
    }

    // Latest-wins: replaces any received bodies not yet written to the local game. Never blocks.
    public void HandleReceivedBodies(LanMessage.Bodies bodies)
    {
        if (!IsActive) return;
        Volatile.Write(ref _receivedBodies, bodies);
        Update();
    }

    // Failures are logged, never shown in chat. The game reports an entitybodies failure once per streak.
    public void HandleResponse(GameEvent.Responded response)
    {
        if (response is { Keyword: "entitydrive" or "entitybodies" or "entityinteracting" or "entityrelease" or "entitybreak" or "reportedbodies" }
            && response.Outcome != "ok")
            _log(ConnectionLogSeverity.Warning, LocalGameEventName.HoldCommandFailed,
                string.Join(' ', ["RESPONSE", response.Keyword, response.Outcome, .. response.Fields]));
    }

    // The compare-and-set every Claim goes through. False when the other Holder already has the entity.
    private bool TryTake(Entity entity, Holder holder, out bool claimed)
    {
        claimed = _holds.TryAdd(entity, holder);
        return claimed || _holds[entity] == holder;
    }

    private bool IsHeldBy(Entity entity, Holder holder)
    {
        lock (_holds) return IsHeldByLocked(entity, holder);
    }

    private bool IsHeldByLocked(Entity entity, Holder holder) => _holds.TryGetValue(entity, out var current) && current == holder;

    private bool TryEnd(Entity entity, Holder holder)
    {
        lock (_holds) return IsHeldByLocked(entity, holder) && _holds.Remove(entity);
    }

    private Task SendAsync(LanMessage.HoldMessage message) => _sessions.SendHoldMessageAsync(message, _cancellationToken);

    private void Update()
    {
        if (Interlocked.CompareExchange(ref _working, 1, 0) == 0) _ = WorkAsync();
    }

    private async Task WorkAsync()
    {
        try
        {
            // Without the Capability the worker never releases, so nothing is ever written.
            if (!(await _negotiation.WaitAsync(_cancellationToken)).GrantsInteractions) return;
            while (true)
            {
                var present = _sessions.OtherPlayerArrival != 0;
                if (present != _subscribed)
                {
                    _subscribed = present;
                    await _writeLine(present
                        ? GameInteractionProtocol.SubscribeReportedBodies(ReportedBodiesRateHz)
                        : GameInteractionProtocol.UnsubscribeReportedBodies, _cancellationToken);
                    continue;
                }
                if (_lines.TryDequeue(out var line))
                {
                    await _writeLine(line, _cancellationToken);
                    continue;
                }
                if (Interlocked.Exchange(ref _receivedBodies, null) is { } bodies)
                {
                    // Filtered as it is written, so bodies never follow the line that released their entity, and never
                    // precede a line queued since the check above, such as the entitydrive of a Claim that arrived meanwhile.
                    BodyEntry[] driven;
                    lock (_holds)
                    {
                        if (!_lines.IsEmpty)
                        {
                            Interlocked.CompareExchange(ref _receivedBodies, bodies, null);
                            continue;
                        }
                        driven = [.. bodies.Entries.Where(entry => IsHeldByLocked(new(bodies.Map, entry.PropId), Holder.Other))];
                    }
                    if (driven.Length > 0)
                        await _writeLine(GameInteractionProtocol.EntityBodies(bodies.TimeMs, driven, bodies.Map), _cancellationToken);
                    continue;
                }

                Volatile.Write(ref _working, 0);
                // Work offered after the checks above but before the release would otherwise wait for the next Update.
                var pending = (_sessions.OtherPlayerArrival != 0) != _subscribed || !_lines.IsEmpty ||
                    Volatile.Read(ref _receivedBodies) is not null;
                if (!pending || Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
            }
        }
        // The local game Session ended; the game releases its Peer-Driven Entities and subscription with it. _working stays 1.
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
