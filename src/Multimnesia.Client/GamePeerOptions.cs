using System.Net;

namespace Multimnesia.Client;

public sealed class GamePeerOptions
{
    public const string SectionName = "GamePeer";
    public string RelayUrl { get; set; } = "http://127.0.0.1:5000/chat";
    public string GameHost { get; set; } = IPAddress.Loopback.ToString();
    public int GamePort { get; set; } = 5150;
    public int PollIntervalMilliseconds { get; set; } = 15;
    public string RemotePlayerEntity { get; set; } = "entities/ornament/arabic_statue/arabic_statue.ent";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(RelayUrl, UriKind.Absolute, out var relayUri) || relayUri.Scheme is not ("http" or "https"))
            errors.Add("GamePeer:RelayUrl must be an absolute HTTP or HTTPS URL.");
        if (!IPAddress.TryParse(GameHost, out var gameAddress) || !IPAddress.IsLoopback(gameAddress))
            errors.Add("GamePeer:GameHost must be a loopback IP address; never expose the Game Interaction Protocol to the LAN.");
        if (GamePort is < 1 or > 65535) errors.Add("GamePeer:GamePort must be between 1 and 65535.");
        if (PollIntervalMilliseconds is < 5 or > 1000) errors.Add("GamePeer:PollIntervalMilliseconds must be between 5 and 1000.");
        if (string.IsNullOrWhiteSpace(RemotePlayerEntity) || Path.IsPathRooted(RemotePlayerEntity))
            errors.Add("GamePeer:RemotePlayerEntity must be a path relative to the game installation.");
        return errors;
    }
}
