using System.Reflection;

namespace Multimnesia.Client;

// Reads the version and codename stamped from src/Directory.Build.props.
public static class GamePeerVersion
{
    private static readonly Assembly Assembly = typeof(GamePeerVersion).Assembly;

    // May carry build metadata after a '+', such as the source commit.
    public static string InformationalVersion { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Version { get; } = InformationalVersion.Split('+')[0];

    public static string Codename { get; } = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "Codename")?.Value ?? "";

    public static string DisplayName => $"Amnesia Multiplayer v{Version} \"{Codename}\"";

    public static string ConnectedNotice => $"{DisplayName} connected.";
}
