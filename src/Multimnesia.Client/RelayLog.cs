using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Multimnesia.Client;

public enum RelaySeverity { Debug, Information, Warning, Error }

public enum RelayRole { Host, Joining }

public enum RelayFailureCategory { None, Protocol, Timeout, Capacity, Flood, Io }

public enum RelayEventName { HandshakeAttempted, HandshakeRejected, PeerAdmitted, PeerDisconnected }

public sealed record RelayLogEntry(
    DateTimeOffset Timestamp,
    RelaySeverity Severity,
    RelayEventName Event,
    RelayRole Role,
    string Transition,
    Guid? SessionCorrelationId,
    Guid? PeerCorrelationId,
    RelayFailureCategory Failure = RelayFailureCategory.None,
    IPEndPoint? Endpoint = null);

public static class RelayLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void ConsoleSink(RelayLogEntry entry) => Console.WriteLine(JsonSerializer.Serialize(new
    {
        timestamp = entry.Timestamp,
        severity = entry.Severity.ToString(),
        @event = entry.Event.ToString(),
        role = entry.Role.ToString(),
        transition = entry.Transition,
        sessionCorrelationId = entry.SessionCorrelationId,
        peerCorrelationId = entry.PeerCorrelationId,
        failure = entry.Failure.ToString(),
        endpoint = entry.Severity == RelaySeverity.Debug ? entry.Endpoint?.ToString() : null
    }, SerializerOptions));
}
