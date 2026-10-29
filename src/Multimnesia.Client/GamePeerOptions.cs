using System.Net;
using Microsoft.Extensions.Configuration;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed class GamePeerOptions
{
    public const string SectionName = "GamePeer";
    public int RelayPort { get; set; } = 5000;
    public int JoinTimeoutSeconds { get; set; } = 10;
    public string GameHost { get; set; } = IPAddress.Loopback.ToString();
    public int GamePort { get; set; } = 5150;
    public string LogLevel { get; set; } = "Information";
    // The Chat Color of this player's Chat Author name on the other player's screen; unset means the game default.
    public string? ChatColor { get; set; }
    // Opt-in records of what crossed this Game Peer's connections, written to the traffic file beside the main log.
    public bool TrafficStatistics { get; set; }
    public bool TrafficTrace { get; set; }
    // The Traffic Trace stops once the traffic file reaches this size; Traffic Statistics carry on.
    public int TrafficTraceMaxMegabytes { get; set; } = 200;

    public RelaySeverity ParsedLogLevel => Enum.Parse<RelaySeverity>(LogLevel, ignoreCase: true);

    public ChatColor? ParsedChatColor => Contracts.ChatColor.TryParse(ChatColor, out var color) ? color : null;

    public TrafficOptions ParsedTrafficOptions => new()
    {
        Statistics = TrafficStatistics,
        Trace = TrafficTrace,
        TraceMaxBytes = TrafficTraceMaxMegabytes * 1024L * 1024
    };

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
        if (!string.IsNullOrEmpty(ChatColor) && ParsedChatColor is null)
            errors.Add("GamePeer:ChatColor must be six hex digits, RRGGBB, without '#'.");
        if (TrafficTraceMaxMegabytes < 1) errors.Add("GamePeer:TrafficTraceMaxMegabytes must be a positive number.");
        return errors;
    }
}

public static class GamePeerConfiguration
{
    private static readonly Dictionary<string, string> SwitchMappings = new()
    {
        ["--color"] = $"{GamePeerOptions.SectionName}:{nameof(GamePeerOptions.ChatColor)}",
        ["--stats"] = $"{GamePeerOptions.SectionName}:{nameof(GamePeerOptions.TrafficStatistics)}",
        ["--trace"] = $"{GamePeerOptions.SectionName}:{nameof(GamePeerOptions.TrafficTrace)}"
    };
    private static readonly string[] Flags = ["--stats", "--trace"];

    public static GamePeerOptions Load(string[] args, string? jsonPath)
    {
        var configuration = new ConfigurationManager();
        if (jsonPath is not null) configuration.AddJsonFile(jsonPath, optional: true, reloadOnChange: false);
        configuration.AddCommandLine(WithFlagValues(args), SwitchMappings);
        return configuration.GetSection(GamePeerOptions.SectionName).Get<GamePeerOptions>() ?? new GamePeerOptions();
    }

    // A flag given alone means true, so `--stats --trace` does not read `--trace` as the value of `--stats`.
    private static string[] WithFlagValues(string[] args) => [.. args.Select((arg, index) =>
        Flags.Contains(arg, StringComparer.OrdinalIgnoreCase) && (index + 1 == args.Length || !bool.TryParse(args[index + 1], out _))
            ? $"{arg}=true"
            : arg)];
}
