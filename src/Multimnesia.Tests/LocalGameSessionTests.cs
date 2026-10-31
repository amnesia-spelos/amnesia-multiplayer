using System.Net;
using System.Net.Sockets;
using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class LocalGameSessionTests : IAsyncDisposable
{
    private const string Negotiate = "protocol 2 avatars localpose interactions sharedcallbacks playerplace enemies lean settings";
    // Every Capability but `settings`, so these Sessions do not announce the Chat Color.
    private const string GrantedWithoutSettings =
        "RESPONSE protocol ok 2 avatars localpose interactions sharedcallbacks playerplace enemies lean";
    private const string AvatarsUnsupportedNotice = "chatentry - - SYSTEM:Your game does not support Avatars with lean; movement will not be shared.";
    private const string InteractionsUnsupportedNotice = "chatentry - - SYSTEM:Your game does not support interactions; moving props will not be shared.";
    private const string SharedCallbacksUnsupportedNotice =
        "chatentry - - SYSTEM:Your game does not support Shared Callbacks; triggers, buttons, and lamps will not be shared.";
    private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly FakeSessionOperations _sessions = new();
    private readonly List<LocalGameLogEntry> _log = [];
    private SessionCallbacks _callbacks = null!;
    private TrafficRecorder _traffic = TrafficRecorder.Disabled;
    private ChatColor? _configuredChatColor;

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _cancellation.Dispose();
    }

    private LocalGameSession CreateSession(Stream gameStream) => new(
        gameStream,
        callbacks => { _callbacks = callbacks; return _sessions; },
        entry => { lock (_log) _log.Add(entry); },
        trafficRecorder: _traffic,
        chatColor: _configuredChatColor);

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
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean");
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
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean");

        foreach (var message in messages) Assert.Equal($"chatentry - - Bob:{message}", await game.ReadLineAsync());
        await Task.WhenAll(displays);
    }

    [Fact]
    public async Task Granting_the_Shared_Pose_Capabilities_makes_the_Shared_Pose_available_and_logs_the_negotiation()
    {
        var (game, session, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();

        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean");
        Assert.Equal(InteractionsUnsupportedNotice, await game.ReadLineAsync());
        Assert.Equal(SharedCallbacksUnsupportedNotice, await game.ReadLineAsync());
        await _callbacks.DisplaySystem("marker");

        Assert.Equal("chatentry - - SYSTEM:marker", await game.ReadLineAsync());
        Assert.True(session.IsSharedPoseAvailable);
        var logged = Assert.Single(_log, entry => entry.Event == LocalGameEventName.ProtocolNegotiated);
        Assert.Equal(ConnectionLogSeverity.Information, logged.Severity);
        Assert.Equal("ok", logged.Negotiation!.Outcome);
        Assert.Equal(2, logged.Negotiation.Version);
        Assert.Equal(["avatars", "localpose", "lean"], logged.Negotiation.Capabilities);
    }

    [Theory]
    [InlineData("RESPONSE protocol ok 2 avatars", "ok")]
    [InlineData("RESPONSE protocol ok 2 localpose", "ok")]
    [InlineData("RESPONSE protocol ok 2 avatars localpose", "ok")]
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
        Assert.Equal([("mp-test-cs", SharedStoryStartOutcome.Started)], _sessions.SentOutcomes);
    }

    [Fact]
    public async Task The_Session_Host_relays_its_Main_Story_start_the_moment_the_game_reports_it()
    {
        _sessions.Joined = false;
        _sessions.JoiningPlayerAdmitted = true;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync(GrantedWithoutSettings);

        await game.SendAsync("EVENT:MainStoryStarted");

        Assert.Equal("chatentry - - SYSTEM:Starting the Main Story for the other player.", await game.ReadLineAsync());
        Assert.Equal(1, _sessions.SentMainStoryStarts);
    }

    [Fact]
    public async Task A_Joining_Player_starts_the_Session_Hosts_Main_Story_and_reports_back()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync(GrantedWithoutSettings);

        await _callbacks.ReceiveMainStoryStarted();
        Assert.Equal("startmainstory", await game.ReadLineAsync());
        await game.SendAsync("RESPONSE:startmainstory:starting");
        await game.SendAsync("EVENT:MainStoryStarted");

        Assert.Equal("chatentry - - SYSTEM:The host started the Main Story.", await game.ReadLineAsync());
        Assert.Equal([SharedStoryStartOutcome.Started], _sessions.SentMainStoryOutcomes);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task The_Session_Host_sees_why_its_Main_Story_start_failed_for_a_Joining_Player()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync(GrantedWithoutSettings);

        await _callbacks.ReceiveMainStoryStartOutcome(SharedStoryStartOutcome.NotInMainMenu);

        Assert.Equal(
            "chatentry - - SYSTEM:The Main Story could not start for the Joining Player: their game is not in the main menu.",
            await game.ReadLineAsync());
    }

    [Fact]
    public async Task A_new_Shared_Main_Story_Start_from_the_Session_Host_discards_the_kept_changes()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");

        await _callbacks.ReceiveWorldMessage(new LanMessage.ButtonState("maps/old.map", 5, true));
        await _callbacks.ReceiveMainStoryStarted();
        await _callbacks.ReceiveWorldMessage(new LanMessage.ButtonState("maps/first.map", 6, true));
        await game.SendAsync("EVENT mapentered maps/old.map");
        await game.SendAsync("EVENT mapentered maps/first.map");

        string? line;
        do line = await game.ReadLineAsync(); while (line == "startmainstory");
        Assert.Equal("buttonstate 6 1 maps/first.map", line);
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

    private const string LocalPoseState = "STATE localpose 1000 1 1.2500 2.5000 -3.7500 90.0000 -45.0000 0 1 -0.5000 custom_stories/mp-test-cs/maps/start.map";
    private static readonly LanMessage.Pose ReceivedPose = new(
        123456, 3, 1.25, -2.5, 3.75, 90, -45, true, false, 1, "custom_stories/My Story: Part 2/maps/cellar one.map");

    private async Task<FakeGame> ConnectWithSharedPoseAsync()
    {
        var (game, _, _) = await ConnectAsync();
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean");
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
            "avatarpose partner 123456 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 0 1.0000 custom_stories/My Story: Part 2/maps/cellar one.map",
            await game.ReadLineAsync());
        _sessions.SetPresent(false);

        Assert.Equal("avatarremove partner", await game.ReadLineAsync());
        Assert.Equal("localpose unsubscribe", await game.ReadLineAsync());
        Assert.Equal(
            [new LanMessage.Pose(1000, 1, 1.25, 2.5, -3.75, 90, -45, false, true, -0.5, "custom_stories/mp-test-cs/maps/start.map")],
            _sessions.SentPoses);
    }

    [Fact]
    public async Task Teleporting_to_the_other_player_places_the_local_player_at_their_Pose_and_is_never_relayed_as_chat()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean playerplace");
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
    public async Task Successful_hosting_is_displayed_in_green_in_the_local_game()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");
        _sessions.HostResult = SessionOperationResult.SucceededWith("Hosting Multiplayer Session at 100.101.102.103:5000.");

        await game.SendAsync("EVENT:CHAT:Alice:/host");

        Assert.Equal("chatentry - 00FF00 SYSTEM:Hosting Multiplayer Session at 100.101.102.103:5000.", await game.ReadLineAsync());
    }

    private const string ChatColorDescription =
        "Your name's color in the other players' chat, as RRGGBB. Empty uses the game default.";
    private const string ChatColorIgnoredNotice =
        "chatentry - - SYSTEM:Your Chat color setting was ignored: use six hex digits, RRGGBB, or leave it empty.";

    private void ConfigureChatColor(string configuredColor) =>
        _configuredChatColor = ChatColor.TryParse(configuredColor, out var color) ? color : null;

    private async Task<FakeGame> HostWithSettingsAsync(string configuredColor)
    {
        ConfigureChatColor(configuredColor);
        var (game, _, _) = await ConnectAsync();
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks settings");
        Assert.Equal($"settingannounce multimnesia.color Chat color\t{configuredColor}\t{ChatColorDescription}", await game.ReadLineAsync());
        await game.SendAsync("EVENT:CHAT:Alice:/host");
        return game;
    }

    private async Task<ChatColor?> AuthorColorOfSentChatAsync(FakeGame game, string message)
    {
        await game.SendAsync($"EVENT:CHAT:Alice:{message}");
        await WaitUntilAsync(() => _sessions.SentChat.Any(entry => entry.Message == message));
        return _sessions.SentChat.Single(entry => entry.Message == message).AuthorColor;
    }

    [Fact]
    public async Task The_game_stored_Chat_Color_takes_priority_over_the_configured_one()
    {
        await using var game = await HostWithSettingsAsync("FF00FF");

        await game.SendAsync("RESPONSE settingannounce ok multimnesia.color 00ff00");

        Assert.Equal("00FF00", (await AuthorColorOfSentChatAsync(game, "hi"))?.Hex);
    }

    [Fact]
    public async Task An_empty_Chat_Color_setting_uses_the_game_default_even_when_one_is_configured()
    {
        await using var game = await HostWithSettingsAsync("FF00FF");

        await game.SendAsync("RESPONSE settingannounce ok multimnesia.color ");

        Assert.Null(await AuthorColorOfSentChatAsync(game, "hi"));
    }

    [Fact]
    public async Task Before_the_game_answers_the_announce_the_configured_Chat_Color_is_used()
    {
        await using var game = await HostWithSettingsAsync("FF00FF");

        Assert.Equal("FF00FF", (await AuthorColorOfSentChatAsync(game, "hi"))?.Hex);
    }

    [Fact]
    public async Task A_changed_Chat_Color_applies_to_later_chat_entries()
    {
        await using var game = await HostWithSettingsAsync("");
        await game.SendAsync("RESPONSE settingannounce ok multimnesia.color ");
        Assert.Null(await AuthorColorOfSentChatAsync(game, "before"));

        await game.SendAsync("EVENT settingchanged multimnesia.color 0000FF");

        Assert.Equal("0000FF", (await AuthorColorOfSentChatAsync(game, "after"))?.Hex);
    }

    [Theory]
    [InlineData("RESPONSE settingannounce ok multimnesia.color #00FF00")]
    [InlineData("EVENT settingchanged multimnesia.color green")]
    public async Task An_invalid_Chat_Color_setting_keeps_the_previous_color_and_tells_the_local_player(string line)
    {
        await using var game = await HostWithSettingsAsync("FF00FF");

        await game.SendAsync(line);

        Assert.Equal(ChatColorIgnoredNotice, await game.ReadLineAsync());
        Assert.Equal("FF00FF", (await AuthorColorOfSentChatAsync(game, "hi"))?.Hex);
    }

    [Fact]
    public async Task A_change_to_another_setting_is_ignored()
    {
        await using var game = await HostWithSettingsAsync("FF00FF");

        await game.SendAsync("EVENT settingchanged other.color 00FF00");

        Assert.Equal("FF00FF", (await AuthorColorOfSentChatAsync(game, "hi"))?.Hex);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData("RESPONSE settingannounce duplicate multimnesia.color")]
    [InlineData("RESPONSE settingannounce unavailable multimnesia.color")]
    [InlineData("RESPONSE settingannounce invalid")]
    public async Task A_refused_announce_is_logged_and_the_configured_Chat_Color_is_used(string line)
    {
        await using var game = await HostWithSettingsAsync("FF00FF");

        await game.SendAsync(line);

        await WaitUntilAsync(() => _log.Any(entry => entry.Event == LocalGameEventName.PeerSettingRefused));
        var logged = _log.Single(entry => entry.Event == LocalGameEventName.PeerSettingRefused);
        Assert.Equal(ConnectionLogSeverity.Warning, logged.Severity);
        Assert.Equal(line, logged.Line);
        Assert.Equal("FF00FF", (await AuthorColorOfSentChatAsync(game, "hi"))?.Hex);
    }

    [Fact]
    public async Task A_game_without_settings_is_not_sent_the_announce_and_the_configured_Chat_Color_is_used()
    {
        ConfigureChatColor("FF00FF");
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());
        await game.SendAsync(GrantedWithoutSettings);
        await game.SendAsync("EVENT:CHAT:Alice:/host");

        Assert.Equal("FF00FF", (await AuthorColorOfSentChatAsync(game, "hi"))?.Hex);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(200)));
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
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");
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
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");

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
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");

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
            await first.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");
            Assert.Equal(["avatarcreate partner", "localpose subscribe 30", "reportedbodies subscribe 30"], await ReadSetupAsync(first));
        }

        await second.SendAsync("Welcome");
        Assert.Equal(Negotiate, await second.ReadLineAsync());
        await second.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");

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
    public async Task The_Session_Host_enables_Avatar_sight_only_while_players_share_a_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        var startup = new List<string?>();
        for (var i = 0; i < 4; i++) startup.Add(await game.ReadLineAsync());
        Assert.Contains("avatarcreate partner", startup);
        Assert.DoesNotContain(startup, line => line!.StartsWith("avatarenemysight "));

        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, true, 0, "maps/a.map"));
        var shared = new[] { await game.ReadLineAsync(), await game.ReadLineAsync() };
        Assert.Contains("avatarenemysight partner 1", shared);
        Assert.Contains(shared, line => line!.StartsWith("avatarpose partner "));

        await game.SendAsync("EVENT mapentered maps/b.map");
        Assert.Equal("avatarenemysight partner 0", await game.ReadLineAsync());
        await game.SendAsync("EVENT mapentered maps/a.map");
        Assert.Equal("avatarenemysight partner 1", await game.ReadLineAsync());

        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, true, 0, "maps/b.map"));
        var separated = new[] { await game.ReadLineAsync(), await game.ReadLineAsync() };
        Assert.Contains("avatarenemysight partner 0", separated);
        Assert.Contains(separated, line => line!.StartsWith("avatarpose partner "));

        await _callbacks.ReceivePose(new(3, 0, 0, 0, 0, 0, 0, false, true, 0, "maps/a.map"));
        var returned = new[] { await game.ReadLineAsync(), await game.ReadLineAsync() };
        Assert.Contains("avatarenemysight partner 1", returned);
        await game.SendAsync("EVENT mapentered maps/a.map");
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));

        _sessions.SetPresent(false);
        var departure = new List<string?>();
        for (var i = 0; i < 4; i++) departure.Add(await game.ReadLineAsync());
        Assert.Contains("avatarremove partner", departure);
        _sessions.SetPresent(true);
        for (var i = 0; i < 4; i++) await game.ReadLineAsync();
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceivePose(new(4, 0, 0, 0, 0, 0, 0, false, true, 0, "maps/a.map"));
        var rejoined = new[] { await game.ReadLineAsync(), await game.ReadLineAsync() };
        Assert.Contains("avatarenemysight partner 1", rejoined);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Joining_Players_and_games_without_enemies_never_enable_Avatar_sight(bool joined, bool enemies)
    {
        _sessions.Joined = joined;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks" + (enemies ? " enemies" : ""));
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, true, 0, "maps/a.map"));
        Assert.StartsWith("avatarpose partner ", await game.ReadLineAsync());
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Avatar_sight_failures_are_logged_without_chat_feedback()
    {
        await using var game = await ConnectWithSharedPoseAsync();
        await game.SendAsync("RESPONSE avatarenemysight invalid");
        await WaitUntilAsync(() => { lock (_log) return _log.Any(entry => entry.Event == LocalGameEventName.SharedPoseCommandFailed); });
        var failure = Assert.Single(_log, entry => entry.Event == LocalGameEventName.SharedPoseCommandFailed);
        Assert.Equal("RESPONSE avatarenemysight invalid", failure.Line);
        Assert.Equal(ConnectionLogSeverity.Warning, failure.Severity);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Enemy_reports_are_sent_only_by_the_Session_Host_while_players_share_a_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        // Presence enables the existing streams and enemy reports.
        var commands = new List<string?>();
        for (var i = 0; i < 4; i++) commands.Add(await game.ReadLineAsync());
        Assert.Contains("enemyposes subscribe 30", commands);
        await game.SendAsync("EVENT mapentered maps/a.map");
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/elsewhere.map"));
        await game.SendAsync("STATE enemypose 139 1000 0 -2 0 28.25 90 Walk 0.2 1 maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        Assert.Empty(_sessions.SentEnemyPoses);
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
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
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
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
    public async Task Each_enemy_attack_is_sent_once_by_the_Session_Host_while_players_share_a_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 4; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/elsewhere.map"));
        await game.SendAsync("EVENT enemyattack 139 normal 1.2 1.5 1 0 0.75 0.9 maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        Assert.Empty(_sessions.SentEnemyEvents);
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
        await game.SendAsync("EVENT enemyattack 139 normal 1.2 1.5 1 0 0.75 0.9 maps/a.map");
        await game.SendAsync("STATE localpose 2 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 2);
        Assert.Equal(EnemyAttacksProtocolTests.Sample, Assert.Single(_sessions.SentEnemyEvents));
    }

    [Fact]
    public async Task A_received_enemy_attack_is_delivered_only_on_the_current_map()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
        Assert.StartsWith("enemypose 139 ", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyEvent(EnemyAttacksProtocolTests.Sample with { Map = "maps/elsewhere.map" });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceiveEnemyEvent(EnemyAttacksProtocolTests.Sample);
        Assert.Equal("enemyattack 139 normal 1.2000 1.5000 1.0000 0.0000 0.7500 0.9000 maps/a.map", await game.ReadLineAsync());
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(_sessions.SentEnemyEvents);
    }

    [Fact]
    public async Task Each_enemy_door_break_is_sent_by_the_Session_Host_while_players_share_a_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 4; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/elsewhere.map"));
        await game.SendAsync("EVENT enemydoorbreak 141 maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        Assert.Empty(_sessions.SentEnemyEvents);
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
        await game.SendAsync("EVENT enemydoorbreak 141 maps/a.map");
        await game.SendAsync("STATE localpose 2 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 2);
        Assert.Equal(EnemyDoorBreaksProtocolTests.Sample, Assert.Single(_sessions.SentEnemyEvents));
    }

    [Fact]
    public async Task A_received_enemy_door_break_breaks_the_door_only_on_the_current_map()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveEnemyEvent(EnemyDoorBreaksProtocolTests.Sample with { Map = "maps/elsewhere.map" });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceiveEnemyEvent(EnemyDoorBreaksProtocolTests.Sample);
        Assert.Equal("enemydoorbreak 141 maps/a.map", await game.ReadLineAsync());
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(_sessions.SentEnemyEvents);
    }

    [Fact]
    public async Task Each_hallucination_disappearance_is_sent_by_the_Session_Host_while_players_share_a_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 4; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/elsewhere.map"));
        await game.SendAsync("EVENT enemydisappear 139 4200 maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        Assert.Empty(_sessions.SentEnemyEvents);
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
        await game.SendAsync("EVENT enemydisappear 139 4200 maps/a.map");
        await game.SendAsync("STATE localpose 2 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 2);
        Assert.Equal(EnemyDisappearancesProtocolTests.Sample, Assert.Single(_sessions.SentEnemyEvents));
    }

    [Fact]
    public async Task A_Joining_Player_never_sends_its_own_games_hallucination_disappearance()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await game.SendAsync("EVENT enemydisappear 139 4200 maps/a.map");
        await SyncAsync(game);
        Assert.Empty(_sessions.SentEnemyEvents);
    }

    [Fact]
    public async Task A_received_hallucination_disappearance_plays_once_only_on_the_current_map()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveEnemyEvent(EnemyDisappearancesProtocolTests.Sample with { Map = "maps/elsewhere.map" });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceiveEnemyEvent(EnemyDisappearancesProtocolTests.Sample);
        Assert.Equal("enemydisappear 139 4200 maps/a.map", await game.ReadLineAsync());
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(_sessions.SentEnemyEvents);
    }

    // The game ignores the copy's Enemy Poses sampled no later than the disappearance, so the copy disappears where the Session
    // Host's enemy was, and a later one brings the enemy back as the Session Host's game activated it again.
    [Fact]
    public async Task A_hallucination_disappearance_follows_the_Enemy_Pose_received_before_it_and_later_poses_still_follow()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 4100 });
        await _callbacks.ReceiveEnemyEvent(EnemyDisappearancesProtocolTests.Sample);
        Assert.StartsWith("enemypose 139 4100 ", await game.ReadLineAsync());
        Assert.Equal("enemydisappear 139 4200 maps/a.map", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 9000 });
        Assert.StartsWith("enemypose 139 9000 ", await game.ReadLineAsync());
    }

    // A hallucination can end before its first Enemy Pose reaches the Joining Player; the game plays it on the enemy undriven.
    [Fact]
    public async Task A_hallucination_disappearance_plays_on_a_copy_not_yet_driven()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync(GrantedWithoutSettings);
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceiveEnemyEvent(EnemyDisappearancesProtocolTests.Sample);
        Assert.Equal("enemydisappear 139 4200 maps/a.map", await game.ReadLineAsync());
    }

    [Fact]
    public async Task Each_Kaernk_bite_is_sent_by_the_Session_Host_while_players_share_a_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 4; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/elsewhere.map"));
        await game.SendAsync("EVENT enemyeat 139 maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        Assert.Empty(_sessions.SentEnemyEvents);
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
        await game.SendAsync("EVENT enemyeat 139 maps/a.map");
        await game.SendAsync("EVENT enemyeat 139 maps/a.map");
        await game.SendAsync("STATE localpose 2 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 2);
        Assert.Equal([EnemyBitesProtocolTests.Sample, EnemyBitesProtocolTests.Sample], _sessions.SentEnemyEvents);
    }

    [Fact]
    public async Task A_Joining_Player_never_sends_its_own_games_Kaernk_bite()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await game.SendAsync("EVENT enemyeat 139 maps/a.map");
        await SyncAsync(game);
        Assert.Empty(_sessions.SentEnemyEvents);
    }

    [Fact]
    public async Task A_received_Kaernk_bite_is_shown_on_the_copy_only_on_the_current_map()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveEnemyEvent(EnemyBitesProtocolTests.Sample with { Map = "maps/elsewhere.map" });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceiveEnemyEvent(EnemyBitesProtocolTests.Sample);
        Assert.Equal("enemyeat 139 maps/a.map", await game.ReadLineAsync());
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(_sessions.SentEnemyEvents);
    }

    // The game shows the bite where the copy is shown, so the copy must first reach the Enemy Pose received before it.
    [Fact]
    public async Task A_Kaernk_bite_follows_the_Enemy_Pose_received_before_it()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 4100 });
        await _callbacks.ReceiveEnemyEvent(EnemyBitesProtocolTests.Sample);
        Assert.StartsWith("enemypose 139 4100 ", await game.ReadLineAsync());
        Assert.Equal("enemyeat 139 maps/a.map", await game.ReadLineAsync());
    }

    [Fact]
    public async Task An_enemy_attack_follows_the_Enemy_Pose_received_before_it()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        await _callbacks.ReceiveEnemyEvent(EnemyAttacksProtocolTests.Sample);
        Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
        Assert.StartsWith("enemypose 139 1000 ", await game.ReadLineAsync());
        Assert.StartsWith("enemyattack 139 ", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 2000 });
        await _callbacks.ReceiveEnemyEvent(EnemyAttacksProtocolTests.Sample);
        Assert.StartsWith("enemypose 139 2000 ", await game.ReadLineAsync());
        Assert.StartsWith("enemyattack 139 ", await game.ReadLineAsync());
    }

    // The Session Host's game on maps/a.map, with the Joining Player's Pose on the same map.
    private async Task<FakeGame> HostSharingAMapAsync()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await game.ReadLineAsync();
        await game.SendAsync(GrantedWithoutSettings);
        _sessions.SetPresent(true);
        for (var i = 0; i < 4; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
        var avatar = new[] { await game.ReadLineAsync(), await game.ReadLineAsync() };
        Assert.Contains("avatarenemysight partner 1", avatar);
        return game;
    }

    // The Joining Player's game on maps/a.map, driving the Session Host's Grunt 139.
    private async Task<FakeGame> JoiningPlayerDrivingAGruntAsync()
    {
        var (game, _, _) = await ConnectAsync();
        await game.ReadLineAsync();
        await game.SendAsync(GrantedWithoutSettings);
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
        Assert.StartsWith("enemypose 139 1000 ", await game.ReadLineAsync());
        return game;
    }

    [Fact]
    public async Task A_Session_Host_callback_death_answers_its_own_game_and_kills_the_Joining_Player_with_its_count()
    {
        await using var game = await HostSharingAMapAsync();
        await game.SendAsync("EVENT playerdeath 1 2 1 cp_test maps/a.map");
        Assert.Equal("shareddeath 1 1 2 cp_test maps/a.map", await game.ReadLineAsync());
        await WaitUntilAsync(() => _sessions.SentDeathMessages.Count == 1);
        Assert.Equal(new LanMessage.SharedDeath("maps/a.map", 1, 0, 2, "cp_test"), Assert.Single(_sessions.SentDeathMessages));
    }

    [Fact]
    public async Task A_death_without_a_callback_or_with_the_other_player_elsewhere_coordinates_nothing()
    {
        await using var game = await HostSharingAMapAsync();
        // Only _auto: the game preserves Shared Enemies itself.
        await game.SendAsync("EVENT playerdeath 1 0 0 _auto maps/a.map");
        await game.SendAsync("EVENT checkpoint 1 0 _auto maps/a.map");
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/elsewhere.map"));
        Assert.Contains("avatarenemysight partner 0", new[] { await game.ReadLineAsync(), await game.ReadLineAsync() });
        await game.SendAsync("EVENT playerdeath 2 0 1 cp_test maps/a.map");
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(_sessions.SentDeathMessages);
    }

    [Fact]
    public async Task One_episode_lasts_until_this_game_completes_its_checkpoint()
    {
        await using var game = await HostSharingAMapAsync();
        await game.SendAsync("EVENT playerdeath 1 0 1 cp_test maps/a.map");
        Assert.Equal("shareddeath 1 1 0 cp_test maps/a.map", await game.ReadLineAsync());
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Report);
        await game.SendAsync("EVENT playerdeath 2 0 1 cp_test maps/a.map");
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await game.SendAsync("EVENT checkpoint 1 1 cp_test maps/a.map");
        await game.SendAsync("EVENT playerdeath 3 1 1 cp_test maps/a.map");
        Assert.Equal("shareddeath 2 3 1 cp_test maps/a.map", await game.ReadLineAsync());
        Assert.Equal([0, 1], _sessions.SentDeathMessages.Select(death => ((LanMessage.SharedDeath)death).Count));
    }

    // The game reports its Pose after every earlier line, so this waits until the Game Peer has seen them all.
    private async Task SyncAsync(FakeGame game)
    {
        var sent = _sessions.SentPoses.Count;
        await game.SendAsync($"STATE localpose {sent + 100} 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count > sent);
    }

    [Theory]
    [InlineData("EVENT checkpoint 2 1 cp_test maps/a.map", "shareddeath 1 0 2 cp_test maps/a.map", 2, "cp_test")]
    // A mismatched registration still kills the Session Host, so nobody is stranded; the game keeps its own count.
    [InlineData("EVENT checkpoint 0 0 _auto maps/a.map", "shareddeath 1 0 0 _auto maps/a.map", 0, "_auto")]
    public async Task A_Joining_Player_death_kills_the_Session_Host_and_is_answered_with_the_Session_Host_count(
        string checkPoint, string command, int count, string name)
    {
        await using var game = await HostSharingAMapAsync();
        await game.SendAsync(checkPoint);
        await SyncAsync(game);
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Report with { Map = "maps/elsewhere.map" });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Report);
        Assert.Equal(command, await game.ReadLineAsync());
        await WaitUntilAsync(() => _sessions.SentDeathMessages.Count == 1);
        Assert.Equal(new LanMessage.SharedDeath("maps/a.map", 1, 3, count, name), Assert.Single(_sessions.SentDeathMessages));
        // The Session Host's own death while it dies for the episode belongs to the same episode.
        await game.SendAsync("EVENT playerdeath 1 2 1 cp_test maps/a.map");
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Single(_sessions.SentDeathMessages);
    }

    [Fact]
    public async Task The_Session_Host_relays_no_enemy_reports_from_its_episode_until_its_checkpoint_resets_them()
    {
        await using var game = await HostSharingAMapAsync();
        await game.SendAsync("EVENT playerdeath 1 0 1 cp_test maps/a.map");
        Assert.StartsWith("shareddeath 1 1 ", await game.ReadLineAsync());
        await game.SendAsync("STATE enemypose 139 1000 0 -2 0 28.25 90 Walk 0.2 1 maps/a.map");
        await game.SendAsync("EVENT enemyattack 139 normal 1.2 1.5 1 0 0.75 0.9 maps/a.map");
        await game.SendAsync("EVENT enemydisappear 139 4200 maps/a.map");
        await game.SendAsync("EVENT enemyeat 139 maps/a.map");
        await SyncAsync(game);
        Assert.Empty(_sessions.SentEnemyPoses);
        Assert.Empty(_sessions.SentEnemyEvents);
        await game.SendAsync("EVENT checkpoint 1 1 cp_test maps/a.map");
        await game.SendAsync("STATE enemypose 139 1000 0 -2 0 28.25 90 Walk 0.2 1 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentEnemyPoses.Count == 1);
    }

    [Fact]
    public async Task A_door_an_enemy_breaks_during_an_episode_is_broken_in_every_game()
    {
        await using var game = await HostSharingAMapAsync();
        await game.SendAsync("EVENT playerdeath 1 0 1 cp_test maps/a.map");
        Assert.StartsWith("shareddeath 1 1 ", await game.ReadLineAsync());
        await game.SendAsync("EVENT enemydoorbreak 141 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentEnemyEvents.Count == 1);
        Assert.Equal(EnemyDoorBreaksProtocolTests.Sample, Assert.Single(_sessions.SentEnemyEvents));
    }

    [Fact]
    public async Task A_Joining_Player_applies_a_received_door_break_during_an_episode()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Episode with { Death = 0 });
        Assert.StartsWith("shareddeath 1 0 ", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyEvent(EnemyDoorBreaksProtocolTests.Sample);
        Assert.Equal("enemydoorbreak 141 maps/a.map", await game.ReadLineAsync());
    }

    // The checkpoint resets the copy the bite was sampled from, so a bite arriving during the episode is stale.
    [Fact]
    public async Task A_Joining_Player_shows_no_received_Kaernk_bite_during_an_episode()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Episode with { Death = 0 });
        Assert.StartsWith("shareddeath 1 0 ", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyEvent(EnemyBitesProtocolTests.Sample);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task A_Joining_Player_death_after_the_open_episode_reached_it_is_answered_once_this_episode_completes()
    {
        await using var game = await HostSharingAMapAsync();
        await game.SendAsync("EVENT playerdeath 1 0 1 cp_test maps/a.map");
        Assert.Equal("shareddeath 1 1 0 cp_test maps/a.map", await game.ReadLineAsync());
        // The Joining Player already respawned from episode 1 and died again, while this game is still dying.
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Report with { Death = 4, AfterEpisode = 1 });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await game.SendAsync("EVENT checkpoint 1 1 cp_test maps/a.map");
        Assert.Equal("shareddeath 2 0 1 cp_test maps/a.map", await game.ReadLineAsync());
        await WaitUntilAsync(() => _sessions.SentDeathMessages.Count == 2);
        Assert.Equal(new LanMessage.SharedDeath("maps/a.map", 2, 4, 1, "cp_test"), _sessions.SentDeathMessages[1]);
    }

    [Fact]
    public async Task An_episode_this_game_refuses_ends_at_once()
    {
        await using var game = await HostSharingAMapAsync();
        await game.SendAsync("EVENT checkpoint 0 1 cp_test maps/a.map");
        await SyncAsync(game);
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Report);
        Assert.Equal("shareddeath 1 0 0 cp_test maps/a.map", await game.ReadLineAsync());
        // In a flashback, say: the player was not killed, and no checkpoint follows.
        await game.SendAsync("RESPONSE shareddeath unavailable");
        await SyncAsync(game);
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Report with { Death = 4 });
        Assert.Equal("shareddeath 2 0 0 cp_test maps/a.map", await game.ReadLineAsync());
    }

    [Fact]
    public async Task A_Joining_Player_reports_only_its_own_deaths_that_run_a_callback()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await game.SendAsync("EVENT playerdeath 1 0 0 _auto maps/a.map");
        await game.SendAsync("EVENT checkpoint 1 0 _auto maps/a.map");
        await game.SendAsync("EVENT playerdeath 2 4 1 cp_test maps/a.map");
        await WaitUntilAsync(() => _sessions.SentDeathMessages.Count == 1);
        Assert.Equal(new LanMessage.PlayerDeath("maps/a.map", 2, 0), Assert.Single(_sessions.SentDeathMessages));
        // It is answered by the Session Host's episode, never by this game's own.
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task A_Joining_Player_death_report_names_the_last_episode_it_received()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Episode with { Episode = 5, Death = 0 });
        Assert.StartsWith("shareddeath 1 0 ", await game.ReadLineAsync());
        await game.SendAsync("EVENT checkpoint 3 1 cp_test maps/a.map");
        Assert.Equal("enemyrelease 139 maps/a.map", await game.ReadLineAsync());
        await game.SendAsync("EVENT playerdeath 1 3 1 cp_test maps/a.map");
        await WaitUntilAsync(() => _sessions.SentDeathMessages.Count == 1);
        Assert.Equal(new LanMessage.PlayerDeath("maps/a.map", 1, 5), Assert.Single(_sessions.SentDeathMessages));
    }

    [Fact]
    public async Task A_Joining_Player_applies_an_episode_on_its_current_map_only()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Episode with { Map = "maps/elsewhere.map" });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Episode);
        Assert.Equal("shareddeath 1 3 2 cp_test maps/a.map", await game.ReadLineAsync());
        Assert.Empty(_sessions.SentDeathMessages);
    }

    [Fact]
    public async Task After_a_Shared_Death_the_Joining_Player_drives_a_fresh_copy_once_its_checkpoint_reset_the_old_one()
    {
        await using var game = await JoiningPlayerDrivingAGruntAsync();
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Episode with { Death = 0 });
        Assert.Equal("shareddeath 1 0 2 cp_test maps/a.map", await game.ReadLineAsync());
        // Samples the Session Host sent before its own reset would drive a copy this game's checkpoint is about to reset.
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 2000 });
        await _callbacks.ReceiveEnemyEvent(EnemyAttacksProtocolTests.Sample);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        // The checkpoint deactivated the copy; releasing it keeps it inactive until the next sample drives it again.
        await game.SendAsync("EVENT checkpoint 3 1 cp_test maps/a.map");
        Assert.Equal("enemyrelease 139 maps/a.map", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 9000 });
        Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
        Assert.StartsWith("enemypose 139 9000 ", await game.ReadLineAsync());
    }

    [Fact]
    public async Task A_Joining_Player_death_before_any_checkpoint_report_is_left_to_its_own_game()
    {
        await using var game = await HostSharingAMapAsync();
        await _callbacks.ReceiveDeathMessage(SharedDeathsProtocolTests.Report);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(_sessions.SentDeathMessages);
    }

    [Fact]
    public async Task A_new_Map_Visit_drives_a_fresh_copy_and_the_Host_leaving_releases_it()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        for (var visit = 1; visit <= 2; visit++)
        {
            await game.SendAsync("EVENT mapentered maps/a.map");
            await game.SendAsync($"STATE localpose {visit} 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
            await WaitUntilAsync(() => _sessions.SentPoses.Count == visit);
            await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
            Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
            Assert.StartsWith("enemypose 139 ", await game.ReadLineAsync());
        }
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/next.map"));
        var leaving = new List<string?> { await game.ReadLineAsync(), await game.ReadLineAsync() };
        Assert.Contains("enemyrelease 139 maps/a.map", leaving);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task A_Joining_Player_arriving_on_the_Session_Hosts_map_drives_its_enemies_until_leaving()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/elsewhere.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/elsewhere.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
        Assert.StartsWith("avatarpose ", await game.ReadLineAsync());
        // The Session Host's chase on a map this game is not on leaves this game's enemies vanilla.
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 2 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 2);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 2000 });
        Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
        Assert.StartsWith("enemypose 139 2000 ", await game.ReadLineAsync());
        // Leaving the map ends the Map Visit, at which the game releases every copy itself.
        await game.SendAsync("EVENT mapentered maps/elsewhere.map");
        await game.SendAsync("STATE localpose 3 0 0 0 0 0 0 0 0 0.0000 maps/elsewhere.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 3);
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 3000 });
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task The_Session_Host_arriving_on_the_Joining_Players_map_takes_over_its_enemies()
    {
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 3; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        await game.SendAsync("STATE localpose 1 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 1);
        await _callbacks.ReceivePose(new(1, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/elsewhere.map"));
        Assert.StartsWith("avatarpose ", await game.ReadLineAsync());
        // An Enemy Pose ahead of the Session Host's own Pose on this map does not yet take over this game's enemy.
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample);
        Assert.True(await game.WritesNothingWithinAsync(TimeSpan.FromMilliseconds(100)));
        await _callbacks.ReceivePose(new(2, 0, 0, 0, 0, 0, 0, false, false, 0, "maps/a.map"));
        Assert.StartsWith("avatarpose ", await game.ReadLineAsync());
        await _callbacks.ReceiveEnemyPose(EnemyPosesProtocolTests.Sample with { TimeMs = 2000 });
        Assert.Equal("enemydrive 139 maps/a.map", await game.ReadLineAsync());
        Assert.StartsWith("enemypose 139 2000 ", await game.ReadLineAsync());
    }

    [Fact]
    public async Task The_Session_Host_sends_Enemy_Poses_only_while_the_Joining_Player_is_on_its_map()
    {
        _sessions.Joined = false;
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        await game.ReadLineAsync();
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks playerplace enemies");
        _sessions.SetPresent(true);
        for (var i = 0; i < 4; i++) await game.ReadLineAsync();
        await game.SendAsync("EVENT mapentered maps/a.map");
        // The Joining Player arrives, leaves, and arrives again while the Session Host's game keeps reporting its enemy.
        foreach (var (map, time) in new[] { ("maps/a.map", 1000UL), ("maps/elsewhere.map", 2000UL), ("maps/a.map", 3000UL) })
        {
            await _callbacks.ReceivePose(new(time, 0, 0, 0, 0, 0, 0, false, false, 0, map));
            await game.SendAsync($"STATE enemypose 139 {time} 0 -2 0 28.25 90 Walk 0.2 1 maps/a.map");
            await game.SendAsync($"STATE localpose {time} 0 0 0 0 0 0 0 0 0.0000 maps/a.map");
            await WaitUntilAsync(() => _sessions.SentPoses.Any(pose => pose.TimeMs == time));
        }
        Assert.Equal([1000UL, 3000UL], _sessions.SentEnemyPoses.Select(pose => pose.TimeMs));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private MemoryStream RecordTraffic(bool trace = false)
    {
        var output = new MemoryStream();
        _traffic = TrafficRecorder.Start(output, new TrafficOptions
        {
            Statistics = true,
            Trace = trace,
            StatisticsInterval = TimeSpan.FromMilliseconds(50)
        }, _ => { });
        return output;
    }

    private async Task<TrafficFile> StopTrafficAsync(MemoryStream output)
    {
        await _cancellation.CancelAsync();
        await _traffic.DisposeAsync();
        return TrafficFile.Parse(output);
    }

    private static TrafficCounts LinesOf(params string[] lines) =>
        new(lines.Length, lines.Sum(line => System.Text.Encoding.UTF8.GetByteCount(line) + 1));

    [Fact]
    public async Task Traffic_Statistics_count_local_game_lines_and_their_bytes_per_kind_and_direction_without_their_content()
    {
        var output = RecordTraffic(trace: true);
        await using var game = await ConnectWithSharedPoseAsync();
        await _callbacks.ReceiveChat(new ChatEntry("Bob", "the password is swordfish"));
        Assert.Equal("chatentry - - Bob:the password is swordfish", await game.ReadLineAsync());
        await Task.Delay(80, TestContext.Current.CancellationToken);
        await _callbacks.ReceivePose(ReceivedPose);
        Assert.StartsWith("avatarpose partner ", await game.ReadLineAsync());
        await game.SendAsync(LocalPoseState);
        await game.SendAsync(LocalPoseState);
        await WaitUntilAsync(() => _sessions.SentPoses.Count == 2);

        var file = await StopTrafficAsync(output);

        var connection = Assert.Single(file.Records("summary")).GetProperty("connection").GetInt32();
        Assert.Equal("game", Assert.Single(file.Records("summary")).GetProperty("channel").GetString());
        Assert.True(file.Statistics(connection).Count >= 2);
        Assert.Equal(LinesOf(Negotiate), file.StatisticsTotal(connection, "outbound", "protocol"));
        Assert.Equal(
            LinesOf(InteractionsUnsupportedNotice, SharedCallbacksUnsupportedNotice, "chatentry - - Bob:the password is swordfish"),
            file.StatisticsTotal(connection, "outbound", "chatentry"));
        Assert.Equal(LinesOf("avatarcreate partner"), file.StatisticsTotal(connection, "outbound", "avatarcreate"));
        Assert.Equal(1, file.StatisticsTotal(connection, "outbound", "avatarpose").Count);
        Assert.Equal(LinesOf("Welcome to the Amnesia TCP server!"), file.StatisticsTotal(connection, "inbound", "other"));
        Assert.Equal(LinesOf("RESPONSE protocol ok 2 avatars localpose lean"), file.StatisticsTotal(connection, "inbound", "RESPONSE protocol"));
        Assert.Equal(LinesOf(LocalPoseState, LocalPoseState), file.StatisticsTotal(connection, "inbound", "STATE localpose"));
        Assert.Contains(file.Records("trace"), record => record.GetProperty("kind").GetString() == "avatarpose");
        Assert.DoesNotContain("swordfish", file.Text);
        Assert.DoesNotContain("cellar one", file.Text);
        Assert.DoesNotContain("mp-test-cs", file.Text);
    }

    [Fact]
    public async Task Traffic_Statistics_record_how_long_writers_waited_at_the_write_gate()
    {
        var output = RecordTraffic();
        var (game, _, _) = await ConnectAsync();
        await using var _ = game;
        Assert.Equal(Negotiate, await game.ReadLineAsync());

        // The first holds the gate until the negotiation is answered; the second waits at the gate meanwhile.
        var first = _callbacks.ReceiveChat(new ChatEntry("Bob", "first")).AsTask();
        var second = _callbacks.ReceiveChat(new ChatEntry("Bob", "second")).AsTask();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await game.SendAsync("RESPONSE protocol ok 2 avatars localpose lean interactions sharedcallbacks");
        await Task.WhenAll(first, second);

        var summary = Assert.Single((await StopTrafficAsync(output)).Records("summary"));
        Assert.True(summary.GetProperty("writeGateWaitPeakMs").GetDouble() >= 100);
        Assert.True(summary.GetProperty("writeGateWaitTotalMs").GetDouble() >= 100);
    }

    [Fact]
    public async Task Traffic_Statistics_count_received_Poses_replaced_before_the_frozen_local_game_could_take_them()
    {
        var output = RecordTraffic();
        await using var game = await ConnectWithSharedPoseAsync();

        for (ulong time = 0; time < 1_000; time++) await _callbacks.ReceivePose(ReceivedPose with { TimeMs = time });

        var summary = Assert.Single((await StopTrafficAsync(output)).Records("summary"));
        Assert.True(summary.GetProperty("replacedPoses").GetInt64() > 0);
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
        private readonly List<LanMessage.EnemyEvent> _sentEnemyEvents = [];
        public List<LanMessage.EnemyEvent> SentEnemyEvents { get { lock (_sentEnemyEvents) return [.. _sentEnemyEvents]; } }

        public Task SendEnemyEventAsync(LanMessage.EnemyEvent message, CancellationToken cancellationToken)
        {
            lock (_sentEnemyEvents) _sentEnemyEvents.Add(message);
            return Task.CompletedTask;
        }
        private readonly List<LanMessage.DeathMessage> _sentDeathMessages = [];
        public List<LanMessage.DeathMessage> SentDeathMessages { get { lock (_sentDeathMessages) return [.. _sentDeathMessages]; } }

        public Task SendDeathMessageAsync(LanMessage.DeathMessage message, CancellationToken cancellationToken)
        {
            lock (_sentDeathMessages) _sentDeathMessages.Add(message);
            return Task.CompletedTask;
        }
        public List<(string, SharedStoryStartOutcome)> SentOutcomes { get; } = [];
        public SessionOperationResult HostResult { get; set; } = SessionOperationResult.Succeeded;
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => Task.FromResult(HostResult);
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
            string identifier, SharedStoryStartOutcome outcome, CancellationToken cancellationToken)
        {
            SentOutcomes.Add((identifier, outcome));
            return Task.CompletedTask;
        }
        public bool JoiningPlayerAdmitted { get; set; }
        private int _sentMainStoryStarts;
        public int SentMainStoryStarts => Volatile.Read(ref _sentMainStoryStarts);

        public Task<bool> SendMainStoryStartedAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sentMainStoryStarts);
            return Task.FromResult(JoiningPlayerAdmitted);
        }
        public List<SharedStoryStartOutcome> SentMainStoryOutcomes { get; } = [];

        public Task SendMainStoryStartOutcomeAsync(SharedStoryStartOutcome outcome, CancellationToken cancellationToken)
        {
            SentMainStoryOutcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }
}
