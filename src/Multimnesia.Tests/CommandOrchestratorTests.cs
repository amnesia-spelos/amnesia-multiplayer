using Multimnesia.Client;

namespace Multimnesia.Tests;

public sealed class CommandOrchestratorTests
{
    public static TheoryData<string, PeerInput> GrammarCases => new()
    {
        { "/host", new PeerInput.Command(PeerCommand.Host) },
        { "/join game-box", new PeerInput.Command(new PeerCommand.Join("game-box")) },
        { "/join 192.168.1.10", new PeerInput.Command(new PeerCommand.Join("192.168.1.10")) },
        { "/leave", new PeerInput.Command(PeerCommand.Leave) },
        { "hello", new PeerInput.OrdinaryChat() },
        { "/host now", new PeerInput.Rejected("Usage: /host") },
        { "/join", new PeerInput.Rejected("Usage: /join <hostname-or-IPv4>") },
        { "/join host extra", new PeerInput.Rejected("Usage: /join <hostname-or-IPv4>") },
        { "/leave now", new PeerInput.Rejected("Usage: /leave") },
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
        { GamePeerState.Local, "/join host", GamePeerState.Joining, null },
        { GamePeerState.Local, "/leave", GamePeerState.Local, "You are not in a Multiplayer Session." },
        { GamePeerState.Hosting, "/host", GamePeerState.Hosting, "Leave the current Multiplayer Session first." },
        { GamePeerState.Hosting, "/join host", GamePeerState.Hosting, "Leave the current Multiplayer Session first." },
        { GamePeerState.Hosting, "/leave", GamePeerState.Local, null },
        { GamePeerState.Joining, "/host", GamePeerState.Joining, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joining, "/join host", GamePeerState.Joining, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joining, "/leave", GamePeerState.Local, null },
        { GamePeerState.Joined, "/host", GamePeerState.Joined, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joined, "/join host", GamePeerState.Joined, "Leave the current Multiplayer Session first." },
        { GamePeerState.Joined, "/leave", GamePeerState.Local, null },
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
        var orchestrator = new GamePeerOrchestrator(new RecordingSessionOperations());

        Assert.Null(await orchestrator.HandleAsync(new ChatEntry("Player", "already visible"), TestContext.Current.CancellationToken));
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

    private sealed class RecordingSessionOperations : ISessionOperations
    {
        public SessionOperationResult HostResult { get; init; } = SessionOperationResult.Succeeded;
        public SessionOperationResult JoinResult { get; init; } = SessionOperationResult.Succeeded;
        public Task<SessionOperationResult> HostAsync(CancellationToken cancellationToken) => Task.FromResult(HostResult);
        public Task<SessionOperationResult> JoinAsync(string host, CancellationToken cancellationToken) => Task.FromResult(JoinResult);
        public Task LeaveAsync(GamePeerState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
