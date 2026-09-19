using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class LocalGameSessionTests : IAsyncDisposable
{
    private const string NegotiateSharedPose = "protocol 2 avatars localpose";
    private const string AvatarsUnsupportedNotice = "chat:SYSTEM:Your game does not support Avatars; movement will not be shared.";
    private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly FakeSessionOperations _sessions = new();
    private readonly List<LocalGameLogEntry> _log = [];
    private SessionCallbacks _callbacks = null!;

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _cancellation.Dispose();
    }

    private LocalGameSession CreateSession(Stream gameStream) => new(
        gameStream,
        callbacks => { _callbacks = callbacks; return _sessions; },
        entry => { lock (_log) _log.Add(entry); });

    private async Task<(FakeGame Game, LocalGameSession Session, Task Run)> ConnectAsync()
    {
        var game = await FakeGame.StartAsync();
        var session = CreateSession(game.PeerStream);
        var run = session.RunAsync(_cancellation.Token);
        await game.SendAsync("Welcome to the Amnesia TCP server!");
        return (game, session, run);
    }

    [Fact]
    public async Task Negotiation_is_the_first_Command_written_after_the_greeting()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;

        Assert.Equal(NegotiateSharedPose, await game.ReadLineAsync());
    }

    [Fact]
    public async Task Nothing_else_is_written_until_the_negotiation_Response_arrives()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(NegotiateSharedPose, await game.ReadLineAsync());

        var display = _callbacks.ReceiveChat(new ChatEntry("Bob", "hello")).AsTask();

        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(300)));
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose");
        Assert.Equal("chat:Bob:hello", await game.ReadLineAsync());
        await display;
    }

    [Fact]
    public async Task Writes_held_back_by_the_negotiation_keep_their_order()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        var messages = Enumerable.Range(1, 20).Select(number => $"message {number}").ToArray();

        var displays = messages.Select(message => _callbacks.ReceiveChat(new ChatEntry("Bob", message)).AsTask()).ToArray();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose");

        foreach (var message in messages) Assert.Equal($"chat:Bob:{message}", await game.ReadLineAsync());
        await Task.WhenAll(displays);
    }

    [Fact]
    public async Task Granting_both_Capabilities_makes_the_Shared_Pose_available_and_logs_the_negotiation()
    {
        var (game, session, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();

        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose");
        await _callbacks.DisplaySystem("marker");

        Assert.Equal("chat:SYSTEM:marker", await game.ReadLineAsync());
        Assert.True(session.IsSharedPoseAvailable);
        var logged = Assert.Single(_log, entry => entry.Event == LocalGameEventName.ProtocolNegotiated);
        Assert.Equal(ConnectionLogSeverity.Information, logged.Severity);
        Assert.Equal("ok", logged.Negotiation!.Outcome);
        Assert.Equal(2, logged.Negotiation.Version);
        Assert.Equal(["avatars", "localpose"], logged.Negotiation.Capabilities);
    }

    [Theory]
    [InlineData("RESPONSE protocol ok 2 avatars", "ok")]
    [InlineData("RESPONSE protocol ok 2 localpose", "ok")]
    [InlineData("RESPONSE protocol ok 2", "ok")]
    [InlineData("RESPONSE protocol unsupported-version", "unsupported-version")]
    [InlineData("RESPONSE protocol invalid", "invalid")]
    [InlineData("WARNING:Unknown command", "unknown-command")]
    public async Task Other_outcomes_leave_the_Session_on_the_legacy_protocol_with_one_SYSTEM_notice(string response, string outcome)
    {
        var (game, session, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();

        await game.SendAsync(response);

        Assert.Equal(AvatarsUnsupportedNotice, await game.ReadLineAsync());
        Assert.False(session.IsSharedPoseAvailable);
        var logged = Assert.Single(_log, entry => entry.Event == LocalGameEventName.ProtocolNegotiated);
        Assert.Equal(ConnectionLogSeverity.Warning, logged.Severity);
        Assert.Equal(outcome, logged.Negotiation!.Outcome);
        Assert.DoesNotContain(_log, entry => entry.Event == LocalGameEventName.UnrecognizedGameReply);

        await game.SendAsync("WARNING:Unknown command");
        await _callbacks.DisplaySystem("marker");
        Assert.Equal("chat:SYSTEM:marker", await game.ReadLineAsync());
    }

    [Fact]
    public async Task The_connected_notice_is_the_first_chat_line_after_the_negotiation()
    {
        await using var game = await FakeGame.StartAsync();
        var session = new LocalGameSession(
            game.PeerStream, callbacks => { _callbacks = callbacks; return _sessions; }, _ => { }, connectedNotice: "Version line.");
        _ = session.RunAsync(_cancellation.Token);
        await game.SendAsync("Welcome to the Amnesia TCP server!");
        Assert.Equal(NegotiateSharedPose, await game.ReadLineAsync());

        await game.SendAsync("WARNING:Unknown command");

        Assert.Equal("chat:SYSTEM:Version line.", await game.ReadLineAsync());
        Assert.Equal(AvatarsUnsupportedNotice, await game.ReadLineAsync());
    }

    [Fact]
    public async Task Chat_and_the_Shared_Custom_Story_Start_keep_working_on_an_older_game()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("WARNING:Unknown command");
        Assert.Equal(AvatarsUnsupportedNotice, await game.ReadLineAsync());

        await _callbacks.ReceiveChat(new ChatEntry("Bob", "hello"));
        Assert.Equal("chat:Bob:hello", await game.ReadLineAsync());

        await _callbacks.ReceiveCustomStoryStarted("mp-test-cs");
        Assert.Equal("startcustomstory:mp-test-cs", await game.ReadLineAsync());
        await game.SendAsync("RESPONSE:startcustomstory:starting");

        Assert.Equal("chat:SYSTEM:The host started Custom Story mp-test-cs.", await game.ReadLineAsync());
        Assert.Equal([("mp-test-cs", SharedCustomStoryStartOutcome.Started)], _sessions.SentOutcomes);
    }

    [Fact]
    public async Task Every_reconnection_negotiates_first()
    {
        var games = new Queue<FakeGame>([await FakeGame.StartAsync(), await FakeGame.StartAsync()]);
        var first = games.Peek();
        await using var second = games.Last();
        var losses = new List<int>();
        var run = LocalGameSession.RunReconnectingAsync(
            _ => Task.FromResult<Stream?>(games.TryDequeue(out var game) ? game.PeerStream : null),
            CreateSession,
            (attempt, _) => { losses.Add(attempt); return Task.CompletedTask; },
            _ => { },
            _cancellation.Token);

        await using (first)
        {
            await first.SendAsync("Welcome");
            Assert.Equal(NegotiateSharedPose, await first.ReadLineAsync());
        }
        await second.SendAsync("Welcome");

        Assert.Equal(NegotiateSharedPose, await second.ReadLineAsync());
        Assert.Equal([1], losses);
        await _cancellation.CancelAsync();
        await run;
    }

    private const string LocalPoseState = "STATE localpose 1000 1 1.2500 2.5000 -3.7500 90.0000 -45.0000 0 custom_stories/mp-test-cs/maps/start.map";
    private static readonly LanMessage.Pose ReceivedPose = new(
        123456, 3, 1.25, -2.5, 3.75, 90, -45, true, "custom_stories/My Story: Part 2/maps/cellar one.map");

    private async Task<FakeGame> ConnectWithSharedPoseAsync()
    {
        var (game, _, _) = await ConnectAsync();
        Assert.Equal(NegotiateSharedPose, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose");
        _sessions.SetPresent(true);
        Assert.Equal("avatarcreate partner", await game.ReadLineAsync());
        Assert.Equal("localpose subscribe 30", await game.ReadLineAsync());
        return game;
    }

    [Fact]
    public async Task The_Shared_Pose_runs_between_the_local_game_and_the_other_player_while_they_are_present()
    {
        await using var game = await ConnectWithSharedPoseAsync();

        await game.SendAsync(LocalPoseState);
        await WaitUntilAsync(() => _sessions.SentPoses.Count > 0);
        await _callbacks.ReceivePose(ReceivedPose);
        Assert.Equal(
            "avatarpose partner 123456 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 custom_stories/My Story: Part 2/maps/cellar one.map",
            await game.ReadLineAsync());
        _sessions.SetPresent(false);

        Assert.Equal("avatarremove partner", await game.ReadLineAsync());
        Assert.Equal("localpose unsubscribe", await game.ReadLineAsync());
        Assert.Equal(
            [new LanMessage.Pose(1000, 1, 1.25, 2.5, -3.75, 90, -45, false, "custom_stories/mp-test-cs/maps/start.map")],
            _sessions.SentPoses);
    }

    [Fact]
    public async Task Received_Poses_do_not_hold_back_chat_while_the_local_game_is_frozen_loading()
    {
        await using var game = await ConnectWithSharedPoseAsync();

        for (ulong time = 0; time < 10_000; time++) await _callbacks.ReceivePose(ReceivedPose with { TimeMs = time });
        await _callbacks.ReceiveChat(new ChatEntry("Bob", "still loading?")).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var ahead = new List<string>();
        for (var line = await game.ReadLineAsync(); line != "chat:Bob:still loading?"; line = await game.ReadLineAsync())
            ahead.Add(line!);
        Assert.True(ahead.Count <= SharedPose.MaxUnconfirmedPoses + 1, $"{ahead.Count} lines were written ahead of the chat.");
    }

    [Fact]
    public async Task Received_Poses_keep_flowing_while_the_local_game_answers_its_pings()
    {
        await using var game = await ConnectWithSharedPoseAsync();

        const int poses = SharedPose.MaxUnconfirmedPoses * 3;
        for (ulong time = 1; time <= poses; time++)
        {
            await _callbacks.ReceivePose(ReceivedPose with { TimeMs = time });
            var line = await game.ReadLineAsync();
            if (line == "ping")
            {
                await game.SendAsync("RESPONSE:ping:pong");
                line = await game.ReadLineAsync();
            }
            Assert.StartsWith($"avatarpose partner {time} ", line);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task The_loopback_socket_to_the_game_disables_Nagle()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync(_cancellation.Token);

        await using var stream = await LocalGameSocket.ConnectAsync(
            IPAddress.Loopback.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, _cancellation.Token);
        using var _ = await accept;

        Assert.True(Assert.IsType<NetworkStream>(stream).Socket.NoDelay);
    }

    private sealed class FakeGame : IAsyncDisposable
    {
        private readonly TcpClient _peerSide;
        private readonly TcpClient _gameSide;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private Task<string?>? _pendingRead;

        private FakeGame(TcpClient peerSide, TcpClient gameSide)
        {
            _peerSide = peerSide;
            _gameSide = gameSide;
            _reader = GameInteractionProtocol.CreateReader(gameSide.GetStream());
            _writer = GameInteractionProtocol.CreateWriter(gameSide.GetStream());
        }

        public Stream PeerStream => _peerSide.GetStream();

        public static async Task<FakeGame> StartAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync();
            var peerSide = new TcpClient();
            await peerSide.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new FakeGame(peerSide, await accept);
        }

        public Task SendAsync(string line) => _writer.WriteLineAsync(line);

        public async Task<string?> ReadLineAsync()
        {
            var read = _pendingRead ?? _reader.ReadLineAsync();
            _pendingRead = null;
            return await read.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task<bool> WritesNothingWithinAsync(TimeSpan window)
        {
            _pendingRead ??= _reader.ReadLineAsync();
            return await Task.WhenAny(_pendingRead, Task.Delay(window)) != _pendingRead;
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            _reader.Dispose();
            _gameSide.Dispose();
            _peerSide.Dispose();
        }
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        private bool _present;
        public event Action? OtherPlayerPresenceChanged;
        public bool IsOtherPlayerPresent => Volatile.Read(ref _present);
        public List<LanMessage.Pose> SentPoses { get { lock (_sentPoses) return [.. _sentPoses]; } }
        private readonly List<LanMessage.Pose> _sentPoses = [];

        public void SetPresent(bool present)
        {
            Volatile.Write(ref _present, present);
            OtherPlayerPresenceChanged?.Invoke();
        }

        public void SendPose(LanMessage.Pose pose) { lock (_sentPoses) _sentPoses.Add(pose); }
        public bool IsJoined => true;
        public List<(string, SharedCustomStoryStartOutcome)> SentOutcomes { get; } = [];
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken)
        {
            SentOutcomes.Add((identifier, outcome));
            return Task.CompletedTask;
        }
    }
}
