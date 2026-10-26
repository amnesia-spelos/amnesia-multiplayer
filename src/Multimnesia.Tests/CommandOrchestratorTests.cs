using Multimnesia.Client;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class CommandOrchestratorTests
{
    public static TheoryData<string, PeerInput> GrammarCases => new()
    {
        { "/host", new PeerInput.Command(PeerCommand.Host) },
        { "/join game-box", new PeerInput.Command(new PeerCommand.Join("game-box")) },
        { "/join 192.168.1.10", new PeerInput.Command(new PeerCommand.Join("192.168.1.10")) },
        { "/leave", new PeerInput.Command(PeerCommand.Leave) },
        { "/tp-to-peer", new PeerInput.Command(PeerCommand.TeleportToPeer) },
        { "hello", new PeerInput.OrdinaryChat() },
        { "/host now", new PeerInput.Rejected("Usage: /host") },
        { "/join", new PeerInput.Rejected("Usage: /join <hostname-or-IPv4>") },
        { "/join host extra", new PeerInput.Rejected("Usage: /join <hostname-or-IPv4>") },
        { "/leave now", new PeerInput.Rejected("Usage: /leave") },
        { "/tp-to-peer Daniel", new PeerInput.Rejected("Usage: /tp-to-peer") },
        { "/tp-to-peer ", new PeerInput.Rejected("Usage: /tp-to-peer") },
        { "/tp-to-peers", new PeerInput.Rejected("Unknown command") },
        { "/dance", new PeerInput.Rejected("Unknown command") },
        { "/hostile", new PeerInput.Rejected("Unknown command") },
        { "/leaver", new PeerInput.Rejected("Unknown command") },
        { "/joining", new PeerInput.Rejected("Unknown command") },
    };

    [Theory]
    [MemberData(nameof(GrammarCases))]
    public void Command_grammar_is_exact(string message, PeerInput expected)
    {
        Assert.Equal(expected, PeerCommandParser.Parse(message));
    }

    public static TheoryData<GamePeerState, string, GamePeerState, string?> StateCases => new()
    {
        { GamePeerState.Local, "/host", GamePeerState.Hosting, null },
        { GamePeerState.Local, "/join host", GamePeerState.Joined, null },
        { GamePeerState.Local, "/leave", GamePeerState.Local, "You are not in a Multiplayer Session." },
        { GamePeerState.Hosting, "/host", GamePeerState.Hosting, "Leave the current Multiplayer Session first." },
        { GamePeerState.Hosting, "/join host", GamePeerState.Hosting, "Leave the current Multiplayer Session first." },
        { GamePeerState.Hosting, "/leave", GamePeerState.Local, "Hosting stopped." },
        { GamePeerState.Joining, "/host", GamePeerState.Joining, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joining, "/join host", GamePeerState.Joining, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joining, "/leave", GamePeerState.Local, "Joining cancelled." },
        { GamePeerState.Joined, "/host", GamePeerState.Joined, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joined, "/join host", GamePeerState.Joined, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joined, "/leave", GamePeerState.Local, "You left the Multiplayer Session." },
    };

    [Theory]
    [MemberData(nameof(StateCases))]
    public async Task Every_command_state_combination_is_deterministic(
        GamePeerState initial, string text, GamePeerState expectedState, string? expectedFeedback)
    {
        var operations = new RecordingSessionOperations();
        var orchestrator = new GamePeerOrchestrator(operations, initial);

        var feedback = await orchestrator.HandleAsync(new ChatEntry("Player", text), TestContext.Current.CancellationToken);

        Assert.Equal(expectedState, orchestrator.State);
        Assert.Equal(expectedFeedback, feedback?.Message);
    }

    [Fact]
    public async Task Ordinary_chat_has_no_local_echo()
    {
        var operations = new RecordingSessionOperations();
        var orchestrator = new GamePeerOrchestrator(operations);

        Assert.Null(await orchestrator.HandleAsync(new ChatEntry("Player", "already visible"), TestContext.Current.CancellationToken));
        Assert.Empty(operations.SentChat);
    }

    [Fact]
    public async Task Slash_prefixed_commands_are_not_relayed_as_ordinary_chat()
    {
        var operations = new RecordingSessionOperations();
        var orchestrator = new GamePeerOrchestrator(operations, GamePeerState.Joined);

        await orchestrator.HandleAsync(new ChatEntry("Player", "/dance"), TestContext.Current.CancellationToken);

        Assert.Empty(operations.SentChat);
    }

    [Theory]
    [InlineData(GamePeerState.Hosting)]
    [InlineData(GamePeerState.Joined)]
    public async Task Teleporting_to_the_other_player_in_a_Multiplayer_Session_reports_the_teleport_and_is_never_relayed(
        GamePeerState state)
    {
        var operations = new RecordingSessionOperations();
        var teleports = 0;
        var orchestrator = new GamePeerOrchestrator(operations, state, _ =>
        {
            teleports++;
            return Task.FromResult("Teleported to the other player.");
        });

        var feedback = await orchestrator.HandleAsync(new ChatEntry("Player", "/tp-to-peer"), TestContext.Current.CancellationToken);

        Assert.Equal(1, teleports);
        Assert.Equal(new ChatEntry("SYSTEM", "Teleported to the other player."), feedback);
        Assert.Equal(state, orchestrator.State);
        Assert.Empty(operations.SentChat);
    }

    [Theory]
    [InlineData(GamePeerState.Local)]
    [InlineData(GamePeerState.Joining)]
    public async Task Teleporting_to_the_other_player_outside_a_Multiplayer_Session_does_not_teleport(GamePeerState state)
    {
        var teleports = 0;
        var orchestrator = new GamePeerOrchestrator(new RecordingSessionOperations(), state, _ =>
        {
            teleports++;
            return Task.FromResult("Teleported to the other player.");
        });

        var feedback = await orchestrator.HandleAsync(new ChatEntry("Player", "/tp-to-peer"), TestContext.Current.CancellationToken);

        Assert.Equal(0, teleports);
        Assert.Equal("You are not in a Multiplayer Session.", feedback?.Message);
        Assert.Equal(state, orchestrator.State);
    }

    [Fact]
    public async Task Failed_network_operation_keeps_local_state_and_reports_failure()
    {
        var orchestrator = new GamePeerOrchestrator(new RecordingSessionOperations
        {
            HostResult = SessionOperationResult.Failed("Multiplayer connectivity is not available yet.")
        });

        var feedback = await orchestrator.HandleAsync(new ChatEntry("Player", "/host"), TestContext.Current.CancellationToken);

        Assert.Equal(GamePeerState.Local, orchestrator.State);
        Assert.Equal(new ChatEntry("SYSTEM", "Multiplayer connectivity is not available yet."), feedback);
    }

    [Fact]
    public async Task Successful_hosting_is_reported_in_green()
    {
        var orchestrator = new GamePeerOrchestrator(new RecordingSessionOperations
        {
            HostResult = SessionOperationResult.SucceededWith("Hosting Multiplayer Session at 100.101.102.103:31337.")
        });

        var feedback = await orchestrator.HandleAsync(new ChatEntry("Player", "/host"), TestContext.Current.CancellationToken);

        Assert.True(ChatColor.TryParse("00FF00", out var green));
        Assert.Equal(new ChatEntry("SYSTEM", "Hosting Multiplayer Session at 100.101.102.103:31337.", MessageColor: green), feedback);
    }

    [Fact]
    public async Task Join_transitions_through_joining_until_negotiated_admission_completes()
    {
        var completion = new TaskCompletionSource<SessionOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new RecordingSessionOperations { Join = _ => completion.Task };
        var orchestrator = new GamePeerOrchestrator(operations);

        var pending = orchestrator.HandleAsync(new ChatEntry("Player", "/join game-box"), TestContext.Current.CancellationToken);
        Assert.Equal(GamePeerState.Joining, orchestrator.State);
        completion.SetResult(SessionOperationResult.SucceededWith("Joined the Multiplayer Session."));
        var feedback = await pending;

        Assert.Equal(GamePeerState.Joined, orchestrator.State);
        Assert.Equal("Joined the Multiplayer Session.", feedback?.Message);
    }

    [Fact]
    public async Task Leave_immediately_cancels_an_in_progress_join()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new RecordingSessionOperations
        {
            Join = async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); }
                return SessionOperationResult.Failed("Joining cancelled.");
            }
        };
        var orchestrator = new GamePeerOrchestrator(operations);
        var joining = orchestrator.HandleAsync(new ChatEntry("Player", "/join game-box"), TestContext.Current.CancellationToken);

        var feedback = await orchestrator.HandleAsync(new ChatEntry("Player", "/leave"), TestContext.Current.CancellationToken);
        await cancelled.Task;
        await joining;

        Assert.Equal(GamePeerState.Local, orchestrator.State);
        Assert.Equal(new ChatEntry("SYSTEM", "Joining cancelled."), feedback);
    }

    [Fact]
    public async Task Successful_join_can_be_left_without_reusing_a_disposed_cancellation_source()
    {
        var orchestrator = new GamePeerOrchestrator(new RecordingSessionOperations());
        await orchestrator.HandleAsync(new ChatEntry("Player", "/join game-box"), TestContext.Current.CancellationToken);

        var feedback = await orchestrator.HandleAsync(new ChatEntry("Player", "/leave"), TestContext.Current.CancellationToken);

        Assert.Equal(GamePeerState.Local, orchestrator.State);
        Assert.Equal("You left the Multiplayer Session.", feedback?.Message);
    }

    private sealed class RecordingSessionOperations : ISessionOperations
    {
        public event Action? MultiplayerSessionEnded { add { } remove { } }
        public event Action? OtherPlayerPresenceChanged { add { } remove { } }
        public int OtherPlayerArrival => 0;
        public void SendEnemyPose(LanMessage.EnemyPose pose) { }
        public Task SendEnemyEventAsync(LanMessage.EnemyEvent message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendDeathMessageAsync(LanMessage.DeathMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SendPose(LanMessage.Pose pose) { }
        public void SendBodies(LanMessage.Bodies bodies) { }
        public Task SendHoldMessageAsync(LanMessage.HoldMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendWorldMessageAsync(LanMessage.WorldMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsJoined => false;
        public List<ChatEntry> SentChat { get; } = [];
        public SessionOperationResult HostResult { get; init; } = SessionOperationResult.Succeeded;
        public SessionOperationResult JoinResult { get; init; } = SessionOperationResult.Succeeded;
        public Func<CancellationToken, Task<SessionOperationResult>>? Join { get; init; }
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => Task.FromResult(HostResult);
        public Task<SessionOperationResult> JoinAsync(string destination, CancellationToken cancellationToken) =>
            Join?.Invoke(cancellationToken) ?? Task.FromResult(JoinResult);
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendChatAsync(ChatEntry entry, CancellationToken cancellationToken)
        {
            SentChat.Add(entry);
            return Task.CompletedTask;
        }
        public Task<bool> SendCustomStoryStartedAsync(string identifier, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task SendCustomStoryStartOutcomeAsync(
            string identifier, SharedCustomStoryStartOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
