using System.Text;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class GameInteractionProtocolTests
{
    [Fact]
    public async Task Newline_framing_preserves_UTF8_chat()
    {
        var bytes = Encoding.UTF8.GetBytes("EVENT:CHAT:Žofie:ahoj: všichni 👋\n");
        await using var stream = new MemoryStream(bytes);
        using var reader = GameInteractionProtocol.CreateReader(stream);

        var entry = Assert.IsType<GameEvent.ChatSubmitted>(
            GameInteractionProtocol.ParseEvent(await reader.ReadLineAsync(TestContext.Current.CancellationToken)));

        Assert.Equal(new ChatEntry("Žofie", "ahoj: všichni 👋"), entry.Entry);
    }

    [Fact]
    public void Chat_fields_are_trimmed_before_scalar_limits_are_applied()
    {
        var author = new string('a', 32);
        var message = new string('m', 256);

        var parsed = Assert.IsType<GameEvent.ChatSubmitted>(
            GameInteractionProtocol.ParseEvent($"EVENT:CHAT: {author} : {message} "));

        Assert.Equal(new ChatEntry(author, message), parsed.Entry);
    }

    [Theory]
    [InlineData("EVENT:CHAT::hello")]
    [InlineData("EVENT:CHAT:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:hello")]
    [InlineData("EVENT:CHAT:Alice:")]
    [InlineData("EVENT:CHAT:SYSTEM:forged feedback")]
    public void Invalid_chat_entries_are_rejected_whole(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Fact]
    public void Custom_story_started_events_carry_their_identifier()
    {
        var parsed = Assert.IsType<GameEvent.CustomStoryStarted>(
            GameInteractionProtocol.ParseEvent("EVENT:CustomStoryStarted:mp-test-cs"));

        Assert.Equal("mp-test-cs", parsed.Identifier);
    }

    [Theory]
    [InlineData("EVENT:CustomStoryStarted:")]
    [InlineData("EVENT:CustomStoryStarted:a:b")]
    [InlineData("EVENT:CustomStoryStarted:a|b")]
    [InlineData("EVENT:CustomStoryStarted:a\tb")]
    [InlineData("EVENT:CustomStoryStartedmp-test-cs")]
    public void Invalid_custom_story_identifiers_are_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Fact]
    public void Overlong_custom_story_identifiers_are_unknown()
    {
        Assert.IsType<GameEvent.Unknown>(
            GameInteractionProtocol.ParseEvent("EVENT:CustomStoryStarted:" + new string('a', 129)));
    }

    [Theory]
    [InlineData("RESPONSE:startcustomstory:starting", StartCustomStoryOutcome.Starting)]
    [InlineData("RESPONSE:startcustomstory:not found", StartCustomStoryOutcome.NotFound)]
    [InlineData("RESPONSE:startcustomstory:invalid", StartCustomStoryOutcome.Invalid)]
    [InlineData("RESPONSE:startcustomstory:not in main menu", StartCustomStoryOutcome.NotInMainMenu)]
    [InlineData("RESPONSE:startcustomstory:exploded", StartCustomStoryOutcome.Unrecognized)]
    [InlineData("RESPONSE:startcustomstory:", StartCustomStoryOutcome.Unrecognized)]
    public void Start_custom_story_responses_are_typed(string line, StartCustomStoryOutcome expected)
    {
        var parsed = Assert.IsType<GameEvent.StartCustomStoryResponded>(GameInteractionProtocol.ParseEvent(line));

        Assert.Equal(expected, parsed.Outcome);
    }

    [Fact]
    public void Main_story_started_events_are_typed()
    {
        Assert.IsType<GameEvent.MainStoryStarted>(GameInteractionProtocol.ParseEvent("EVENT:MainStoryStarted"));
    }

    [Theory]
    [InlineData("EVENT:MainStoryStarted:")]
    [InlineData("EVENT:MainStoryStarted:mp-test-cs")]
    [InlineData("EVENT:MainStoryStartedx")]
    public void Main_story_started_events_with_anything_after_them_are_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("RESPONSE:startmainstory:starting", StartMainStoryOutcome.Starting)]
    [InlineData("RESPONSE:startmainstory:not in main menu", StartMainStoryOutcome.NotInMainMenu)]
    [InlineData("RESPONSE:startmainstory:not found", StartMainStoryOutcome.Unrecognized)]
    [InlineData("RESPONSE:startmainstory:exploded", StartMainStoryOutcome.Unrecognized)]
    [InlineData("RESPONSE:startmainstory:", StartMainStoryOutcome.Unrecognized)]
    public void Start_main_story_responses_are_typed(string line, StartMainStoryOutcome expected)
    {
        var parsed = Assert.IsType<GameEvent.StartMainStoryResponded>(GameInteractionProtocol.ParseEvent(line));

        Assert.Equal(expected, parsed.Outcome);
    }

    [Fact]
    public void Unknown_command_warnings_are_typed()
    {
        Assert.IsType<GameEvent.UnknownCommandWarned>(GameInteractionProtocol.ParseEvent("WARNING:Unknown command"));
    }

    [Fact]
    public void Ping_answers_are_typed()
    {
        Assert.IsType<GameEvent.Ponged>(GameInteractionProtocol.ParseEvent("RESPONSE:ping:pong"));
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent("RESPONSE:ping:pongs"));
    }

    [Theory]
    [InlineData("RESPONSE:startcustomstory:exploded", true)]
    [InlineData("WARNING:Unknown command", true)]
    [InlineData("RESPONSE:startcustomstory:starting", false)]
    [InlineData("RESPONSE:startcustomstory:not in main menu", false)]
    [InlineData("RESPONSE chatentry unavailable", false)]
    [InlineData("EVENT:CustomStoryStarted:mp-test-cs", false)]
    [InlineData("RESPONSE:startmainstory:exploded", true)]
    [InlineData("RESPONSE:startmainstory:starting", false)]
    [InlineData("EVENT:MainStoryStarted", false)]
    public void Replies_that_make_a_start_unavailable_are_recognized_for_logging(string line, bool expected)
    {
        Assert.Equal(expected, GameInteractionProtocol.IsUnrecognizedReply(GameInteractionProtocol.ParseEvent(line)));
    }

    [Theory]
    [InlineData("RESPONSE chatentry ok", ChatEntryOutcome.Ok)]
    [InlineData("RESPONSE chatentry invalid", ChatEntryOutcome.Invalid)]
    [InlineData("RESPONSE chatentry invalid-author", ChatEntryOutcome.InvalidAuthor)]
    [InlineData("RESPONSE chatentry invalid-message", ChatEntryOutcome.InvalidMessage)]
    [InlineData("RESPONSE chatentry unavailable", ChatEntryOutcome.Unavailable)]
    public void Chat_entry_responses_are_typed(string line, ChatEntryOutcome expected)
    {
        Assert.Equal(new GameEvent.ChatEntryResponded(expected), GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("RESPONSE chatentry")]
    [InlineData("RESPONSE chatentry exploded")]
    [InlineData("RESPONSE chatentry ok extra")]
    public void Malformed_chat_entry_responses_are_not_typed(string line)
    {
        Assert.IsNotType<GameEvent.ChatEntryResponded>(GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("RESPONSE:chat:unavailable")]
    [InlineData("EVENT:MapChanged:multiplayer-test.map")]
    [InlineData("")]
    public void Other_lines_remain_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Fact]
    public void Start_custom_story_command_names_the_identifier()
    {
        Assert.Equal("startcustomstory:mp-test-cs", GameInteractionProtocol.StartCustomStory("mp-test-cs"));
    }

    [Fact]
    public void Start_main_story_command_is_bare()
    {
        Assert.Equal("startmainstory", GameInteractionProtocol.StartMainStory);
    }

    [Fact]
    public async Task Chat_commands_are_written_as_UTF8_newline_frames()
    {
        await using var stream = new MemoryStream();
        await using (var writer = GameInteractionProtocol.CreateWriter(stream))
            await writer.WriteLineAsync(GameInteractionProtocol.Display(new ChatEntry("SYSTEM", "Příliš žluťoučký 👋")));

        Assert.Equal("chatentry - - SYSTEM:Příliš žluťoučký 👋\n", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void A_chat_entry_without_a_chat_color_is_displayed_in_default_colors()
    {
        Assert.Equal("chatentry - - Alice:hello: there", GameInteractionProtocol.Display(new ChatEntry("Alice", "hello: there")));
    }

    [Fact]
    public void A_chat_color_applies_to_the_chat_author_name_only()
    {
        Assert.True(ChatColor.TryParse("ff00ff", out var color));

        Assert.Equal("chatentry FF00FF - Alice:hi", GameInteractionProtocol.Display(new ChatEntry("Alice", "hi", color)));
    }

    [Fact]
    public void A_message_color_applies_to_the_chat_message()
    {
        Assert.True(ChatColor.TryParse("00ff00", out var color));

        Assert.Equal("chatentry - 00FF00 SYSTEM:ready", GameInteractionProtocol.Display(new ChatEntry("SYSTEM", "ready", MessageColor: color)));
    }
}
