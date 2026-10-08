using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class HoldsTests
{
    private const string Map = "custom_stories/mp-test-cs/maps/holds room.map";
    private const string Subscribe = "reportedbodies subscribe 30";
    private const string Unsubscribe = "reportedbodies unsubscribe";
    private const string Drive = $"entitydrive 12 {Map}";
    private const string Release = $"entityrelease 12 {Map}";
    private static readonly ProtocolNegotiation Granted = new("ok", 2, ["avatars", "localpose", "interactions"]);
    private static readonly BodyState Carried = new(1.25, -2.5, 3.75, 0, 0, 0, 1, 0.5, 0, -1, 0, 90, 0);
    private const string CarriedText = "1.2500 -2.5000 3.7500 0.0000 0.0000 0.0000 1.0000 0.5000 0.0000 -1.0000 0.0000 90.0000 0.0000";
    private static readonly GameEvent Grab = new GameEvent.InteractionStarted(12, 3, Map);
    private static readonly GameEvent Throw = new GameEvent.InteractionEnded(12, 3, InteractionEnding.Thrown, Map);
    private static readonly GameEvent Rest = new GameEvent.ReportSettled(12, Map);
    private static readonly LanMessage.Claim ClaimOf12 = new(Map, 12, ClaimReason.Interact);

    private readonly FakeSessionOperations _sessions = new();
    private readonly RecordingGame _game = new();

    private Holds CreateHolds(ProtocolNegotiation? negotiation = null) => CreateHolds(_sessions, _game, negotiation);

    private static Holds CreateHolds(ISessionOperations sessions, RecordingGame game, ProtocolNegotiation? negotiation = null) =>
        new(sessions, game.WriteLineAsync, game.Log, Task.FromResult(negotiation ?? Granted), TestContext.Current.CancellationToken);

    private static GameEvent.ReportedBodies Report(ulong timeMs, params BodyEntry[] entries) => new(timeMs, entries, Map);

    private static LanMessage.Pose PoseOn(string map) => new(1000, 0, 0, 0, 0, 0, 0, false, false, map);

    private static GameEvent LocalPoseOn(string map) => new GameEvent.LocalPoseReported(new(1000, 0, 0, 0, 0, 0, 0, false, false, map));

    private bool LoggedHoldEnding(string holder, string reason) => _game.Logged.Any(entry =>
        entry.Event == LocalGameEventName.HoldEnded &&
        entry.Line.StartsWith(holder, StringComparison.Ordinal) && entry.Line.EndsWith(reason, StringComparison.Ordinal));

    private async Task<Holds> CreatePresentHoldsAsync()
    {
        var holds = CreateHolds();
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(1);
        return holds;
    }

    [Fact]
    public async Task The_other_player_becoming_present_subscribes_to_the_reported_bodies_and_leaving_ends_it()
    {
        CreateHolds();

        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(1);
        _sessions.SetPresent(false);

        await _game.WaitForLinesAsync(2);
        Assert.Equal([Subscribe, Unsubscribe], _game.Lines);
    }

    [Theory]
    [InlineData("ok", 2, new[] { "avatars", "localpose" })]
    [InlineData("unknown-command", null, new string[0])]
    public async Task Nothing_is_written_or_sent_unless_the_local_game_granted_interactions(string outcome, int? version, string[] capabilities)
    {
        var holds = CreateHolds(new ProtocolNegotiation(outcome, version, capabilities));

        _sessions.SetPresent(true);
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_game.Lines);
        Assert.Empty(_sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
    }

    [Fact]
    public async Task The_other_player_departing_returns_their_entities_to_local_physics_and_forgets_their_Holds()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(2);

        _sessions.SetPresent(false);
        await _game.WaitForLinesAsync(4);

        Assert.Equal([Subscribe, Drive, Unsubscribe, Release], _game.Lines);
        Assert.True(LoggedHoldEnding("The other player's Hold on entity 12", "the other player left."));
    }

    [Fact]
    public async Task A_replacement_player_starts_with_a_clean_slate()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await holds.HandleLocalGameEventAsync(new GameEvent.InteractionStarted(40, 0, Map));
        await _game.WaitForLinesAsync(2);

        _sessions.SetPresent(false);
        await _game.WaitForLinesAsync(4);
        _sessions.SetPresent(true);
        await _game.WaitForLinesAsync(5);
        // The replacement never Claimed 12, and the local player's Hold on 40 is unknown to them, so it is Claimed anew.
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        await holds.HandleLocalGameEventAsync(new GameEvent.InteractionStarted(40, 0, Map));
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(6);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive, Unsubscribe, Release, Subscribe, Drive], _game.Lines);
        LanMessage.HoldMessage claimOf40 = new LanMessage.Claim(Map, 40, ClaimReason.Interact);
        Assert.Equal(2, _sessions.SentHoldMessages.Count(message => message == claimOf40));
        Assert.True(LoggedHoldEnding("The local player's Hold on entity 40", "the other player left."));
    }

    [Fact]
    public async Task A_replacement_without_a_departure_in_between_also_starts_with_a_clean_slate()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(2);

        _sessions.Replace();
        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive, Release], _game.Lines);
    }

    [Fact]
    public async Task The_Holder_leaving_the_map_returns_their_entities_there_to_local_physics()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(2);

        holds.HandleReceivedPose(PoseOn(Map));
        holds.HandleReceivedPose(PoseOn("maps/other.map"));
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive, Release], _game.Lines);
        Assert.True(LoggedHoldEnding("The other player's Hold on entity 12", "the other player left the map."));
    }

    [Fact]
    public async Task The_local_player_changing_map_forgets_the_Holds_on_the_map_they_left()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(LocalPoseOn(Map));
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await holds.HandleLocalGameEventAsync(new GameEvent.InteractionStarted(40, 0, Map));
        await _game.WaitForLinesAsync(2);

        // The local game released its Peer-Driven Entities and emptied its report with the map change, so nothing is written.
        await holds.HandleLocalGameEventAsync(LocalPoseOn("maps/other.map"));
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(40, 0, Carried)));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive], _game.Lines);
        Assert.Empty(_sessions.SentBodies);
        Assert.True(LoggedHoldEnding("The other player's Hold on entity 12", "the local player left the map."));
        Assert.True(LoggedHoldEnding("The local player's Hold on entity 40", "the local player left the map."));
    }

    [Fact]
    public async Task A_grab_on_a_new_map_ends_the_Holds_on_the_map_the_local_player_left_before_it_is_Claimed()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        await holds.HandleLocalGameEventAsync(new GameEvent.InteractionStarted(12, 3, "maps/other.map"));
        await holds.HandleLocalGameEventAsync(Grab);

        Assert.Equal(3, _sessions.SentHoldMessages.Count(message => message is LanMessage.Claim));
        Assert.True(LoggedHoldEnding("The local player's Hold on entity 12", "the local player left the map."));
    }

    [Fact]
    public async Task A_local_interaction_start_Claims_the_entity_and_shares_the_interaction()
    {
        var holds = await CreatePresentHoldsAsync();

        await holds.HandleLocalGameEventAsync(Grab);

        Assert.Equal([ClaimOf12, new LanMessage.Interaction(Map, 12, 3, true)], _sessions.SentHoldMessages);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldClaimed && entry.Line.Contains("12"));
    }

    [Fact]
    public async Task A_local_contact_Claims_the_entity_for_Settling_and_streams_it_until_it_settles()
    {
        var holds = await CreatePresentHoldsAsync();

        await holds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(40, Map));
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(40, 0, Carried)));
        await holds.HandleLocalGameEventAsync(new GameEvent.ReportSettled(40, Map));
        await holds.HandleLocalGameEventAsync(Report(1033, new BodyEntry(40, 0, Carried)));

        Assert.Equal([new LanMessage.Claim(Map, 40, ClaimReason.Contact), new LanMessage.Settled(Map, 40)], _sessions.SentHoldMessages);
        Assert.Equal([new LanMessage.Bodies(1000, Map, [new(40, 0, Carried)])], _sessions.SentBodies);
        Assert.Contains(_game.Logged, entry =>
            entry.Event == LocalGameEventName.HoldClaimed && entry.Line == $"The local player Claimed entity 40 on {Map} by contact.");
    }

    [Fact]
    public async Task A_local_contact_never_takes_an_entity_the_other_player_Holds()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(2);

        // The local game reported the contact before it applied the entitydrive, which takes the entity out of its report.
        await holds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(12, Map));
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));

        Assert.Empty(_sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
        Assert.Equal([Subscribe, Drive], _game.Lines);
        Assert.Contains(_game.Logged, entry =>
            entry.Event == LocalGameEventName.HoldClaimDenied &&
            entry.Line == $"The local player's contact with entity 12 on {Map} was refused: the other player Holds it.");
    }

    [Fact]
    public async Task A_local_contact_with_an_entity_the_local_player_Holds_continues_that_Hold()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Throw);

        await holds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(12, Map));

        Assert.Single(_sessions.SentHoldMessages, message => message is LanMessage.Claim);
        Assert.DoesNotContain(_game.Logged, entry => entry.Event == LocalGameEventName.HoldClaimDenied);
    }

    [Fact]
    public async Task A_Joining_Player_denied_a_contact_Claim_drives_the_entity_for_the_Session_Host_without_an_interaction()
    {
        _sessions.IsJoined = true;
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(12, Map));

        holds.HandleReceivedHoldMessage(new LanMessage.Claim(Map, 12, ClaimReason.Contact));
        holds.HandleReceivedHoldMessage(new LanMessage.ClaimDenied(Map, 12));
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));

        await _game.WaitForLinesAsync(2);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Subscribe, Drive], _game.Lines);
        Assert.Empty(_sessions.SentBodies);
    }

    [Fact]
    public async Task The_other_players_contact_Claim_drives_the_entity_without_an_interaction()
    {
        var holds = await CreatePresentHoldsAsync();

        holds.HandleReceivedHoldMessage(new LanMessage.Claim(Map, 12, ClaimReason.Contact));
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await _game.WaitForLinesAsync(3);
        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));

        await _game.WaitForLinesAsync(4);
        Assert.Equal([Subscribe, Drive, $"entitybodies 1000 1 12 3 {CarriedText} {Map}", Release], _game.Lines);
    }

    [Fact]
    public async Task On_the_Session_Host_a_contact_Claim_on_an_entity_it_Holds_is_denied()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(12, Map));

        holds.HandleReceivedHoldMessage(new LanMessage.Claim(Map, 12, ClaimReason.Contact));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([new LanMessage.Claim(Map, 12, ClaimReason.Contact), new LanMessage.ClaimDenied(Map, 12)], _sessions.SentHoldMessages);
        Assert.Equal([Subscribe], _game.Lines);
    }

    [Fact]
    public async Task Every_Hold_of_the_local_player_streams_in_one_bodies_message_per_report()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(40, Map));
        await holds.HandleLocalGameEventAsync(new GameEvent.InteractionStarted(41, 0, Map));

        await holds.HandleLocalGameEventAsync(Report(1000,
            new BodyEntry(12, 3, Carried), new BodyEntry(40, 0, Carried), new BodyEntry(40, 1, Carried), new BodyEntry(41, 0, Carried)));

        Assert.Equal(
            [new LanMessage.Bodies(1000, Map, [new(12, 3, Carried), new(40, 0, Carried), new(40, 1, Carried), new(41, 0, Carried)])],
            _sessions.SentBodies);
    }

    [Fact]
    public async Task A_bodies_message_carries_at_most_32_entries_and_never_part_of_an_entity()
    {
        var holds = await CreatePresentHoldsAsync();
        int[] props = [.. Enumerable.Range(100, 33)];
        foreach (var prop in props) await holds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(prop, Map));

        // 30 single-body entities, one with three bodies that would take the message past 32, then two more that fit.
        BodyEntry[] entries =
        [
            .. props[..30].Select(prop => new BodyEntry(prop, 0, Carried)),
            .. Enumerable.Range(0, 3).Select(body => new BodyEntry(130, body, Carried)),
            new BodyEntry(131, 0, Carried), new BodyEntry(132, 0, Carried),
        ];
        await holds.HandleLocalGameEventAsync(Report(1000, entries));

        var sent = Assert.Single(_sessions.SentBodies);
        Assert.Equal([.. entries[..30], new BodyEntry(131, 0, Carried), new BodyEntry(132, 0, Carried)], sent.Entries);
    }

    [Fact]
    public async Task Nothing_is_Claimed_while_the_other_player_is_not_present()
    {
        var holds = CreateHolds();

        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));

        Assert.Empty(_sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
    }

    [Fact]
    public async Task Only_the_bodies_of_entities_the_local_player_Holds_are_streamed()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Report(999, new BodyEntry(12, 3, Carried)));

        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried), new BodyEntry(40, 0, Carried)));
        await holds.HandleLocalGameEventAsync(new GameEvent.ReportedBodies(1033, [new(12, 3, Carried)], "maps/other.map"));

        Assert.Equal([new LanMessage.Bodies(1000, Map, [new(12, 3, Carried)])], _sessions.SentBodies);
    }

    [Fact]
    public async Task The_Hold_outlasts_the_interaction_while_the_entity_is_Settling_and_ends_when_it_settles()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        await holds.HandleLocalGameEventAsync(Rest);
        await holds.HandleLocalGameEventAsync(Report(1033, new BodyEntry(12, 3, Carried)));

        Assert.Equal(
            [ClaimOf12, new LanMessage.Interaction(Map, 12, 3, true), new LanMessage.Interaction(Map, 12, 3, false), new LanMessage.Settled(Map, 12)],
            _sessions.SentHoldMessages);
        Assert.Equal([new LanMessage.Bodies(1000, Map, [new(12, 3, Carried)])], _sessions.SentBodies);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldEnded && entry.Line.Contains("settled"));
    }

    [Fact]
    public async Task A_local_break_relays_the_final_state_and_ends_the_Hold()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Throw);

        await holds.HandleLocalGameEventAsync(new GameEvent.ReportBroke(12, Carried, Map));
        await holds.HandleLocalGameEventAsync(Report(1033, new BodyEntry(12, 3, Carried)));
        await holds.HandleLocalGameEventAsync(Grab);

        LanMessage.HoldMessage start = new LanMessage.Interaction(Map, 12, 3, true), end = new LanMessage.Interaction(Map, 12, 3, false);
        Assert.Equal([ClaimOf12, start, end, new LanMessage.Broke(Map, 12, Carried), ClaimOf12, start], _sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
        Assert.True(LoggedHoldEnding("The local player's Hold on entity 12", "broke."));
    }

    [Fact]
    public async Task Grabbing_a_Settling_entity_again_continues_the_Hold_and_a_settled_one_is_Claimed_anew()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Throw);

        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(Rest);
        await holds.HandleLocalGameEventAsync(Grab);

        LanMessage.HoldMessage start = new LanMessage.Interaction(Map, 12, 3, true), end = new LanMessage.Interaction(Map, 12, 3, false);
        Assert.Equal([ClaimOf12, start, end, start, end, new LanMessage.Settled(Map, 12), ClaimOf12, start], _sessions.SentHoldMessages);
    }

    [Fact]
    public async Task Events_about_entities_the_local_player_does_not_Hold_are_not_shared()
    {
        var holds = await CreatePresentHoldsAsync();

        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(Rest);

        Assert.Empty(_sessions.SentHoldMessages);
    }

    [Fact]
    public async Task The_other_players_Claim_makes_the_entity_a_Peer_Driven_Entity_and_their_interaction_is_mirrored()
    {
        var holds = await CreatePresentHoldsAsync();

        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, false));

        await _game.WaitForLinesAsync(4);
        Assert.Equal([Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entityinteracting 12 0 {Map}"], _game.Lines);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldClaimed && entry.Line.Contains("other player"));
    }

    [Fact]
    public async Task Received_bodies_drive_only_the_entities_the_other_player_Holds()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedBodies(new(999, Map, [new(12, 3, Carried)]));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(2);
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried), new(40, 0, Carried)]));

        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Subscribe, Drive, $"entitybodies 1000 1 12 3 {CarriedText} {Map}"], _game.Lines);
    }

    [Fact]
    public async Task Received_bodies_are_latest_wins_while_a_write_to_the_local_game_is_pending()
    {
        var holds = await CreatePresentHoldsAsync();
        var release = _game.HoldWrites();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForHeldWriteAsync();

        for (ulong time = 1; time <= 100; time++) holds.HandleReceivedBodies(new(time, Map, [new(12, 3, Carried)]));
        release();

        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Subscribe, Drive, $"entitybodies 100 1 12 3 {CarriedText} {Map}"], _game.Lines);
    }

    [Fact]
    public async Task The_other_players_entity_settling_returns_it_to_local_physics_and_ends_their_Hold()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);

        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive, Release], _game.Lines);
        Assert.Contains(_game.Logged, entry => entry.Event == LocalGameEventName.HoldEnded && entry.Line.Contains("settled"));
    }

    [Fact]
    public async Task The_other_players_entity_breaking_breaks_it_from_the_final_state_and_ends_their_Hold()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);

        holds.HandleReceivedHoldMessage(new LanMessage.Broke(Map, 12, Carried));
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));
        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive, $"entitybreak 12 {CarriedText} {Map}"], _game.Lines);
        Assert.True(LoggedHoldEnding("The other player's Hold on entity 12", "broke."));
    }

    [Fact]
    public async Task Messages_about_entities_the_other_player_does_not_Hold_are_not_applied()
    {
        var holds = await CreatePresentHoldsAsync();

        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));
        holds.HandleReceivedHoldMessage(new LanMessage.Claim("maps/other.map", 12, ClaimReason.Interact));
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        await _game.WaitForLinesAsync(2);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, "entitydrive 12 maps/other.map"], _game.Lines);
    }

    [Fact]
    public async Task On_the_Session_Host_the_first_Claim_wins_and_a_later_one_is_denied()
    {
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([ClaimOf12, new LanMessage.Interaction(Map, 12, 3, true), new LanMessage.ClaimDenied(Map, 12)],
            _sessions.SentHoldMessages);
        Assert.Equal([Subscribe], _game.Lines);
        Assert.Contains(_game.Logged, entry =>
            entry.Event == LocalGameEventName.HoldClaimDenied && entry.Line == $"Denied the other player's Claim on entity 12 on {Map}.");
    }

    [Fact]
    public async Task On_the_Session_Host_its_own_Claim_on_an_entity_the_other_player_Holds_is_denied()
    {
        var holds = await CreatePresentHoldsAsync();
        holds.HandleReceivedHoldMessage(ClaimOf12);
        await _game.WaitForLinesAsync(2);

        // The local game grabbed before it applied the entitydrive, which takes the entity out of the player's hands.
        await holds.HandleLocalGameEventAsync(Grab);
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));

        Assert.Empty(_sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
        Assert.Equal([Subscribe, Drive], _game.Lines);
        Assert.Contains(_game.Logged, entry =>
            entry.Event == LocalGameEventName.HoldClaimDenied && entry.Line == $"The local player's grab of entity 12 on {Map} was refused: the other player Holds it.");
    }

    [Fact]
    public async Task A_denied_Joining_Player_drives_the_entity_for_the_Session_Host_from_its_interaction_and_bodies()
    {
        _sessions.IsJoined = true;
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        // The Session Host's Claim was ordered first; until the denial arrives, the local player's Claim stands.
        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        holds.HandleReceivedBodies(new(999, Map, [new(12, 3, Carried)]));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        holds.HandleReceivedHoldMessage(new LanMessage.ClaimDenied(Map, 12));
        await _game.WaitForLinesAsync(3);
        holds.HandleReceivedBodies(new(1000, Map, [new(12, 3, Carried)]));
        await holds.HandleLocalGameEventAsync(new GameEvent.InteractionEnded(12, 3, InteractionEnding.Released, Map));
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));

        await _game.WaitForLinesAsync(4);
        Assert.Equal([Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entitybodies 1000 1 12 3 {CarriedText} {Map}"], _game.Lines);
        Assert.Equal([ClaimOf12, new LanMessage.Interaction(Map, 12, 3, true)], _sessions.SentHoldMessages);
        Assert.Empty(_sessions.SentBodies);
        Assert.Contains(_game.Logged, entry =>
            entry.Event == LocalGameEventName.HoldClaimDenied && entry.Line == $"The local player's Claim on entity 12 on {Map} was denied.");
    }

    [Fact]
    public async Task A_denial_that_overtakes_the_Session_Hosts_Claim_still_hands_it_the_entity()
    {
        _sessions.IsJoined = true;
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        holds.HandleReceivedHoldMessage(new LanMessage.ClaimDenied(Map, 12));
        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));

        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Subscribe, Drive, $"entityinteracting 12 1 {Map}"], _game.Lines);
    }

    [Fact]
    public async Task A_Session_Host_Claim_that_settles_before_the_local_one_is_ordered_leaves_the_local_Hold_standing()
    {
        _sessions.IsJoined = true;
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        holds.HandleReceivedHoldMessage(new LanMessage.ClaimDenied(Map, 40));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe], _game.Lines);
        Assert.Equal([new LanMessage.Bodies(1000, Map, [new(12, 3, Carried)])], _sessions.SentBodies);
    }

    [Fact]
    public async Task A_Session_Host_Claim_that_breaks_before_the_local_one_is_ordered_breaks_the_local_copy()
    {
        _sessions.IsJoined = true;
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);

        holds.HandleReceivedHoldMessage(ClaimOf12);
        holds.HandleReceivedHoldMessage(new LanMessage.Interaction(Map, 12, 3, true));
        holds.HandleReceivedHoldMessage(new LanMessage.Broke(Map, 12, Carried));
        await holds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        holds.HandleReceivedHoldMessage(new LanMessage.ClaimDenied(Map, 12));
        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe, Drive, $"entitybreak 12 {CarriedText} {Map}"], _game.Lines);
        Assert.Empty(_sessions.SentBodies);
        Assert.True(LoggedHoldEnding("The other player's Hold on entity 12", "broke."));
    }

    [Fact]
    public async Task A_local_Hold_breaking_before_its_denial_leaves_nothing_to_drive_or_break()
    {
        _sessions.IsJoined = true;
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);
        holds.HandleReceivedHoldMessage(ClaimOf12);

        await holds.HandleLocalGameEventAsync(new GameEvent.ReportBroke(12, Carried, Map));
        holds.HandleReceivedHoldMessage(new LanMessage.ClaimDenied(Map, 12));
        holds.HandleReceivedHoldMessage(new LanMessage.Broke(Map, 12, Carried));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([Subscribe], _game.Lines);
        Assert.Contains(new LanMessage.Broke(Map, 12, Carried), _sessions.SentHoldMessages);
    }

    [Fact]
    public async Task A_local_Hold_settling_before_its_denial_hands_the_entity_to_the_Session_Host_at_once()
    {
        _sessions.IsJoined = true;
        var holds = await CreatePresentHoldsAsync();
        await holds.HandleLocalGameEventAsync(Grab);
        holds.HandleReceivedHoldMessage(ClaimOf12);

        await holds.HandleLocalGameEventAsync(Throw);
        await holds.HandleLocalGameEventAsync(Rest);
        await _game.WaitForLinesAsync(2);
        // A grab now is not a new Claim, so the denial still on its way cannot be taken for an answer to one.
        await holds.HandleLocalGameEventAsync(Grab);
        holds.HandleReceivedHoldMessage(new LanMessage.ClaimDenied(Map, 12));
        holds.HandleReceivedHoldMessage(new LanMessage.Settled(Map, 12));

        await _game.WaitForLinesAsync(3);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([Subscribe, Drive, Release], _game.Lines);
        Assert.Single(_sessions.SentHoldMessages, message => message is LanMessage.Claim);
    }

    [Fact]
    public async Task Two_Game_Peers_grabbing_at_once_agree_on_exactly_one_Holder()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        var joiningGame = new RecordingGame();
        Holds hostHolds = null!;
        Holds joiningHolds = null!;
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { hostHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { hostHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { joiningHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { joiningHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        hostHolds = CreateHolds(host, hostGame);
        joiningHolds = CreateHolds(joining, joiningGame);
        await new GamePeerOrchestrator(host).HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        await new GamePeerOrchestrator(joining).HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await hostGame.WaitForLinesAsync(1);
        await joiningGame.WaitForLinesAsync(1);

        await Task.WhenAll(
            Task.Run(() => hostHolds.HandleLocalGameEventAsync(Grab), TestContext.Current.CancellationToken),
            Task.Run(() => joiningHolds.HandleLocalGameEventAsync(Grab), TestContext.Current.CancellationToken));

        // The loser's game is told to drive the entity for the winner; the winner's game is not.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!hostGame.Lines.Contains(Drive) && !joiningGame.Lines.Contains(Drive)) await Task.Delay(5, timeout.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.NotEqual(hostGame.Lines.Contains(Drive), joiningGame.Lines.Contains(Drive));
        var hostLost = hostGame.Lines.Contains(Drive);
        var (loser, winner) = hostLost ? (hostGame, joiningHolds) : (joiningGame, hostHolds);
        Assert.Equal([Subscribe, Drive, $"entityinteracting 12 1 {Map}"], loser.Lines);

        // Both tables agree: the winner's bodies drive the loser's copy, and its settling releases it.
        await winner.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        await loser.WaitForLinesAsync(4);
        await winner.HandleLocalGameEventAsync(Throw);
        await winner.HandleLocalGameEventAsync(Rest);
        await loser.WaitForLinesAsync(6);
        Assert.Equal(
            [Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entitybodies 1000 1 12 3 {CarriedText} {Map}",
                $"entityinteracting 12 0 {Map}", Release],
            loser.Lines);
    }

    [Fact]
    public async Task Two_Game_Peers_see_each_others_grab_and_throw_until_it_settles()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        var joiningGame = new RecordingGame();
        Holds hostHolds = null!;
        Holds joiningHolds = null!;
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { hostHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { hostHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { joiningHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { joiningHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        hostHolds = CreateHolds(host, hostGame);
        joiningHolds = CreateHolds(joining, joiningGame);
        await new GamePeerOrchestrator(host).HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        await new GamePeerOrchestrator(joining).HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await hostGame.WaitForLinesAsync(1);
        await joiningGame.WaitForLinesAsync(1);

        // The Joining Player grabs and throws prop 12; the Multiplayer Relay's arbiter accepts the Claim.
        await joiningHolds.HandleLocalGameEventAsync(Grab);
        await joiningHolds.HandleLocalGameEventAsync(Report(1000, new BodyEntry(12, 3, Carried)));
        await hostGame.WaitForLinesAsync(4);
        await joiningHolds.HandleLocalGameEventAsync(Throw);
        await joiningHolds.HandleLocalGameEventAsync(Rest);
        await hostGame.WaitForLinesAsync(6);

        Assert.Equal(
            [Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entitybodies 1000 1 12 3 {CarriedText} {Map}",
                $"entityinteracting 12 0 {Map}", Release],
            hostGame.Lines);

        // Once it settled, the Session Host can take it, through the same arbiter.
        await hostHolds.HandleLocalGameEventAsync(Grab);
        await joiningGame.WaitForLinesAsync(3);
        Assert.Equal([Subscribe, Drive, $"entityinteracting 12 1 {Map}"], joiningGame.Lines);
    }

    [Fact]
    public async Task Two_Game_Peers_see_a_thrown_breakable_break()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        var joiningGame = new RecordingGame();
        Holds hostHolds = null!;
        Holds joiningHolds = null!;
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { hostHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { hostHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { joiningHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { joiningHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        hostHolds = CreateHolds(host, hostGame);
        joiningHolds = CreateHolds(joining, joiningGame);
        await new GamePeerOrchestrator(host).HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        await new GamePeerOrchestrator(joining).HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await hostGame.WaitForLinesAsync(1);
        await joiningGame.WaitForLinesAsync(1);

        await joiningHolds.HandleLocalGameEventAsync(Grab);
        await joiningHolds.HandleLocalGameEventAsync(Throw);
        await joiningHolds.HandleLocalGameEventAsync(new GameEvent.ReportBroke(12, Carried, Map));
        await hostGame.WaitForLinesAsync(5);

        Assert.Equal(
            [Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entityinteracting 12 0 {Map}", $"entitybreak 12 {CarriedText} {Map}"],
            hostGame.Lines);
    }

    [Fact]
    public async Task Two_Game_Peers_see_a_thrown_prop_topple_a_stack()
    {
        var port = FreePort();
        var hostGame = new RecordingGame();
        var joiningGame = new RecordingGame();
        Holds hostHolds = null!;
        Holds joiningHolds = null!;
        await using var host = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { hostHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { hostHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        await using var joining = new TcpSessionOperations(new SessionNetworkOptions { Port = port },
            receiveHoldMessage: message => { joiningHolds.HandleReceivedHoldMessage(message); return ValueTask.CompletedTask; },
            receiveBodies: bodies => { joiningHolds.HandleReceivedBodies(bodies); return ValueTask.CompletedTask; });
        hostHolds = CreateHolds(host, hostGame);
        joiningHolds = CreateHolds(joining, joiningGame);
        await new GamePeerOrchestrator(host).HandleAsync(new ChatEntry("Host", "/host"), TestContext.Current.CancellationToken);
        await new GamePeerOrchestrator(joining).HandleAsync(new ChatEntry("Joiner", "/join 127.0.0.1"), TestContext.Current.CancellationToken);
        await hostGame.WaitForLinesAsync(1);
        await joiningGame.WaitForLinesAsync(1);

        // The Joining Player throws prop 12 into a stack of 40 on 41; 40 knocks 41 in turn.
        await joiningHolds.HandleLocalGameEventAsync(Grab);
        await joiningHolds.HandleLocalGameEventAsync(Throw);
        await joiningHolds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(40, Map));
        await joiningHolds.HandleLocalGameEventAsync(new GameEvent.ReportContacted(41, Map));
        await hostGame.WaitForLinesAsync(6);
        await joiningHolds.HandleLocalGameEventAsync(Report(1000,
            new BodyEntry(12, 3, Carried), new BodyEntry(40, 0, Carried), new BodyEntry(41, 0, Carried)));
        await hostGame.WaitForLinesAsync(7);
        foreach (var prop in new[] { 12, 40, 41 }) await joiningHolds.HandleLocalGameEventAsync(new GameEvent.ReportSettled(prop, Map));

        await hostGame.WaitForLinesAsync(10);
        Assert.Equal(
            [Subscribe, Drive, $"entityinteracting 12 1 {Map}", $"entityinteracting 12 0 {Map}", $"entitydrive 40 {Map}", $"entitydrive 41 {Map}",
                $"entitybodies 1000 3 12 3 {CarriedText} 40 0 {CarriedText} 41 0 {CarriedText} {Map}",
                Release, $"entityrelease 40 {Map}", $"entityrelease 41 {Map}"],
            hostGame.Lines);
    }

    [Theory]
    [InlineData("entitydrive", "not-found", new[] { "12" })]
    [InlineData("entitydrive", "wrong-map", new[] { "12" })]
    [InlineData("entitybodies", "not-found", new[] { "12" })]
    [InlineData("entityinteracting", "invalid", new string[0])]
    [InlineData("entityrelease", "not-granted", new string[0])]
    [InlineData("reportedbodies", "invalid", new string[0])]
    public void Failed_Peer_Driven_Entity_Commands_are_logged(string keyword, string outcome, string[] fields)
    {
        var holds = CreateHolds();

        holds.HandleResponse(new GameEvent.Responded(keyword, outcome, fields));

        var logged = Assert.Single(_game.Logged);
        Assert.Equal(ConnectionLogSeverity.Warning, logged.Severity);
        Assert.Equal(LocalGameEventName.HoldCommandFailed, logged.Event);
        Assert.Equal(string.Join(' ', ["RESPONSE", keyword, outcome, .. fields]), logged.Line);
    }

    [Theory]
    [InlineData("entitydrive", "ok", new[] { "12" })]
    [InlineData("reportedbodies", "ok", new[] { "subscribe", "30" })]
    [InlineData("avatarpose", "not-found", new[] { "partner" })]
    public void Other_Responses_are_not_logged(string keyword, string outcome, string[] fields)
    {
        var holds = CreateHolds();

        holds.HandleResponse(new GameEvent.Responded(keyword, outcome, fields));

        Assert.Empty(_game.Logged);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingGame
    {
        private readonly List<string> _lines = [];
        private readonly List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> _log = [];
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _hold;

        public List<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public List<(ConnectionLogSeverity Severity, LocalGameEventName Event, string Line)> Logged { get { lock (_log) return [.. _log]; } }

        public void Log(ConnectionLogSeverity severity, LocalGameEventName gameEvent, string line)
        {
            lock (_log) _log.Add((severity, gameEvent, line));
        }

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (_lines) _lines.Add(line);
            if (Volatile.Read(ref _hold) is { } hold)
            {
                _held.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken);
            }
        }

        // The next write is recorded but does not complete until released.
        public Action HoldWrites()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _hold, hold);
            return () => { Volatile.Write(ref _hold, null); hold.TrySetResult(); };
        }

        public Task WaitForHeldWriteAsync() => _held.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public async Task WaitForLinesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Lines.Count < count) await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        private int _arrivalCount;
        private int _currentArrival;
        private readonly List<LanMessage.HoldMessage> _sentHoldMessages = [];
        private readonly List<LanMessage.Bodies> _sentBodies = [];
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged;
        public bool IsJoined { get; set; }
        public int OtherPlayerArrival => Volatile.Read(ref _currentArrival);
        public List<LanMessage.HoldMessage> SentHoldMessages { get { lock (_sentHoldMessages) return [.. _sentHoldMessages]; } }
        public List<LanMessage.Bodies> SentBodies { get { lock (_sentBodies) return [.. _sentBodies]; } }

        // Becoming present again is a new arrival.
        public void SetPresent(bool present)
        {
            Volatile.Write(ref _currentArrival, present ? Interlocked.Increment(ref _arrivalCount) : 0);
            OtherPlayerPresenceChanged?.Invoke();
        }

        // A Session Host can admit a replacement Joining Player before it notices the departure.
        public void Replace()
        {
            Volatile.Write(ref _currentArrival, Interlocked.Increment(ref _arrivalCount));
            OtherPlayerPresenceChanged?.Invoke();
        }

        public void SendBodies(LanMessage.Bodies bodies) { lock (_sentBodies) _sentBodies.Add(bodies); }

        public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken)
        {
            lock (_sentHoldMessages) _sentHoldMessages.Add(message);
            return Task.CompletedTask;
        }

        public void SendPose(LanMessage.Pose pose) { }
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
