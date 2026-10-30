using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class PeerSettingsProtocolTests
{
    [Theory]
    [InlineData("ok", 2, new[] { "avatars", "settings" }, true)]
    [InlineData("ok", 2, new[] { "avatars", "localpose", "lean" }, false)]
    [InlineData("unknown-command", null, new string[0], false)]
    public void Settings_are_granted_only_when_the_game_lists_the_Capability(
        string outcome, int? version, string[] capabilities, bool granted)
    {
        Assert.Equal(granted, new ProtocolNegotiation(outcome, version, capabilities).GrantsSettings);
    }

    [Fact]
    public void An_announce_separates_its_fields_after_the_key_with_tabs()
    {
        Assert.Equal(
            "settingannounce multimnesia.color Chat color\tFF00FF\tAs RRGGBB.",
            GameInteractionProtocol.SettingAnnounce("multimnesia.color", "Chat color", "FF00FF", "As RRGGBB."));
    }

    [Fact]
    public void An_announce_may_have_an_empty_default()
    {
        Assert.Equal(
            "settingannounce multimnesia.color Chat color\t\tAs RRGGBB.",
            GameInteractionProtocol.SettingAnnounce("multimnesia.color", "Chat color", "", "As RRGGBB."));
    }

    [Theory]
    [InlineData("RESPONSE settingannounce ok multimnesia.color FF00FF", "settingannounce", "ok", "multimnesia.color", "FF00FF")]
    [InlineData("RESPONSE settingvalue ok multimnesia.color FF00FF", "settingvalue", "ok", "multimnesia.color", "FF00FF")]
    [InlineData("RESPONSE settingannounce ok multimnesia.color two words ", "settingannounce", "ok", "multimnesia.color", "two words ")]
    [InlineData("RESPONSE settingannounce ok multimnesia.color ", "settingannounce", "ok", "multimnesia.color", "")]
    [InlineData("RESPONSE settingannounce duplicate multimnesia.color", "settingannounce", "duplicate", "multimnesia.color", null)]
    [InlineData("RESPONSE settingannounce unavailable multimnesia.color", "settingannounce", "unavailable", "multimnesia.color", null)]
    [InlineData("RESPONSE settingvalue not-found multimnesia.color", "settingvalue", "not-found", "multimnesia.color", null)]
    [InlineData("RESPONSE settingannounce invalid", "settingannounce", "invalid", null, null)]
    public void Setting_Responses_carry_their_outcome_key_and_whole_value(
        string line, string keyword, string outcome, string? key, string? value)
    {
        Assert.Equal(new GameEvent.SettingResponded(keyword, outcome, key, value), GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("RESPONSE settingannounce ok multimnesia.color")]
    [InlineData("RESPONSE settingannounce ok")]
    [InlineData("RESPONSE settingannounce duplicate")]
    [InlineData("RESPONSE settingannounce invalid multimnesia.color extra")]
    public void Malformed_setting_Responses_are_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("EVENT settingchanged multimnesia.color 00FF00", "multimnesia.color", "00FF00")]
    [InlineData("EVENT settingchanged multimnesia.color ", "multimnesia.color", "")]
    [InlineData("EVENT settingchanged multimnesia.color not a color", "multimnesia.color", "not a color")]
    public void A_changed_setting_carries_its_key_and_whole_value(string line, string key, string value)
    {
        Assert.Equal(new GameEvent.SettingChanged(key, value), GameInteractionProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("EVENT settingchanged multimnesia.color")]
    [InlineData("EVENT settingchanged  FF00FF")]
    public void Malformed_setting_changes_are_unknown(string line)
    {
        Assert.IsType<GameEvent.Unknown>(GameInteractionProtocol.ParseEvent(line));
    }
}
