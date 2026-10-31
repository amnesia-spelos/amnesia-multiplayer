using System.Text.Json;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class TrafficRecorderTests
{
    private static readonly LanMessage.Pose Pose = new(1, 0, 1, 2, 3, 0, 0, false, false, 0, "maps/secret_cellar.map");
    private readonly List<string> _warnings = [];

    private TrafficRecorder Start(Stream output, bool statistics = true, bool trace = false, long traceMaxBytes = 1024 * 1024,
        int traceCapacity = 1024) =>
        TrafficRecorder.Start(output, new TrafficOptions
        {
            Statistics = statistics,
            Trace = trace,
            TraceMaxBytes = traceMaxBytes,
            TraceCapacity = traceCapacity,
            StatisticsInterval = TimeSpan.FromMilliseconds(50)
        }, warning => { lock (_warnings) _warnings.Add(warning); });

    [Fact]
    public async Task Statistics_count_messages_and_bytes_per_kind_and_direction_and_the_summary_totals_them()
    {
        var output = new MemoryStream();
        var recorder = Start(output);
        var connection = recorder.OpenConnection(TrafficChannel.Lan, RelayRole.Joining, Guid.NewGuid());

        connection.RecordLan(TrafficDirection.Outbound, Pose, 100);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        connection.RecordLan(TrafficDirection.Outbound, Pose, 110);
        connection.RecordLan(TrafficDirection.Inbound, new LanMessage.Heartbeat(), 25);
        connection.Close("disconnected");
        await recorder.DisposeAsync();

        var file = TrafficFile.Parse(output);
        Assert.True(file.Statistics(connection.Id).Count >= 2);
        Assert.Equal(new TrafficCounts(2, 210), file.StatisticsTotal(connection.Id, "outbound", "pose"));
        Assert.Equal(new TrafficCounts(1, 25), file.StatisticsTotal(connection.Id, "inbound", "heartbeat"));
        var summary = Assert.Single(file.Records("summary"));
        Assert.Equal(new TrafficCounts(2, 210), TrafficFile.Counts(summary, "outbound", "pose"));
        Assert.Equal(new TrafficCounts(1, 25), TrafficFile.Counts(summary, "inbound", "heartbeat"));
        Assert.Equal(["opened", "closed"], file.Records("connection").Select(record => record.GetProperty("event").GetString()));
        Assert.All(file.All, record => Assert.True(record.TryGetProperty("timestamp", out _)));
    }

    [Fact]
    public async Task Still_open_connections_get_a_summary_at_shutdown()
    {
        var output = new MemoryStream();
        var recorder = Start(output);
        var game = recorder.OpenConnection(TrafficChannel.LocalGame);
        game.RecordGameLine(TrafficDirection.Outbound, "chatentry - - Bob:hello");

        await recorder.DisposeAsync();

        var summary = Assert.Single(TrafficFile.Parse(output).Records("summary"));
        Assert.Equal(new TrafficCounts(1, 24), TrafficFile.Counts(summary, "outbound", "chatentry"));
    }

    [Fact]
    public async Task Trace_records_give_direction_kind_and_size_but_never_content()
    {
        var output = new MemoryStream();
        var recorder = Start(output, statistics: false, trace: true);
        var lan = recorder.OpenConnection(TrafficChannel.Lan, RelayRole.Host);
        var game = recorder.OpenConnection(TrafficChannel.LocalGame);

        lan.RecordLan(TrafficDirection.Inbound, new LanMessage.ChatEntry("Bob", "the password is swordfish"), 80);
        lan.RecordLan(TrafficDirection.Outbound, Pose, 120);
        game.RecordGameLine(TrafficDirection.Inbound, "EVENT:CHAT:Bob:the password is swordfish");
        game.RecordGameLine(TrafficDirection.Outbound, "chatentry - - Bob:the password is swordfish");
        game.RecordGameLine(TrafficDirection.Inbound, "STATE localpose 1 0 1 2 3 0 0 0 0 maps/secret_cellar.map");
        await recorder.DisposeAsync();

        var file = TrafficFile.Parse(output);
        var traces = file.Records("trace").Select(record => (
            record.GetProperty("connection").GetInt32(), record.GetProperty("direction").GetString(),
            record.GetProperty("kind").GetString(), record.GetProperty("bytes").GetInt32())).ToArray();
        Assert.Equal([
            (lan.Id, "inbound", "chat-entry", 80),
            (lan.Id, "outbound", "pose", 120),
            (game.Id, "inbound", "EVENT:CHAT", 41),
            (game.Id, "outbound", "chatentry", 44),
            (game.Id, "inbound", "STATE localpose", 57)], traces);
        Assert.Empty(file.Records("statistics"));
        Assert.DoesNotContain("swordfish", file.Text);
        Assert.DoesNotContain("secret_cellar", file.Text);
    }

    [Theory]
    [InlineData("Welcome to the Amnesia TCP server!", "other")]
    [InlineData("RESPONSE avatarpose ok", "RESPONSE avatarpose")]
    [InlineData("RESPONSE:startcustomstory:not found", "RESPONSE:startcustomstory")]
    [InlineData("WARNING:Unknown command", "WARNING:Unknown")]
    [InlineData("EVENT mapentered 2 maps/secret_cellar.map", "EVENT mapentered")]
    public void Game_lines_from_the_local_game_are_named_by_their_protocol_keywords_only(string line, string kind)
    {
        Assert.Equal(kind, TrafficKinds.OfGameLine(TrafficDirection.Inbound, line));
    }

    [Fact]
    public void Main_story_messages_are_named_by_their_wire_types()
    {
        Assert.Equal("main-story-started", TrafficKinds.Of(new LanMessage.MainStoryStarted()));
        Assert.Equal("main-story-start-outcome", TrafficKinds.Of(new LanMessage.MainStoryStartOutcome(SharedStoryStartOutcome.Started)));
    }

    [Fact]
    public async Task Without_either_feature_nothing_is_written()
    {
        var output = new MemoryStream();
        var recorder = Start(output, statistics: false, trace: false);
        var connection = recorder.OpenConnection(TrafficChannel.LocalGame);

        connection.RecordGameLine(TrafficDirection.Outbound, "ping");
        connection.Close("ended");
        await recorder.DisposeAsync();

        Assert.False(connection.IsRecording);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task Reaching_the_trace_size_limit_stops_trace_records_once_with_one_warning_and_statistics_carry_on()
    {
        var output = new MemoryStream();
        var recorder = Start(output, trace: true, traceMaxBytes: 2_000);
        var connection = recorder.OpenConnection(TrafficChannel.Lan, RelayRole.Host);

        for (var i = 0; i < 100; i++) connection.RecordLan(TrafficDirection.Outbound, new LanMessage.Heartbeat(), 25);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        for (var i = 0; i < 100; i++) connection.RecordLan(TrafficDirection.Outbound, new LanMessage.Heartbeat(), 25);
        await recorder.DisposeAsync();

        var file = TrafficFile.Parse(output);
        Assert.InRange(file.Records("trace").Count, 1, 99);
        Assert.False(recorder.IsTraceOn);
        Assert.Equal(new TrafficCounts(200, 5_000), file.StatisticsTotal(connection.Id, "outbound", "heartbeat"));
        lock (_warnings) Assert.Single(_warnings, warning => warning.Contains("Traffic Trace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_full_trace_queue_drops_and_counts_records_without_blocking_the_recording_side()
    {
        var output = new StallingStream();
        var recorder = Start(output, trace: true, traceCapacity: 10);
        var connection = recorder.OpenConnection(TrafficChannel.Lan, RelayRole.Host);
        await output.Stalled.WaitAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 50; i++) connection.RecordLan(TrafficDirection.Outbound, new LanMessage.Heartbeat(), 25);
        output.Release();
        await recorder.DisposeAsync();

        var file = TrafficFile.Parse(output.Written);
        var dropped = file.Statistics(connection.Id).Sum(record => record.GetProperty("traceDropped").GetInt64());
        Assert.Equal(50, file.Records("trace").Count + dropped);
        Assert.InRange(dropped, 40, 50);
        Assert.Equal(dropped, Assert.Single(file.Records("summary")).GetProperty("traceDropped").GetInt64());
    }

    [Fact]
    public async Task An_unwritable_traffic_file_turns_both_features_off_with_a_warning()
    {
        var recorder = Start(new FailingStream(), trace: true);
        var connection = recorder.OpenConnection(TrafficChannel.LocalGame);

        await WaitUntilAsync(() => !connection.IsRecording);
        connection.RecordGameLine(TrafficDirection.Outbound, "ping");
        await recorder.DisposeAsync();

        Assert.False(recorder.IsStatisticsOn);
        Assert.False(recorder.IsTraceOn);
        lock (_warnings) Assert.Single(_warnings, warning => warning.Contains("traffic file", StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class TrafficFileTests : IDisposable
{
    private static readonly DateTime StartedAt = new(2026, 9, 19, 14, 3, 7);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"multimnesia-traffic-{Guid.NewGuid():N}");
    private readonly List<string> _warnings = [];

    private string LogsDirectory => Path.Combine(_root, "logs");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private TrafficRecorder Open(bool statistics = true, bool trace = false) =>
        TrafficRecorder.Open(LogsDirectory, StartedAt, new TrafficOptions { Statistics = statistics, Trace = trace },
            warning => _warnings.Add(warning));

    [Fact]
    public async Task The_traffic_file_is_named_after_the_run_start_like_the_main_log()
    {
        var recorder = Open();
        await recorder.DisposeAsync();

        var path = Path.Combine(LogsDirectory, "traffic-20260919-140307.jsonl");
        Assert.Equal(path, recorder.FilePath);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task With_neither_feature_no_traffic_file_is_created()
    {
        var recorder = Open(statistics: false, trace: false);
        await recorder.DisposeAsync();

        Assert.Null(recorder.FilePath);
        Assert.False(Directory.Exists(LogsDirectory));
    }

    [Fact]
    public async Task Only_the_newest_ten_traffic_files_are_kept()
    {
        Directory.CreateDirectory(LogsDirectory);
        var older = Enumerable.Range(1, 12).Select(day => $"traffic-202609{day:00}-120000.jsonl").ToArray();
        foreach (var name in older) File.WriteAllText(Path.Combine(LogsDirectory, name), "old");
        File.WriteAllText(Path.Combine(LogsDirectory, "game-peer-20260901-120000.log"), "a log");

        var recorder = Open();
        await recorder.DisposeAsync();

        var expected = older.Skip(3).Append("traffic-20260919-140307.jsonl").Append("game-peer-20260901-120000.log").Order();
        Assert.Equal(expected, Directory.GetFiles(LogsDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_traffic_file_that_cannot_be_created_degrades_to_a_warning()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(LogsDirectory, "a file where the directory should be");

        var recorder = Open(trace: true);
        var connection = recorder.OpenConnection(TrafficChannel.LocalGame);
        connection.RecordGameLine(TrafficDirection.Outbound, "ping");
        await recorder.DisposeAsync();

        Assert.False(connection.IsRecording);
        Assert.Null(recorder.FilePath);
        Assert.Single(_warnings, warning => warning.Contains("traffic file", StringComparison.Ordinal));
    }
}

public readonly record struct TrafficCounts(long Count, long Bytes);

public sealed class TrafficFile(string text)
{
    public string Text { get; } = text;

    public IReadOnlyList<JsonElement> All { get; } = text
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement)
        .ToArray();

    public static TrafficFile Parse(MemoryStream output) => new(System.Text.Encoding.UTF8.GetString(output.ToArray()));

    public static TrafficFile Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return new(reader.ReadToEnd());
    }

    public List<JsonElement> Records(string type) => All.Where(record => record.GetProperty("type").GetString() == type).ToList();

    public List<JsonElement> Statistics(int connection) =>
        Records("statistics").Where(record => record.GetProperty("connection").GetInt32() == connection).ToList();

    public TrafficCounts StatisticsTotal(int connection, string direction, string kind) =>
        Statistics(connection).Select(record => Counts(record, direction, kind))
            .Aggregate(new TrafficCounts(0, 0), (sum, counts) => new(sum.Count + counts.Count, sum.Bytes + counts.Bytes));

    public static TrafficCounts Counts(JsonElement record, string direction, string kind) =>
        record.GetProperty(direction).TryGetProperty(kind, out var counts)
            ? new(counts.GetProperty("count").GetInt64(), counts.GetProperty("bytes").GetInt64())
            : new(0, 0);
}

// Blocks the first write until released, like a disk that stops answering.
public sealed class StallingStream : Stream
{
    private readonly ManualResetEventSlim _released = new();
    private readonly SemaphoreSlim _stalled = new(0, 1);
    private int _signalled;

    public MemoryStream Written { get; } = new();

    public SemaphoreSlim Stalled => _stalled;

    public void Release() => _released.Set();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Interlocked.Exchange(ref _signalled, 1) == 0) _stalled.Release();
        _released.Wait();
        lock (Written) Written.Write(buffer, offset, count);
    }

    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public sealed class FailingStream : Stream
{
    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The disk is full.");
    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
