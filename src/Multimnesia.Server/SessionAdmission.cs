using Multimnesia.Contracts;

namespace Multimnesia.Server;

public sealed class SessionAdmission
{
    private readonly object _gate = new();
    private readonly List<string> _connections = [];

    public AdmissionResult TryAdmit(string connectionId)
    {
        lock (_gate)
        {
            var existingIndex = _connections.IndexOf(connectionId);
            if (existingIndex >= 0) return AcceptedAt(existingIndex);
            if (_connections.Count == 2) return AdmissionResult.Rejected("This Multiplayer Session already has two Game Peers.");
            _connections.Add(connectionId);
            return AcceptedAt(_connections.Count - 1);
        }
    }

    public bool IsAdmitted(string connectionId) { lock (_gate) return _connections.Contains(connectionId); }
    public GamePeerRole? Depart(string connectionId)
    {
        lock (_gate)
        {
            var index = _connections.IndexOf(connectionId);
            if (index < 0) return null;
            var role = index == 0 ? GamePeerRole.SessionHost : GamePeerRole.JoiningPlayer;
            if (role == GamePeerRole.SessionHost) _connections.Clear();
            else _connections.RemoveAt(index);
            return role;
        }
    }

    private AdmissionResult AcceptedAt(int index) => new(
        true, index == 0 ? GamePeerRole.SessionHost : GamePeerRole.JoiningPlayer, _connections.Count == 2, null);
}
