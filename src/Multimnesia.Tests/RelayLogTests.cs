using System.Net;
using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class RelayLogTests
{
    [Fact]
    public void Endpoints_are_included_only_at_debug_severity()
    {
        var debugJson = RelayLog.Format(new RelayLogEntry(
            DateTimeOffset.UtcNow, RelaySeverity.Debug, RelayEventName.HandshakeAttempted, RelayRole.Host,
            "None->Attempting", null, null, Endpoint: new IPEndPoint(IPAddress.Loopback, 12345)));
        var infoJson = RelayLog.Format(new RelayLogEntry(
            DateTimeOffset.UtcNow, RelaySeverity.Information, RelayEventName.PeerAdmitted, RelayRole.Host,
            "None->Admitted", Guid.NewGuid(), null, Endpoint: new IPEndPoint(IPAddress.Loopback, 12345)));

        Assert.Contains("127.0.0.1:12345", debugJson);
        Assert.DoesNotContain("127.0.0.1:12345", infoJson);
    }

    [Fact]
    public void Structured_fields_required_by_the_hardening_spec_are_present()
    {
        var json = RelayLog.Format(new RelayLogEntry(
            DateTimeOffset.UtcNow, RelaySeverity.Warning, RelayEventName.PeerDisconnected, RelayRole.Joining,
            "Admitted->None", Guid.NewGuid(), Guid.NewGuid(), RelayFailureCategory.Timeout));

        Assert.Contains("\"timestamp\"", json);
        Assert.Contains("\"severity\":\"Warning\"", json);
        Assert.Contains("\"event\":\"PeerDisconnected\"", json);
        Assert.Contains("\"role\":\"Joining\"", json);
        Assert.Contains("\"transition\":\"Admitted->None\"", json);
        Assert.Contains("\"sessionCorrelationId\"", json);
        Assert.Contains("\"peerCorrelationId\"", json);
        Assert.Contains("\"failure\":\"Timeout\"", json);
    }
}
