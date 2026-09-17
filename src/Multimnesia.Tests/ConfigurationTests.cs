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
        Assert.Empty(options.Validate());
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
