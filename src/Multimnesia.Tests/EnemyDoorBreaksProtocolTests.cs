using System.Buffers.Binary;
using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class EnemyDoorBreaksProtocolTests
{
    public static LanMessage.EnemyDoorBreak Sample => new("maps/a.map", 141);

    [Fact]
    public async Task Enemy_door_breaks_round_trip_with_their_door()
    {
        await using var stream = new MemoryStream();
        await LanProtocol.WriteAsync(stream, Sample, TestContext.Current.CancellationToken);
        Assert.Equal("{\"type\":\"enemy-door-break\",\"map\":\"maps/a.map\",\"doorId\":141}", Encoding.UTF8.GetString(stream.ToArray().AsSpan(4)));
        stream.Position = 0;
        Assert.Equal(Sample, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("doorId", "2147483648")]
    [InlineData("doorId", "\"141\"")]
    [InlineData("doorId", "null")]
    [InlineData("map", "\"maps/a\\nquit\"")]
    [InlineData("map", "\"\"")]
    public async Task Malformed_enemy_door_breaks_are_rejected_on_read(string field, string json)
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
    public async Task Invalid_local_enemy_door_breaks_are_rejected_before_writing()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, Sample with { Map = "" }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void A_game_door_break_report_is_an_enemy_door_break_and_becomes_the_same_Command()
    {
        var reported = Assert.IsType<GameEvent.EnemyDoorBroke>(GameInteractionProtocol.ParseEvent(
            "EVENT enemydoorbreak 0141 custom_stories/mp test/maps/a.map"));
        Assert.Equal(Sample with { Map = "custom_stories/mp test/maps/a.map" }, reported.DoorBreak);
        Assert.Equal("enemydoorbreak 141 maps/a.map", GameInteractionProtocol.EnemyDoorBreak(Sample));
        Assert.Equal("enemydoorbreak -141 maps/a.map", GameInteractionProtocol.EnemyDoorBreak(Sample with { DoorId = -141 }));
    }

    [Theory]
    [InlineData("EVENT enemydoorbreak 141")]
    [InlineData("EVENT enemydoorbreak door maps/a.map")]
    [InlineData("EVENT enemydoorbreak 2147483648 maps/a.map")]
    [InlineData("EVENT enemydoorbreak  141 maps/a.map")]
    public void Invalid_game_reports_are_not_enemy_door_breaks(string line) =>
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
}
