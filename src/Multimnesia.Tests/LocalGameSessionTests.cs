using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class LocalGameSessionTests : IAsyncDisposable
{
    private const string Negotiate = "protocol 2 avatars localpose interactions sharedcallbacks playerplace enemies";
    private const string AvatarsUnsupportedNotice = "chatentry - - SYSTEM:Your game does not support Avatars; movement will not be shared.";
    private const string InteractionsUnsupportedNotice = "chatentry - - SYSTEM:Your game does not support interactions; moving props will not be shared.";
    private const string SharedCallbacksUnsupportedNotice =
        "chatentry - - SYSTEM:Your game does not support Shared Callbacks; triggers, buttons, and lamps will not be shared.";
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

        Assert.Equal(Negotiate, await game.ReadLineAsync());
    }

    [Fact]
    public async Task Nothing_else_is_written_until_the_negotiation_Response_arrives()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());

        var display = _callbacks.ReceiveChat(new ChatEntry("Bob", "hello")).AsTask();

        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(300)));
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose");
        Assert.Equal("chatentry - - Bob:hello", await game.ReadLineAsync());
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

        foreach (var message in messages) Assert.Equal($"chatentry - - Bob:{message}", await game.ReadLineAsync());
        await Task.WhenAll(displays);
    }

    [Fact]
    public async Task Granting_both_Capabilities_makes_the_Shared_Pose_available_and_logs_the_negotiation()
    {
        var (game, session, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();

        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose");
        Assert.Equal(InteractionsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(SharedCallbacksUnsupportedNotice, await game.ReadLineAsync());
        await _callbacks.DisplaySystem("marker");

        Assert.Equal("chatentry - - SYSTEM:marker", await game.ReadLineAsync());
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
    public async Task Other_outcomes_leave_the_Session_on_the_legacy_protocol_with_SYSTEM_notices(string response, string outcome)
    {
        var (game, session, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();

        await game.SendAsync(response);

        Assert.Equal(AvatarsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(InteractionsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(SharedCallbacksUnsupportedNotice, await game.ReadLineAsync());
        Assert.False(session.IsSharedPoseAvailable);
        var logged = Assert.Single(_log, entry => entry.Event == LocalGameEventName.ProtocolNegotiated);
        Assert.Equal(ConnectionLogSeverity.Warning, logged.Severity);
        Assert.Equal(outcome, logged.Negotiation!.Outcome);
        Assert.DoesNotContain(_log, entry => entry.Event == LocalGameEventName.UnrecognizedGameReply);

        await game.SendAsync("WARNING:Unknown command");
        await _callbacks.DisplaySystem("marker");
        Assert.Equal("chatentry - - SYSTEM:marker", await game.ReadLineAsync());
    }

    [Fact]
    public async Task The_connected_notice_is_the_first_chat_line_after_the_negotiation()
    {
        await using var game = await FakeGame.StartAsync();
        var session = new LocalGameSession(
            game.PeerStream, callbacks => { _callbacks = callbacks; return _sessions; }, _ => { }, connectedNotice: "Version line.");
        _ = session.RunAsync(_cancellation.Token);
        await game.SendAsync("Welcome to the Amnesia TCP server!");
        Assert.Equal(Negotiate, await game.ReadLineAsync());

        await game.SendAsync("WARNING:Unknown command");

        Assert.Equal("chatentry - - SYSTEM:Version line.", await game.ReadLineAsync());
        Assert.Equal(AvatarsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(InteractionsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(SharedCallbacksUnsupportedNotice, await game.ReadLineAsync());
    }

    [Fact]
    public async Task Chat_and_the_Shared_Custom_Story_Start_keep_working_on_an_older_game()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("WARNING:Unknown command");
        Assert.Equal(AvatarsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(InteractionsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(SharedCallbacksUnsupportedNotice, await game.ReadLineAsync());

        await _callbacks.ReceiveChat(new ChatEntry("Bob", "hello"));
        Assert.Equal("chatentry - - Bob:hello", await game.ReadLineAsync());

        await _callbacks.ReceiveCustomStoryStarted("mp-test-cs");
        Assert.Equal("startcustomstory:mp-test-cs", await game.ReadLineAsync());
        await game.SendAsync("RESPONSE:startcustomstory:starting");

        Assert.Equal("chatentry - - SYSTEM:The host started Custom Story mp-test-cs.", await game.ReadLineAsync());
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
            Assert.Equal(Negotiate, await first.ReadLineAsync());
        }
        await second.SendAsync("Welcome");

        Assert.Equal(Negotiate, await second.ReadLineAsync());
        Assert.Equal([1], losses);
        await _cancellation.CancelAsync();
        await run;
    }

    private const string LocalPoseState = "STATE localpose 1000 1 1.2500 2.5000 -3.7500 90.0000 -45.0000 0 1 custom_stories/mp-test-cs/maps/start.map";
    private static readonly LanMessage.Pose ReceivedPose = new(
        123456, 3, 1.25, -2.5, 3.75, 90, -45, true, false, "custom_stories/My Story: Part 2/maps/cellar one.map");

    private async Task<FakeGame> ConnectWithSharedPoseAsync()
    {
        var (game, _, _) = await ConnectAsync();
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose");
        Assert.Equal(InteractionsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(SharedCallbacksUnsupportedNotice, await game.ReadLineAsync());
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
            "avatarpose partner 123456 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 0 custom_stories/My Story: Part 2/maps/cellar one.map",
            await game.ReadLineAsync());
        _sessions.SetPresent(false);

        Assert.Equal("avatarremove partner", await game.ReadLineAsync());
        Assert.Equal("localpose unsubscribe", await game.ReadLineAsync());
        Assert.Equal(
            [new LanMessage.Pose(1000, 1, 1.25, 2.5, -3.75, 90, -45, false, true, "custom_stories/mp-test-cs/maps/start.map")],
            _sessions.SentPoses);
    }

    [Fact]
    public async Task Teleporting_to_the_other_player_places_the_local_player_at_their_Pose_and_is_never_relayed_as_chat()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose playerplace");
        Assert.Equal(InteractionsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(SharedCallbacksUnsupportedNotice, await game.ReadLineAsync());
        await game.SendAsync("EVENT:CHAT:Alice:/host");
        _sessions.SetPresent(true);
        Assert.Equal("avatarcreate partner", await game.ReadLineAsync());
        Assert.Equal("localpose subscribe 30", await game.ReadLineAsync());
        await _callbacks.ReceivePose(ReceivedPose);
        Assert.StartsWith("avatarpose partner ", await game.ReadLineAsync());

        await game.SendAsync("EVENT:CHAT:Alice:/tp-to-peer");

        Assert.Equal("playerplace 1.2500 -2.5000 3.7500 90.0000 1 custom_stories/My Story: Part 2/maps/cellar one.map", await game.ReadLineAsync());
        await game.SendAsync("RESPONSE playerplace ok");
        Assert.Equal("chatentry - - SYSTEM:Teleported to the other player.", await game.ReadLineAsync());
        Assert.Empty(_sessions.SentChat);
    }

    [Fact]
    public async Task Received_Poses_do_not_hold_back_chat_while_the_local_game_is_frozen_loading()
    {
        await using var game = await ConnectWithSharedPoseAsync();

        for (ulong time = 0; time < 10_000; time++) await _callbacks.ReceivePose(ReceivedPose with { TimeMs = time });
        await _callbacks.ReceiveChat(new ChatEntry("Bob", "still loading?")).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var ahead = new List<string>();
        for (var line = await game.ReadLineAsync(); line != "chatentry - - Bob:still loading?"; line = await game.ReadLineAsync())
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

    private const string HoldMap = "custom_stories/mp-test-cs/maps/holds room.map";
    private const string ReportedBody = "12 3 1.2500 -2.5000 3.7500 0.0000 0.0000 0.0000 1.0000 0.5000 0.0000 -1.0000 0.0000 90.0000 0.0000";
    private static readonly BodyEntry ReportedEntry = new(12, 3, new BodyState(1.25, -2.5, 3.75, 0, 0, 0, 1, 0.5, 0, -1, 0, 90, 0));

    private async Task<FakeGame> ConnectWithHoldsAsync()
    {
        var (game, _, _) = await ConnectAsync();
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks");
        _sessions.SetPresent(true);
        Assert.Equal(["avatarcreate partner", "localpose subscribe 30", "reportedbodies subscribe 30"], await ReadSetupAsync(game));
        return game;
    }

    // The Shared Pose and the Holds set up independently, so their lines may interleave.
    private static async Task<string[]> ReadSetupAsync(FakeGame game) =>
        [.. new[] { (await game.ReadLineAsync())!, (await game.ReadLineAsync())!, (await game.ReadLineAsync())! }.Order()];

    [Fact]
    public async Task A_grab_and_throw_in_the_local_game_is_Claimed_and_streamed_to_the_other_player()
    {
        await using var game = await ConnectWithHoldsAsync();

        await game.SendAsync($"EVENT interactionstart 12 3 {HoldMap}");
        await game.SendAsync($"STATE reportedbodies 1000 1 {ReportedBody} {HoldMap}");
        await game.SendAsync($"EVENT interactionend 12 3 thrown {HoldMap}");
        await game.SendAsync($"EVENT reportsettled 12 {HoldMap}");

        await WaitUntilAsync(() => _sessions.SentHoldMessages.Count == 4);
        Assert.Equal(
            [
                new LanMessage.Claim(HoldMap, 12, ClaimReason.Interact), new LanMessage.Interaction(HoldMap, 12, 3, true),
                new LanMessage.Interaction(HoldMap, 12, 3, false), new LanMessage.Settled(HoldMap, 12)
            ],
            _sessions.SentHoldMessages);
        Assert.Equal([new LanMessage.Bodies(1000, HoldMap, [ReportedEntry])], _sessions.SentBodies);
    }

    [Fact]
    public async Task Hold_messages_are_written_while_Poses_wait_for_the_local_game_frozen_loading()
    {
        await using var game = await ConnectWithHoldsAsync();

        for (ulong time = 0; time < 10_000; time++) await _callbacks.ReceivePose(ReceivedPose with { TimeMs = time });
        await _callbacks.ReceiveHoldMessage(new LanMessage.Claim(HoldMap, 12, ClaimReason.Interact));
        await _callbacks.ReceiveBodies(new LanMessage.Bodies(1000, HoldMap, [ReportedEntry]));

        var ahead = new List<string>();
        for (var line = await game.ReadLineAsync(); line != $"entitydrive 12 {HoldMap}"; line = await game.ReadLineAsync())
            ahead.Add(line!);
        Assert.True(ahead.Count <= SharedPose.MaxUnconfirmedPoses + 1, $"{ahead.Count} lines were written ahead of the Claim.");
        string? next;
        do next = await game.ReadLineAsync(); while (next!.StartsWith("avatarpose ", StringComparison.Ordinal) || next == "ping");
        Assert.Equal($"entitybodies 1000 1 {ReportedBody} {HoldMap}", next);
    }

    [Fact]
    public async Task The_other_player_walking_into_another_map_returns_the_entity_they_Hold_to_local_physics()
    {
        await using var game = await ConnectWithHoldsAsync();

        await _callbacks.ReceivePose(ReceivedPose with { Map = HoldMap });
        await _callbacks.ReceiveHoldMessage(new LanMessage.Claim(HoldMap, 12, ClaimReason.Interact));
        await _callbacks.ReceivePose(ReceivedPose with { TimeMs = ReceivedPose.TimeMs + 33 });

        string? line;
        do line = await game.ReadLineAsync(); while (line!.StartsWith("avatarpose ", StringComparison.Ordinal) || line == $"entitydrive 12 {HoldMap}");
        Assert.Equal($"entityrelease 12 {HoldMap}", line);
    }

    [Fact]
    public async Task Shared_Callbacks_are_relayed_both_ways_and_kept_for_the_map_the_local_game_enters()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks");

        await game.SendAsync($"EVENT sharedcallback useitem 12 key_study {HoldMap}");
        await WaitUntilAsync(() => _sessions.SentWorldMessages.Count == 1);
        Assert.Equal([new LanMessage.UseItem(HoldMap, 12, "key_study")], _sessions.SentWorldMessages);

        await _callbacks.ReceiveWorldMessage(new LanMessage.ButtonState("maps/next.map", 5, true));
        await _callbacks.ReceiveWorldMessage(new LanMessage.Interact(HoldMap, 7));
        Assert.Equal($"sharedcallback interact 7 {HoldMap}", await game.ReadLineAsync());
        await game.SendAsync("RESPONSE sharedcallback not-found");
        await game.SendAsync("EVENT mapentered maps/next.map");
        Assert.Equal("buttonstate 5 1 maps/next.map", await game.ReadLineAsync());

        await WaitUntilAsync(() => { lock (_log) return _log.Any(entry => entry.Event == LocalGameEventName.SharedCallbackNotFound); });
    }

    [Fact]
    public async Task A_new_Shared_Custom_Story_Start_from_the_Session_Host_discards_the_kept_changes()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks");

        await _callbacks.ReceiveWorldMessage(new LanMessage.ButtonState("maps/old.map", 5, true));
        await _callbacks.ReceiveCustomStoryStarted("mp-test-cs");
        await _callbacks.ReceiveWorldMessage(new LanMessage.ButtonState("maps/first.map", 6, true));
        await game.SendAsync("EVENT mapentered maps/old.map");
        await game.SendAsync("EVENT mapentered maps/first.map");

        string? line;
        do line = await game.ReadLineAsync(); while (line!.StartsWith("startcustomstory:", StringComparison.Ordinal));
        Assert.Equal("buttonstate 6 1 maps/first.map", line);
    }

    [Fact]
    public async Task After_a_local_game_reconnect_the_Avatar_is_recreated_and_received_Poses_drive_it_again()
    {
        _sessions.SetPresent(true);
        var games = new Queue<FakeGame>([await FakeGame.StartAsync(), await FakeGame.StartAsync()]);
        var first = games.Peek();
        await using var second = games.Last();
        var run = LocalGameSession.RunReconnectingAsync(
            _ => Task.FromResult<Stream?>(games.TryDequeue(out var game) ? game.PeerStream : null),
            CreateSession,
            (_, _) => Task.CompletedTask,
            _ => { },
            _cancellation.Token);
        await using (first)
        {
            await first.SendAsync("Welcome");
            Assert.Equal(Negotiate, await first.ReadLineAsync());
            await first.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks");
            Assert.Equal(["avatarcreate partner", "localpose subscribe 30", "reportedbodies subscribe 30"], await ReadSetupAsync(first));
        }

        await second.SendAsync("Welcome");
        Assert.Equal(Negotiate, await second.ReadLineAsync());
        await second.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks");

        Assert.Equal(["avatarcreate partner", "localpose subscribe 30", "reportedbodies subscribe 30"], await ReadSetupAsync(second));
        await _callbacks.ReceivePose(ReceivedPose);
        Assert.StartsWith("avatarpose partner 123456 ", await second.ReadLineAsync());
        await _cancellation.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_missing_Avatar_model_is_shown_once_in_chat_and_avatarpose_failures_never()
    {
        await using var game = await ConnectWithSharedPoseAsync();

        await game.SendAsync("RESPONSE avatarpose not-found partner");
        await game.SendAsync("RESPONSE avatarcreate model-not-found partner");
        await game.SendAsync("RESPONSE avatarcreate model-not-found partner");
        await game.SendAsync("RESPONSE avatarpose invalid partner");

        Assert.Equal("chatentry - - SYSTEM:The Avatar model is not installed; the other player will be invisible.", await game.ReadLineAsync());
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(300)));
        lock (_log)
            Assert.Equal(
                ["RESPONSE avatarpose not-found partner", "RESPONSE avatarcreate model-not-found partner",
                 "RESPONSE avatarcreate model-not-found partner", "RESPONSE avatarpose invalid partner"],
                _log.Where(entry => entry.Event == LocalGameEventName.SharedPoseCommandFailed).Select(entry => entry.Line));
    }

    [Fact]
    public async Task Chat_entry_failures_are_logged_as_warnings_and_never_shown_in_chat()
    {
        await using var game = await ConnectWithSharedPoseAsync();

        await game.SendAsync("RESPONSE chatentry ok");
        await game.SendAsync("RESPONSE chatentry invalid");
        await game.SendAsync("RESPONSE chatentry invalid-author");
        await game.SendAsync("RESPONSE chatentry invalid-message");
        await game.SendAsync("RESPONSE chatentry unavailable");

        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(300)));
        lock (_log)
        {
            var failures = _log.Where(entry => entry.Event == LocalGameEventName.ChatEntryFailed).ToList();
            Assert.Equal(
                ["RESPONSE chatentry invalid", "RESPONSE chatentry invalid-author",
                 "RESPONSE chatentry invalid-message", "RESPONSE chatentry unavailable"],
                failures.Select(entry => entry.Line));
            Assert.All(failures, entry => Assert.Equal(ConnectionLogSeverity.Warning, entry.Severity));
        }
    }

    [Fact]
    public async Task Enemy_reports_are_sent_only_by_the_Session_Host_while_players_share_a_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        // Presence enables the existing streams and enemy reports.
        var commands = new List<string?>();
        for (var i = 0; i < 4; i++) commands.Add(await game.ReadLineAsync());
        Assert.Contains("enemyposes subscribe 30", commands);
        await game.SendAsync("EVENT mapentered maps/a.map");
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, "maps/elsewhere.map"));
        await game.SendAsync("STATE enemypose 139 1000 0 -2 0 28.25 90 Walk 0.2 1 maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        Assert.Empty(_sessions.SentEnemyPoses);
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, "maps/a.map"));
        await game.SendAsync("STATE enemypose 139 1000 0 -2 0 28.25 90 Walk 0.2 1 maps/a.map");
        await game.SendAsync("STATE enemypose 140 1000 0 -2 0 29 90 Walk 0.2 1 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentEnemyPoses.Count == 2);
        Assert.Equal(EnemyPosesProtocolTests.Sample, _sessions.SentEnemyPoses[0]);
        Assert.Equal(140, _sessions.SentEnemyPoses[1].EntityId);
    }

    [Fact]
    public async Task Enemy_Poses_drive_only_the_current_map_and_release_on_departure()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { Map = "maps/elsewhere.map" });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
        Assert.Equal("enemypose 139 1000 0 -2.0000 0.0000 28.2500 90.0000 Walk 0.2000 1 maps/a.map", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 2000 });
        Assert.StartsWith("enemypose 139 2000 ", await game.ReadLineAsync());
        Assert.Empty(_sessions.SentEnemyPoses);
        _sessions.SetPresent(false);
        var departure = new List<string?>();
        for (var i = 0; i < 4; i++) departure.Add(await game.ReadLineAsync());
        Assert.Contains("enemyrelease 139 maps/a.map", departure);
    }

    [Fact]
    public async Task A_new_Map_Visit_drives_a_fresh_copy_and_the_Host_leaving_releases_it()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        for (var visit = 1; visit <= 2; visit++)
        {
            await game.SendAsync("EVENT mapentered maps/a.map");
            await game.SendAsync($"STATE localpose {visit} 0 0 0 0 0 0 0 0 maps/a.map");
            await WaitUntilAsync(() => _sessions.SentPoses.Count == visit);
            await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
            Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
            Assert.StartsWith("enemypose 139 ", await game.ReadLineAsync());
        }
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, "maps/next.map"));
        var leaving = new List<string?> { await game.ReadLineAsync(), await game.ReadLineAsync() };
        Assert.Contains("enemyrelease 139 maps/a.map", leaving);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
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
        private int _arrival;
        public event Action? OtherPlayerPresenceChanged;
        public int OtherPlayerArrival => Volatile.Read(ref _arrival);
        public List<LanMessage.Pose> SentPoses { get { lock (_sentPoses) return [.. _sentPoses]; } }
        private readonly List<LanMessage.Pose> _sentPoses = [];

        public void SetPresent(bool present)
        {
            Volatile.Write(ref _arrival, present ? 1 : 0);
            OtherPlayerPresenceChanged?.Invoke();
        }

        public void SendPose(LanMessage.Pose pose) { lock (_sentPoses) _sentPoses.Add(pose); }
        public List<LanMessage.HoldMessage> SentHoldMessages { get { lock (_sentHoldMessages) return [.. _sentHoldMessages]; } }
        private readonly List<LanMessage.HoldMessage> _sentHoldMessages = [];
        public List<LanMessage.Bodies> SentBodies { get { lock (_sentBodies) return [.. _sentBodies]; } }
        private readonly List<LanMessage.Bodies> _sentBodies = [];
        public void SendBodies(LanMessage.Bodies bodies) { lock (_sentBodies) _sentBodies.Add(bodies); }

        public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken)
        {
            lock (_sentHoldMessages) _sentHoldMessages.Add(message);
            return Task.CompletedTask;
        }
        public List<LanMessage.WorldMessage> SentWorldMessages { get { lock (_sentWorldMessages) return [.. _sentWorldMessages]; } }
        private readonly List<LanMessage.WorldMessage> _sentWorldMessages = [];

        public Task SendWorldMessageAsync(LanMessage.WorldMessage message, CancellationToken cancellationToken)
        {
            lock (_sentWorldMessages) _sentWorldMessages.Add(message);
            return Task.CompletedTask;
        }
        public bool Joined { get; set; } = true;
        public bool IsJoined => Joined;
        private readonly List<LanMessage.EnemyPose> _sentEnemyPoses = [];
        public List<LanMessage.EnemyPose> SentEnemyPoses { get { lock (_sentEnemyPoses) return [.. _sentEnemyPoses]; } }
        public void SendEnemyPose(LanMessage.EnemyPose pose) { lock (_sentEnemyPoses) _sentEnemyPoses.Add(pose); }
        public List<(string, SharedCustomStoryStartOutcome)> SentOutcomes { get; } = [];
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => Task.FromResult(SessionOperationResult.Succeeded);
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public List<ChatEntry> SentChat { get { lock (_sentChat) return [.. _sentChat]; } }
        private readonly List<ChatEntry> _sentChat = [];

        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken)
        {
            lock (_sentChat) _sentChat.Add(entry);
            return Task.CompletedTask;
        }
        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken)
        {
            SentOutcomes.Add((identifier, outcome));
            return Task.CompletedTask;
        }
    }
}
