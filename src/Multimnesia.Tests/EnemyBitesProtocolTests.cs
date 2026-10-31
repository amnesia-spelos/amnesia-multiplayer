using System.Buffers.Binary;
using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class EnemyBitesProtocolTests
{
    public static LanMessage.EnemyBite Sample => new("maps/a.map", 139);

    [Fact]
    public async Task Enemy_bites_round_trip_with_their_enemy()
    {
        await using var stream = new MemoryStream();
        await LanProtocol.WriteAsync(stream, Sample, TestContext.Current.CancellationToken);
        Assert.Equal("{\"type\":\"enemy-bite\",\"map\":\"maps/a.map\",\"entityId\":139}", Encoding.UTF8.GetString(stream.ToArray().AsSpan(4)));
        stream.Position = 0;
        Assert.Equal(Sample, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("entityId", "2147483648")]
    [InlineData("entityId", "\"139\"")]
    [InlineData("entityId", "null")]
    [InlineData("map", "\"maps/a\\nquit\"")]
    [InlineData("map", "\"\"")]
    public async Task Malformed_enemy_bites_are_rejected_on_read(string field, string json)
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
    public async Task Invalid_local_enemy_bites_are_rejected_before_writing()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, Sample with { Map = "" }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void A_game_bite_report_is_an_enemy_bite_and_becomes_the_same_Command()
    {
        var reported = Assert.IsType<GameEvent.EnemyAte>(GameInteractionProtocol.ParseEvent(
            "EVENT enemyeat 0139 custom_stories/mp test/maps/a.map"));
        Assert.Equal(Sample with { Map = "custom_stories/mp test/maps/a.map" }, reported.Bite);
        Assert.Equal("enemyeat 139 maps/a.map", GameInteractionProtocol.EnemyEat(Sample));
        Assert.Equal("enemyeat -139 maps/a.map", GameInteractionProtocol.EnemyEat(Sample with { EntityId = -139 }));
    }

    [Theory]
    [InlineData("EVENT enemyeat 139")]
    [InlineData("EVENT enemyeat kaernk maps/a.map")]
    [InlineData("EVENT enemyeat 2147483648 maps/a.map")]
    [InlineData("EVENT enemyeat  139 maps/a.map")]
    public void Invalid_game_reports_are_not_enemy_bites(string line) =>
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
}
