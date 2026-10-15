using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

// Expected lines come from the amnesia-tdd-tcp Protocol Version 2 contract for `sharedcallbacks` and `interactions`.
public sealed class SharedCallbacksProtocolTests
{
    private const string StoryMap = "custom_stories/My Story: Part 2/maps/cellar one.map";

    [Theory]
    [InlineData("ok", 2, new[] { "avatars", "localpose", "interactions", "sharedcallbacks" }, true)]
    [InlineData("ok", 2, new[] { "sharedcallbacks" }, true)]
    [InlineData("ok", 2, new[] { "avatars", "localpose", "interactions" }, false)]
    [InlineData("unknown-command", null, new string[0], false)]
    public void Shared_Callbacks_are_granted_only_when_the_game_lists_the_Capability(
        string outcome, int? version, string[] capabilities, bool granted)
    {
        Assert.Equal(granted, new ProtocolNegotiation(outcome, version, capabilities).GrantsSharedCallbacks);
    }

    public static TheoryData<string, LanMessage.WorldMessage> Reports => new()
    {
        { $"EVENT sharedcallback playercollide 30 1 {StoryMap}", new LanMessage.PlayerCollide(StoryMap, 30, Entered: true) },
        { "EVENT sharedcallback playercollide -30 -1 maps/a.map", new LanMessage.PlayerCollide("maps/a.map", -30, Entered: false) },
        { "EVENT sharedcallback entitycollide 30 12 1 maps/a.map", new LanMessage.EntityCollide("maps/a.map", 30, 12, Entered: true) },
        { "EVENT sharedcallback interact 0012 maps/a.map", new LanMessage.Interact("maps/a.map", 12) },
        { $"EVENT sharedcallback useitem 12 key_study {StoryMap}", new LanMessage.UseItem(StoryMap, 12, "key_study") },
        { $"EVENT sharedcallback pickup 42 {StoryMap}", new LanMessage.Pickup(StoryMap, 42) },
        { "EVENT sharedcallback flashback -17 maps/a.map", new LanMessage.Flashback("maps/a.map", -17) },
        {
            $"EVENT sharedcallback combine hollow_needle_1 stone_hammer_1 {StoryMap}",
            new LanMessage.Combine(StoryMap, "hollow_needle_1", "stone_hammer_1")
        },
        { "EVENT buttonstate 12 1 maps/a.map", new LanMessage.ButtonState("maps/a.map", 12, SwitchedOn: true) },
        { "EVENT buttonstate 12 0 maps/a.map", new LanMessage.ButtonState("maps/a.map", 12, SwitchedOn: false) },
        { $"EVENT lampstate -13 1 {StoryMap}", new LanMessage.LampState(StoryMap, -13, Lit: true) },
        { "EVENT lampstate 13 0 maps/a.map", new LanMessage.LampState("maps/a.map", 13, Lit: false) },
    };

    [Theory]
    [MemberData(nameof(Reports))]
    public void Reports_become_the_world_messages_they_name(string line, LanMessage.WorldMessage message)
    {
        Assert.Equal(new GameEvent.WorldChanged(message), GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("EVENT sharedcallback lookat 12 maps/a.map")]
    [InlineData("EVENT sharedcallback playercollide 30 0 maps/a.map")]
    [InlineData("EVENT sharedcallback playercollide 30 +1 maps/a.map")]
    [InlineData("EVENT sharedcallback playercollide 30 1")]
    [InlineData("EVENT sharedcallback playercollide 30x 1 maps/a.map")]
    [InlineData("EVENT sharedcallback entitycollide 30 1 maps/a.map")]
    [InlineData("EVENT sharedcallback interact maps/a.map")]
    [InlineData("EVENT sharedcallback interact  12 maps/a.map")]
    [InlineData("EVENT sharedcallback useitem 12 maps/a.map")]
    [InlineData("EVENT sharedcallback useitem 12 keyé maps/a.map")]
    [InlineData("EVENT sharedcallback pickup maps/a.map")]
    [InlineData("EVENT sharedcallback pickup key_study maps/a.map")]
    [InlineData("EVENT sharedcallback flashback maps/a.map")]
    [InlineData("EVENT sharedcallback flashback 17")]
    [InlineData("EVENT sharedcallback combine hollow_needle_1 maps/a.map")]
    [InlineData("EVENT sharedcallback combine hollow_needle_1 stoné maps/a.map")]
    [InlineData("EVENT sharedcallback combine hollow_needle_1 stone_hammer_1")]
    [InlineData("EVENT sharedcallback")]
    [InlineData("EVENT buttonstate 12 2 maps/a.map")]
    [InlineData("EVENT buttonstate 12 1")]
    [InlineData("EVENT lampstate 12 -1 maps/a.map")]
    [InlineData("EVENT lampstate 2147483648 1 maps/a.map")]
    public void Malformed_reports_are_unknown(string line)
    {
        Assert.Equal(new GameEvent.Unknown(line), GameInteractionProtocol.ParseEvent(line));
    }

    [Fact]
    public void An_overlong_item_name_is_unknown()
    {
        var line = $"EVENT sharedcallback useitem 12 {new string('k', LanProtocol.MaximumItemNameLength + 1)} maps/a.map";

        Assert.Equal(new GameEvent.Unknown(line), GameInteractionProtocol.ParseEvent(line));
    }

    [Fact]
    public void An_overlong_combined_item_name_is_unknown()
    {
        var line = $"EVENT sharedcallback combine hollow_needle_1 {new string('k', LanProtocol.MaximumItemNameLength + 1)} maps/a.map";

        Assert.Equal(new GameEvent.Unknown(line), GameInteractionProtocol.ParseEvent(line));
    }

    public static TheoryData<LanMessage.WorldMessage, string> Commands => new()
    {
        { new LanMessage.PlayerCollide(StoryMap, 30, Entered: true), $"sharedcallback playercollide 30 1 {StoryMap}" },
        { new LanMessage.PlayerCollide("maps/a.map", -30, Entered: false), "sharedcallback playercollide -30 -1 maps/a.map" },
        { new LanMessage.EntityCollide("maps/a.map", 30, 12, Entered: false), "sharedcallback entitycollide 30 12 -1 maps/a.map" },
        { new LanMessage.Interact("maps/a.map", 12), "sharedcallback interact 12 maps/a.map" },
        { new LanMessage.UseItem(StoryMap, 12, "key_study"), $"sharedcallback useitem 12 key_study {StoryMap}" },
        { new LanMessage.Pickup(StoryMap, 42), $"sharedcallback pickup 42 {StoryMap}" },
        { new LanMessage.Flashback("maps/a.map", -17), "sharedcallback flashback -17 maps/a.map" },
        {
            new LanMessage.Combine(StoryMap, "hollow_needle_1", "stone_hammer_1"),
            $"sharedcallback combine hollow_needle_1 stone_hammer_1 {StoryMap}"
        },
        { new LanMessage.ButtonState("maps/a.map", 12, SwitchedOn: true), "buttonstate 12 1 maps/a.map" },
        { new LanMessage.LampState("maps/a.map", 13, Lit: false), "lampstate 13 0 maps/a.map" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Each_world_message_composes_the_Command_that_applies_it(LanMessage.WorldMessage message, string command)
    {
        Assert.Equal(command, GameInteractionProtocol.Apply(message));
    }

    [Fact]
    public void Map_Visit_starts_name_the_whole_map_path()
    {
        Assert.Equal(new GameEvent.MapEntered(StoryMap), GameInteractionProtocol.ParseEvent($"EVENT mapentered {StoryMap}"));
        Assert.Equal(new GameEvent.Unknown("EVENT mapentered "), GameInteractionProtocol.ParseEvent("EVENT mapentered "));
    }

    [Fact]
    public void Entity_releases_name_the_entity_and_the_map()
    {
        Assert.Equal(new GameEvent.EntityReleased(-12, StoryMap), GameInteractionProtocol.ParseEvent($"EVENT entityreleased -12 {StoryMap}"));
        Assert.Equal(new GameEvent.Unknown("EVENT entityreleased maps/a.map"), GameInteractionProtocol.ParseEvent("EVENT entityreleased maps/a.map"));
    }
}
