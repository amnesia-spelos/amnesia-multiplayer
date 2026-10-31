using System.Buffers.Binary;
using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class EnemyDisappearancesProtocolTests
{
    public static LanMessage.EnemyDisappearance Sample => new("maps/a.map", 139, 4200);

    [Fact]
    public async Task Enemy_disappearances_round_trip_with_their_enemy_and_time()
    {
        await using var stream = new MemoryStream();
        await LanProtocol.WriteAsync(stream, Sample, TestContext.Current.CancellationToken);
        Assert.Equal("{\"type\":\"enemy-disappearance\",\"map\":\"maps/a.map\",\"entityId\":139,\"timeMs\":4200}",
            Encoding.UTF8.GetString(stream.ToArray().AsSpan(4)));
        stream.Position = 0;
        Assert.Equal(Sample, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("entityId", "2147483648")]
    [InlineData("entityId", "\"139\"")]
    [InlineData("timeMs", "-1")]
    [InlineData("timeMs", "4200.5")]
    [InlineData("timeMs", "18446744073709551616")]
    [InlineData("timeMs", "null")]
    [InlineData("map", "\"maps/a\\nquit\"")]
    [InlineData("map", "\"\"")]
    public async Task Malformed_enemy_disappearances_are_rejected_on_read(string field, string json)
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
    public async Task Invalid_local_enemy_disappearances_are_rejected_before_writing()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, Sample with { Map = "" }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void A_game_disappearance_report_is_an_enemy_disappearance_and_becomes_the_same_Command()
    {
        var reported = Assert.IsType<GameEvent.EnemyDisappeared>(GameInteractionProtocol.ParseEvent(
            "EVENT enemydisappear 0139 4200 custom_stories/mp test/maps/a.map"));
        Assert.Equal(Sample with { Map = "custom_stories/mp test/maps/a.map" }, reported.Disappearance);
        Assert.Equal("enemydisappear 139 4200 maps/a.map", GameInteractionProtocol.EnemyDisappear(Sample));
        Assert.Equal("enemydisappear 139 18446744073709551615 maps/a.map",
            GameInteractionProtocol.EnemyDisappear(Sample with { TimeMs = ulong.MaxValue }));
    }

    [Theory]
    [InlineData("EVENT enemydisappear 139 4200")]
    [InlineData("EVENT enemydisappear grunt 4200 maps/a.map")]
    [InlineData("EVENT enemydisappear 139 -1 maps/a.map")]
    [InlineData("EVENT enemydisappear 139 +4200 maps/a.map")]
    [InlineData("EVENT enemydisappear 139 4200.0 maps/a.map")]
    [InlineData("EVENT enemydisappear 139 18446744073709551616 maps/a.map")]
    [InlineData("EVENT enemydisappear  139 4200 maps/a.map")]
    public void Invalid_game_reports_are_not_enemy_disappearances(string line) =>
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
}
