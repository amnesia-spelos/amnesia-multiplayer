using System.Buffers.Binary;
using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class EnemyPosesProtocolTests
{
    public static LanMessage.EnemyPose Sample => new("maps/a.map", 139, 1000, 0, -2, 0, 28.25, 90, "Walk", 0.2, true);

    [Fact]
    public async Task Enemy_Pose_round_trips_with_animation_and_sender_clock()
    {
        await using var stream = new MemoryStream();
        await LanProtocol.WriteAsync(stream, Sample, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(Sample, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("x", "1e20")]
    [InlineData("y", "null")]
    [InlineData("yaw", "1e309")]
    [InlineData("clipTime", "-0.1")]
    [InlineData("clipTime", "1000000001")]
    [InlineData("clip", "\"Walk Run\"")]
    [InlineData("clip", "\"Walk\\nenemyrelease\"")]
    [InlineData("clip", "\"\"")]
    [InlineData("entityId", "2147483648")]
    [InlineData("timeMs", "-1")]
    [InlineData("teleportCounter", "4294967296")]
    [InlineData("loop", "1")]
    [InlineData("map", "\"maps/a\\nquit\"")]
    public async Task Malformed_Enemy_Poses_are_rejected_on_read(string field, string json)
    {
        using var valid = new MemoryStream();
        await LanProtocol.WriteAsync(valid, Sample, TestContext.Current.CancellationToken);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(valid.ToArray().AsSpan(4))!;
        payload[field] = System.Text.Json.Nodes.JsonNode.Parse(json);
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Invalid_local_Enemy_Poses_are_rejected_before_writing()
    {
        LanMessage.EnemyPose[] invalid = [Sample with { X = double.NaN }, Sample with { Yaw = double.PositiveInfinity },
            Sample with { ClipTime = -1 }, Sample with { Clip = new string('a', 129) }, Sample with { Map = "" }];
        foreach (var pose in invalid)
        {
            using var stream = new MemoryStream();
            await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.WriteAsync(stream, pose, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(0, stream.Length);
        }
    }

    [Theory]
    [InlineData("STATE enemypose 139 1 0 0 0 0 0 Walk -1 1 maps/a.map")]
    [InlineData("STATE enemypose 139 1 0 0 0 0 0 Walk 0 2 maps/a.map")]
    [InlineData("STATE enemypose 139 1 0 NaN 0 0 0 Walk 0 1 maps/a.map")]
    public void Invalid_game_reports_are_not_Enemy_Poses(string line) =>
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
}
