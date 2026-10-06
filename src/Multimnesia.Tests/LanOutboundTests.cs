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

    private static LanMessage.Bodies BodiesAt(ulong timeMs) =>
        new(timeMs, "maps/a.map", [new(12, 3, new BodyState(1, 2, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0))]);

    [Fact]
    public async Task Unsent_bodies_are_replaced_by_newer_ones_without_replacing_the_Pose()
    {
        var outbound = new LanOutbound(capacity: 8);
        await using var frames = outbound.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        for (ulong time = 1; time <= 100; time++) outbound.OfferBodies(BodiesAt(time));
        outbound.OfferPose(PoseAt(7));

        var sent = new List<LanMessage>();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await frames.MoveNextAsync());
            sent.Add(frames.Current.Message);
        }
        Assert.Equal(2, sent.Count);
        Assert.Contains(BodiesAt(100), sent);
        Assert.Contains(PoseAt(7), sent);
    }

    [Fact]
    public async Task Session_messages_are_sent_in_order_ahead_of_unsent_bodies()
    {
        var outbound = new LanOutbound(capacity: 8);
        await using var frames = outbound.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        outbound.OfferBodies(BodiesAt(1));
        Assert.True(outbound.TryWrite(new(new LanMessage.Claim("maps/a.map", 12, ClaimReason.Interact))));
        Assert.True(outbound.TryWrite(new(new LanMessage.Interaction("maps/a.map", 12, 3, true))));

        var sent = new List<LanMessage>();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await frames.MoveNextAsync());
            sent.Add(frames.Current.Message);
        }
        Assert.Equal(
            [new LanMessage.Claim("maps/a.map", 12, ClaimReason.Interact), new LanMessage.Interaction("maps/a.map", 12, 3, true), BodiesAt(1)],
            sent);
    }

    [Fact]
    public void Bodies_never_use_the_session_message_capacity()
    {
        var outbound = new LanOutbound(capacity: 1);

        for (ulong time = 1; time <= 100; time++) outbound.OfferBodies(BodiesAt(time));

        Assert.True(outbound.TryWrite(new(new LanMessage.Heartbeat())));
        Assert.False(outbound.TryWrite(new(new LanMessage.Heartbeat())));
    }
}
