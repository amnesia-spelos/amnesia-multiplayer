namespace Multimnesia.Contracts;

public enum GamePeerRole { SessionHost, JoiningPlayer }

public sealed record AdmissionResult(bool Accepted, GamePeerRole? Role, bool SessionReady, string? Error)
{
    public static AdmissionResult Rejected(string error) => new(false, null, false, error);
}

public sealed record RelayPosition(double X, double Y, double Z, double RotationX, double RotationY, double RotationZ);
