using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed record OutboundMessage(LanMessage Message, TaskCompletionSource? Sent = null);

// The frames for one admitted Game Peer: session messages in order, then the newest Pose whenever none are waiting.
public sealed class LanOutbound(int capacity)
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
    private LanMessage.Pose? _pose;

    // False when the session messages are full or the outbound is complete.
    public bool TryWrite(OutboundMessage message)
    {
        if (!_messages.Writer.TryWrite(message)) return false;
        _wake.Writer.TryWrite(true);
        return true;
    }

    // Latest-wins: replaces any Pose not yet sent.
    public void OfferPose(LanMessage.Pose pose)
    {
        Volatile.Write(ref _pose, pose);
        _wake.Writer.TryWrite(true);
    }

    public void Complete()
    {
        _messages.Writer.TryComplete();
        _wake.Writer.TryComplete();
    }

    // Single reader. Ends once complete and the session messages are sent; an unsent Pose is dropped.
    public async IAsyncEnumerable<OutboundMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_messages.Reader.TryRead(out var message)) { yield return message; continue; }
            if (_messages.Reader.Completion.IsCompleted) yield break;
            if (Interlocked.Exchange(ref _pose, null) is { } pose) { yield return new(pose); continue; }
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
