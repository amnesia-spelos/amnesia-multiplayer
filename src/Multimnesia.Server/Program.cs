using System.Text.Json.Serialization;
using Multimnesia.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SessionAdmission>();
builder.Services.AddSignalR().AddJsonProtocol(json =>
    json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var options = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
var errors = options.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors) Console.Error.WriteLine($"Configuration error: {error}");
    return 2;
}

builder.WebHost.UseUrls($"http://{FormatHost(options.ListenAddress)}:{options.Port}");
var app = builder.Build();
app.MapHub<MultiplayerRelayHub>("/chat");

Console.WriteLine("WARNING: This unauthenticated diagnostic Multiplayer Relay permits raw script relay.");
Console.WriteLine("Use it only on a trusted LAN. Never expose it to the internet.");
Console.WriteLine($"Multiplayer Relay listening on http://{FormatHost(options.ListenAddress)}:{options.Port}");

try
{
    await app.RunAsync();
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}

static string FormatHost(string address) => address.Contains(':') ? $"[{address}]" : address;
