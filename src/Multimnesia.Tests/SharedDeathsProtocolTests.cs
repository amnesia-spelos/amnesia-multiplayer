using System.Buffers.Binary;
using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class SharedDeathsProtocolTests
{
    public static LanMessage.PlayerDeath Report => new("maps/a.map", 3, 0);
    public static LanMessage.SharedDeath Episode => new("maps/a.map", 1, 3, 2, "cp_test");

    [Fact]
    public async Task Death_messages_round_trip()
    {
        LanMessage[] messages = [Report, Report with { AfterEpisode = 7 }, Episode, Episode with { Death = 0, Count = 0, CheckPoint = "_auto" }];
        foreach (var message in messages)
        {
            await using var stream = new MemoryStream();
            await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
            stream.Position = 0;
            Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("death", "0")]
    [InlineData("death", "-1")]
    [InlineData("death", "2147483648")]
    [InlineData("afterEpisode", "-1")]
    [InlineData("afterEpisode", "null")]
    [InlineData("map", "\"\"")]
    [InlineData("map", "\"maps/a\\nquit\"")]
    public async Task Malformed_death_reports_are_rejected_on_read(string field, string json) =>
        await AssertRejectedAsync(Report, field, json);

    [Theory]
    [InlineData("episode", "0")]
    [InlineData("death", "-1")]
    [InlineData("count", "-1")]
    [InlineData("count", "2147483648")]
    [InlineData("count", "1.5")]
    [InlineData("checkPoint", "\"\"")]
    [InlineData("checkPoint", "\"two words\"")]
    [InlineData("checkPoint", "\"RoomLightColorChanged(\\\"x\\\", 0)\\nexec:quit\"")]
    [InlineData("checkPoint", "null")]
    [InlineData("map", "\"\"")]
    public async Task Malformed_Shared_Death_episodes_are_rejected_on_read(string field, string json) =>
        await AssertRejectedAsync(Episode, field, json);

    [Fact]
    public async Task Invalid_local_death_messages_are_rejected_before_writing()
    {
        LanMessage[] invalid = [Report with { Death = 0 }, Report with { AfterEpisode = -1 }, Report with { Map = "" },
            Episode with { Episode = 0 }, Episode with { Death = -1 },
            Episode with { Count = -1 }, Episode with { CheckPoint = "two words" }, Episode with { CheckPoint = new string('a', 65) }];
        foreach (var message in invalid)
        {
            using var stream = new MemoryStream();
            await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public void Game_checkpoint_and_death_reports_carry_the_checkpoint_as_it_stood()
    {
        var checkPoint = Assert.IsType<GameEvent.CheckPointReported>(GameInteractionProtocol.ParseEvent(
            "EVENT checkpoint 3 1 cp_test custom_stories/mp test/maps/a.map"));
        Assert.Equal(new CheckPoint(3, true, "cp_test", "custom_stories/mp test/maps/a.map"), checkPoint.CheckPoint);
        var death = Assert.IsType<GameEvent.PlayerDied>(GameInteractionProtocol.ParseEvent("EVENT playerdeath 2 0 0 _auto maps/a.map"));
        Assert.Equal(2, death.Death);
        Assert.Equal(new CheckPoint(0, false, "_auto", "maps/a.map"), death.CheckPoint);
        Assert.Equal("maps/a.map", GameInteractionProtocol.LocalMapOf(death));
        Assert.Equal("custom_stories/mp test/maps/a.map", GameInteractionProtocol.LocalMapOf(checkPoint));
    }

    [Theory]
    [InlineData("EVENT checkpoint 3 2 cp_test maps/a.map")]
    [InlineData("EVENT checkpoint -1 1 cp_test maps/a.map")]
    [InlineData("EVENT checkpoint 3 1 cp_test")]
    [InlineData("EVENT playerdeath 0 0 1 cp_test maps/a.map")]
    [InlineData("EVENT playerdeath x 0 1 cp_test maps/a.map")]
    [InlineData("EVENT playerdeath 1 0 1 maps/a.map")]
    public void Invalid_game_reports_are_not_checkpoints_or_deaths(string line) =>
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));

    [Fact]
    public void An_episode_becomes_this_game_s_numbered_shareddeath_Command() =>
        Assert.Equal("shareddeath 5 3 2 cp_test maps/a.map", GameInteractionProtocol.SharedDeath(5, Episode with { Episode = 9 }));

    private static async Task AssertRejectedAsync(LanMessage message, string field, string json)
    {
        using var valid = new MemoryStream();
        await LanProtocol.WriteAsync(valid, message, TestContext.Current.CancellationToken);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(valid.ToArray().AsSpan(4))!;
        payload[field] = System.Text.Json.Nodes.JsonNode.Parse(json);
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }
}
