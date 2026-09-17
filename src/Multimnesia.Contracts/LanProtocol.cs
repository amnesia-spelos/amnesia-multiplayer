using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Multimnesia.Contracts;

public abstract record LanMessage
{
    public sealed record JoinRequest(int ProtocolVersion, Guid PeerCorrelationId) : LanMessage;
    public sealed record AdmissionAccepted(int ProtocolVersion, Guid SessionCorrelationId) : LanMessage;
    public sealed record AdmissionRejected(string Reason) : LanMessage;
    public sealed record ChatEntry(string Author, string Message) : LanMessage;
}

public sealed class LanProtocolException(string message, Exception? innerException = null) : IOException(message, innerException);

public static class LanProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async ValueTask WriteAsync(Stream stream, LanMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes<object>(message switch
        {
            LanMessage.JoinRequest value => new { type = "join-request", protocolVersion = value.ProtocolVersion, peerCorrelationId = value.PeerCorrelationId },
            LanMessage.AdmissionAccepted value => new { type = "admission-accepted", protocolVersion = value.ProtocolVersion, sessionCorrelationId = value.SessionCorrelationId },
            LanMessage.AdmissionRejected value => new { type = "admission-rejected", reason = value.Reason },
            LanMessage.ChatEntry value when IsValidChat(value.Author, value.Message) =>
                new { type = "chat-entry", author = value.Author, message = value.Message },
            LanMessage.ChatEntry => throw new LanProtocolException("Invalid Chat Entry."),
            _ => throw new LanProtocolException("Unsupported LAN message type.")
        });
        if (payload.Length > MaximumFrameBytes) throw new LanProtocolException("LAN message exceeds the maximum frame size.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<LanMessage> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new LanProtocolException("LAN message exceeds the maximum frame size.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var type = RequiredString(root, "type", 32);
            return type switch
            {
                "join-request" => new LanMessage.JoinRequest(
                    RequiredInt32(root, "protocolVersion"), RequiredGuid(root, "peerCorrelationId")),
                "admission-accepted" => new LanMessage.AdmissionAccepted(
                    RequiredInt32(root, "protocolVersion"), RequiredGuid(root, "sessionCorrelationId")),
                "admission-rejected" => new LanMessage.AdmissionRejected(RequiredString(root, "reason", 256)),
                "chat-entry" => ReadChatEntry(root),
                _ => throw new LanProtocolException("Unknown LAN message type.")
            };
        }
        catch (LanProtocolException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new LanProtocolException("Malformed LAN message.", exception);
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { await stream.ReadExactlyAsync(buffer, cancellationToken); }
        catch (EndOfStreamException exception) { throw new LanProtocolException("Incomplete LAN message.", exception); }
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength)
    {
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength) throw new LanProtocolException("Malformed LAN message.");
        return value;
    }

    private static int RequiredInt32(JsonElement root, string name) => root.GetProperty(name).GetInt32();

    private static Guid RequiredGuid(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (!value.TryGetGuid(out var result) || result == Guid.Empty) throw new LanProtocolException("Malformed LAN message.");
        return result;
    }

    private static LanMessage.ChatEntry ReadChatEntry(JsonElement root)
    {
        var entry = new LanMessage.ChatEntry(root.GetProperty("author").GetString()!, root.GetProperty("message").GetString()!);
        if (!IsValidChat(entry.Author, entry.Message)) throw new LanProtocolException("Invalid Chat Entry.");
        return entry;
    }

    private static bool IsValidChat(string? author, string? message) =>
        IsValidField(author, 32, rejectColon: true) &&
        IsValidField(message, 256, rejectColon: false) &&
        !author!.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) &&
        !message!.StartsWith('/');

    private static bool IsValidField(string? value, int maximumScalars, bool rejectColon)
    {
        if (string.IsNullOrWhiteSpace(value) || (rejectColon && value.Contains(':'))) return false;
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control) return false;
            if (++count > maximumScalars) return false;
        }
        return true;
    }
}
