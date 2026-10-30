using Multimnesia.Contracts;

namespace Multimnesia.Client;

public enum ChatColorSettingOutcome { None, Applied, Ignored, Refused }

// The Chat Color this player's Chat Entries carry. It is the configured one until the game answers the `multimnesia.color`
// Peer Setting, then whatever the player set in the game's options, where empty means the game default.
public sealed class ChatColorSetting(ChatColor? configured)
{
    public const string Key = "multimnesia.color";
    public const string IgnoredNotice = "Your Chat color setting was ignored: use six hex digits, RRGGBB, or leave it empty.";
    private const string Label = "Chat color";
    private const string Description = "Your name's color in the other players' chat, as RRGGBB. Empty uses the game default.";

    // Written by the game Session's read loop, read by the chat it submits.
    private ChatColor? _current = configured;

    public ChatColor? Current => Volatile.Read(ref _current);

    // The configured color is the default, so the game follows it until the player changes the setting.
    public string Announce { get; } = GameInteractionProtocol.SettingAnnounce(Key, Label, configured?.Hex ?? "", Description);

    // Ignored keeps the previous color; Refused keeps the configured one, since the game never gave this Session the setting.
    public ChatColorSettingOutcome Handle(GameEvent gameEvent) => gameEvent switch
    {
        GameEvent.SettingResponded { Keyword: "settingannounce", Outcome: "ok", Key: Key, Value: { } value } => Apply(value),
        // `invalid` names no key; this is the only Peer Setting the Game Peer announces, so it can only be this one.
        GameEvent.SettingResponded { Keyword: "settingannounce", Key: Key or null } => ChatColorSettingOutcome.Refused,
        GameEvent.SettingChanged { Key: Key } changed => Apply(changed.Value),
        _ => ChatColorSettingOutcome.None
    };

    private ChatColorSettingOutcome Apply(string value)
    {
        if (value.Length == 0) Volatile.Write(ref _current, null);
        else if (ChatColor.TryParse(value, out var color)) Volatile.Write(ref _current, color);
        else return ChatColorSettingOutcome.Ignored;
        return ChatColorSettingOutcome.Applied;
    }
}
