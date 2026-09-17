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

        admission.Depart("first");
        var replacement = admission.TryAdmit("replacement");

        Assert.True(replacement.Accepted);
        Assert.Equal(GamePeerRole.SessionHost, replacement.Role);
        Assert.False(replacement.SessionReady);
    }

    [Fact]
    public void Joining_Player_departure_preserves_host_and_opens_replacement_slot()
    {
        var admission = new SessionAdmission();
        admission.TryAdmit("host");
        admission.TryAdmit("joining");

        var role = admission.Depart("joining");
        var replacement = admission.TryAdmit("replacement");

        Assert.Equal(GamePeerRole.JoiningPlayer, role);
        Assert.True(admission.IsAdmitted("host"));
        Assert.Equal(GamePeerRole.JoiningPlayer, replacement.Role);
        Assert.True(replacement.SessionReady);
    }
}
