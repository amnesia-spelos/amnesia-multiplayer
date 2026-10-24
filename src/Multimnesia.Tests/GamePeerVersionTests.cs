using System.Diagnostics;
using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class GamePeerVersionTests
{
    [Fact]
    public void The_Game_Peer_is_Dread_0_3_1()
    {
        Assert.Equal("0.3.1", GamePeerVersion.Version);
        Assert.Equal("Dread", GamePeerVersion.Codename);
    }

    [Fact]
    public void The_connected_notice_names_the_version_and_codename()
    {
        Assert.Equal("Amnesia Multiplayer v0.3.1 \"Dread\" connected.", GamePeerVersion.ConnectedNotice);
    }

    [Fact]
    public void The_executable_file_version_matches()
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(typeof(GamePeerVersion).Assembly.Location);

        Assert.Equal("0.3.1.0", fileVersion.FileVersion);
    }
}
