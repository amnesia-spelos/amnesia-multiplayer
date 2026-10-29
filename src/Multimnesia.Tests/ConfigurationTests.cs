using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Game_peer_defaults_keep_the_game_endpoint_on_loopback()
    {
        var options = new GamePeerOptions();

        Assert.Equal("127.0.0.1", options.GameHost);
        Assert.Equal(5150, options.GamePort);
        Assert.Equal(5000, options.RelayPort);
        Assert.Equal(10, options.JoinTimeoutSeconds);
        Assert.Equal("Information", options.LogLevel);
        Assert.Null(options.ChatColor);
        Assert.Null(options.ParsedChatColor);
        Assert.False(options.TrafficStatistics);
        Assert.False(options.TrafficTrace);
        Assert.Equal(200, options.TrafficTraceMaxMegabytes);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(new[] { "--stats" }, true, false)]
    [InlineData(new[] { "--trace" }, false, true)]
    [InlineData(new[] { "--stats", "--trace" }, true, true)]
    [InlineData(new[] { "--trace", "--color", "ff00ff", "--stats" }, true, true)]
    [InlineData(new[] { "--stats", "true" }, true, false)]
    [InlineData(new[] { "--stats", "false", "--trace" }, false, true)]
    [InlineData(new[] { "--GamePeer:TrafficTrace", "true" }, false, true)]
    public void Traffic_switches_turn_on_each_feature_independently(string[] args, bool statistics, bool trace)
    {
        var options = GamePeerConfiguration.Load(args, jsonPath: null);

        Assert.Equal(statistics, options.TrafficStatistics);
        Assert.Equal(trace, options.TrafficTrace);
    }

    [Fact]
    public void Traffic_features_can_be_turned_on_in_the_JSON_configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"multimnesia-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "GamePeer": { "TrafficStatistics": true, "TrafficTrace": true, "TrafficTraceMaxMegabytes": 5 } }""");
        try
        {
            var options = GamePeerConfiguration.Load([], path);

            Assert.True(options.TrafficStatistics);
            Assert.True(options.TrafficTrace);
            Assert.Equal(5, options.TrafficTraceMaxMegabytes);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Game_peer_rejects_a_trace_size_that_is_not_positive(int megabytes)
    {
        var options = new GamePeerOptions { TrafficTraceMaxMegabytes = megabytes };

        Assert.Contains(options.Validate(), error => error.Contains("GamePeer:TrafficTraceMaxMegabytes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FF00FF")]
    [InlineData("ff00ff")]
    public void Game_peer_accepts_a_six_hex_digit_chat_color(string value)
    {
        var options = new GamePeerOptions { ChatColor = value };

        Assert.Empty(options.Validate());
        Assert.Equal("FF00FF", options.ParsedChatColor?.Hex);
    }

    [Theory]
    [InlineData("FF00")]
    [InlineData("#FF00FF")]
    [InlineData("FF00FFAA")]
    [InlineData("ZZZZZZ")]
    public void Game_peer_rejects_a_malformed_chat_color(string value)
    {
        var options = new GamePeerOptions { ChatColor = value };

        Assert.Contains(options.Validate(), error => error.Contains("GamePeer:ChatColor", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--color", "ff00ff")]
    [InlineData("--GamePeer:ChatColor", "ff00ff")]
    public void Both_command_line_forms_set_the_chat_color(string key, string value)
    {
        var options = GamePeerConfiguration.Load([key, value], jsonPath: null);

        Assert.Equal("ff00ff", options.ChatColor);
    }

    [Fact]
    public void Game_peer_rejects_a_non_loopback_game_endpoint()
    {
        var options = new GamePeerOptions { GameHost = "192.168.1.20" };

        Assert.Contains(options.Validate(), error => error.Contains("loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Game_peer_rejects_an_invalid_relay_port()
    {
        var options = new GamePeerOptions { RelayPort = 0 };

        Assert.Contains(options.Validate(), error => error.Contains("RelayPort", StringComparison.Ordinal));
    }

    [Fact]
    public void Game_peer_rejects_an_unrecognized_log_level()
    {
        var options = new GamePeerOptions { LogLevel = "Verbose" };

        Assert.Contains(options.Validate(), error => error.Contains("LogLevel", StringComparison.Ordinal));
    }
}
