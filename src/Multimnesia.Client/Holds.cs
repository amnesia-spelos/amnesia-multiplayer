using System.Collections.Concurrent;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

// Shares the players' Holds on map-placed entities (ADR 0004). A local interaction or contact becomes a Claim, and the
// bodies of every entity the local player Holds stream to the other player in one message per report. The other
// player's Claims make their entities Peer-Driven Entities in the local game, which follow their bodies until they settle.
// Contention resolves by the order Claims reach the Session Host's table, and the loser's game drives the entity for the
// winner. Every line written to the local game is composed here.
// Active only while the other player is present and the local game granted `interactions`.
// Every Hold ends when it settles or breaks, when the other player leaves or is replaced, or when its Holder or the local
// player leaves its map. Maps are known from Poses and `interactions` Events, so a reload of the same map is not seen, and
// the Holder leaving a map is not seen without their Poses. One per local game Session: the game releases its Peer-Driven
// Entities and subscription when that Session ends, and the other player sees this Game Peer depart.
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
    // Guarded by the _holds lock. On the Joining Player: the Session Host's Claims on entities the local player Holds,
    // each with whether the Session Host is interacting. The Multiplayer Relay ordered them before the local player's
    // Claim, which it denies unless the Session Host's Hold settles first.
    private readonly Dictionary<Entity, bool> _earlierClaims = [];
    // Guarded by the _holds lock: the other player's arrival the table belongs to, and the map the local player was last seen on.
    private int _arrival;
    private string? _localMap;
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
        sessions.OtherPlayerPresenceChanged += HandlePresenceChanged;
        // After a local game reconnect, the other player may already be present.
        HandlePresenceChanged();
    }

    private bool IsActive =>
        _negotiation.IsCompletedSuccessfully && _negotiation.Result.GrantsInteractions && _sessions.OtherPlayerArrival != 0;

    // The Session Host's Game Peer is the Multiplayer Relay, whose table orders every Claim.
    private bool IsArbiter => !_sessions.IsJoined;

    // Called for each `interactions` Event and `reportedbodies` State Update the local game sends. Never waits for the
    // other player: Hold messages are queued on the session lane, and bodies replace any not yet sent.
    public async Task HandleLocalGameEventAsync(GameEvent gameEvent)
    {
        if (!IsActive) return;
        // Before the event itself, so a grab on a new map is Claimed rather than taken for a Hold on the map left.
        if (LocalMapOf(gameEvent) is { } map)
            lock (_holds) ObserveLocalMapLocked(map);
        switch (gameEvent)
        {
            case GameEvent.InteractionStarted started:
                var entity = new Entity(started.Map, started.EntityId);
                Task claim = Task.CompletedTask, start;
                lock (_holds)
                {
                    // Grabbing an entity the local player Holds while it is Settling continues that Hold.
                    if (!TryTake(entity, Holder.Local, out var claimed))
                    {
                        // The entitydrive already queued for the other player takes it out of the local player's hands.
                        _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimDenied,
                            $"The local player's grab of {entity} was refused: the other player Holds it.");
                        return;
                    }
                    if (claimed)
                    {
                        _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimed, $"The local player Claimed {entity}.");
                        claim = SendLocked(new LanMessage.Claim(entity.Map, entity.PropId, ClaimReason.Interact));
                    }
                    start = SendLocked(new LanMessage.Interaction(entity.Map, entity.PropId, started.BodyId, true));
                }
                await claim;
                await start;
                break;
            // A Hold taken by touch starts Settling: there is no interaction to share.
            case GameEvent.ReportContacted contacted:
                var touched = new Entity(contacted.Map, contacted.EntityId);
                Task contactClaim;
                lock (_holds)
                {
                    // Contact never takes an entity that already has a Hold. The entitydrive already queued for the other
                    // player takes it out of the local game's report.
                    if (!TryTake(touched, Holder.Local, out var claimed))
                    {
                        _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimDenied,
                            $"The local player's contact with {touched} was refused: the other player Holds it.");
                        break;
                    }
                    if (!claimed) break;
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimed,
                        $"The local player Claimed {touched} by contact.");
                    contactClaim = SendLocked(new LanMessage.Claim(touched.Map, touched.PropId, ClaimReason.Contact));
                }
                await contactClaim;
                break;
            case GameEvent.InteractionEnded ended:
                Task end;
                lock (_holds)
                {
                    if (!IsHeldByLocked(new(ended.Map, ended.EntityId), Holder.Local)) break;
                    end = SendLocked(new LanMessage.Interaction(ended.Map, ended.EntityId, ended.BodyId, false));
                }
                await end;
                break;
            case GameEvent.ReportSettled reportSettled:
                var settled = new Entity(reportSettled.Map, reportSettled.EntityId);
                Task settledSent;
                bool driven;
                lock (_holds)
                {
                    if (!IsHeldByLocked(settled, Holder.Local)) break;
                    EndLocked(settled, "settled");
                    settledSent = SendLocked(new LanMessage.Settled(settled.Map, settled.PropId));
                    // The Multiplayer Relay ordered the Session Host's Claim first, so its denial of the local player's
                    // is on its way; until then, a grab would send a new Claim that the denial could be taken to answer.
                    driven = _earlierClaims.Remove(settled, out var interacting);
                    if (driven)
                    {
                        _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimed,
                            $"The other player's Claim on {settled}, ordered ahead of the local player's, took effect.");
                        DriveLocked(settled, interacting);
                    }
                }
                if (driven) Update();
                await settledSent;
                break;
            case GameEvent.ReportBroke reportBroke:
                var broken = new Entity(reportBroke.Map, reportBroke.EntityId);
                Task brokeSent;
                lock (_holds)
                {
                    if (!IsHeldByLocked(broken, Holder.Local)) break;
                    EndLocked(broken, "broke");
                    brokeSent = SendLocked(new LanMessage.Broke(broken.Map, broken.PropId, reportBroke.State));
                    // The local copy is gone, so the Session Host's Claim ordered ahead of the local player's has nothing
                    // left to drive here, even if its Hold later breaks too.
                    if (_earlierClaims.Remove(broken))
                        _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldEnded,
                            $"The other player's Hold on {broken}, Claimed ahead of the local player, ended: the local copy broke.");
                }
                await brokeSent;
                break;
            case GameEvent.ReportedBodies reported:
                List<BodyEntry> held = [];
                lock (_holds)
                {
                    // One message carries every Hold. The game reports at most 32 bodies, but an entity whose bodies would
                    // not fit is left out whole rather than half driven.
                    foreach (var bodies in reported.Entries.GroupBy(entry => entry.PropId))
                    {
                        if (!IsHeldByLocked(new(reported.Map, bodies.Key), Holder.Local)) continue;
                        if (held.Count + bodies.Count() <= LanProtocol.MaximumBodies) held.AddRange(bodies);
                    }
                }
                if (held.Count > 0) _sessions.SendBodies(new(reported.TimeMs, reported.Map, held));
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
                // Already held by the other player when their Claim Denial overtook their Claim.
                case LanMessage.Claim when TryTake(entity, Holder.Other, out var claimed):
                    if (!claimed) return;
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimed, $"The other player Claimed {entity}.");
                    DriveLocked(entity, interacting: false);
                    break;
                // The local player's Claim was ordered first. The denial ends the other player's interaction: their Game
                // Peer drives the entity for the local player. Never awaited, so a failure ends the session unobserved here.
                case LanMessage.Claim when IsArbiter:
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimDenied, $"Denied the other player's Claim on {entity}.");
                    _ = SendLocked(new LanMessage.ClaimDenied(entity.Map, entity.PropId));
                    return;
                // The Multiplayer Relay accepts a Claim only on an entity nobody Holds, so it ordered the Session Host's first.
                case LanMessage.Claim:
                    _earlierClaims[entity] = false;
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimed,
                        $"The other player Claimed {entity} ahead of the local player.");
                    return;
                case LanMessage.ClaimDenied when IsHeldByLocked(entity, Holder.Local):
                    _holds.Remove(entity);
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldClaimDenied, $"The local player's Claim on {entity} was denied.");
                    // Driving the entity ends the local player's interaction with it.
                    DriveLocked(entity, _earlierClaims.Remove(entity, out var interacting) && interacting);
                    break;
                case LanMessage.Interaction interaction when IsHeldByLocked(entity, Holder.Other):
                    _lines.Enqueue(GameInteractionProtocol.EntityInteracting(entity.PropId, interaction.Active, entity.Map));
                    break;
                case LanMessage.Interaction interaction when _earlierClaims.ContainsKey(entity):
                    _earlierClaims[entity] = interaction.Active;
                    return;
                case LanMessage.Settled when IsHeldByLocked(entity, Holder.Other):
                    EndLocked(entity, "settled");
                    break;
                // Breaking it ends driving, so the debris and contained item fall under local physics.
                case LanMessage.Broke broke when IsHeldByLocked(entity, Holder.Other):
                    BreakLocked(entity, broke.State);
                    break;
                // The Multiplayer Relay ordered the Session Host's Claim first, so its break stands, and the denial of the
                // local player's Claim is on its way. The game breaks only a Peer-Driven Entity, so it is driven first.
                case LanMessage.Broke broke when _earlierClaims.Remove(entity):
                    DriveLocked(entity, interacting: false);
                    BreakLocked(entity, broke.State);
                    break;
                // The Multiplayer Relay accepted the local player's Claim after the Session Host's Hold ended.
                case LanMessage.Settled when _earlierClaims.Remove(entity):
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldEnded,
                        $"The other player's Hold on {entity} ended: settled before the local player's Claim was ordered.");
                    return;
                default:
                    _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldMessageIgnored, $"Ignored {message}.");
                    return;
            }
        }
        Update();
    }

    // Called for each Pose the other player sends: they Hold nothing outside the map they are on. Never blocks.
    // A Claim is sent ahead of any later Pose, so a Pose never predates the Claims it is compared with.
    public void HandleReceivedPose(LanMessage.Pose pose)
    {
        if (!IsActive) return;
        int ended;
        lock (_holds)
            ended = EndAllLocked((entity, holder) => holder == Holder.Other && entity.Map != pose.Map, "the other player left the map");
        if (ended > 0) Update();
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

    private bool IsHeldByLocked(Entity entity, Holder holder) => _holds.TryGetValue(entity, out var current) && current == holder;

    // The entity becomes a Peer-Driven Entity for the other player, mirroring whether they are interacting with it.
    private void DriveLocked(Entity entity, bool interacting)
    {
        _holds[entity] = Holder.Other;
        _lines.Enqueue(GameInteractionProtocol.EntityDrive(entity.PropId, entity.Map));
        if (interacting) _lines.Enqueue(GameInteractionProtocol.EntityInteracting(entity.PropId, true, entity.Map));
    }

    // The other player's entity breaks from their final state instead of returning to local physics.
    private void BreakLocked(Entity entity, BodyState state)
    {
        EndLocked(entity, "broke", releaseInLocalGame: false);
        _lines.Enqueue(GameInteractionProtocol.EntityBreak(entity.PropId, state, entity.Map));
    }

    // A departure or a replacement: none of the table, the local player's Holds included, means anything to who comes next.
    private void HandlePresenceChanged()
    {
        lock (_holds)
        {
            var arrival = _sessions.OtherPlayerArrival;
            if (arrival != _arrival)
            {
                _arrival = arrival;
                EndAllLocked((_, _) => true, "the other player left");
            }
        }
        Update();
    }

    // The local game released its Peer-Driven Entities and emptied its report on the map it left.
    private void ObserveLocalMapLocked(string map)
    {
        var left = _localMap;
        _localMap = map;
        if (left is not null && left != map)
            EndAllLocked((entity, _) => entity.Map == left, "the local player left the map", releaseInLocalGame: false);
    }

    private static string? LocalMapOf(GameEvent gameEvent) => gameEvent switch
    {
        GameEvent.LocalPoseReported reported => reported.Pose.Map,
        GameEvent.InteractionStarted started => started.Map,
        GameEvent.InteractionEnded ended => ended.Map,
        GameEvent.ReportContacted contacted => contacted.Map,
        GameEvent.ReportSettled settled => settled.Map,
        GameEvent.ReportBroke broke => broke.Map,
        GameEvent.ReportedBodies reported => reported.Map,
        _ => null
    };

    private int EndAllLocked(Func<Entity, Holder, bool> ends, string reason, bool releaseInLocalGame = true)
    {
        Entity[] ending = [.. _holds.Where(hold => ends(hold.Key, hold.Value)).Select(hold => hold.Key)];
        foreach (var entity in ending) EndLocked(entity, reason, releaseInLocalGame);
        // Never driven in the local game, so nothing to release.
        foreach (var entity in _earlierClaims.Keys.Where(entity => ends(entity, Holder.Other)).ToArray())
        {
            _earlierClaims.Remove(entity);
            _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldEnded,
                $"The other player's Hold on {entity}, Claimed ahead of the local player, ended: {reason}.");
        }
        return ending.Length;
    }

    // The other player's entity returns to local physics from its last streamed state; the local player's needs nothing.
    private void EndLocked(Entity entity, string reason, bool releaseInLocalGame = true)
    {
        _holds.Remove(entity, out var holder);
        if (holder == Holder.Other && releaseInLocalGame)
            _lines.Enqueue(GameInteractionProtocol.EntityRelease(entity.PropId, entity.Map));
        _log(ConnectionLogSeverity.Information, LocalGameEventName.HoldEnded,
            $"The {(holder == Holder.Local ? "local" : "other")} player's Hold on {entity} ended: {reason}.");
    }

    // Queued on the session lane before the _holds lock is released, so Hold messages leave in the order the Holds changed
    // in, whichever thread changed them. Queuing never calls back into Holds; only a full lane, which ends the Multiplayer Session, leaves work for the task.
    private Task SendLocked(LanMessage.HoldMessage message) => _sessions.SendHoldMessageAsync(message, _cancellationToken);

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
