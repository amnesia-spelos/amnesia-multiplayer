using System.Diagnostics;
using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class GamePeerVersionTests
{
    [Fact]
    public void The_Game_Peer_is_Whisper_0_1_0()
    {
        Assert.Equal("0.1.0", GamePeerVersion.Version);
        Assert.Equal("Whisper", GamePeerVersion.Codename);
    }

    [Fact]
    public void The_connected_notice_names_the_version_and_codename()
    {
        Assert.Equal("Amnesia Multiplayer v0.1.0 \"Whisper\" connected.", GamePeerVersion.ConnectedNotice);
    }

    [Fact]
    public void The_executable_file_version_matches()
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(typeof(GamePeerVersion).Assembly.Location);

        Assert.Equal("0.1.0.0", fileVersion.FileVersion);
    }
}
