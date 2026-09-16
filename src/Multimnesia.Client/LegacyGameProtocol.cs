using System.Globalization;

namespace Multimnesia.Client;

public readonly record struct PlayerPosition(double X, double Y, double Z, double RotationX, double RotationY, double RotationZ)
;

public abstract record GameEvent
{
    public sealed record PositionReported(PlayerPosition Position) : GameEvent;
    public sealed record ScriptCalled(string Script) : GameEvent;
    public sealed record CommandCompleted : GameEvent;
    public sealed record Unknown(string Line) : GameEvent;
}

public static class LegacyGameProtocol
{
    private const string PositionPrefix = "RESPONSE:getposrot:";
    private const string ScriptCallPrefix = "SCRIPT_CALL:";

    public const string PositionRequest = "getposrot";

    public static GameEvent ParseEvent(string line)
    {
        if (TryParsePosition(line, out var position)) return new GameEvent.PositionReported(position);
        if (line.StartsWith(ScriptCallPrefix, StringComparison.Ordinal)) return new GameEvent.ScriptCalled(line[ScriptCallPrefix.Length..]);
        if (line.StartsWith("RESPONSE:exec:", StringComparison.Ordinal)) return new GameEvent.CommandCompleted();
        return new GameEvent.Unknown(line);
    }

    public static bool TryParsePosition(string line, out PlayerPosition position)
    {
        position = default;
        if (!line.StartsWith(PositionPrefix, StringComparison.Ordinal)) return false;
        return TryParsePositionPayload(line[PositionPrefix.Length..], out position);
    }

    public static bool TryParsePositionPayload(string payload, out PlayerPosition position)
    {
        position = default;
        var halves = payload.Split(':', StringSplitOptions.TrimEntries);
        if (halves.Length != 2) return false;
        var coordinates = halves[0].Split(',', StringSplitOptions.TrimEntries);
        var rotation = halves[1].Split(',', StringSplitOptions.TrimEntries);
        if (coordinates.Length != 3 || rotation.Length != 3) return false;

        Span<double> values = stackalloc double[6];
        for (var index = 0; index < values.Length; index++)
        {
            var source = index < 3 ? coordinates[index] : rotation[index - 3];
            if (!double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out values[index])) return false;
        }
        position = new(values[0], values[1], values[2], values[3], values[4], values[5]);
        return true;
    }

    public static string CreateRemotePlayerUpdate(PlayerPosition position, string entityPath) => string.Create(
        CultureInfo.InvariantCulture,
        $"exec:if(!GetEntityExists(\"RemotePlayer\")){{CreateEntityAtFirstArea(\"RemotePlayer\", \"{entityPath}\", true);}};SetEntityPosRot(\"RemotePlayer\", {position.X:R}, {position.Y + 1.0:R}, {position.Z:R}, {position.RotationX:R}, {position.RotationY:R}, {position.RotationZ:R});");

    public static string ExecuteScripts(IEnumerable<string> scripts) => $"exec:{string.Join(';', scripts)}";
}
