using System.Text;
using Multimnesia.Client;

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
    public void Unknown_command_warnings_are_typed()
    {
        Assert.IsType<GameEvent.UnknownCommandWarned>(GameInteractionProtocol.ParseEvent("WARNING:Unknown command"));
    }

    [Theory]
    [InlineData("RESPONSE:startcustomstory:exploded", true)]
    [InlineData("WARNING:Unknown command", true)]
    [InlineData("RESPONSE:startcustomstory:starting", false)]
    [InlineData("RESPONSE:startcustomstory:not in main menu", false)]
    [InlineData("RESPONSE:chat:message displayed", false)]
    [InlineData("EVENT:CustomStoryStarted:mp-test-cs", false)]
    public void Replies_that_make_a_start_unavailable_are_recognized_for_logging(string line, bool expected)
    {
        Assert.Equal(expected, GameInteractionProtocol.IsUnrecognizedReply(GameInteractionProtocol.ParseEvent(line)));
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
    public async Task Chat_commands_are_written_as_UTF8_newline_frames()
    {
        await using var stream = new MemoryStream();
        await using (var writer = GameInteractionProtocol.CreateWriter(stream))
            await writer.WriteLineAsync(GameInteractionProtocol.Display(new ChatEntry("SYSTEM", "Příliš žluťoučký 👋")));

        Assert.Equal("chat:SYSTEM:Příliš žluťoučký 👋\n", Encoding.UTF8.GetString(stream.ToArray()));
    }
}
