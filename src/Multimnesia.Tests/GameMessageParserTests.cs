using System.Globalization;
using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class LegacyGameProtocolTests
{
    [Fact]
    public void Position_response_is_parsed_with_wire_invariant_numbers()
    {
        using var _ = new TemporaryCulture("cs-CZ");

        var parsed = LegacyGameProtocol.TryParsePosition(
            "RESPONSE:getposrot:1.25, 2.5, -3.75:10.5, 20.25, 30.75",
            out var position);

        Assert.True(parsed);
        Assert.Equal(1.25, position.X);
        Assert.Equal(20.25, position.RotationY);
    }

    [Theory]
    [InlineData("RESPONSE:getposrot:1,2:3,4,5")]
    [InlineData("RESPONSE:getposrot:1,not-a-number,3:4,5,6")]
    [InlineData("SCRIPT_CALL:Something()")]
    public void Malformed_or_other_messages_are_not_positions(string line)
    {
        Assert.False(LegacyGameProtocol.TryParsePosition(line, out _));
    }

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo _original = CultureInfo.CurrentCulture;

        public TemporaryCulture(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _original;
    }
}
