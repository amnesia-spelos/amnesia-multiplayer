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
    public async Task Chat_commands_are_written_as_UTF8_newline_frames()
    {
        await using var stream = new MemoryStream();
        await using (var writer = GameInteractionProtocol.CreateWriter(stream))
            await writer.WriteLineAsync(GameInteractionProtocol.Display(new ChatEntry("SYSTEM", "Příliš žluťoučký 👋")));

        Assert.Equal("chat:SYSTEM:Příliš žluťoučký 👋\n", Encoding.UTF8.GetString(stream.ToArray()));
    }
}
