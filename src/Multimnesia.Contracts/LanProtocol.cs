using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Multimnesia.Contracts;

public abstract record LanMessage
{
    public sealed record JoinRequest(int ProtocolVersion, Guid PeerCorrelationId) : LanMessage;
    public sealed record AdmissionAccepted(int ProtocolVersion, Guid SessionCorrelationId) : LanMessage;
    public sealed record AdmissionRejected(string Reason) : LanMessage;
    // AuthorColor is the sender's Chat Color as six hex digits, or null for the game default.
    public sealed record ChatEntry(string Author, string Message, string? AuthorColor = null) : LanMessage;
    public sealed record Departure : LanMessage;
    public sealed record Heartbeat : LanMessage;
    public sealed record CustomStoryStarted(string Identifier) : LanMessage;
    public sealed record CustomStoryStartOutcome(string Identifier, SharedCustomStoryStartOutcome Outcome) : LanMessage;
    // The fields of a `STATE localpose` State Update, unchanged in meaning (ADR 0003).
    public sealed record Pose(
        ulong TimeMs, uint TeleportCounter, double X, double Y, double Z, double Yaw, double Pitch, bool Crouch, bool Lantern, string Map) : LanMessage;

    // One Shared Enemy, using the game's enemy report clock and animation fields (ADR 0009).
    public sealed record EnemyPose(
        string Map, int EntityId, ulong TimeMs, uint TeleportCounter,
        double X, double Y, double Z, double Yaw, string Clip, double ClipTime, bool Loop) : LanMessage;

    // What a Shared Enemy did in the Session Host's game, which simulates it, for every other game on the map (ADR 0009).
    // These ride the ordered session lane, and only the Session Host sends them.
    public abstract record EnemyEvent(string Map) : LanMessage;
    // One Shared Enemy attack at its hit-test moment, with the box it tests: its size, and its center offset along the
    // enemy body's right, up, and forward axes. It never carries damage; the victim's game rolls its own (ADR 0009).
    public sealed record EnemyAttack(
        string Map, int EntityId, EnemyAttackKind Attack,
        double SizeX, double SizeY, double SizeZ, double OffsetX, double OffsetY, double OffsetZ) : EnemyEvent(Map);
    // A Shared Enemy broke the map-placed door DoorId. Every other game breaks its own copy, and the debris stays local.
    public sealed record EnemyDoorBreak(string Map, int DoorId) : EnemyEvent(Map);

    // A Shared Death (ADR 0009): what the games agree on when a player dies on a map whose checkpoint has a death callback.
    // A death message names a checkpoint only by its bounded, script-chosen name; it never names the callback function.
    public abstract record DeathMessage(string Map) : LanMessage;
    // A Joining Player's own death, numbered by its game, on a map whose current checkpoint has a death callback.
    // AfterEpisode is the last Shared Death episode it received before the death, or 0: a death after an episode reached
    // the Joining Player is a new one, not one that episode already joined.
    public sealed record PlayerDeath(string Map, int Death, int AfterEpisode) : DeathMessage(Map);
    // One Shared Death episode as the Session Host's Game Peer decided it, numbered from 1 in that order. Death is the
    // Joining Player's reported death it answers, or 0 to kill the Joining Player; Count is the Session Host's
    // pre-increment checkpoint count, which every game passes to its own callback when its own checkpoint is CheckPoint.
    public sealed record SharedDeath(string Map, int Episode, int Death, int Count, string CheckPoint) : DeathMessage(Map);

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

    // A change one player made to a map's world that every other player's game makes for itself (ADR 0006). Each names a
    // Map Script registration, an item, a Flashback area, a button, or a lamp only by its map path and integer entity IDs,
    // plus bounded item names.
    public abstract record WorldMessage(string Map) : LanMessage;
    // A Shared Callback: the receiving game runs the callback its own Map Script registered for the same entities, or makes
    // the same pickup, combine, flashback, sticky attach, or enemy auto-disable with its own entities and inventory.
    public abstract record SharedCallback(string Map) : WorldMessage(Map);
    // A collide callback whose parent is "Player", with EntityId as its child; Entered is false when they stopped colliding.
    public sealed record PlayerCollide(string Map, int EntityId, bool Entered) : SharedCallback(Map);
    // A collide callback between two entities, one of which the sender Holds or is a Shared Enemy its game simulates.
    public sealed record EntityCollide(string Map, int ParentId, int EntityId, bool Entered) : SharedCallback(Map);
    public sealed record Interact(string Map, int EntityId) : SharedCallback(Map);
    public sealed record UseItem(string Map, int EntityId, string Item) : SharedCallback(Map);
    // The sender picked up the map-placed, non-consumable item EntityId.
    public sealed record Pickup(string Map, int EntityId) : SharedCallback(Map);
    // The sender set off the Flashback area EntityId, which started its flashback.
    public sealed record Flashback(string Map, int EntityId) : SharedCallback(Map);
    // The sender combined the inventory items ItemA and ItemB, which have a combine callback.
    public sealed record Combine(string Map, string ItemA, string ItemB) : SharedCallback(Map);
    // The sticky area AreaId attached the body BodyId of EntityId, which the sender Held.
    public sealed record StickyAttach(string Map, int AreaId, int EntityId, int BodyId) : SharedCallback(Map);
    // The Shared Enemy EntityId, which the sender's game simulates, reached the end of its patrol unseen and disabled itself;
    // the receiving game disables its own copy and runs its OnAutoDisabled callback.
    public sealed record AutoDisabled(string Map, int EntityId) : SharedCallback(Map);
    public sealed record ButtonState(string Map, int EntityId, bool SwitchedOn) : WorldMessage(Map);
    public sealed record LampState(string Map, int EntityId, bool Lit) : WorldMessage(Map);
}

public enum ClaimReason { Interact, Contact }

// Selects the damage range the victim's game rolls from, as the game's own attack kinds do.
public enum EnemyAttackKind { Normal, BreakDoor }

// A body state as the Game Interaction Protocol carries it: world translation, world rotation as a unit quaternion,
// linear velocity in world units per second, and angular velocity in degrees per second, along the world axes.
public sealed record BodyState(
    double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw,
    double Vx, double Vy, double Vz, double Wx, double Wy, double Wz);

// One body of a map-placed entity, named by the entity's ID in its map file and the body's ID in its entity file.
public sealed record BodyEntry(int PropId, int BodyId, BodyState State);

public enum SharedCustomStoryStartOutcome { Started, NotFound, Invalid, NotInMainMenu, Unavailable }

// A message read from the wire, with its framed size: the payload and its 4-byte length prefix.
public readonly record struct LanFrame(LanMessage Message, int Bytes);

public sealed class LanProtocolException(string message, Exception? innerException = null) : IOException(message, innerException);

public static class LanProtocol
{
    public const int CurrentVersion = 13;
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
    // The game's own bound on an item name in a Shared Callback, whose characters are printable ASCII without spaces.
    public const int MaximumItemNameLength = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // Returns the framed size: the payload and its 4-byte length prefix.
    public static async ValueTask<int> WriteAsync(Stream stream, LanMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes<object>(message switch
        {
            LanMessage.EnemyPose value when IsValid(value) => new
            {
                type = "enemy-pose", map = value.Map, entityId = value.EntityId, timeMs = value.TimeMs,
                teleportCounter = value.TeleportCounter, x = value.X, y = value.Y, z = value.Z, yaw = value.Yaw,
                clip = value.Clip, clipTime = value.ClipTime, loop = value.Loop
            },
            LanMessage.EnemyPose => throw new LanProtocolException("Invalid Enemy Pose."),
            LanMessage.EnemyAttack value when IsValid(value) => new
            {
                type = "enemy-attack", map = value.Map, entityId = value.EntityId, attack = AttackWireName(value.Attack),
                size = new[] { value.SizeX, value.SizeY, value.SizeZ }, offset = new[] { value.OffsetX, value.OffsetY, value.OffsetZ }
            },
            LanMessage.EnemyAttack => throw new LanProtocolException("Invalid enemy attack."),
            LanMessage.EnemyDoorBreak value when IsValid(value) => new { type = "enemy-door-break", map = value.Map, doorId = value.DoorId },
            LanMessage.EnemyDoorBreak => throw new LanProtocolException("Invalid enemy door break."),
            LanMessage.PlayerDeath value when IsValid(value) => new
            {
                type = "player-death", map = value.Map, death = value.Death, afterEpisode = value.AfterEpisode
            },
            LanMessage.SharedDeath value when IsValid(value) => new
            {
                type = "shared-death", map = value.Map, episode = value.Episode, death = value.Death, count = value.Count,
                checkPoint = value.CheckPoint
            },
            LanMessage.DeathMessage => throw new LanProtocolException("Invalid death message."),
            LanMessage.JoinRequest value => new { type = "join-request", protocolVersion = value.ProtocolVersion, peerCorrelationId = value.PeerCorrelationId },
            LanMessage.AdmissionAccepted value => new { type = "admission-accepted", protocolVersion = value.ProtocolVersion, sessionCorrelationId = value.SessionCorrelationId },
            LanMessage.AdmissionRejected value => new { type = "admission-rejected", reason = value.Reason },
            LanMessage.ChatEntry value when IsValidChat(value.Author, value.Message) =>
                new { type = "chat-entry", author = value.Author, message = value.Message, authorColor = NormalizedAuthorColor(value.AuthorColor) },
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
            LanMessage.WorldMessage value when !IsValidMap(value.Map) => throw new LanProtocolException("Invalid world message."),
            LanMessage.PlayerCollide value => new
            {
                type = "shared-callback", map = value.Map, kind = "player-collide", entityId = value.EntityId, state = CollideState(value.Entered)
            },
            LanMessage.EntityCollide value => new
            {
                type = "shared-callback",
                map = value.Map,
                kind = "entity-collide",
                parentId = value.ParentId,
                entityId = value.EntityId,
                state = CollideState(value.Entered)
            },
            LanMessage.Interact value => new { type = "shared-callback", map = value.Map, kind = "interact", entityId = value.EntityId },
            LanMessage.UseItem value when IsValidItemName(value.Item) => new
            {
                type = "shared-callback", map = value.Map, kind = "use-item", entityId = value.EntityId, item = value.Item
            },
            LanMessage.UseItem => throw new LanProtocolException("Invalid world message."),
            LanMessage.Pickup value => new { type = "shared-callback", map = value.Map, kind = "pickup", entityId = value.EntityId },
            LanMessage.Flashback value => new { type = "shared-callback", map = value.Map, kind = "flashback", entityId = value.EntityId },
            LanMessage.Combine value when IsValidItemName(value.ItemA) && IsValidItemName(value.ItemB) => new
            {
                type = "shared-callback", map = value.Map, kind = "combine", itemA = value.ItemA, itemB = value.ItemB
            },
            LanMessage.Combine => throw new LanProtocolException("Invalid world message."),
            LanMessage.StickyAttach value => new
            {
                type = "shared-callback", map = value.Map, kind = "sticky-attach", areaId = value.AreaId, entityId = value.EntityId,
                bodyId = value.BodyId
            },
            LanMessage.AutoDisabled value => new { type = "shared-callback", map = value.Map, kind = "auto-disabled", entityId = value.EntityId },
            LanMessage.ButtonState value => new { type = "button-state", map = value.Map, entityId = value.EntityId, switchedOn = value.SwitchedOn },
            LanMessage.LampState value => new { type = "lamp-state", map = value.Map, entityId = value.EntityId, lit = value.Lit },
            _ => throw new LanProtocolException("Unsupported LAN message type.")
        });
        if (payload.Length > MaximumFrameBytes) throw new LanProtocolException("LAN message exceeds the maximum frame size.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return header.Length + payload.Length;
    }

    public static async ValueTask<LanMessage> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        (await ReadFrameAsync(stream, cancellationToken)).Message;

    public static async ValueTask<LanFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
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
            LanMessage message = type switch
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
                "enemy-pose" => ReadEnemyPose(root),
                "enemy-attack" => ReadEnemyAttack(root),
                "enemy-door-break" => new LanMessage.EnemyDoorBreak(RequiredMap(root), RequiredInt32(root, "doorId")),
                "player-death" => ReadPlayerDeath(root),
                "shared-death" => ReadSharedDeath(root),
                "claim" => new LanMessage.Claim(
                    RequiredMap(root), RequiredInt32(root, "propId"), ParseReason(RequiredString(root, "reason", 32))),
                "claim-denied" => new LanMessage.ClaimDenied(RequiredMap(root), RequiredInt32(root, "propId")),
                "interaction" => new LanMessage.Interaction(
                    RequiredMap(root), RequiredInt32(root, "propId"), RequiredInt32(root, "bodyId"), root.GetProperty("active").GetBoolean()),
                "broke" => new LanMessage.Broke(RequiredMap(root), RequiredInt32(root, "propId"), RequiredBodyState(root)),
                "settled" => new LanMessage.Settled(RequiredMap(root), RequiredInt32(root, "propId")),
                "bodies" => ReadBodies(root),
                "shared-callback" => ReadSharedCallback(root),
                "button-state" => new LanMessage.ButtonState(
                    RequiredMap(root), RequiredInt32(root, "entityId"), root.GetProperty("switchedOn").GetBoolean()),
                "lamp-state" => new LanMessage.LampState(RequiredMap(root), RequiredInt32(root, "entityId"), root.GetProperty("lit").GetBoolean()),
                _ => throw new LanProtocolException("Unknown LAN message type.")
            };
            return new LanFrame(message, header.Length + length);
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
        var hasAuthorColor = root.TryGetProperty("authorColor", out var color) && color.ValueKind != JsonValueKind.Null;
        if (hasAuthorColor && color.ValueKind != JsonValueKind.String) throw new LanProtocolException("Invalid Chat Entry.");
        var entry = new LanMessage.ChatEntry(
            root.GetProperty("author").GetString()!, root.GetProperty("message").GetString()!,
            hasAuthorColor ? NormalizedAuthorColor(color.GetString()) : null);
        if (!IsValidChat(entry.Author, entry.Message)) throw new LanProtocolException("Invalid Chat Entry.");
        return entry;
    }

    // One case on the wire, so the receiving Game Peer never sees two spellings of one Chat Color.
    private static string? NormalizedAuthorColor(string? authorColor) => authorColor is null ? null
        : ChatColor.TryParse(authorColor, out var color) ? color.Hex
        : throw new LanProtocolException("Invalid Chat Entry.");

    // Clip names are identifiers, never command text. The game accepts one non-space field, at most 128 characters.
    public static bool IsValidEnemyClip(string? clip) =>
        clip is { Length: > 0 and <= 128 } && clip.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && !char.IsSurrogate(c));

    public static bool IsValid(LanMessage.EnemyPose pose) =>
        IsValidMap(pose.Map) && IsPoseNumber(pose.X) && IsPoseNumber(pose.Y) && IsPoseNumber(pose.Z) &&
        IsPoseNumber(pose.Yaw) && IsValidEnemyClip(pose.Clip) && IsPoseNumber(pose.ClipTime) && pose.ClipTime >= 0;

    private static LanMessage.EnemyPose ReadEnemyPose(JsonElement root)
    {
        var pose = new LanMessage.EnemyPose(
            RequiredMap(root), RequiredInt32(root, "entityId"), root.GetProperty("timeMs").GetUInt64(),
            root.GetProperty("teleportCounter").GetUInt32(), root.GetProperty("x").GetDouble(),
            root.GetProperty("y").GetDouble(), root.GetProperty("z").GetDouble(), root.GetProperty("yaw").GetDouble(),
            RequiredString(root, "clip", 128), root.GetProperty("clipTime").GetDouble(), root.GetProperty("loop").GetBoolean());
        return IsValid(pose) ? pose : throw new LanProtocolException("Invalid Enemy Pose.");
    }

    public static bool IsValid(LanMessage.EnemyAttack attack) =>
        IsValidMap(attack.Map) && Enum.IsDefined(attack.Attack) &&
        new[] { attack.SizeX, attack.SizeY, attack.SizeZ, attack.OffsetX, attack.OffsetY, attack.OffsetZ }.All(IsPoseNumber) &&
        attack.SizeX > 0 && attack.SizeY > 0 && attack.SizeZ > 0;

    private static string AttackWireName(EnemyAttackKind attack) => attack switch
    {
        EnemyAttackKind.Normal => "normal",
        EnemyAttackKind.BreakDoor => "break-door",
        _ => throw new LanProtocolException("Unknown enemy attack.")
    };

    private static EnemyAttackKind ParseAttack(string wireName) => wireName switch
    {
        "normal" => EnemyAttackKind.Normal,
        "break-door" => EnemyAttackKind.BreakDoor,
        _ => throw new LanProtocolException("Unknown enemy attack.")
    };

    public static bool IsValid(LanMessage.EnemyDoorBreak doorBreak) => IsValidMap(doorBreak.Map);

    private static LanMessage.EnemyAttack ReadEnemyAttack(JsonElement root)
    {
        var attackKind = ParseAttack(RequiredString(root, "attack", 32));
        var size = RequiredVector(root, "size", 3);
        var offset = RequiredVector(root, "offset", 3);
        var attack = new LanMessage.EnemyAttack(
            RequiredMap(root), RequiredInt32(root, "entityId"), attackKind, size[0], size[1], size[2], offset[0], offset[1], offset[2]);
        return IsValid(attack) ? attack : throw new LanProtocolException("Invalid enemy attack.");
    }

    public static bool IsValid(LanMessage.PlayerDeath death) => IsValidMap(death.Map) && death.Death > 0 && death.AfterEpisode >= 0;

    public static bool IsValid(LanMessage.SharedDeath death) =>
        IsValidMap(death.Map) && death.Episode > 0 && death.Death >= 0 && death.Count >= 0 && IsValidItemName(death.CheckPoint);

    private static LanMessage.PlayerDeath ReadPlayerDeath(JsonElement root)
    {
        var death = new LanMessage.PlayerDeath(RequiredMap(root), RequiredInt32(root, "death"), RequiredInt32(root, "afterEpisode"));
        return IsValid(death) ? death : throw new LanProtocolException("Invalid death message.");
    }

    // A checkpoint name has the bounds of an item name, so it is one field of a game line.
    private static LanMessage.SharedDeath ReadSharedDeath(JsonElement root)
    {
        var death = new LanMessage.SharedDeath(RequiredMap(root), RequiredInt32(root, "episode"), RequiredInt32(root, "death"),
            RequiredInt32(root, "count"), RequiredItemName(root, "checkPoint"));
        return IsValid(death) ? death : throw new LanProtocolException("Invalid death message.");
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

    // A function name or script text is never read: an unknown kind is malformed.
    private static LanMessage.SharedCallback ReadSharedCallback(JsonElement root)
    {
        var map = RequiredMap(root);
        return RequiredString(root, "kind", 32) switch
        {
            "player-collide" => new LanMessage.PlayerCollide(map, RequiredInt32(root, "entityId"), RequiredCollideState(root)),
            "entity-collide" => new LanMessage.EntityCollide(
                map, RequiredInt32(root, "parentId"), RequiredInt32(root, "entityId"), RequiredCollideState(root)),
            "interact" => new LanMessage.Interact(map, RequiredInt32(root, "entityId")),
            "use-item" => new LanMessage.UseItem(map, RequiredInt32(root, "entityId"), RequiredItemName(root, "item")),
            "pickup" => new LanMessage.Pickup(map, RequiredInt32(root, "entityId")),
            "flashback" => new LanMessage.Flashback(map, RequiredInt32(root, "entityId")),
            "combine" => new LanMessage.Combine(map, RequiredItemName(root, "itemA"), RequiredItemName(root, "itemB")),
            "sticky-attach" => new LanMessage.StickyAttach(
                map, RequiredInt32(root, "areaId"), RequiredInt32(root, "entityId"), RequiredInt32(root, "bodyId")),
            "auto-disabled" => new LanMessage.AutoDisabled(map, RequiredInt32(root, "entityId")),
            _ => throw new LanProtocolException("Unknown Shared Callback kind.")
        };
    }

    // The game's collide state: 1 when the bodies started colliding, -1 when they stopped.
    private static int CollideState(bool entered) => entered ? 1 : -1;

    private static bool RequiredCollideState(JsonElement root) => RequiredInt32(root, "state") switch
    {
        1 => true,
        -1 => false,
        _ => throw new LanProtocolException("Invalid collide state.")
    };

    private static string RequiredItemName(JsonElement root, string name)
    {
        var item = root.GetProperty(name).GetString();
        return IsValidItemName(item) ? item! : throw new LanProtocolException("Invalid item name.");
    }

    // 1 to 64 printable ASCII characters, '!' through '~', so an item name is one field of a game line.
    public static bool IsValidItemName(string? item) =>
        item is { Length: > 0 and <= MaximumItemNameLength } && item.All(character => character is >= '!' and <= '~');

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
