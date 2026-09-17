using Multimnesia.Client;
using Multimnesia.Server;

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
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Game_peer_rejects_a_non_loopback_game_endpoint()
    {
        var options = new GamePeerOptions { GameHost = "192.168.1.20" };

        Assert.Contains(options.Validate(), error => error.Contains("loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Relay_rejects_an_invalid_port()
    {
        var options = new RelayOptions { Port = 0 };

        Assert.Contains(options.Validate(), error => error.Contains("port", StringComparison.OrdinalIgnoreCase));
    }
}
