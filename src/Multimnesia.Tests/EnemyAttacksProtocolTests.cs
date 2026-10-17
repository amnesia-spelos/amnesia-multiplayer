using System.Buffers.Binary;
using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class EnemyAttacksProtocolTests
{
    public static LanMessage.EnemyAttack Sample => new("maps/a.map", 139, EnemyAttackKind.Normal, 1.2, 1.5, 1, 0, 0.75, 0.9);

    [Fact]
    public async Task Enemy_attacks_round_trip_with_their_box()
    {
        LanMessage.EnemyAttack[] attacks = [Sample, Sample with { Attack = EnemyAttackKind.BreakDoor, OffsetX = -0.25 }];
        foreach (var attack in attacks)
        {
            await using var stream = new MemoryStream();
            await LanProtocol.WriteAsync(stream, attack, TestContext.Current.CancellationToken);
            stream.Position = 0;
            Assert.Equal(attack, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("attack", "\"bite\"")]
    [InlineData("attack", "null")]
    [InlineData("size", "[1, 1]")]
    [InlineData("size", "[0, 1, 1]")]
    [InlineData("size", "[1, -1, 1]")]
    [InlineData("size", "[1, 1, 1e20]")]
    [InlineData("offset", "[0, 0, 1e309]")]
    [InlineData("offset", "[0, null, 0]")]
    [InlineData("entityId", "2147483648")]
    [InlineData("map", "\"maps/a\\nquit\"")]
    [InlineData("map", "\"\"")]
    public async Task Malformed_enemy_attacks_are_rejected_on_read(string field, string json)
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
    public async Task Invalid_local_enemy_attacks_are_rejected_before_writing()
    {
        LanMessage.EnemyAttack[] invalid = [Sample with { SizeX = 0 }, Sample with { SizeZ = -1 }, Sample with { OffsetY = double.NaN },
            Sample with { OffsetZ = double.PositiveInfinity }, Sample with { Map = "" }, Sample with { Attack = (EnemyAttackKind)7 }];
        foreach (var attack in invalid)
        {
            using var stream = new MemoryStream();
            await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.WriteAsync(stream, attack, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public void A_game_attack_report_is_an_enemy_attack_and_becomes_the_same_Command()
    {
        var reported = Assert.IsType<GameEvent.EnemyAttackReported>(GameInteractionProtocol.ParseEvent(
            "EVENT enemyattack 139 breakdoor 1.2 1.5 1 -0.25 0.75 0.9 custom_stories/mp test/maps/a.map"));
        Assert.Equal(Sample with { Map = "custom_stories/mp test/maps/a.map", Attack = EnemyAttackKind.BreakDoor, OffsetX = -0.25 },
            reported.Attack);
        Assert.Equal("enemyattack 139 normal 1.2000 1.5000 1.0000 0.0000 0.7500 0.9000 maps/a.map", GameInteractionProtocol.EnemyAttack(Sample));
    }

    [Theory]
    [InlineData("EVENT enemyattack 139 bite 1 1 1 0 0 1 maps/a.map")]
    [InlineData("EVENT enemyattack 139 normal 0 1 1 0 0 1 maps/a.map")]
    [InlineData("EVENT enemyattack 139 normal 1 1 1 0 NaN 1 maps/a.map")]
    [InlineData("EVENT enemyattack 139 normal 1 1 1 0 0 1")]
    [InlineData("EVENT enemyattack x normal 1 1 1 0 0 1 maps/a.map")]
    public void Invalid_game_reports_are_not_enemy_attacks(string line) =>
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
}
