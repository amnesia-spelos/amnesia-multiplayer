using Multimnesia.Contracts;

namespace Multimnesia.Client;

// Shows the other player as the Avatar `partner` in the local game, and streams the local Pose to them (ADR 0003).
// Active only while the other player is present and the local game granted both Capabilities.
public sealed class SharedPose
{
    public const string AvatarIdentifier = "partner";
    public const int LocalPoseRateHz = 30;
    // A game frozen loading reads nothing, so Poses it has not confirmed with a pong are capped at about a second's worth,
    // and never fill the socket ahead of chat. A paused or background game still answers, so its Avatar keeps moving.
    public const int MaxUnconfirmedPoses = 30;
    public const int PingEveryPoses = 10;
    private const int NoPingPending = -1;

    private readonly ISessionOperations _sessions;
    private readonly Func<string, CancellationToken, ValueTask> _writeLine;
    private readonly Task<ProtocolNegotiation> _negotiation;
    private readonly CancellationToken _cancellationToken;
    private LanMessage.Pose? _receivedPose;
    // Poses written that the local game has not yet confirmed, and how many of them the pending ping confirms.
    private int _unconfirmedPoses;
    private int _posesConfirmedByPing = NoPingPending;
    // 1 while a worker is writing to the local game; only that worker reads or writes _avatarCreated.
    private int _working;
    private bool _avatarCreated;

    public SharedPose(
        ISessionOperations sessions,
        Func<string, CancellationToken, ValueTask> writeLine,
        Task<ProtocolNegotiation> negotiation,
        CancellationToken cancellationToken)
    {
        _sessions = sessions;
        _writeLine = writeLine;
        _negotiation = negotiation;
        _cancellationToken = cancellationToken;
        sessions.OtherPlayerPresenceChanged += Update;
    }

    private bool IsGranted => _negotiation.IsCompletedSuccessfully && _negotiation.Result.GrantsSharedPose;

    private bool CanWritePose => Volatile.Read(ref _unconfirmedPoses) < MaxUnconfirmedPoses;

    // Called for each `STATE localpose` the local game reports.
    public void HandleLocalPose(LocalPose pose)
    {
        if (IsGranted)
            _sessions.SendPose(new(pose.TimeMs, pose.TeleportCounter, pose.X, pose.Y, pose.Z, pose.Yaw, pose.Pitch, pose.Crouch, pose.Map));
    }

    // Latest-wins: replaces any received Pose not yet written to the local game. Never blocks.
    public void HandleReceivedPose(LanMessage.Pose pose)
    {
        Volatile.Write(ref _receivedPose, pose);
        Update();
    }

    // The local game answered the ping, so it has processed every Pose written before it.
    public void HandlePong()
    {
        var confirmed = Interlocked.Exchange(ref _posesConfirmedByPing, NoPingPending);
        if (confirmed == NoPingPending) return;
        Interlocked.Add(ref _unconfirmedPoses, -confirmed);
        Update();
    }

    private void Update()
    {
        if (Interlocked.CompareExchange(ref _working, 1, 0) == 0) _ = WorkAsync();
    }

    private async Task WorkAsync()
    {
        try
        {
            // Without both Capabilities the worker never releases, so nothing is ever written.
            if (!(await _negotiation.WaitAsync(_cancellationToken)).GrantsSharedPose) return;
            while (true)
            {
                var present = _sessions.IsOtherPlayerPresent;
                if (present != _avatarCreated)
                {
                    _avatarCreated = present;
                    if (present)
                    {
                        await _writeLine(GameInteractionProtocol.AvatarCreate(AvatarIdentifier), _cancellationToken);
                        await _writeLine(GameInteractionProtocol.SubscribeLocalPose(LocalPoseRateHz), _cancellationToken);
                    }
                    else
                    {
                        await _writeLine(GameInteractionProtocol.AvatarRemove(AvatarIdentifier), _cancellationToken);
                        await _writeLine(GameInteractionProtocol.UnsubscribeLocalPose, _cancellationToken);
                    }
                    continue;
                }
                if (!_avatarCreated) Interlocked.Exchange(ref _receivedPose, null);
                else if (CanWritePose && Interlocked.Exchange(ref _receivedPose, null) is { } pose)
                {
                    await _writeLine(GameInteractionProtocol.AvatarPose(AvatarIdentifier, pose), _cancellationToken);
                    var unconfirmed = Interlocked.Increment(ref _unconfirmedPoses);
                    if (unconfirmed >= PingEveryPoses &&
                        Interlocked.CompareExchange(ref _posesConfirmedByPing, unconfirmed, NoPingPending) == NoPingPending)
                        await _writeLine(GameInteractionProtocol.Ping, _cancellationToken);
                    continue;
                }

                Volatile.Write(ref _working, 0);
                // Work offered after the checks above but before the release would otherwise wait for the next Update.
                var pending = _sessions.IsOtherPlayerPresent != _avatarCreated ||
                    Volatile.Read(ref _receivedPose) is not null && (!_avatarCreated || CanWritePose);
                if (!pending || Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
            }
        }
        // The local game Session ended; the game drops its Avatars and subscription with it. _working stays 1.
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
