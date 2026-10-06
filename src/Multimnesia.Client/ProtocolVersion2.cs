using System.Globalization;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

// How the local game answered the Protocol Version 2 negotiation.
public sealed record ProtocolNegotiation(string Outcome, int? Version, IReadOnlyList<string> Capabilities)
{
    public const int SupportedVersion = 2;
    public const string Avatars = "avatars";
    public const string LocalPose = "localpose";
    public const string Interactions = "interactions";

    // An older game answers `protocol` with WARNING:Unknown command.
    public static ProtocolNegotiation UnknownCommand { get; } = new("unknown-command", null, []);

    public bool GrantsSharedPose =>
        Outcome == "ok" && Version == SupportedVersion && Capabilities.Contains(Avatars) && Capabilities.Contains(LocalPose);

    // A game older than the Capability ignores its name, so it is simply missing from the granted list.
    public bool GrantsInteractions => Outcome == "ok" && Version == SupportedVersion && Capabilities.Contains(Interactions);

    // RESPONSE protocol ok <version> [<capability>...], or RESPONSE protocol <failure>
    public static ProtocolNegotiation FromResponse(GameEvent.Responded response) =>
        response.Outcome == "ok" && response.Fields.Count > 0 &&
        int.TryParse(response.Fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? new(response.Outcome, version, response.Fields.Skip(1).ToArray())
            : new(response.Outcome, null, []);
}

// The local player's Pose as a Protocol Version 2 `localpose` State Update reports it: feet position, body yaw and camera pitch in degrees.
public sealed record LocalPose(
    ulong TimeMs, uint TeleportCounter, double X, double Y, double Z, double Yaw, double Pitch, bool Crouch, bool Lantern, string Map);

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

    // An Entity or Body Identifier: -?digits within 32 bits; leading zeros are accepted, '+' is not.
    public static bool TryParseIdentifier(string text, out int value)
    {
        value = 0;
        var digits = text.StartsWith('-') ? text[1..] : text;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) &&
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public static string FormatIdentifier(int value) => value.ToString(CultureInfo.InvariantCulture);

    // <x> <y> <z> <qx> <qy> <qz> <qw> <vx> <vy> <vz> <wx> <wy> <wz>, within the same bounds the LAN protocol checks.
    public const int BodyStateFieldCount = 13;

    public static bool TryParseBodyState(ReadOnlySpan<string> fields, out BodyState state)
    {
        state = default!;
        if (fields.Length != BodyStateFieldCount) return false;
        var numbers = new double[BodyStateFieldCount];
        for (var index = 0; index < numbers.Length; index++)
            if (!TryParseNumber(fields[index], out numbers[index])) return false;
        var parsed = new BodyState(
            numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6],
            numbers[7], numbers[8], numbers[9], numbers[10], numbers[11], numbers[12]);
        if (!LanProtocol.IsValid(parsed)) return false;
        state = parsed;
        return true;
    }

    // The quaternion is written normalized, so one accepted within tolerance never rounds to one the game rejects.
    public static string FormatBodyState(BodyState state)
    {
        var length = Math.Sqrt(state.Qx * state.Qx + state.Qy * state.Qy + state.Qz * state.Qz + state.Qw * state.Qw);
        var (qx, qy, qz, qw) = double.IsFinite(length) && length > 0
            ? (state.Qx / length, state.Qy / length, state.Qz / length, state.Qw / length)
            : (0, 0, 0, 1);
        return string.Join(' ', new[]
        {
            state.X, state.Y, state.Z, qx, qy, qz, qw, state.Vx, state.Vy, state.Vz, state.Wx, state.Wy, state.Wz
        }.Select(FormatNumber));
    }
}
