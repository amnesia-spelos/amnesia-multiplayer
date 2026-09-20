using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class GamePeerLogTests : IDisposable
{
    private static readonly DateTime StartedAt = new(2026, 9, 19, 14, 3, 7);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"multimnesia-logs-{Guid.NewGuid():N}");
    private readonly StringWriter _console = new();
    private readonly StringWriter _consoleErrors = new();

    private string LogsDirectory => Path.Combine(_root, "logs");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private GamePeerLog Open(RelaySeverity minimumSeverity = RelaySeverity.Information, DateTime? startedAt = null) =>
        GamePeerLog.Open(LogsDirectory, startedAt ?? StartedAt, minimumSeverity, _console, _consoleErrors);

    private string ReadLogFile(string name = "game-peer-20260919-140307.log") =>
        File.ReadAllText(Path.Combine(LogsDirectory, name));

    [Fact]
    public void Each_run_creates_a_log_file_named_after_its_start_time()
    {
        using var log = Open();

        Assert.Equal(["game-peer-20260919-140307.log"], Directory.GetFiles(LogsDirectory).Select(Path.GetFileName));
        Assert.Equal(Path.Combine(LogsDirectory, "game-peer-20260919-140307.log"), log.FilePath);
    }

    [Fact]
    public void The_log_file_starts_with_the_version_protocol_and_OS_header()
    {
        using (Open()) { }

        var lines = ReadLogFile().Split(Environment.NewLine);
        Assert.Equal($"Game Peer: {GamePeerVersion.DisplayName} ({GamePeerVersion.InformationalVersion})", lines[0]);
        Assert.Equal($"LanProtocol version: {LanProtocol.CurrentVersion}", lines[1]);
        Assert.Equal($"OS: {Environment.OSVersion}", lines[2]);
    }

    [Fact]
    public void The_file_receives_the_same_entries_as_the_console_at_the_configured_level()
    {
        using (var log = Open(RelaySeverity.Information))
        {
            log.Write(RelaySeverity.Debug, "hidden");
            log.Write(RelaySeverity.Information, "shown");
            log.Write(RelaySeverity.Error, "failed");
        }

        var file = ReadLogFile();
        Assert.DoesNotContain("hidden", file);
        Assert.Contains("shown", file);
        Assert.Contains("failed", file);
        Assert.DoesNotContain("hidden", _console.ToString());
        Assert.Contains("shown", _console.ToString());
        Assert.Contains("failed", _consoleErrors.ToString());
    }

    [Fact]
    public void Only_the_newest_ten_log_files_are_kept_at_startup()
    {
        Directory.CreateDirectory(LogsDirectory);
        var older = Enumerable.Range(1, 12)
            .Select(day => $"game-peer-202609{day:00}-120000.log")
            .ToArray();
        foreach (var name in older) File.WriteAllText(Path.Combine(LogsDirectory, name), "old");
        File.WriteAllText(Path.Combine(LogsDirectory, "notes.txt"), "not a log");

        using (Open()) { }

        var expected = older.Skip(3).Append("game-peer-20260919-140307.log").Append("notes.txt").Order();
        Assert.Equal(expected, Directory.GetFiles(LogsDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void An_unwritable_log_folder_falls_back_to_the_console_and_says_so()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(LogsDirectory, "a file where the logs folder should be");

        using var log = Open();
        log.Write(RelaySeverity.Information, "still logged");

        Assert.Null(log.FilePath);
        Assert.Contains("console only", _consoleErrors.ToString());
        Assert.Contains("still logged", _console.ToString());
    }

    [Fact]
    public void A_log_file_that_cannot_be_opened_falls_back_to_the_console_and_says_so()
    {
        Directory.CreateDirectory(Path.Combine(LogsDirectory, "game-peer-20260919-140307.log"));

        using var log = Open();
        log.Write(RelaySeverity.Information, "still logged");

        Assert.Null(log.FilePath);
        Assert.Contains("console only", _consoleErrors.ToString());
        Assert.Contains("still logged", _console.ToString());
    }
}
