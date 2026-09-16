using Multimnesia.Server;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class SessionAdmissionTests
{
    [Fact]
    public void First_two_game_peers_are_assigned_roles_and_the_third_is_rejected()
    {
        var admission = new SessionAdmission();

        var first = admission.TryAdmit("first");
        var second = admission.TryAdmit("second");
        var third = admission.TryAdmit("third");

        Assert.Equal(GamePeerRole.SessionHost, first.Role);
        Assert.False(first.SessionReady);
        Assert.Equal(GamePeerRole.JoiningPlayer, second.Role);
        Assert.True(second.SessionReady);
        Assert.False(third.Accepted);
    }

    [Fact]
    public void A_disconnected_game_peer_ends_admission_state_for_a_clean_test()
    {
        var admission = new SessionAdmission();
        admission.TryAdmit("first");
        admission.TryAdmit("second");

        admission.EndSessionFor("first");
        var replacement = admission.TryAdmit("replacement");

        Assert.True(replacement.Accepted);
        Assert.Equal(GamePeerRole.SessionHost, replacement.Role);
        Assert.False(replacement.SessionReady);
    }
}
