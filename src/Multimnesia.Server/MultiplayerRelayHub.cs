using Microsoft.AspNetCore.SignalR;
using Multimnesia.Contracts;

namespace Multimnesia.Server;

public sealed class MultiplayerRelayHub(SessionAdmission admission) : Hub
{
    public async Task<AdmissionResult> JoinSession()
    {
        var result = admission.TryAdmit(Context.ConnectionId);
        if (!result.Accepted) throw new HubException(result.Error);
        Console.WriteLine($"Game Peer {Context.ConnectionId} admitted as {result.Role}.");
        if (result.SessionReady) await Clients.All.SendAsync("SessionReady", Context.ConnectionAborted);
        return result;
    }

    public Task SendPlayerPosition(RelayPosition position)
    {
        EnsureAdmitted();
        return Clients.Others.SendAsync("ReceivePosition", position, Context.ConnectionAborted);
    }

    public Task SendScriptCall(string script)
    {
        EnsureAdmitted();
        return Clients.Others.SendAsync("ReceiveScriptCall", script, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (admission.EndSessionFor(Context.ConnectionId))
        {
            Console.WriteLine($"Game Peer {Context.ConnectionId} disconnected.");
            await Clients.Others.SendAsync("RemoteDisconnected");
        }
        await base.OnDisconnectedAsync(exception);
    }

    private void EnsureAdmitted()
    {
        if (!admission.IsAdmitted(Context.ConnectionId))
            throw new HubException("Join the Multiplayer Session before sending data.");
    }
}
