using System.Diagnostics.CodeAnalysis;

namespace Multimnesia.Contracts;

// The color a Chat Author name or a chat message is shown in: six hex digits, RRGGBB, kept in upper case.
public sealed record ChatColor
{
    private ChatColor(string hex) => Hex = hex;

    public static ChatColor Green { get; } = new("00FF00");

    public string Hex { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out ChatColor? color)
    {
        color = value is { Length: 6 } && value.All(char.IsAsciiHexDigit) ? new(value.ToUpperInvariant()) : null;
        return color is not null;
    }

    public override string ToString() => Hex;
}
