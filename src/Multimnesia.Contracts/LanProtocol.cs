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
    public sealed record Departure : LanMessage;
    public sealed record Heartbeat : LanMessage;
    public sealed record CustomStoryStarted(string Identifier) : LanMessage;
    public sealed record CustomStoryStartOutcome(string Identifier, SharedCustomStoryStartOutcome Outcome) : LanMessage;
    // The fields of a `STATE localpose` State Update, unchanged in meaning (ADR 0003).
    public sealed record Pose(
        ulong TimeMs, uint TeleportCounter, double X, double Y, double Z, double Yaw, double Pitch, bool Crouch, bool Lantern, string Map) : LanMessage;

    // The discrete Hold messages, which ride the ordered session lane (ADR 0004). Each names one map-placed entity by
    // its map path and the integer ID it has in its map file.
    public abstract record HoldMessage(string Map, int PropId) : LanMessage;
    public sealed record Claim(string Map, int PropId, ClaimReason Reason) : HoldMessage(Map, PropId);
    public sealed record ClaimDenied(string Map, int PropId) : HoldMessage(Map, PropId);
    // The Holder started (Active) or ended an interaction with body BodyId of the entity.
    public sealed record Interaction(string Map, int PropId, int BodyId, bool Active) : HoldMessage(Map, PropId);
    // The Held entity broke; State is the final state of the body whose transform the entity follows.
    public sealed record Broke(string Map, int PropId, BodyState State) : HoldMessage(Map, PropId);
    public sealed record Settled(string Map, int PropId) : HoldMessage(Map, PropId);

    // The Holder's bodies at TimeMs on their game clock: the latest-wins body stream, beside the Pose.
    public sealed record Bodies(ulong TimeMs, string Map, IReadOnlyList<BodyEntry> Entries) : LanMessage
    {
        public bool Equals(Bodies? other) =>
            other is not null && TimeMs == other.TimeMs && Map == other.Map && Entries.SequenceEqual(other.Entries);

        public override int GetHashCode() => HashCode.Combine(TimeMs, Map, Entries.Count);
    }
}

public enum ClaimReason { Interact, Contact }

// A body state as the Game Interaction Protocol carries it: world translation, world rotation as a unit quaternion,
// linear velocity in world units per second, and angular velocity in degrees per second, along the world axes.
public sealed record BodyState(
    double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw,
    double Vx, double Vy, double Vz, double Wx, double Wy, double Wz);

// One body of a map-placed entity, named by the entity's ID in its map file and the body's ID in its entity file.
public sealed record BodyEntry(int PropId, int BodyId, BodyState State);

public enum SharedCustomStoryStartOutcome { Started, NotFound, Invalid, NotInMainMenu, Unavailable }

public sealed class LanProtocolException(string message, Exception? innerException = null) : IOException(message, innerException);

public static class LanProtocol
{
    public const int CurrentVersion = 5;
    // Fits the largest bodies message: 32 entries of 13 longest-form numbers and a fully escaped map path.
    public const int MaximumFrameBytes = 32 * 1024;
    // Keeps a Pose's numbers within the game's 15-digit, 4-decimal line format; real positions and angles are far smaller.
    // Body states use the same bound, which is the game's own.
    public const double MaximumPoseMagnitude = 1e9;
    // Even fully escaped in JSON, a map path this long leaves the Pose within one frame.
    public const int MaximumPoseMapScalars = 256;
    // The game reports at most this many bodies at once.
    public const int MaximumBodies = 32;
    // The game's tolerance for a unit quaternion's length.
    public const double UnitQuaternionTolerance = 0.01;
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
            LanMessage.Departure => new { type = "departure" },
            LanMessage.Heartbeat => new { type = "heartbeat" },
            LanMessage.CustomStoryStarted value => new { type = "custom-story-started", identifier = ValidIdentifier(value.Identifier) },
            LanMessage.CustomStoryStartOutcome value => new
            {
                type = "custom-story-start-outcome",
                identifier = ValidIdentifier(value.Identifier),
                outcome = OutcomeWireName(value.Outcome)
            },
            LanMessage.Pose value when IsValid(value) => new
            {
                type = "pose",
                timeMs = value.TimeMs,
                teleportCounter = value.TeleportCounter,
                x = value.X,
                y = value.Y,
                z = value.Z,
                yaw = value.Yaw,
                pitch = value.Pitch,
                crouch = value.Crouch,
                lantern = value.Lantern,
                map = value.Map
            },
            LanMessage.Pose => throw new LanProtocolException("Invalid Pose."),
            LanMessage.HoldMessage value when !IsValidMap(value.Map) => throw new LanProtocolException("Invalid Hold message."),
            LanMessage.Claim value => new { type = "claim", map = value.Map, propId = value.PropId, reason = ReasonWireName(value.Reason) },
            LanMessage.ClaimDenied value => new { type = "claim-denied", map = value.Map, propId = value.PropId },
            LanMessage.Interaction value => new
            {
                type = "interaction", map = value.Map, propId = value.PropId, bodyId = value.BodyId, active = value.Active
            },
            LanMessage.Broke value when IsValid(value.State) => new
            {
                type = "broke",
                map = value.Map,
                propId = value.PropId,
                position = Position(value.State),
                orientation = Orientation(value.State),
                linearVelocity = LinearVelocity(value.State),
                angularVelocity = AngularVelocity(value.State)
            },
            LanMessage.Broke => throw new LanProtocolException("Invalid Hold message."),
            LanMessage.Settled value => new { type = "settled", map = value.Map, propId = value.PropId },
            LanMessage.Bodies value when IsValid(value) => new
            {
                type = "bodies",
                timeMs = value.TimeMs,
                map = value.Map,
                entries = value.Entries.Select(entry => new
                {
                    propId = entry.PropId,
                    bodyId = entry.BodyId,
                    position = Position(entry.State),
                    orientation = Orientation(entry.State),
                    linearVelocity = LinearVelocity(entry.State),
                    angularVelocity = AngularVelocity(entry.State)
                }).ToArray()
            },
            LanMessage.Bodies => throw new LanProtocolException("Invalid bodies message."),
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
                "departure" => new LanMessage.Departure(),
                "heartbeat" => new LanMessage.Heartbeat(),
                "custom-story-started" => new LanMessage.CustomStoryStarted(RequiredIdentifier(root)),
                "custom-story-start-outcome" => new LanMessage.CustomStoryStartOutcome(
                    RequiredIdentifier(root), ParseOutcome(RequiredString(root, "outcome", 32))),
                "pose" => ReadPose(root),
                "claim" => new LanMessage.Claim(
                    RequiredMap(root), RequiredInt32(root, "propId"), ParseReason(RequiredString(root, "reason", 32))),
                "claim-denied" => new LanMessage.ClaimDenied(RequiredMap(root), RequiredInt32(root, "propId")),
                "interaction" => new LanMessage.Interaction(
                    RequiredMap(root), RequiredInt32(root, "propId"), RequiredInt32(root, "bodyId"), root.GetProperty("active").GetBoolean()),
                "broke" => new LanMessage.Broke(RequiredMap(root), RequiredInt32(root, "propId"), RequiredBodyState(root)),
                "settled" => new LanMessage.Settled(RequiredMap(root), RequiredInt32(root, "propId")),
                "bodies" => ReadBodies(root),
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

    private static string RequiredIdentifier(JsonElement root)
    {
        var value = root.GetProperty("identifier").GetString();
        return CustomStoryIdentifier.IsValid(value) ? value! : throw new LanProtocolException("Invalid Custom Story Identifier.");
    }

    private static string ValidIdentifier(string identifier) =>
        CustomStoryIdentifier.IsValid(identifier) ? identifier : throw new LanProtocolException("Invalid Custom Story Identifier.");

    private static string OutcomeWireName(SharedCustomStoryStartOutcome outcome) => outcome switch
    {
        SharedCustomStoryStartOutcome.Started => "started",
        SharedCustomStoryStartOutcome.NotFound => "not-found",
        SharedCustomStoryStartOutcome.Invalid => "invalid",
        SharedCustomStoryStartOutcome.NotInMainMenu => "not-in-main-menu",
        SharedCustomStoryStartOutcome.Unavailable => "unavailable",
        _ => throw new LanProtocolException("Unknown Custom Story start outcome.")
    };

    private static SharedCustomStoryStartOutcome ParseOutcome(string wireName) => wireName switch
    {
        "started" => SharedCustomStoryStartOutcome.Started,
        "not-found" => SharedCustomStoryStartOutcome.NotFound,
        "invalid" => SharedCustomStoryStartOutcome.Invalid,
        "not-in-main-menu" => SharedCustomStoryStartOutcome.NotInMainMenu,
        "unavailable" => SharedCustomStoryStartOutcome.Unavailable,
        _ => throw new LanProtocolException("Unknown Custom Story start outcome.")
    };

    private static LanMessage.ChatEntry ReadChatEntry(JsonElement root)
    {
        var entry = new LanMessage.ChatEntry(root.GetProperty("author").GetString()!, root.GetProperty("message").GetString()!);
        if (!IsValidChat(entry.Author, entry.Message)) throw new LanProtocolException("Invalid Chat Entry.");
        return entry;
    }

    public static bool IsValid(LanMessage.Pose pose) =>
        IsPoseNumber(pose.X) && IsPoseNumber(pose.Y) && IsPoseNumber(pose.Z) && IsPoseNumber(pose.Yaw) && IsPoseNumber(pose.Pitch) &&
        IsValidMap(pose.Map);

    public static bool IsValid(LanMessage.Bodies bodies) =>
        IsValidMap(bodies.Map) && bodies.Entries is { Count: <= MaximumBodies } entries &&
        entries.All(entry => entry is not null && IsValid(entry.State));

    public static bool IsValid(BodyState? state)
    {
        if (state is null) return false;
        double[] numbers = [.. Position(state), .. Orientation(state), .. LinearVelocity(state), .. AngularVelocity(state)];
        if (!numbers.All(IsPoseNumber)) return false;
        var length = Math.Sqrt(state.Qx * state.Qx + state.Qy * state.Qy + state.Qz * state.Qz + state.Qw * state.Qw);
        return Math.Abs(length - 1) <= UnitQuaternionTolerance;
    }

    private static bool IsValidMap(string? map) => !string.IsNullOrEmpty(map) && IsValidField(map, MaximumPoseMapScalars, rejectColon: false);

    private static double[] Position(BodyState state) => [state.X, state.Y, state.Z];
    private static double[] Orientation(BodyState state) => [state.Qx, state.Qy, state.Qz, state.Qw];
    private static double[] LinearVelocity(BodyState state) => [state.Vx, state.Vy, state.Vz];
    private static double[] AngularVelocity(BodyState state) => [state.Wx, state.Wy, state.Wz];

    private static string ReasonWireName(ClaimReason reason) => reason switch
    {
        ClaimReason.Interact => "interact",
        ClaimReason.Contact => "contact",
        _ => throw new LanProtocolException("Unknown Claim reason.")
    };

    private static ClaimReason ParseReason(string wireName) => wireName switch
    {
        "interact" => ClaimReason.Interact,
        "contact" => ClaimReason.Contact,
        _ => throw new LanProtocolException("Unknown Claim reason.")
    };

    private static string RequiredMap(JsonElement root)
    {
        var map = root.GetProperty("map").GetString();
        return IsValidMap(map) ? map! : throw new LanProtocolException("Invalid map path.");
    }

    private static double[] RequiredVector(JsonElement element, string name, int length)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length) throw new LanProtocolException("Malformed LAN message.");
        return [.. value.EnumerateArray().Select(number => number.GetDouble())];
    }

    private static BodyState RequiredBodyState(JsonElement element)
    {
        var position = RequiredVector(element, "position", 3);
        var orientation = RequiredVector(element, "orientation", 4);
        var linear = RequiredVector(element, "linearVelocity", 3);
        var angular = RequiredVector(element, "angularVelocity", 3);
        var state = new BodyState(
            position[0], position[1], position[2], orientation[0], orientation[1], orientation[2], orientation[3],
            linear[0], linear[1], linear[2], angular[0], angular[1], angular[2]);
        return IsValid(state) ? state : throw new LanProtocolException("Invalid body state.");
    }

    private static LanMessage.Bodies ReadBodies(JsonElement root)
    {
        var timeMs = root.GetProperty("timeMs").GetUInt64();
        var map = RequiredMap(root);
        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumBodies)
            throw new LanProtocolException("Invalid bodies message.");
        return new(timeMs, map, [.. entries.EnumerateArray().Select(entry =>
            new BodyEntry(RequiredInt32(entry, "propId"), RequiredInt32(entry, "bodyId"), RequiredBodyState(entry)))]);
    }

    private static bool IsPoseNumber(double value) => double.IsFinite(value) && Math.Abs(value) <= MaximumPoseMagnitude;

    private static LanMessage.Pose ReadPose(JsonElement root)
    {
        var pose = new LanMessage.Pose(
            root.GetProperty("timeMs").GetUInt64(),
            root.GetProperty("teleportCounter").GetUInt32(),
            root.GetProperty("x").GetDouble(),
            root.GetProperty("y").GetDouble(),
            root.GetProperty("z").GetDouble(),
            root.GetProperty("yaw").GetDouble(),
            root.GetProperty("pitch").GetDouble(),
            root.GetProperty("crouch").GetBoolean(),
            root.GetProperty("lantern").GetBoolean(),
            root.GetProperty("map").GetString()!);
        return IsValid(pose) ? pose : throw new LanProtocolException("Invalid Pose.");
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
