using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();

var app = builder.Build();
app.MapHub<ChatHub>("/chat");
app.Run();

public class ChatHub : Hub
{
    public async Task SendMessage(string user, string message)
    {
        Console.WriteLine($"{user}: {message}");
        await Clients.All.SendAsync("ReceiveMessage", user, message);
    }

    public async Task SendPlayerPos(string posMsg)
    {
        Console.WriteLine($"pos: {posMsg}");
        await Clients.Others.SendAsync("ReceivePosition", posMsg);
    }

    public async Task ScriptExec(string script)
    {
        Console.WriteLine($"script: {script}");
        await Clients.Others.SendAsync("ScriptExec", script);
    }
}
