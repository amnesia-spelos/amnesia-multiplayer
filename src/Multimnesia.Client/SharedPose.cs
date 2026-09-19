using Multimnesia.Contracts;

namespace Multimnesia.Client;

// Shows the other player as the Avatar `partner` in the local game, and streams the local Pose to them (ADR 0003).
// Active only while the other player is present and the local game granted both Capabilities.
// One per local game Session: the game drops its Avatars and subscription when that Session ends.
public sealed class SharedPose
{
    public const string AvatarIdentifier = "partner";
    public const int LocalPoseRateHz = 30;
    public const string AvatarModelMissingNotice = "The Avatar model is not installed; the other player will be invisible.";
    // A game frozen loading reads nothing, so Poses it has not confirmed with a pong are capped at about a second's worth,
    // and never fill the socket ahead of chat. A paused or background game still answers, so its Avatar keeps moving.
    public const int MaxUnconfirmedPoses = 30;
    public const int PingEveryPoses = 10;
    private const int NoPingPending = -1;
    private const int NoAvatar = 0;

    private readonly ISessionOperations _sessions;
    private readonly Func<string, CancellationToken, ValueTask> _writeLine;
    private readonly Func<string, ValueTask> _displaySystem;
    private readonly Action<ConnectionLogSeverity, LocalGameEventName, string> _log;
    private readonly Task<ProtocolNegotiation> _negotiation;
    private readonly CancellationToken _cancellationToken;
    private LanMessage.Pose? _receivedPose;
    // Poses written that the local game has not yet confirmed, and how many of them the pending ping confirms.
    private int _unconfirmedPoses;
    private int _posesConfirmedByPing = NoPingPending;
    private int _modelMissingNoticeShown;
    // 1 while a worker is writing to the local game; only that worker reads or writes _avatarArrival.
    private int _working;
    // The OtherPlayerArrival the Avatar was created for, or NoAvatar.
    private int _avatarArrival = NoAvatar;

    public SharedPose(
        ISessionOperations sessions,
        Func<string, CancellationToken, ValueTask> writeLine,
        Func<string, ValueTask> displaySystem,
        Action<ConnectionLogSeverity, LocalGameEventName, string> log,
        Task<ProtocolNegotiation> negotiation,
        CancellationToken cancellationToken)
    {
        _sessions = sessions;
        _writeLine = writeLine;
        _displaySystem = displaySystem;
        _log = log;
        _negotiation = negotiation;
        _cancellationToken = cancellationToken;
        sessions.OtherPlayerPresenceChanged += Update;
        // After a local game reconnect, the other player may already be present.
        Update();
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

    // Failures are logged, never shown in chat, except a missing Avatar model, which is explained once.
    // The game reports an avatarpose failure once per streak, so failing Poses do not flood the log.
    public ValueTask HandleResponseAsync(GameEvent.Responded response)
    {
        var line = string.Join(' ', ["RESPONSE", response.Keyword, response.Outcome, .. response.Fields]);
        switch (response)
        {
            case { Keyword: "avatarcreate", Outcome: "ok" or "exists" }:
                _log(ConnectionLogSeverity.Information, LocalGameEventName.AvatarCreated, line);
                break;
            case { Keyword: "avatarcreate", Outcome: "model-not-found" }:
                _log(ConnectionLogSeverity.Warning, LocalGameEventName.SharedPoseCommandFailed, line);
                if (Interlocked.Exchange(ref _modelMissingNoticeShown, 1) == 0) return _displaySystem(AvatarModelMissingNotice);
                break;
            case { Keyword: "avatarcreate" or "avatarremove" or "avatarpose" or "localpose", Outcome: not "ok" }:
                _log(ConnectionLogSeverity.Warning, LocalGameEventName.SharedPoseCommandFailed, line);
                break;
        }
        return ValueTask.CompletedTask;
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
                var arrival = _sessions.OtherPlayerArrival;
                if (arrival != _avatarArrival)
                {
                    var previous = _avatarArrival;
                    _avatarArrival = arrival;
                    // A replacement gets a fresh Avatar: its Poses do not continue the timeline of the player who left.
                    if (previous != NoAvatar)
                        await _writeLine(GameInteractionProtocol.AvatarRemove(AvatarIdentifier), _cancellationToken);
                    if (arrival != NoAvatar)
                        await _writeLine(GameInteractionProtocol.AvatarCreate(AvatarIdentifier), _cancellationToken);
                    if (previous == NoAvatar)
                        await _writeLine(GameInteractionProtocol.SubscribeLocalPose(LocalPoseRateHz), _cancellationToken);
                    else if (arrival == NoAvatar)
                        await _writeLine(GameInteractionProtocol.UnsubscribeLocalPose, _cancellationToken);
                    continue;
                }
                if (_avatarArrival == NoAvatar) Interlocked.Exchange(ref _receivedPose, null);
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
                var pending = _sessions.OtherPlayerArrival != _avatarArrival ||
                    Volatile.Read(ref _receivedPose) is not null && (_avatarArrival == NoAvatar || CanWritePose);
                if (!pending || Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
            }
        }
        // The local game Session ended; the game drops its Avatars and subscription with it. _working stays 1.
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
