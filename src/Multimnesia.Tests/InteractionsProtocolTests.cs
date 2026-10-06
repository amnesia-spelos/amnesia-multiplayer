using System.Globalization;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

// Expected values come from the amnesia-tdd-tcp Protocol Version 2 contract fixtures (contract.jsonl) for `interactions`.
public sealed class InteractionsProtocolTests
{
    private const string StoryMap = "custom_stories/My Story: Part 2/maps/cellar one.map";
    private static readonly BodyState Thrown = new(1.25, -2.5, 3.75, 0, 0.7071, 0, 0.7071, 0.5, 0, -1, 0, 90, 0);
    private static readonly BodyState AtRest = new(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);
    private const string ThrownText = "1.2500 -2.5000 3.7500 0.0000 0.7071 0.0000 0.7071 0.5000 0.0000 -1.0000 0.0000 90.0000 0.0000";
    private const string AtRestText = "0.0000 0.0000 0.0000 0.0000 0.0000 0.0000 1.0000 0.0000 0.0000 0.0000 0.0000 0.0000 0.0000";

    [Fact]
    public void The_negotiation_requests_interactions_after_the_Shared_Pose_Capabilities()
    {
        Assert.Equal("protocol 2 avatars localpose interactions", GameInteractionProtocol.Negotiate);
    }

    [Theory]
    [InlineData("ok", 2, new[] { "avatars", "localpose", "interactions" }, true)]
    [InlineData("ok", 2, new[] { "interactions" }, true)]
    [InlineData("ok", 2, new[] { "avatars", "localpose" }, false)]
    [InlineData("unknown-command", null, new string[0], false)]
    public void Interactions_are_granted_only_when_the_game_lists_the_Capability(
        string outcome, int? version, string[] capabilities, bool granted)
    {
        Assert.Equal(granted, new ProtocolNegotiation(outcome, version, capabilities).GrantsInteractions);
    }

    [Fact]
    public void Interaction_starts_name_the_entity_its_body_and_the_whole_map_path()
    {
        Assert.Equal(
            new GameEvent.InteractionStarted(12, 3, StoryMap),
            GameInteractionProtocol.ParseEvent($"EVENT interactionstart 12 3 {StoryMap}"));
    }

    [Theory]
    [InlineData("released", InteractionEnding.Released)]
    [InlineData("thrown", InteractionEnding.Thrown)]
    [InlineData("too-far", InteractionEnding.TooFar)]
    [InlineData("destroyed", InteractionEnding.Destroyed)]
    public void Interaction_ends_carry_how_they_ended(string wireName, InteractionEnding ending)
    {
        Assert.Equal(
            new GameEvent.InteractionEnded(-7, 0, ending, "maps/a.map"),
            GameInteractionProtocol.ParseEvent($"EVENT interactionend -7 0 {wireName} maps/a.map"));
    }

    [Fact]
    public void Report_contacts_settles_and_breaks_name_the_entity_and_the_map()
    {
        Assert.Equal(new GameEvent.ReportContacted(int.MaxValue, "maps/a.map"),
            GameInteractionProtocol.ParseEvent("EVENT reportcontact 2147483647 maps/a.map"));
        Assert.Equal(new GameEvent.ReportSettled(int.MinValue, "maps/a.map"),
            GameInteractionProtocol.ParseEvent("EVENT reportsettled -2147483648 maps/a.map"));
        Assert.Equal(new GameEvent.ReportBroke(12, Thrown, "maps/a b.map"),
            GameInteractionProtocol.ParseEvent($"EVENT reportbroke 12 {ThrownText} maps/a b.map"));
    }

    [Fact]
    public void Entity_Identifiers_accept_leading_zeros()
    {
        Assert.Equal(new GameEvent.ReportSettled(-12, "maps/a.map"), GameInteractionProtocol.ParseEvent("EVENT reportsettled -0012 maps/a.map"));
    }

    [Fact]
    public void Reported_bodies_State_Updates_carry_every_entry_in_order_and_the_whole_map_path()
    {
        var parsed = Assert.IsType<GameEvent.ReportedBodies>(GameInteractionProtocol.ParseEvent(
            $"STATE reportedbodies 123456 2 12 3 {ThrownText} -7 0 {AtRestText} {StoryMap}"));

        Assert.Equal(123456UL, parsed.TimeMs);
        Assert.Equal([new BodyEntry(12, 3, Thrown), new BodyEntry(-7, 0, AtRest)], parsed.Entries);
        Assert.Equal(StoryMap, parsed.Map);
    }

    [Fact]
    public void Reported_bodies_use_the_full_time_and_identifier_ranges_under_a_comma_decimal_locale()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs-CZ");
        try
        {
            var parsed = Assert.IsType<GameEvent.ReportedBodies>(GameInteractionProtocol.ParseEvent(
                $"STATE reportedbodies 18446744073709551615 1 2147483647 -2147483648 {ThrownText} maps/a.map"));

            Assert.Equal(ulong.MaxValue, parsed.TimeMs);
            Assert.Equal([new BodyEntry(int.MaxValue, int.MinValue, Thrown)], parsed.Entries);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("EVENT interactionstart 12 3")]
    [InlineData("EVENT interactionstart 12 3 ")]
    [InlineData("EVENT interactionstart +12 3 maps/a.map")]
    [InlineData("EVENT interactionstart 2147483648 3 maps/a.map")]
    [InlineData("EVENT interactionstart 12 1.0 maps/a.map")]
    [InlineData("EVENT interactionstart 12  3 maps/a.map")]
    [InlineData("EVENT interactionstartx 12 3 maps/a.map")]
    [InlineData("EVENT interactionend 12 3 dropped maps/a.map")]
    [InlineData("EVENT interactionend 12 3 maps/a.map")]
    [InlineData("EVENT reportsettled - maps/a.map")]
    [InlineData("EVENT reportsettled 12")]
    [InlineData("EVENT reportbroke 12 1.0000 2.0000 3.0000 0.0000 0.0000 0.0000 2.0000 0.0000 0.0000 0.0000 0.0000 0.0000 0.0000 maps/a.map")]
    [InlineData("EVENT reportbroke 12 1000000001 0 0 0 0 0 1 0 0 0 0 0 0 maps/a.map")]
    [InlineData("EVENT reportbroke 12 1,5 0 0 0 0 0 1 0 0 0 0 0 0 maps/a.map")]
    [InlineData("EVENT reportbroke 12 maps/a.map")]
    [InlineData("STATE reportedbodies 1000 33 1 0 0 0 0 0 0 0 1 0 0 0 0 0 0 maps/a.map")]
    [InlineData("STATE reportedbodies 1000 2 12 3 1 2 3 0 0 0 1 0 0 0 0 0 0 maps/a.map")]
    [InlineData("STATE reportedbodies 1000 1 12 3 1 2 3 0 0 0 1 0 0 0 0 0 0")]
    [InlineData("STATE reportedbodies -1 1 12 3 1 2 3 0 0 0 1 0 0 0 0 0 0 maps/a.map")]
    [InlineData("STATE reportedbodies 1000 -1 maps/a.map")]
    [InlineData("STATE reportedbodies 1000 1 12 1 2 3 0 0 0 1 0 0 0 0 0 0 maps/a.map")]
    public void Malformed_interactions_lines_are_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Fact]
    public void Legacy_Events_keep_their_form()
    {
        Assert.IsType<GameEvent.CustomStoryStarted>(GameInteractionProtocol.ParseEvent("EVENT:CustomStoryStarted:mp-test-cs"));
    }

    [Fact]
    public void Peer_Driven_Entity_Commands_are_written_canonically_with_the_map_last()
    {
        Assert.Equal($"entitydrive 12 {StoryMap}", GameInteractionProtocol.EntityDrive(12, StoryMap));
        Assert.Equal("entityinteracting -7 1 maps/a b.map", GameInteractionProtocol.EntityInteracting(-7, true, "maps/a b.map"));
        Assert.Equal("entityinteracting -7 0 maps/a b.map", GameInteractionProtocol.EntityInteracting(-7, false, "maps/a b.map"));
        Assert.Equal("entityrelease 5 maps/a.map", GameInteractionProtocol.EntityRelease(5, "maps/a.map"));
        Assert.Equal($"entitybreak 12 {ThrownText} maps/a.map", GameInteractionProtocol.EntityBreak(12, Thrown, "maps/a.map"));
        Assert.Equal("reportedbodies subscribe 30", GameInteractionProtocol.SubscribeReportedBodies(30));
        Assert.Equal("reportedbodies unsubscribe", GameInteractionProtocol.UnsubscribeReportedBodies);
    }

    [Fact]
    public void Driven_bodies_are_written_entry_by_entry_whatever_the_locale()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs-CZ");
        try
        {
            Assert.Equal(
                $"entitybodies 123456 2 12 3 {ThrownText} -7 0 {AtRestText} maps/main/level01.map",
                GameInteractionProtocol.EntityBodies(123456, [new(12, 3, Thrown), new(-7, 0, AtRest)], "maps/main/level01.map"));
            Assert.Equal("entitybodies 0 0 maps/a.map", GameInteractionProtocol.EntityBodies(0, [], "maps/a.map"));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    // A quaternion the LAN accepted within tolerance could round to one the game rejects, so it is written normalized.
    [Fact]
    public void Orientations_are_written_normalized()
    {
        Assert.Equal(
            $"entitybreak 1 {AtRestText} maps/a.map",
            GameInteractionProtocol.EntityBreak(1, AtRest with { Qw = 0.991 }, "maps/a.map"));
        Assert.Equal(
            "entitybodies 1 1 1 1 0.0000 0.0000 0.0000 0.0000 0.6000 0.0000 0.8000 0.0000 0.0000 0.0000 0.0000 0.0000 0.0000 maps/a.map",
            GameInteractionProtocol.EntityBodies(1, [new(1, 1, AtRest with { Qy = 0.6 * 1.005, Qw = 0.8 * 1.005 })], "maps/a.map"));
    }
}
