using System.Text;

namespace Multimnesia.Contracts;

public static class CustomStoryIdentifier
{
    public const int MaximumScalars = 128;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is '|' or ':' || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control) return false;
            if (++count > MaximumScalars) return false;
        }
        return true;
    }
}
