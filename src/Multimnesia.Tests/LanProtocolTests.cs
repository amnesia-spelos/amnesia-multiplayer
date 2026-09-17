using System.Buffers.Binary;
using System.Text;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class LanProtocolTests
{
    [Fact]
    public async Task Messages_are_length_prefixed_UTF8_JSON_and_round_trip()
    {
        await using var stream = new MemoryStream();
        var message = new LanMessage.JoinRequest(LanProtocol.CurrentVersion, Guid.Parse("11111111-1111-1111-1111-111111111111"));

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);

        var bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.StartsWith("{\"type\":\"join-request\"", Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4));
        stream.Position = 0;
        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Oversized_frames_are_rejected_before_the_payload_is_read()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, LanProtocol.MaximumFrameBytes + 1);
        await using var stream = new MemoryStream(header);

        var error = await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("LAN message exceeds the maximum frame size.", error.Message);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task Unknown_message_types_are_rejected()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"surprise\"}");
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Missing_required_fields_are_reported_as_malformed_protocol_input()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"join-request\"}");
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        await using var stream = new MemoryStream(bytes);

        var error = await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Malformed LAN message.", error.Message);
    }

    [Fact]
    public async Task Chat_limits_count_Unicode_scalars_and_preserve_the_entry_whole()
    {
        var message = new LanMessage.ChatEntry(string.Concat(Enumerable.Repeat("👩‍🚀", 10)) + "ab", new string('m', 255) + "👋");
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    public static TheoryData<string, string> InvalidChat => new()
    {
        { new string('a', 33), "hello" },
        { "Alice", new string('m', 257) },
        { "SYSTEM", "forged feedback" },
        { "Alice", "/leave" },
        { "Alice", "line\nbreak" },
    };

    [Theory]
    [MemberData(nameof(InvalidChat))]
    public async Task Invalid_chat_is_rejected_whole_at_the_LAN_boundary(string author, string message)
    {
        await using var stream = new MemoryStream();

        var error = await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, new LanMessage.ChatEntry(author, message), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Invalid Chat Entry.", error.Message);
        Assert.Empty(stream.ToArray());
    }
}
