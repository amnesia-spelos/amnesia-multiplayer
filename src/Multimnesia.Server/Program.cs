using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();

var app = builder.Build();
app.MapHub<ChatHub>("/chat");
app.Run();

public class ChatHub : Hub
{
    private string _lobbyAuthority = string.Empty;

    public async Task JoinLobby()
    {
        Console.WriteLine($"User: {Context.ConnectionId}");

        if (string.IsNullOrWhiteSpace(_lobbyAuthority))
        {
            _lobbyAuthority = Context.ConnectionId;
        }

        await Clients.Caller.SendAsync("OnLobbyJoined", _lobbyAuthority == Context.ConnectionId);
    }

    public async Task SendPlayerPos(string posMsg)
    {
        await Clients.Others.SendAsync("ReceivePosition", posMsg);
    }

    public async Task ScriptExec(string script)
    {
        Console.WriteLine($"script: {script}");
        await Clients.Others.SendAsync("ScriptExec", script);
    }

    public async Task EntityGrab(string script)
    {
        Console.WriteLine($"entity grab: {script}");
        await Clients.Others.SendAsync("EntityGrab", script);
    }
}
