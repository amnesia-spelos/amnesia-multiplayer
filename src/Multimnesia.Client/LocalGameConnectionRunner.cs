namespace Multimnesia.Client;

public enum ConnectionLogSeverity { Information, Warning }
public enum GameConnectionEvent { ConnectionFailed, ConnectionRetry, ConnectionRecovered }

public sealed record GameConnectionStatus(
    DateTimeOffset Timestamp,
    ConnectionLogSeverity Severity,
    GameConnectionEvent Event,
    int Attempt);

public static class LocalGameReconnectPolicy
{
    public static TimeSpan DelayForAttempt(int attempt, TimeSpan initialBackoff, TimeSpan maximumBackoff)
    {
        var multiplier = Math.Pow(2, Math.Min(Math.Max(attempt, 1) - 1, 30));
        return TimeSpan.FromTicks((long)Math.Min(initialBackoff.Ticks * multiplier, maximumBackoff.Ticks));
    }
}

public static class LocalGameSocket
{
    public static async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        // Poses and chat are small lines; Nagle's algorithm would hold them back.
        var game = new System.Net.Sockets.TcpClient { NoDelay = true };
        try { await game.ConnectAsync(host, port, cancellationToken); return game.GetStream(); }
        catch { game.Dispose(); throw; }
    }
}

public sealed class LocalGameConnector(
    Func<CancellationToken, ValueTask<Stream>> connect,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<GameConnectionStatus> report,
    TimeSpan initialBackoff,
    TimeSpan maximumBackoff)
{
    public async Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var stream = await connect(cancellationToken);
                if (failures > 0) Report(ConnectionLogSeverity.Information, GameConnectionEvent.ConnectionRecovered, failures);
                return stream;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
            {
                failures++;
                if (failures == 1) Report(ConnectionLogSeverity.Warning, GameConnectionEvent.ConnectionFailed, failures);
                else if (failures % 5 == 0) Report(ConnectionLogSeverity.Information, GameConnectionEvent.ConnectionRetry, failures);
            }

            if (cancellationToken.IsCancellationRequested) break;
            var backoff = BoundedBackoff(failures);
            try { await delay(backoff, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
        return null;
    }

    private TimeSpan BoundedBackoff(int attempt)
        => LocalGameReconnectPolicy.DelayForAttempt(attempt, initialBackoff, maximumBackoff);

    private void Report(ConnectionLogSeverity severity, GameConnectionEvent eventName, int attempt) =>
        report(new(DateTimeOffset.UtcNow, severity, eventName, attempt));
}
