using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multimnesia.Client;
using Multimnesia.Contracts;

var configuration = new ConfigurationManager();
configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
configuration.AddCommandLine(args);
var options = configuration.GetSection(GamePeerOptions.SectionName).Get<GamePeerOptions>() ?? new GamePeerOptions();
var errors = options.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors) Console.Error.WriteLine($"Configuration error: {error}");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Console.WriteLine("Stopping Game Peer...");
    cancellation.Cancel();
};

using var game = new TcpClient();
try
{
    await game.ConnectAsync(options.GameHost, options.GamePort, cancellation.Token);
}
catch (Exception exception) when (exception is SocketException or OperationCanceledException)
{
    Console.Error.WriteLine($"Local-game connection failed at {options.GameHost}:{options.GamePort}: {exception.Message}");
    return exception is OperationCanceledException ? 0 : 1;
}

await using var gameStream = game.GetStream();
await using var writer = new StreamWriter(gameStream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
using var reader = new StreamReader(gameStream, Encoding.ASCII, leaveOpen: true);
var welcome = await reader.ReadLineAsync(cancellation.Token);
if (welcome is null)
{
    Console.Error.WriteLine("Local-game connection failed: the game closed the Game Interaction Protocol session during startup.");
    return 1;
}
Console.WriteLine($"Connected to local game at {options.GameHost}:{options.GamePort}. {welcome}");

var remotePositions = new ConcurrentStack<PlayerPosition>();
var remoteScripts = new ConcurrentQueue<string>();
var sessionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

await using var relay = new HubConnectionBuilder()
    .WithUrl(options.RelayUrl)
    .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .Build();
relay.On("SessionReady", () =>
{
    Console.WriteLine("Multiplayer Session ready.");
    sessionReady.TrySetResult();
});
relay.On<RelayPosition>("ReceivePosition", value => remotePositions.Push(ToPlayerPosition(value)));
relay.On<string>("ReceiveScriptCall", script => remoteScripts.Enqueue(script));
relay.On("RemoteDisconnected", () =>
{
    Console.WriteLine("The remote Game Peer disconnected. This diagnostic Multiplayer Session has ended.");
    cancellation.Cancel();
});
relay.Closed += exception =>
{
    if (!cancellation.IsCancellationRequested)
        Console.Error.WriteLine($"Multiplayer Relay disconnected: {exception?.Message ?? "connection closed"}");
    cancellation.Cancel();
    return Task.CompletedTask;
};

try
{
    await relay.StartAsync(cancellation.Token);
    Console.WriteLine($"Connected to Multiplayer Relay at {options.RelayUrl}.");
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"Multiplayer Relay connection failed at {options.RelayUrl}: {exception.Message}");
    return 1;
}

try
{
    var admission = await relay.InvokeAsync<AdmissionResult>("JoinSession", cancellation.Token);
    Console.WriteLine($"Assigned role: {FormatRole(admission.Role)}.");
    if (admission.SessionReady) sessionReady.TrySetResult();
    else Console.WriteLine("Waiting for the other Game Peer...");

    var readTask = ReadGameEventsAsync(reader, relay, cancellation.Token);
    if (!sessionReady.Task.IsCompleted)
    {
        var startupTask = await Task.WhenAny(sessionReady.Task, readTask);
        if (startupTask == readTask) await readTask;
    }
    await sessionReady.Task.WaitAsync(cancellation.Token);

    var updateTask = RunMovementLoopAsync(writer, relay, remotePositions, remoteScripts, options, cancellation.Token);
    await Task.WhenAny(readTask, updateTask);
    cancellation.Cancel();
    await Task.WhenAll(SuppressCancellation(readTask), SuppressCancellation(updateTask));
}
catch (OperationCanceledException)
{
    // Expected for Ctrl+C and orderly connection shutdown.
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Game Peer stopped because of an error: {exception.Message}");
    return 1;
}
finally
{
    if (relay.State != HubConnectionState.Disconnected)
        await relay.StopAsync(CancellationToken.None);
}

return 0;

static async Task ReadGameEventsAsync(StreamReader reader, HubConnection relay, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null) throw new IOException("The local game closed the Game Interaction Protocol session.");

        switch (LegacyGameProtocol.ParseEvent(line))
        {
            case GameEvent.PositionReported position:
                await relay.InvokeAsync("SendPlayerPosition", ToRelayPosition(position.Position), cancellationToken);
                break;
            case GameEvent.ScriptCalled script:
                await relay.InvokeAsync("SendScriptCall", script.Script, cancellationToken);
                break;
            case GameEvent.Unknown unknown:
                Console.WriteLine($"Unrecognized local-game message: {unknown.Line}");
                break;
        }
    }
}

static async Task RunMovementLoopAsync(
    StreamWriter writer,
    HubConnection relay,
    ConcurrentStack<PlayerPosition> remotePositions,
    ConcurrentQueue<string> remoteScripts,
    GamePeerOptions options,
    CancellationToken cancellationToken)
{
    var delay = TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds);
    while (!cancellationToken.IsCancellationRequested)
    {
        await writer.WriteLineAsync(LegacyGameProtocol.PositionRequest.AsMemory(), cancellationToken);
        await Task.Delay(delay, cancellationToken);

        if (remotePositions.TryPop(out var position))
        {
            remotePositions.Clear();
            var command = LegacyGameProtocol.CreateRemotePlayerUpdate(position, options.RemotePlayerEntity);
            await writer.WriteLineAsync(command.AsMemory(), cancellationToken);
        }

        if (remoteScripts.TryDequeue(out var script))
        {
            var scripts = new List<string> { script };
            while (remoteScripts.TryDequeue(out script)) scripts.Add(script);
            await writer.WriteLineAsync(LegacyGameProtocol.ExecuteScripts(scripts).AsMemory(), cancellationToken);
        }
    }
}

static RelayPosition ToRelayPosition(PlayerPosition value) =>
    new(value.X, value.Y, value.Z, value.RotationX, value.RotationY, value.RotationZ);

static PlayerPosition ToPlayerPosition(RelayPosition value) =>
    new(value.X, value.Y, value.Z, value.RotationX, value.RotationY, value.RotationZ);

static string FormatRole(GamePeerRole? role) => role switch
{
    GamePeerRole.SessionHost => "Session Host",
    GamePeerRole.JoiningPlayer => "Joining Player",
    _ => "unknown"
};

static async Task SuppressCancellation(Task task)
{
    try { await task; }
    catch (OperationCanceledException) { }
}
