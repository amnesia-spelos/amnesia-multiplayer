using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class LocalGameConnectionTests
{
    [Fact]
    public void Repeated_local_game_losses_use_bounded_exponential_backoff()
    {
        var delays = Enumerable.Range(1, 7)
            .Select(attempt => LocalGameReconnectPolicy.DelayForAttempt(
                attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)))
            .ToArray();

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)],
            delays);
    }

    [Fact]
    public async Task Connection_failures_retry_with_bounded_backoff_and_recover_without_intervention()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var statuses = new List<GameConnectionStatus>();
        using var cancellation = new CancellationTokenSource();
        var connector = new LocalGameConnector(
            _ => attempts++ < 3
                ? ValueTask.FromException<Stream>(new IOException("offline"))
                : ValueTask.FromResult<Stream>(new MemoryStream()),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            statuses.Add,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250));

        await using var stream = await connector.ConnectAsync(cancellation.Token);

        Assert.Equal(4, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(250)], delays);
        Assert.Equal([GameConnectionEvent.ConnectionFailed, GameConnectionEvent.ConnectionRecovered], statuses.Select(status => status.Event));
    }

    [Fact]
    public async Task Repeated_failures_emit_restrained_retry_status()
    {
        var attempts = 0;
        var statuses = new List<GameConnectionStatus>();
        using var cancellation = new CancellationTokenSource();
        var connector = new LocalGameConnector(
            _ =>
            {
                if (++attempts == 7) cancellation.Cancel();
                return ValueTask.FromException<Stream>(new IOException("private detail"));
            },
            (_, _) => Task.CompletedTask,
            statuses.Add,
            TimeSpan.Zero,
            TimeSpan.Zero);

        await connector.ConnectAsync(cancellation.Token);

        Assert.Equal([GameConnectionEvent.ConnectionFailed, GameConnectionEvent.ConnectionRetry], statuses.Select(status => status.Event));
        Assert.Equal(5, statuses[1].Attempt);
    }
}
