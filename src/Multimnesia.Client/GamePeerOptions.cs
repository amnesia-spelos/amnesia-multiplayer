using System.Net;

namespace Multimnesia.Client;

public sealed class GamePeerOptions
{
    public const string SectionName = "GamePeer";
    public int RelayPort { get; set; } = 5000;
    public int JoinTimeoutSeconds { get; set; } = 10;
    public string GameHost { get; set; } = IPAddress.Loopback.ToString();
    public int GamePort { get; set; } = 5150;
    public string LogLevel { get; set; } = "Information";

    public RelaySeverity ParsedLogLevel => Enum.Parse<RelaySeverity>(LogLevel, ignoreCase: true);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RelayPort is < 1 or > 65535) errors.Add("GamePeer:RelayPort must be between 1 and 65535.");
        if (JoinTimeoutSeconds is < 1 or > 300) errors.Add("GamePeer:JoinTimeoutSeconds must be between 1 and 300.");
        if (!IPAddress.TryParse(GameHost, out var gameAddress) || !IPAddress.IsLoopback(gameAddress))
            errors.Add("GamePeer:GameHost must be a loopback IP address; never expose the Game Interaction Protocol to the LAN.");
        if (GamePort is < 1 or > 65535) errors.Add("GamePeer:GamePort must be between 1 and 65535.");
        if (!Enum.TryParse<RelaySeverity>(LogLevel, ignoreCase: true, out _))
            errors.Add("GamePeer:LogLevel must be one of Debug, Information, Warning, Error.");
        return errors;
    }
}
