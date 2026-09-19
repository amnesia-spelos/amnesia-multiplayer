using Multimnesia.Contracts;

namespace Multimnesia.Client;

// The single sink for everything the Game Peer logs: the console, plus this run's log file while it can be written.
public sealed class GamePeerLog : IDisposable
{
    private const int RetainedFiles = 10;
    private const string FilePrefix = "game-peer-";
    private const string FileExtension = ".log";

    private readonly Lock _gate = new();
    private readonly RelaySeverity _minimumSeverity;
    private readonly TextWriter _console;
    private readonly TextWriter _consoleErrors;
    private StreamWriter? _file;

    private GamePeerLog(RelaySeverity minimumSeverity, TextWriter console, TextWriter consoleErrors)
    {
        _minimumSeverity = minimumSeverity;
        _console = console;
        _consoleErrors = consoleErrors;
    }

    // Null while logging to the console only.
    public string? FilePath { get; private set; }

    // Creates this run's file in the directory, keeping only the newest files; falls back to console-only on failure.
    public static GamePeerLog Open(
        string directory, DateTime startedAt, RelaySeverity minimumSeverity, TextWriter console, TextWriter consoleErrors)
    {
        var log = new GamePeerLog(minimumSeverity, console, consoleErrors);
        var path = Path.Combine(directory, $"{FilePrefix}{startedAt:yyyyMMdd-HHmmss}{FileExtension}");
        try
        {
            Directory.CreateDirectory(directory);
            DeleteOldest(directory, keep: RetainedFiles - 1);
            // Shared so the player can open the file while the Game Peer runs.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            log._file = new StreamWriter(stream) { AutoFlush = true };
            log.FilePath = path;
            log._file.WriteLine($"Game Peer: {GamePeerVersion.DisplayName} ({GamePeerVersion.InformationalVersion})");
            log._file.WriteLine($"LanProtocol version: {LanProtocol.CurrentVersion}");
            log._file.WriteLine($"OS: {Environment.OSVersion}");
        }
        catch (Exception exception)
        {
            log.FallBackToConsole(path, exception);
        }
        return log;
    }

    public void Write(RelaySeverity severity, string line)
    {
        if (severity < _minimumSeverity) return;
        lock (_gate)
        {
            (severity == RelaySeverity.Error ? _consoleErrors : _console).WriteLine(line);
            if (_file is null) return;
            try { _file.WriteLine(line); }
            catch (Exception exception)
            {
                FallBackToConsole(FilePath!, exception);
            }
        }
    }

    public void Write(ConnectionLogSeverity severity, string line) => Write(severity switch
    {
        ConnectionLogSeverity.Warning => RelaySeverity.Warning,
        _ => RelaySeverity.Information
    }, line);

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private void FallBackToConsole(string path, Exception exception)
    {
        try { _file?.Dispose(); }
        catch (Exception disposeException) when (disposeException is IOException or UnauthorizedAccessException) { }
        _file = null;
        FilePath = null;
        _consoleErrors.WriteLine($"Could not write the log file {path}: {exception.Message} Logging to the console only.");
    }

    // File names start with their timestamp, so ordinal order is age order.
    private static void DeleteOldest(string directory, int keep)
    {
        var files = Directory.GetFiles(directory, $"{FilePrefix}*{FileExtension}").Order(StringComparer.Ordinal).ToArray();
        foreach (var file in files.Take(Math.Max(files.Length - keep, 0)))
        {
            // A file still open elsewhere is left for a later run.
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
