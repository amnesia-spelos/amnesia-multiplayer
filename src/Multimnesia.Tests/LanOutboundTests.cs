using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class LanOutboundTests
{
    private static LanMessage.Pose PoseAt(ulong timeMs) => new(timeMs, 0, 1, 2, 3, 90, 0, false, false, "maps/a.map");

    [Fact]
    public async Task An_unsent_Pose_is_replaced_by_a_newer_one()
    {
        var outbound = new LanOutbound(capacity: 8);
        await using var frames = outbound.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        for (ulong time = 1; time <= 100; time++) outbound.OfferPose(PoseAt(time));

        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(PoseAt(100), frames.Current.Message);
        outbound.OfferPose(PoseAt(101));
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(PoseAt(101), frames.Current.Message);
    }

    [Fact]
    public async Task Session_messages_are_sent_in_order_ahead_of_an_unsent_Pose()
    {
        var outbound = new LanOutbound(capacity: 8);
        await using var frames = outbound.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(outbound.TryWrite(new(new LanMessage.ChatEntry("Host", "first"))));
        outbound.OfferPose(PoseAt(1));
        Assert.True(outbound.TryWrite(new(new LanMessage.Heartbeat())));
        outbound.OfferPose(PoseAt(2));
        Assert.True(outbound.TryWrite(new(new LanMessage.ChatEntry("Host", "second"))));

        var sent = new List<LanMessage>();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await frames.MoveNextAsync());
            sent.Add(frames.Current.Message);
        }
        Assert.Equal(
            [new LanMessage.ChatEntry("Host", "first"), new LanMessage.Heartbeat(), new LanMessage.ChatEntry("Host", "second"), PoseAt(2)],
            sent);
    }

    [Fact]
    public void Poses_never_use_the_session_message_capacity()
    {
        var outbound = new LanOutbound(capacity: 2);

        for (ulong time = 1; time <= 100; time++) outbound.OfferPose(PoseAt(time));

        Assert.True(outbound.TryWrite(new(new LanMessage.Heartbeat())));
        Assert.True(outbound.TryWrite(new(new LanMessage.Heartbeat())));
        Assert.False(outbound.TryWrite(new(new LanMessage.Heartbeat())));
    }

    [Fact]
    public async Task Completing_ends_the_frames_after_the_queued_session_messages()
    {
        var outbound = new LanOutbound(capacity: 8);
        Assert.True(outbound.TryWrite(new(new LanMessage.Departure())));
        outbound.OfferPose(PoseAt(1));

        outbound.Complete();
        var sent = new List<LanMessage>();
        await foreach (var frame in outbound.ReadAllAsync(TestContext.Current.CancellationToken)) sent.Add(frame.Message);

        Assert.Equal([new LanMessage.Departure()], sent);
        Assert.False(outbound.TryWrite(new(new LanMessage.Heartbeat())));
    }
}
