using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum TrafficChannel { LocalGame, Lan }

public enum TrafficDirection { Inbound, Outbound }

public sealed record TrafficOptions
{
    public bool Statistics { get; init; }
    public bool Trace { get; init; }
    public long TraceMaxBytes { get; init; } = 200L * 1024 * 1024;
    public TimeSpan StatisticsInterval { get; init; } = TimeSpan.FromSeconds(1);
    // Trace records waiting for the writer; beyond this they are dropped and counted, never awaited.
    public int TraceCapacity { get; init; } = 16 * 1024;
}

// Traffic Statistics and the Traffic Trace: the hot paths only count or enqueue, and one background writer does all
// file I/O. Records name each message by its kind and size only, never its content.
public sealed class TrafficRecorder : IAsyncDisposable
{
    private const string FilePrefix = "traffic-";
    private const string FileExtension = ".jsonl";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Channel<TrafficEvent> _events = Channel.CreateUnbounded<TrafficEvent>(new UnboundedChannelOptions
    {
        SingleReader = true
    });
    private readonly Stream? _output;
    private readonly TrafficOptions _options;
    private readonly Action<string> _warn;
    private readonly Task _writer;
    private readonly TrafficConnection _disabledConnection;
    private volatile bool _statistics;
    private volatile bool _trace;
    private volatile bool _recording;
    private int _pendingTraces;
    private int _lastConnectionId;
    // Only the writer touches these.
    private readonly List<TrafficConnection> _open = [];
    private long _bytesWritten;
    private bool _failed;

    private TrafficRecorder()
    {
        _options = new();
        _warn = _ => { };
        _writer = Task.CompletedTask;
        _disabledConnection = new(this, 0, TrafficChannel.LocalGame, null, null);
    }

    private TrafficRecorder(Stream output, string? path, TrafficOptions options, Action<string> warn)
    {
        _output = output;
        FilePath = path;
        _options = options;
        _warn = warn;
        _statistics = options.Statistics;
        _trace = options.Trace;
        _recording = _statistics || _trace;
        _disabledConnection = new(this, 0, TrafficChannel.LocalGame, null, null);
        _writer = Task.Run(RunWriterAsync);
    }

    // Records nothing; its connections cost one check per message.
    public static TrafficRecorder Disabled { get; } = new();

    // Null while nothing is written to a file.
    public string? FilePath { get; }

    public bool IsStatisticsOn => _statistics;

    public bool IsTraceOn => _trace;

    internal bool IsRecording => _recording;

    // Creates this run's traffic file beside the main log, keeping only the newest files; disabled on failure.
    public static TrafficRecorder Open(string directory, DateTime startedAt, TrafficOptions options, Action<string> warn)
    {
        if (!options.Statistics && !options.Trace) return Disabled;
        var path = Path.Combine(directory, $"{FilePrefix}{startedAt:yyyyMMdd-HHmmss}{FileExtension}");
        try
        {
            Directory.CreateDirectory(directory);
            LogFiles.DeleteOldest(directory, $"{FilePrefix}*{FileExtension}", keep: LogFiles.RetainedFiles - 1);
            // Shared so the player can open the file while the Game Peer runs.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new TrafficRecorder(stream, path, options, warn);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warn($"Could not create the traffic file {path}: {exception.Message} Traffic Statistics and the Traffic Trace are off for this run.");
            return Disabled;
        }
    }

    public static TrafficRecorder Start(Stream output, TrafficOptions options, Action<string> warn) =>
        options.Statistics || options.Trace ? new TrafficRecorder(output, null, options, warn) : Disabled;

    // A Joining Player knows its peer correlation id from the start; the Session Host learns it from the join request.
    public TrafficConnection OpenConnection(TrafficChannel channel, RelayRole? role = null, Guid? peerCorrelationId = null)
    {
        if (!_recording) return _disabledConnection;
        var connection = new TrafficConnection(this, Interlocked.Increment(ref _lastConnectionId), channel, role, peerCorrelationId);
        Enqueue(new(TrafficEventKind.Opened, connection, DateTimeOffset.UtcNow));
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await _writer;
    }

    internal void Enqueue(TrafficEvent trafficEvent) => _events.Writer.TryWrite(trafficEvent);

    internal bool TryEnqueueTrace(TrafficEvent trace)
    {
        if (Interlocked.Increment(ref _pendingTraces) <= _options.TraceCapacity && _events.Writer.TryWrite(trace)) return true;
        Interlocked.Decrement(ref _pendingTraces);
        return false;
    }

    private async Task RunWriterAsync()
    {
        // The writer never takes the Game Peer down: anything unexpected ends recording like a failed write.
        try { await WriteUntilCompleteAsync(); }
        catch (Exception exception)
        {
            if (!_failed) Fail(exception);
            while (_events.Reader.TryRead(out _)) { }
        }
    }

    private async Task WriteUntilCompleteAsync()
    {
        var interval = _options.StatisticsInterval;
        var nextTick = Stopwatch.GetTimestamp() + (long)(interval.TotalSeconds * Stopwatch.Frequency);
        Task<bool>? waiting = null;
        while (true)
        {
            waiting ??= _events.Reader.WaitToReadAsync().AsTask();
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextTick);
            if (!waiting.IsCompleted && remaining > TimeSpan.Zero) await Task.WhenAny(waiting, Task.Delay(remaining));
            if (waiting.IsCompleted)
            {
                var more = await waiting;
                waiting = null;
                while (_events.Reader.TryRead(out var trafficEvent)) Handle(trafficEvent);
                if (!more) break;
            }
            if (Stopwatch.GetTimestamp() >= nextTick)
            {
                if (_statistics) foreach (var connection in _open) WriteStatistics(connection);
                // A writer held back past several ticks covers the missed seconds in one record.
                nextTick = Math.Max(nextTick, Stopwatch.GetTimestamp()) + (long)(interval.TotalSeconds * Stopwatch.Frequency);
            }
            Flush();
        }

        foreach (var connection in _open)
        {
            if (_statistics) WriteStatistics(connection);
            WriteClosed(connection, "shutdown", RelayFailureCategory.None);
        }
        _open.Clear();
        Flush();
        try { _output?.Dispose(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void Handle(TrafficEvent trafficEvent)
    {
        var connection = trafficEvent.Connection;
        switch (trafficEvent.Kind)
        {
            case TrafficEventKind.Trace:
                Interlocked.Decrement(ref _pendingTraces);
                if (!_trace) return;
                if (_bytesWritten >= _options.TraceMaxBytes)
                {
                    StopTrace();
                    return;
                }
                Write(new
                {
                    type = "trace",
                    timestamp = trafficEvent.Timestamp,
                    connection = connection.Id,
                    direction = DirectionName(trafficEvent.Direction),
                    kind = trafficEvent.MessageKind,
                    bytes = trafficEvent.Bytes
                });
                return;
            case TrafficEventKind.Opened:
                connection.IntervalStart = connection.OpenedAt = Stopwatch.GetTimestamp();
                _open.Add(connection);
                WriteConnection(connection, "opened", trafficEvent.Timestamp);
                return;
            case TrafficEventKind.Admitted:
                WriteConnection(connection, "admitted", trafficEvent.Timestamp);
                return;
            case TrafficEventKind.Closed:
                if (!_open.Remove(connection)) return;
                if (_statistics) WriteStatistics(connection);
                WriteClosed(connection, trafficEvent.Reason!, trafficEvent.Failure);
                return;
        }
    }

    private void StopTrace()
    {
        _trace = false;
        _recording = _statistics;
        _warn($"The traffic file reached {_options.TraceMaxBytes / (1024 * 1024)} MB, so the Traffic Trace stopped for the rest of this run." +
            (_statistics ? " Traffic Statistics carry on." : ""));
    }

    private void WriteStatistics(TrafficConnection connection)
    {
        var now = Stopwatch.GetTimestamp();
        var counters = connection.TakeInterval();
        WriteCounters("statistics", connection, counters, Stopwatch.GetElapsedTime(connection.IntervalStart, now));
        connection.IntervalStart = now;
    }

    private void WriteClosed(TrafficConnection connection, string reason, RelayFailureCategory failure)
    {
        if (_statistics)
            WriteCounters("summary", connection, connection.Totals, Stopwatch.GetElapsedTime(connection.OpenedAt));
        Write(new
        {
            type = "connection",
            timestamp = DateTimeOffset.UtcNow,
            connection = connection.Id,
            channel = ChannelName(connection.Channel),
            role = connection.Role?.ToString(),
            peerCorrelationId = connection.PeerCorrelationId,
            @event = "closed",
            reason,
            failure = failure == RelayFailureCategory.None ? null : failure.ToString()
        });
    }

    private void WriteConnection(TrafficConnection connection, string eventName, DateTimeOffset timestamp) => Write(new
    {
        type = "connection",
        timestamp,
        connection = connection.Id,
        channel = ChannelName(connection.Channel),
        role = connection.Role?.ToString(),
        peerCorrelationId = connection.PeerCorrelationId,
        @event = eventName
    });

    private void WriteCounters(string type, TrafficConnection connection, TrafficCounters counters, TimeSpan duration)
    {
        var lan = connection.Channel == TrafficChannel.Lan;
        Write(new
        {
            type,
            timestamp = DateTimeOffset.UtcNow,
            connection = connection.Id,
            channel = ChannelName(connection.Channel),
            role = connection.Role?.ToString(),
            peerCorrelationId = connection.PeerCorrelationId,
            durationMs = Math.Round(duration.TotalMilliseconds),
            inbound = counters.Messages(TrafficDirection.Inbound),
            outbound = counters.Messages(TrafficDirection.Outbound),
            orderedPeak = lan ? counters.OrderedPeak : (int?)null,
            writeGateWaitPeakMs = lan ? (double?)null : Math.Round(TimeSpan.FromTicks(counters.GateWaitPeakTicks).TotalMilliseconds, 3),
            writeGateWaitTotalMs = lan ? (double?)null : Math.Round(TimeSpan.FromTicks(counters.GateWaitTotalTicks).TotalMilliseconds, 3),
            replacedPoses = counters.ReplacedPoses,
            replacedBodies = counters.ReplacedBodies,
            traceDropped = counters.TraceDropped
        });
    }

    private void Write(object record)
    {
        if (_failed) return;
        try
        {
            var line = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
            _output!.Write(line);
            _output.WriteByte((byte)'\n');
            _bytesWritten += line.Length + 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Fail(exception);
        }
    }

    private void Flush()
    {
        if (_failed) return;
        try { _output!.Flush(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Fail(exception);
        }
    }

    // Like the main log's own fallback: the Game Peer carries on, recording nothing more.
    private void Fail(Exception exception)
    {
        _failed = true;
        _statistics = false;
        _trace = false;
        _recording = false;
        _warn($"Could not write the traffic file{(FilePath is null ? "" : $" {FilePath}")}: {exception.Message} " +
            "Traffic Statistics and the Traffic Trace are off for the rest of this run.");
        try { _output!.Dispose(); }
        catch (Exception disposeException) when (disposeException is IOException or UnauthorizedAccessException) { }
    }

    private static string ChannelName(TrafficChannel channel) => channel == TrafficChannel.Lan ? "lan" : "game";

    internal static string DirectionName(TrafficDirection direction) => direction == TrafficDirection.Inbound ? "inbound" : "outbound";
}

internal enum TrafficEventKind { Trace, Opened, Admitted, Closed }

internal readonly record struct TrafficEvent(
    TrafficEventKind Kind,
    TrafficConnection Connection,
    DateTimeOffset Timestamp,
    TrafficDirection Direction = default,
    string? MessageKind = null,
    int Bytes = 0,
    string? Reason = null,
    RelayFailureCategory Failure = RelayFailureCategory.None);

// One connection's counters: the local game, or one other Game Peer.
public sealed class TrafficConnection
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly TrafficRecorder _recorder;
    private readonly Lock _gate = new();
    // Zeroed in place each interval, so counting a known kind never allocates.
    private readonly Dictionary<(TrafficDirection Direction, string Kind), TrafficTally> _interval = [];
    private int _orderedPeak;
    private long _gateWaitPeakTicks;
    private long _gateWaitTotalTicks;
    private long _replacedPoses;
    private long _replacedBodies;
    private long _traceDropped;
    private Guid? _peerCorrelationId;
    private int _closed;

    internal TrafficConnection(TrafficRecorder recorder, int id, TrafficChannel channel, RelayRole? role, Guid? peerCorrelationId)
    {
        _recorder = recorder;
        Id = id;
        Channel = channel;
        Role = role;
        _peerCorrelationId = peerCorrelationId;
    }

    // Records nothing, like every connection of the disabled recorder.
    public static TrafficConnection Disabled { get; } = TrafficRecorder.Disabled.OpenConnection(TrafficChannel.LocalGame);

    public int Id { get; }
    public TrafficChannel Channel { get; }
    public RelayRole? Role { get; }

    public Guid? PeerCorrelationId { get { lock (_gate) return _peerCorrelationId; } }

    // The one check a hot path makes before anything else.
    public bool IsRecording => _recorder.IsRecording;

    // Only the writer touches these.
    internal long OpenedAt { get; set; }
    internal long IntervalStart { get; set; }
    internal TrafficCounters Totals { get; } = new();

    public void Identify(Guid peerCorrelationId)
    {
        lock (_gate) _peerCorrelationId = peerCorrelationId;
    }

    public void Admitted()
    {
        if (IsRecording) _recorder.Enqueue(new(TrafficEventKind.Admitted, this, DateTimeOffset.UtcNow));
    }

    // Only the first close counts.
    public void Close(string reason, RelayFailureCategory failure = RelayFailureCategory.None)
    {
        if (!IsRecording || Interlocked.Exchange(ref _closed, 1) != 0) return;
        _recorder.Enqueue(new(TrafficEventKind.Closed, this, DateTimeOffset.UtcNow, Reason: reason, Failure: failure));
    }

    // Framed bytes: the payload and its 4-byte length prefix.
    public void RecordLan(TrafficDirection direction, LanMessage message, int framedBytes)
    {
        if (!IsRecording) return;
        Record(direction, TrafficKinds.Of(message), framedBytes);
    }

    // Line bytes, including the LF.
    public void RecordGameLine(TrafficDirection direction, string line)
    {
        if (!IsRecording) return;
        Record(direction, TrafficKinds.OfGameLine(direction, line), Utf8.GetByteCount(line) + 1);
    }

    public void ObserveOrderedFill(int fill)
    {
        if (!_recorder.IsStatisticsOn) return;
        lock (_gate) _orderedPeak = Math.Max(_orderedPeak, fill);
    }

    public void AddWriteGateWait(TimeSpan wait)
    {
        if (!_recorder.IsStatisticsOn) return;
        lock (_gate)
        {
            _gateWaitTotalTicks += wait.Ticks;
            _gateWaitPeakTicks = Math.Max(_gateWaitPeakTicks, wait.Ticks);
        }
    }

    public void CountReplacedPose()
    {
        if (!_recorder.IsStatisticsOn) return;
        lock (_gate) _replacedPoses++;
    }

    public void CountReplacedBodies()
    {
        if (!_recorder.IsStatisticsOn) return;
        lock (_gate) _replacedBodies++;
    }

    private void Record(TrafficDirection direction, string kind, int bytes)
    {
        if (_recorder.IsStatisticsOn)
        {
            lock (_gate)
            {
                if (!_interval.TryGetValue((direction, kind), out var tally)) _interval[(direction, kind)] = tally = new();
                tally.Count++;
                tally.Bytes += bytes;
            }
        }
        if (_recorder.IsTraceOn &&
            !_recorder.TryEnqueueTrace(new(TrafficEventKind.Trace, this, DateTimeOffset.UtcNow, direction, kind, bytes)))
        {
            lock (_gate) _traceDropped++;
        }
    }

    // Called by the writer: returns the interval's counters, adds them to the totals, and starts the next interval.
    internal TrafficCounters TakeInterval()
    {
        var counters = new TrafficCounters();
        lock (_gate)
        {
            foreach (var (key, tally) in _interval)
            {
                if (tally.Count == 0) continue;
                counters.Add(key.Direction, key.Kind, tally.Count, tally.Bytes);
                tally.Count = 0;
                tally.Bytes = 0;
            }
            counters.OrderedPeak = _orderedPeak;
            counters.GateWaitPeakTicks = _gateWaitPeakTicks;
            counters.GateWaitTotalTicks = _gateWaitTotalTicks;
            counters.ReplacedPoses = _replacedPoses;
            counters.ReplacedBodies = _replacedBodies;
            counters.TraceDropped = _traceDropped;
            _orderedPeak = 0;
            _gateWaitPeakTicks = _gateWaitTotalTicks = 0;
            _replacedPoses = _replacedBodies = _traceDropped = 0;
        }
        Totals.Accumulate(counters);
        return counters;
    }

    private sealed class TrafficTally
    {
        public long Count;
        public long Bytes;
    }
}

internal sealed class TrafficCounters
{
    private readonly Dictionary<(TrafficDirection Direction, string Kind), (long Count, long Bytes)> _messages = [];

    public int OrderedPeak { get; set; }
    public long GateWaitPeakTicks { get; set; }
    public long GateWaitTotalTicks { get; set; }
    public long ReplacedPoses { get; set; }
    public long ReplacedBodies { get; set; }
    public long TraceDropped { get; set; }

    public void Add(TrafficDirection direction, string kind, long count, long bytes)
    {
        var current = _messages.GetValueOrDefault((direction, kind));
        _messages[(direction, kind)] = (current.Count + count, current.Bytes + bytes);
    }

    // Peaks are the highest of any interval; everything else adds up.
    public void Accumulate(TrafficCounters interval)
    {
        foreach (var (key, value) in interval._messages) Add(key.Direction, key.Kind, value.Count, value.Bytes);
        OrderedPeak = Math.Max(OrderedPeak, interval.OrderedPeak);
        GateWaitPeakTicks = Math.Max(GateWaitPeakTicks, interval.GateWaitPeakTicks);
        GateWaitTotalTicks += interval.GateWaitTotalTicks;
        ReplacedPoses += interval.ReplacedPoses;
        ReplacedBodies += interval.ReplacedBodies;
        TraceDropped += interval.TraceDropped;
    }

    public SortedDictionary<string, object> Messages(TrafficDirection direction) => new(
        _messages.Where(entry => entry.Key.Direction == direction)
            .ToDictionary(entry => entry.Key.Kind, entry => (object)new { count = entry.Value.Count, bytes = entry.Value.Bytes }),
        StringComparer.Ordinal);
}

// Message kinds are protocol names only: never a field that could carry chat text, a map path, or a name.
public static class TrafficKinds
{
    public const string Other = "other";
    private const int MaximumGameKinds = 256;
    private const int MaximumKeywordLength = 32;
    private static readonly ConcurrentDictionary<string, string> GameKinds = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> GameKindLookup =
        GameKinds.GetAlternateLookup<ReadOnlySpan<char>>();

    // The LanProtocol wire type of each message.
    public static string Of(LanMessage message) => message switch
    {
        LanMessage.JoinRequest => "join-request",
        LanMessage.AdmissionAccepted => "admission-accepted",
        LanMessage.AdmissionRejected => "admission-rejected",
        LanMessage.ChatEntry => "chat-entry",
        LanMessage.Departure => "departure",
        LanMessage.Heartbeat => "heartbeat",
        LanMessage.CustomStoryStarted => "custom-story-started",
        LanMessage.CustomStoryStartOutcome => "custom-story-start-outcome",
        LanMessage.MainStoryStarted => "main-story-started",
        LanMessage.MainStoryStartOutcome => "main-story-start-outcome",
        LanMessage.Pose => "pose",
        LanMessage.EnemyPose => "enemy-pose",
        LanMessage.EnemyAttack => "enemy-attack",
        LanMessage.EnemyDoorBreak => "enemy-door-break",
        LanMessage.EnemyDisappearance => "enemy-disappearance",
        LanMessage.EnemyBite => "enemy-bite",
        LanMessage.PlayerDeath => "player-death",
        LanMessage.SharedDeath => "shared-death",
        LanMessage.Claim => "claim",
        LanMessage.ClaimDenied => "claim-denied",
        LanMessage.Interaction => "interaction",
        LanMessage.Broke => "broke",
        LanMessage.Settled => "settled",
        LanMessage.Bodies => "bodies",
        LanMessage.SharedCallback => "shared-callback",
        LanMessage.ButtonState => "button-state",
        LanMessage.LampState => "lamp-state",
        _ => Other
    };

    // A Command the Game Peer wrote is named by its first word. A line from the local game is named by its prefix and
    // keyword, such as "STATE localpose" or "EVENT:CHAT", and only when it starts with one of the protocol's prefixes.
    public static string OfGameLine(TrafficDirection direction, string line)
    {
        var span = line.AsSpan();
        var length = KeywordLength(span);
        if (length == 0) return Other;
        if (direction == TrafficDirection.Inbound)
        {
            if (span[..length] is not ("EVENT" or "RESPONSE" or "STATE" or "WARNING") || length == span.Length) return Other;
            var keyword = KeywordLength(span[(length + 1)..]);
            if (keyword == 0) return Other;
            length += 1 + keyword;
        }
        return Intern(span[..length]);
    }

    private static int KeywordLength(ReadOnlySpan<char> span)
    {
        var length = 0;
        while (length < span.Length && span[length] is not (' ' or ':'))
        {
            if (!char.IsAsciiLetterOrDigit(span[length]) && span[length] is not ('-' or '_')) return 0;
            if (++length > MaximumKeywordLength) return 0;
        }
        return length;
    }

    // Each kind is allocated once; a misbehaving game cannot grow the set without bound.
    private static string Intern(ReadOnlySpan<char> kind)
    {
        if (GameKindLookup.TryGetValue(kind, out var interned)) return interned;
        if (GameKinds.Count >= MaximumGameKinds) return Other;
        var created = kind.ToString();
        return GameKinds.GetOrAdd(created, created);
    }
}
