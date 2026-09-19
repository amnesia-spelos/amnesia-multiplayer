using System.Globalization;

namespace Multimnesia.Client;

// How the local game answered the Protocol Version 2 negotiation.
public sealed record ProtocolNegotiation(string Outcome, int? Version, IReadOnlyList<string> Capabilities)
{
    public const int SupportedVersion = 2;
    public const string Avatars = "avatars";
    public const string LocalPose = "localpose";

    // An older game answers `protocol` with WARNING:Unknown command.
    public static ProtocolNegotiation UnknownCommand { get; } = new("unknown-command", null, []);

    public bool GrantsSharedPose =>
        Outcome == "ok" && Version == SupportedVersion && Capabilities.Contains(Avatars) && Capabilities.Contains(LocalPose);

    // RESPONSE protocol ok <version> [<capability>...], or RESPONSE protocol <failure>
    public static ProtocolNegotiation FromResponse(GameEvent.Responded response) =>
        response.Outcome == "ok" && response.Fields.Count > 0 &&
        int.TryParse(response.Fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? new(response.Outcome, version, response.Fields.Skip(1).ToArray())
            : new(response.Outcome, null, []);
}

// The local player's Pose as a Protocol Version 2 `localpose` State Update reports it: feet position, body yaw and camera pitch in degrees.
public sealed record LocalPose(
    ulong TimeMs, uint TeleportCounter, double X, double Y, double Z, double Yaw, double Pitch, bool Crouch, string Map);

// The Protocol Version 2 line format of the amnesia-tdd-tcp contract: single-space fields, an optional path last, "C"-locale numbers.
public static class ProtocolVersion2Line
{
    private const int MaximumNumberDigits = 15;

    // A path field extends to the end of the line, so it keeps spaces and ':'; it must not be empty.
    public static bool TrySplit(string line, int fixedFieldCount, bool hasPath, out string[] fields)
    {
        fields = [];
        var result = new string[fixedFieldCount + (hasPath ? 1 : 0)];
        var start = 0;
        for (var index = 0; index < fixedFieldCount; index++)
        {
            var end = line.IndexOf(' ', start);
            var isLast = index == fixedFieldCount - 1 && !hasPath;
            if (isLast ? end >= 0 : end < 0) return false;
            var field = isLast ? line[start..] : line[start..end];
            if (field.Length == 0) return false;
            result[index] = field;
            start = end + 1;
        }
        if (hasPath)
        {
            if (start >= line.Length) return false;
            result[fixedFieldCount] = line[start..];
        }
        fields = result;
        return true;
    }

    // Exactly 4 decimals, rounded half away from zero; a value that rounds to zero has no sign.
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Numbers must be finite.");
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        return (rounded == 0 ? 0 : rounded).ToString("0.0000", CultureInfo.InvariantCulture);
    }

    // Accepts -?digits(.digits)? with at most 15 digits, whatever the system locale is.
    public static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        var digits = 0;
        var index = text.StartsWith('-') ? 1 : 0;
        var integerStart = index;
        while (index < text.Length && char.IsAsciiDigit(text[index])) { index++; digits++; }
        if (index == integerStart) return false;
        if (index < text.Length)
        {
            if (text[index] != '.') return false;
            var fractionStart = ++index;
            while (index < text.Length && char.IsAsciiDigit(text[index])) { index++; digits++; }
            if (index == fractionStart || index < text.Length) return false;
        }
        if (digits > MaximumNumberDigits) return false;
        value = double.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return true;
    }
}
