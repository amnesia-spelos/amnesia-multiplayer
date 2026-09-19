using System.Globalization;
using Multimnesia.Client;

namespace Multimnesia.Tests;

// Expected values come from the amnesia-tdd-tcp Protocol Version 2 contract fixtures (contract.jsonl).
public sealed class ProtocolVersion2Tests
{
    [Fact]
    public void Local_pose_State_Updates_carry_every_field_and_the_whole_map_path()
    {
        var parsed = Assert.IsType<GameEvent.LocalPoseReported>(GameInteractionProtocol.ParseEvent(
            "STATE localpose 123456 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 custom_stories/My Story: Part 2/maps/cellar one.map"));

        Assert.Equal(
            new LocalPose(123456, 3, 1.25, -2.5, 3.75, 90, -45, true, "custom_stories/My Story: Part 2/maps/cellar one.map"),
            parsed.Pose);
    }

    [Fact]
    public void Local_pose_State_Updates_use_the_full_time_and_teleport_counter_ranges()
    {
        var parsed = Assert.IsType<GameEvent.LocalPoseReported>(GameInteractionProtocol.ParseEvent(
            "STATE localpose 18446744073709551615 4294967295 0.0000 0.0000 0.0000 0.0000 0.0000 0 maps/a.map"));

        Assert.Equal(new LocalPose(ulong.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0, false, "maps/a.map"), parsed.Pose);
    }

    [Fact]
    public void Local_pose_map_paths_keep_trailing_spaces()
    {
        var parsed = Assert.IsType<GameEvent.LocalPoseReported>(GameInteractionProtocol.ParseEvent(
            "STATE localpose 1 0 1.0000 2.0000 3.0000 4.0000 5.0000 0 maps/a.map "));

        Assert.Equal("maps/a.map ", parsed.Pose.Map);
    }

    [Fact]
    public void Local_pose_State_Updates_parse_the_same_under_a_comma_decimal_locale()
    {
        UnderCommaDecimalLocale(() =>
        {
            var parsed = Assert.IsType<GameEvent.LocalPoseReported>(GameInteractionProtocol.ParseEvent(
                "STATE localpose 1000 1 1.2500 -42.0001 7 90.0000 -45.5000 0 maps/a.map"));

            Assert.Equal(new LocalPose(1000, 1, 1.25, -42.0001, 7, 90, -45.5, false, "maps/a.map"), parsed.Pose);
            Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(
                "STATE localpose 1000 1 1,2500 0.0000 0.0000 0.0000 0.0000 0 maps/a.map"));
        });
    }

    private static void UnderCommaDecimalLocale(Action test)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");
        try { test(); }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("STATE localpose 1 0 1.0000 2.0000 3.0000 4.0000 5.0000 0")]
    [InlineData("STATE localpose 1 0 1.0000 2.0000 3.0000 4.0000 5.0000 0 ")]
    [InlineData("STATE localpose 1  0 1.0000 2.0000 3.0000 4.0000 5.0000 0 maps/a.map")]
    [InlineData("STATE localpose -1 0 1.0000 2.0000 3.0000 4.0000 5.0000 0 maps/a.map")]
    [InlineData("STATE localpose 18446744073709551616 0 1.0000 2.0000 3.0000 4.0000 5.0000 0 maps/a.map")]
    [InlineData("STATE localpose 1 4294967296 1.0000 2.0000 3.0000 4.0000 5.0000 0 maps/a.map")]
    [InlineData("STATE localpose 1 +1 1.0000 2.0000 3.0000 4.0000 5.0000 0 maps/a.map")]
    [InlineData("STATE localpose 1 0 1.0000 2.0000 3.0000 4.0000 5.0000 2 maps/a.map")]
    [InlineData("STATE localpose 1 0 1.0000 2.0000 3.0000 4.0000 nan 0 maps/a.map")]
    [InlineData("STATE localposex 1 0 1.0000 2.0000 3.0000 4.0000 5.0000 0 maps/a.map")]
    public void Malformed_local_pose_State_Updates_are_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("1.2500", 1.25)]
    [InlineData("-42.0001", -42.0001)]
    [InlineData("7", 7)]
    [InlineData("123456789.123456", 123456789.123456)]
    public void Numbers_parse_with_a_point_decimal_separator(string text, double expected)
    {
        Assert.True(ProtocolVersion2Line.TryParseNumber(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1,25")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("+1.0")]
    [InlineData("1e3")]
    [InlineData("nan")]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1234567890.123456")]
    public void Numbers_outside_the_contract_grammar_are_rejected(string text)
    {
        Assert.False(ProtocolVersion2Line.TryParseNumber(text, out _));
    }

    [Theory]
    [InlineData(1.25, "1.2500")]
    [InlineData(-2.5, "-2.5000")]
    [InlineData(3.14159, "3.1416")]
    [InlineData(-0.00005, "-0.0001")]
    [InlineData(-0.00004, "0.0000")]
    [InlineData(-0.0, "0.0000")]
    [InlineData(12345.6789, "12345.6789")]
    public void Numbers_are_written_with_four_decimals_rounded_half_away_from_zero(double value, string expected)
    {
        Assert.Equal(expected, ProtocolVersion2Line.FormatNumber(value));
    }

    [Fact]
    public void Numbers_are_written_with_a_point_under_a_comma_decimal_locale()
    {
        UnderCommaDecimalLocale(() => Assert.Equal("-1234.5000", ProtocolVersion2Line.FormatNumber(-1234.5)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Non_finite_numbers_cannot_be_written(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolVersion2Line.FormatNumber(value));
    }

    [Theory]
    [InlineData("RESPONSE protocol ok 2 avatars localpose", "protocol", "ok", new[] { "2", "avatars", "localpose" })]
    [InlineData("RESPONSE protocol unsupported-version", "protocol", "unsupported-version", new string[0])]
    [InlineData("RESPONSE avatarcreate model-not-found partner", "avatarcreate", "model-not-found", new[] { "partner" })]
    [InlineData("RESPONSE localpose ok subscribe 30", "localpose", "ok", new[] { "subscribe", "30" })]
    public void Protocol_Version_2_Responses_carry_keyword_outcome_and_fields(
        string line, string keyword, string outcome, string[] fields)
    {
        var parsed = Assert.IsType<GameEvent.Responded>(GameInteractionProtocol.ParseEvent(line));

        Assert.Equal(keyword, parsed.Keyword);
        Assert.Equal(outcome, parsed.Outcome);
        Assert.Equal(fields, parsed.Fields);
    }

    [Theory]
    [InlineData("RESPONSE protocol")]
    [InlineData("RESPONSE protocol ")]
    [InlineData("RESPONSE  protocol ok")]
    [InlineData("RESPONSE protocol ok 2  avatars")]
    [InlineData("RESPONSE protocol ok 2 ")]
    public void Malformed_Protocol_Version_2_Responses_are_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Fact]
    public void Legacy_Responses_keep_their_form()
    {
        Assert.IsType<GameEvent.StartCustomStoryResponded>(GameInteractionProtocol.ParseEvent("RESPONSE:startcustomstory:starting"));
    }
}
