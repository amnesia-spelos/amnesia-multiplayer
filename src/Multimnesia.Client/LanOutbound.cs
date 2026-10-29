using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed record OutboundMessage(LanMessage Message, TaskCompletionSource? Sent = null);

// The frames for one admitted Game Peer: session messages in order, then the newest Pose and bodies whenever none are waiting.
public sealed class LanOutbound(int capacity, TrafficConnection? traffic = null)
{
    private readonly Channel<OutboundMessage> _messages = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    // Holds at most one wake-up; the reader rechecks both sources after each.
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly TrafficConnection _traffic = traffic ?? TrafficConnection.Disabled;
    private readonly Dictionary<(string Map, int EntityId), LanMessage.EnemyPose> _enemyPoses = [];
    private readonly Queue<(string Map, int EntityId)> _enemyOrder = [];
    private LanMessage.Pose? _pose;
    private LanMessage.Bodies? _bodies;

    // False when the session messages are full or the outbound is complete.
    public bool TryWrite(OutboundMessage message)
    {
        if (!_messages.Writer.TryWrite(message)) return false;
        if (_traffic.IsRecording) _traffic.ObserveOrderedFill(_messages.Reader.Count);
        _wake.Writer.TryWrite(true);
        return true;
    }

    // Latest-wins: replaces any Pose not yet sent.
    public void OfferPose(LanMessage.Pose pose)
    {
        if (Interlocked.Exchange(ref _pose, pose) is not null) _traffic.CountReplacedPose();
        _wake.Writer.TryWrite(true);
    }

    // Latest-wins, beside the Pose: replaces any bodies not yet sent.
    public void OfferBodies(LanMessage.Bodies bodies)
    {
        if (Interlocked.Exchange(ref _bodies, bodies) is not null) _traffic.CountReplacedBodies();
        _wake.Writer.TryWrite(true);
    }

    // One newest-only slot per enemy. FIFO across slots keeps a busy enemy from starving the rest.
    public void OfferEnemyPose(LanMessage.EnemyPose pose)
    {
        lock (_enemyPoses)
        {
            var key = (pose.Map, pose.EntityId);
            if (!_enemyPoses.ContainsKey(key)) _enemyOrder.Enqueue(key);
            _enemyPoses[key] = pose;
        }
        _wake.Writer.TryWrite(true);
    }

    private LanMessage.EnemyPose? TakeEnemyPose()
    {
        lock (_enemyPoses)
        {
            if (!_enemyOrder.TryDequeue(out var key)) return null;
            var pose = _enemyPoses[key];
            _enemyPoses.Remove(key);
            return pose;
        }
    }

    public void Complete()
    {
        _messages.Writer.TryComplete();
        _wake.Writer.TryComplete();
    }

    // Single reader. Ends once complete and the session messages are sent; an unsent Pose or bodies are dropped.
    public async IAsyncEnumerable<OutboundMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_messages.Reader.TryRead(out var message)) { yield return message; continue; }
            if (_messages.Reader.Completion.IsCompleted) yield break;
            if (Interlocked.Exchange(ref _pose, null) is { } pose) { yield return new(pose); continue; }
            if (Interlocked.Exchange(ref _bodies, null) is { } bodies) { yield return new(bodies); continue; }
            if (TakeEnemyPose() is { } enemy) { yield return new(enemy); continue; }
            if (!await _wake.Reader.WaitToReadAsync(cancellationToken)) yield break;
            _wake.Reader.TryRead(out _);
        }
    }
}

public static class LanSocket
{
    // Poses and chat are small frames; Nagle's algorithm would hold them back.
    public static TcpClient CreateOutbound() => new(AddressFamily.InterNetwork) { NoDelay = true };

    public static async Task<TcpClient> AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        connection.NoDelay = true;
        return connection;
    }
}
