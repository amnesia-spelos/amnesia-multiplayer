using System.Collections.Concurrent;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

// Shares the Map Script callbacks the local player triggers, and the buttons they switch and lamps they light, with the
// other players (ADR 0006). The local game reports each one; this Game Peer sends it, and applies the other players' in
// the local game with a Command it composes from identifiers alone, so every game runs its own Map Script's registration.
// A change for a map the local game is not on is kept as a Pending Shared Callback, per map and in arrival order, and
// applied once the local game enters that map. Kept changes last the Multiplayer Session, until a new Shared Custom Story
// Start discards them. Active only while the local game granted `sharedcallbacks`. One per local game Session.
public sealed class SharedCallbacks
{
    // Far beyond a chapter's worth of triggers on maps the local player has yet to visit; a change past it is dropped.
    public const int MaximumPending = 4096;

    // Arrival orders the kept changes of a map. Story is the Shared Custom Story Start the change belongs to, and Visit
    // the Map Visit its Command was queued for.
    private readonly record struct Change(long Arrival, long Story, LanMessage.WorldMessage Message, long Visit = 0)
    {
        public override string ToString() => Message.ToString();
    }

    private readonly ISessionOperations _sessions;
    private readonly Func<string, CancellationToken, ValueTask> _writeLine;
    private readonly Action<ConnectionLogSeverity, LocalGameEventName, string> _log;
    private readonly Task<ProtocolNegotiation> _negotiation;
    private readonly CancellationToken _cancellationToken;
    // Guards the local map, the Map Visits seen, the arrivals, and the kept changes; lines are enqueued under it so they
    // follow the order the changes arrived in.
    private readonly object _gate = new();
    private string? _localMap;
    private long _visits;
    private long _arrivals;
    private long _stories;
    private readonly Dictionary<string, List<Change>> _pending = [];
    private int _pendingCount;
    // Changes for the local game, and the ones written to it whose Responses are still to come, in the order written.
    private readonly ConcurrentQueue<Change> _lines = new();
    private readonly ConcurrentQueue<Change> _awaitingResponse = new();
    // 1 while a worker is writing to the local game.
    private int _working;

    public SharedCallbacks(
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
    }

    private bool IsRefused => _negotiation.IsCompletedSuccessfully && !_negotiation.Result.GrantsSharedCallbacks;

    // Must be called in the order the local game reported the events. Never waits for the other players: changes are
    // queued on the session lane, in order.
    public async Task HandleLocalGameEventAsync(GameEvent gameEvent)
    {
        if (IsRefused) return;
        Task sent = Task.CompletedTask;
        lock (_gate)
        {
            switch (gameEvent)
            {
                case GameEvent.MapEntered entered:
                    ObserveLocalMapLocked(entered.Map, newVisit: true);
                    break;
                // The Session Host's Custom Story starts are the Shared Custom Story Starts.
                case GameEvent.CustomStoryStarted when !_sessions.IsJoined:
                    DiscardLocked();
                    break;
                case GameEvent.WorldChanged changed:
                    ObserveLocalMapLocked(changed.Change.Map, newVisit: false);
                    sent = _sessions.SendWorldMessageAsync(changed.Change, _cancellationToken);
                    break;
                default:
                    if (GameInteractionProtocol.LocalMapOf(gameEvent) is { } map) ObserveLocalMapLocked(map, newVisit: false);
                    break;
            }
        }
        Update();
        await sent;
    }

    // Called for each change another player sends. Never blocks: the local game is written to later, in order.
    public void HandleReceivedWorldMessage(LanMessage.WorldMessage message)
    {
        if (IsRefused) return;
        lock (_gate)
        {
            var change = new Change(++_arrivals, _stories, message);
            if (message.Map == _localMap) EnqueueLocked(change);
            else KeepLocked(change);
        }
        Update();
    }

    // Joining Player: the Session Host started a Custom Story. Called before any change sent after it is received.
    public void HandleReceivedCustomStoryStart()
    {
        lock (_gate) DiscardLocked();
    }

    // Every run, button, and lamp Command is answered, in the order written. Nothing here is ever shown in chat.
    public void HandleResponse(GameEvent.Responded response)
    {
        if (response.Keyword is not ("sharedcallback" or "buttonstate" or "lampstate")) return;
        var line = string.Join(' ', ["RESPONSE", response.Keyword, response.Outcome, .. response.Fields]);
        if (!_awaitingResponse.TryDequeue(out var change))
        {
            _log(ConnectionLogSeverity.Warning, LocalGameEventName.SharedCallbackFailed, $"{line} answered no Command.");
            return;
        }
        switch (response.Outcome)
        {
            case "ok":
                return;
            // The registration was already removed, or the Map Scripts drifted: an outcome, not an error.
            case "not-found":
                _log(ConnectionLogSeverity.Information, LocalGameEventName.SharedCallbackNotFound, $"Nothing in the local game matched {change}.");
                return;
            // The local game left the map before the Command reached it, so the change waits for its next visit there.
            // A change from before a Shared Custom Story Start was discarded with the rest.
            case "wrong-map":
                lock (_gate)
                {
                    if (change.Story != _stories) return;
                    // Already back on the map: applied at once, so after any change for it that arrived since.
                    if (change.Message.Map == _localMap && change.Visit != _visits) EnqueueLocked(change);
                    else KeepLocked(change);
                }
                Update();
                return;
            default:
                _log(ConnectionLogSeverity.Warning, LocalGameEventName.SharedCallbackFailed, $"{line} for {change}.");
                return;
        }
    }

    // A new Map Visit, or a map other than the one last seen, applies the changes kept for it, in arrival order.
    private void ObserveLocalMapLocked(string map, bool newVisit)
    {
        if (!newVisit && map == _localMap) return;
        _localMap = map;
        _visits++;
        if (!_pending.Remove(map, out var kept)) return;
        _pendingCount -= kept.Count;
        foreach (var change in kept) EnqueueLocked(change);
    }

    private void EnqueueLocked(Change change) => _lines.Enqueue(change with { Visit = _visits });

    private void KeepLocked(Change change)
    {
        if (_pendingCount >= MaximumPending)
        {
            _log(ConnectionLogSeverity.Warning, LocalGameEventName.SharedCallbackFailed,
                $"Dropped {change}: {MaximumPending} Pending Shared Callbacks are already kept.");
            return;
        }
        if (!_pending.TryGetValue(change.Message.Map, out var kept)) _pending[change.Message.Map] = kept = [];
        // A change the local game turned away rejoins the ones that arrived after it in their arrival order.
        var index = kept.FindLastIndex(other => other.Arrival < change.Arrival) + 1;
        kept.Insert(index, change with { Visit = 0 });
        _pendingCount++;
    }

    private void DiscardLocked()
    {
        if (_pendingCount > 0)
            _log(ConnectionLogSeverity.Information, LocalGameEventName.SharedCallbacksDiscarded,
                $"Discarded {_pendingCount} Pending Shared Callbacks for a new Shared Custom Story Start.");
        _pending.Clear();
        _pendingCount = 0;
        // Changes still to be written, or written and still unanswered, belong to the story before it too.
        _lines.Clear();
        _stories++;
        // The new story's first map may be the one last seen; its changes wait for the game to report entering it.
        _localMap = null;
    }

    private void Update()
    {
        if (Interlocked.CompareExchange(ref _working, 1, 0) == 0) _ = WorkAsync();
    }

    private async Task WorkAsync()
    {
        try
        {
            // Without the Capability the worker never releases, so nothing is ever written.
            if (!(await _negotiation.WaitAsync(_cancellationToken)).GrantsSharedCallbacks) return;
            while (true)
            {
                while (_lines.TryDequeue(out var change))
                {
                    // Before the write, so its Response always finds it.
                    _awaitingResponse.Enqueue(change);
                    await _writeLine(GameInteractionProtocol.Apply(change.Message), _cancellationToken);
                }
                Volatile.Write(ref _working, 0);
                // A change offered after the check above but before the release would otherwise wait for the next one.
                if (_lines.IsEmpty || Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
            }
        }
        // The local game Session ended. _working stays 1.
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
