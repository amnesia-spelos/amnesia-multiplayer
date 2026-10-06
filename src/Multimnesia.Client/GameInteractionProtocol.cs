using System.Globalization;
using System.Text;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed record ChatEntry(string Author, string Message)
{
    private const string LocalSubmissionPrefix = "EVENT:CHAT:";

    public static bool TryParseLocalSubmission(string line, out ChatEntry entry)
    {
        entry = default!;
        if (!line.StartsWith(LocalSubmissionPrefix, StringComparison.Ordinal)) return false;

        var authorEnd = line.IndexOf(':', LocalSubmissionPrefix.Length);
        if (authorEnd < 0) return false;
        var author = line[LocalSubmissionPrefix.Length..authorEnd].Trim();
        var message = line[(authorEnd + 1)..].Trim();
        if (author.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
            !IsValid(author, 32, rejectColon: true) || !IsValid(message, 256, rejectColon: false)) return false;

        entry = new(author, message);
        return true;
    }

    private static bool IsValid(string value, int maximumScalars, bool rejectColon)
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

public static class GameInteractionProtocol
{
    private const string CustomStoryStartedPrefix = "EVENT:CustomStoryStarted:";
    private const string StartCustomStoryResponsePrefix = "RESPONSE:startcustomstory:";
    private const string UnknownCommandWarning = "WARNING:Unknown command";
    private const string PongResponse = "RESPONSE:ping:pong";
    private const string LocalPoseStatePrefix = "STATE localpose ";
    private const string ReportedBodiesStatePrefix = "STATE reportedbodies ";
    // Legacy Events continue with ':' instead.
    private const string Version2EventPrefix = "EVENT ";
    private const int BodyEntryFieldCount = 2 + ProtocolVersion2Line.BodyStateFieldCount;
    // Legacy Responses continue with ':' instead.
    private const string Version2ResponsePrefix = "RESPONSE ";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static StreamReader CreateReader(Stream stream) =>
        new(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

    public static StreamWriter CreateWriter(Stream stream) =>
        new(stream, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

    public static GameEvent ParseEvent(string? line)
    {
        if (line is null) return new GameEvent.Unknown(string.Empty);
        if (ChatEntry.TryParseLocalSubmission(line, out var entry)) return new GameEvent.ChatSubmitted(entry);
        if (line.StartsWith(CustomStoryStartedPrefix, StringComparison.Ordinal))
        {
            var identifier = line[CustomStoryStartedPrefix.Length..];
            return CustomStoryIdentifier.IsValid(identifier)
                ? new GameEvent.CustomStoryStarted(identifier)
                : new GameEvent.Unknown(line);
        }
        if (line.StartsWith(StartCustomStoryResponsePrefix, StringComparison.Ordinal))
            return new GameEvent.StartCustomStoryResponded(line[StartCustomStoryResponsePrefix.Length..] switch
            {
                "starting" => StartCustomStoryOutcome.Starting,
                "not found" => StartCustomStoryOutcome.NotFound,
                "invalid" => StartCustomStoryOutcome.Invalid,
                "not in main menu" => StartCustomStoryOutcome.NotInMainMenu,
                _ => StartCustomStoryOutcome.Unrecognized
            });
        if (line == UnknownCommandWarning) return new GameEvent.UnknownCommandWarned();
        if (line == PongResponse) return new GameEvent.Ponged();
        if (line.StartsWith(LocalPoseStatePrefix, StringComparison.Ordinal))
            return TryParseLocalPose(line, out var pose) ? new GameEvent.LocalPoseReported(pose) : new GameEvent.Unknown(line);
        if (line.StartsWith(ReportedBodiesStatePrefix, StringComparison.Ordinal))
            return TryParseReportedBodies(line) ?? new GameEvent.Unknown(line);
        if (line.StartsWith(Version2EventPrefix, StringComparison.Ordinal))
            return TryParseInteractionEvent(line) ?? new GameEvent.Unknown(line);
        if (line.StartsWith(Version2ResponsePrefix, StringComparison.Ordinal))
        {
            // RESPONSE <keyword> <outcome> [<field>...]
            var fields = line.Split(' ');
            return fields.Length >= 3 && !fields.Contains(string.Empty)
                ? new GameEvent.Responded(fields[1], fields[2], fields[3..])
                : new GameEvent.Unknown(line);
        }
        return new GameEvent.Unknown(line);
    }

    // STATE localpose <timeMs> <teleportCounter> <x> <y> <z> <yaw> <pitch> <crouch> <lantern> <map>
    private static bool TryParseLocalPose(string line, out LocalPose pose)
    {
        pose = default!;
        if (!ProtocolVersion2Line.TrySplit(line, fixedFieldCount: 11, hasPath: true, out var fields) ||
            !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var timeMs) ||
            !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var teleportCounter) ||
            !ProtocolVersion2Line.TryParseNumber(fields[4], out var x) ||
            !ProtocolVersion2Line.TryParseNumber(fields[5], out var y) ||
            !ProtocolVersion2Line.TryParseNumber(fields[6], out var z) ||
            !ProtocolVersion2Line.TryParseNumber(fields[7], out var yaw) ||
            !ProtocolVersion2Line.TryParseNumber(fields[8], out var pitch) ||
            fields[9] is not ("0" or "1") ||
            fields[10] is not ("0" or "1")) return false;

        pose = new(timeMs, teleportCounter, x, y, z, yaw, pitch, fields[9] == "1", fields[10] == "1", fields[11]);
        return true;
    }

    // EVENT interactionstart <entityId> <bodyId> <map>, EVENT interactionend <entityId> <bodyId> <ending> <map>,
    // EVENT reportcontact|reportsettled <entityId> <map>, EVENT reportbroke <entityId> <state> <map>
    private static GameEvent? TryParseInteractionEvent(string line)
    {
        var name = line.Split(' ', 3)[1];
        var fixedFieldCount = name switch
        {
            "interactionstart" => 4,
            "interactionend" => 5,
            "reportcontact" or "reportsettled" => 3,
            "reportbroke" => 3 + ProtocolVersion2Line.BodyStateFieldCount,
            _ => 0
        };
        if (fixedFieldCount == 0 ||
            !ProtocolVersion2Line.TrySplit(line, fixedFieldCount, hasPath: true, out var fields) ||
            !ProtocolVersion2Line.TryParseIdentifier(fields[2], out var entityId)) return null;
        var map = fields[fixedFieldCount];
        switch (name)
        {
            case "interactionstart" when ProtocolVersion2Line.TryParseIdentifier(fields[3], out var bodyId):
                return new GameEvent.InteractionStarted(entityId, bodyId, map);
            case "interactionend" when ProtocolVersion2Line.TryParseIdentifier(fields[3], out var bodyId) && ParseEnding(fields[4]) is { } ending:
                return new GameEvent.InteractionEnded(entityId, bodyId, ending, map);
            case "reportcontact":
                return new GameEvent.ReportContacted(entityId, map);
            case "reportsettled":
                return new GameEvent.ReportSettled(entityId, map);
            case "reportbroke" when ProtocolVersion2Line.TryParseBodyState(fields.AsSpan(3, ProtocolVersion2Line.BodyStateFieldCount), out var state):
                return new GameEvent.ReportBroke(entityId, state, map);
            default:
                return null;
        }
    }

    private static InteractionEnding? ParseEnding(string wireName) => wireName switch
    {
        "released" => InteractionEnding.Released,
        "thrown" => InteractionEnding.Thrown,
        "too-far" => InteractionEnding.TooFar,
        "destroyed" => InteractionEnding.Destroyed,
        _ => null
    };

    // STATE reportedbodies <timeMs> <count> <entry>... <map>, where each entry is <entityId> <bodyId> <state>
    private static GameEvent? TryParseReportedBodies(string line)
    {
        if (!ProtocolVersion2Line.TrySplit(line, fixedFieldCount: 4, hasPath: true, out var header) ||
            !ulong.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out var timeMs) ||
            !int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count > LanProtocol.MaximumBodies ||
            !ProtocolVersion2Line.TrySplit(header[4], count * BodyEntryFieldCount, hasPath: true, out var fields)) return null;

        var entries = new BodyEntry[count];
        for (var index = 0; index < count; index++)
        {
            var entry = fields.AsSpan(index * BodyEntryFieldCount, BodyEntryFieldCount);
            if (!ProtocolVersion2Line.TryParseIdentifier(entry[0], out var entityId) ||
                !ProtocolVersion2Line.TryParseIdentifier(entry[1], out var bodyId) ||
                !ProtocolVersion2Line.TryParseBodyState(entry[2..], out var state)) return null;
            entries[index] = new(entityId, bodyId, state);
        }
        return new GameEvent.ReportedBodies(timeMs, entries, fields[^1]);
    }

    // Replies that LocalGameCommands turns into an Unrecognized start, which the Joining Player reports as unavailable.
    public static bool IsUnrecognizedReply(GameEvent gameEvent) => gameEvent is
        GameEvent.StartCustomStoryResponded { Outcome: StartCustomStoryOutcome.Unrecognized } or GameEvent.UnknownCommandWarned;

    public static string Display(ChatEntry entry) => $"chat:{entry.Author}:{entry.Message}";

    public static string StartCustomStory(string identifier) => $"startcustomstory:{identifier}";

    // The first Command of every game Session; the Shared Pose needs the first two Capabilities, Holds the third.
    public const string Negotiate =
        $"protocol 2 {ProtocolNegotiation.Avatars} {ProtocolNegotiation.LocalPose} {ProtocolNegotiation.Interactions}";

    // Uses the default model, with collision on.
    public static string AvatarCreate(string avatarIdentifier) => $"avatarcreate {avatarIdentifier}";

    public static string AvatarRemove(string avatarIdentifier) => $"avatarremove {avatarIdentifier}";

    // avatarpose <id> <timeMs> <teleportCounter> <x> <y> <z> <yaw> <pitch> <crouch> <lantern> <map>
    public static string AvatarPose(string avatarIdentifier, LanMessage.Pose pose) => string.Join(' ',
        "avatarpose", avatarIdentifier,
        pose.TimeMs.ToString(CultureInfo.InvariantCulture),
        pose.TeleportCounter.ToString(CultureInfo.InvariantCulture),
        ProtocolVersion2Line.FormatNumber(pose.X),
        ProtocolVersion2Line.FormatNumber(pose.Y),
        ProtocolVersion2Line.FormatNumber(pose.Z),
        ProtocolVersion2Line.FormatNumber(pose.Yaw),
        ProtocolVersion2Line.FormatNumber(pose.Pitch),
        pose.Crouch ? "1" : "0",
        pose.Lantern ? "1" : "0",
        pose.Map);

    public static string SubscribeLocalPose(int hz) => $"localpose subscribe {hz.ToString(CultureInfo.InvariantCulture)}";

    public const string UnsubscribeLocalPose = "localpose unsubscribe";

    public static string SubscribeReportedBodies(int hz) => $"reportedbodies subscribe {hz.ToString(CultureInfo.InvariantCulture)}";

    public const string UnsubscribeReportedBodies = "reportedbodies unsubscribe";

    // Every Peer-Driven Entity Command ends with the map the Game Peer means, so it never reaches an entity in another map.
    public static string EntityDrive(int entityId, string map) =>
        $"entitydrive {ProtocolVersion2Line.FormatIdentifier(entityId)} {map}";

    public static string EntityInteracting(int entityId, bool active, string map) =>
        $"entityinteracting {ProtocolVersion2Line.FormatIdentifier(entityId)} {(active ? "1" : "0")} {map}";

    public static string EntityRelease(int entityId, string map) =>
        $"entityrelease {ProtocolVersion2Line.FormatIdentifier(entityId)} {map}";

    public static string EntityBreak(int entityId, BodyState state, string map) =>
        $"entitybreak {ProtocolVersion2Line.FormatIdentifier(entityId)} {ProtocolVersion2Line.FormatBodyState(state)} {map}";

    // entitybodies <timeMs> <count> [<entry>...] <map>
    public static string EntityBodies(ulong timeMs, IReadOnlyList<BodyEntry> entries, string map) => string.Join(' ',
    [
        "entitybodies",
        timeMs.ToString(CultureInfo.InvariantCulture),
        entries.Count.ToString(CultureInfo.InvariantCulture),
        .. entries.Select(entry => string.Join(' ',
            ProtocolVersion2Line.FormatIdentifier(entry.PropId),
            ProtocolVersion2Line.FormatIdentifier(entry.BodyId),
            ProtocolVersion2Line.FormatBodyState(entry.State))),
        map
    ]);

    // A legacy Command; the game answers it after processing every line written before it.
    public const string Ping = "ping";
}

public enum StartCustomStoryOutcome { Starting, NotFound, Invalid, NotInMainMenu, Unrecognized }

public enum InteractionEnding { Released, Thrown, TooFar, Destroyed }

public abstract record GameEvent
{
    public sealed record ChatSubmitted(ChatEntry Entry) : GameEvent;
    public sealed record CustomStoryStarted(string Identifier) : GameEvent;
    public sealed record StartCustomStoryResponded(StartCustomStoryOutcome Outcome) : GameEvent;
    public sealed record UnknownCommandWarned : GameEvent;
    public sealed record Ponged : GameEvent;
    public sealed record LocalPoseReported(LocalPose Pose) : GameEvent;
    // The `interactions` Events about the local player's interactions, and the report of the bodies they decide.
    public sealed record InteractionStarted(int EntityId, int BodyId, string Map) : GameEvent;
    public sealed record InteractionEnded(int EntityId, int BodyId, InteractionEnding Ending, string Map) : GameEvent;
    public sealed record ReportContacted(int EntityId, string Map) : GameEvent;
    public sealed record ReportSettled(int EntityId, string Map) : GameEvent;
    public sealed record ReportBroke(int EntityId, BodyState State, string Map) : GameEvent;
    public sealed record ReportedBodies(ulong TimeMs, IReadOnlyList<BodyEntry> Entries, string Map) : GameEvent;
    public sealed record Responded(string Keyword, string Outcome, IReadOnlyList<string> Fields) : GameEvent;
    public sealed record Unknown(string Line) : GameEvent;
}
