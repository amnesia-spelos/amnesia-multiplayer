using System.Net;

namespace Multimnesia.Server;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";
    public string ListenAddress { get; set; } = IPAddress.Loopback.ToString();
    public int Port { get; set; } = 5000;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IPAddress.TryParse(ListenAddress, out _)) errors.Add("Relay:ListenAddress must be an IPv4 or IPv6 address.");
        if (Port is < 1 or > 65535) errors.Add("Relay:Port must be between 1 and 65535.");
        return errors;
    }
}
