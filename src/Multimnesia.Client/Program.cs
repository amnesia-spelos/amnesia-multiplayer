using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;

Console.Write("(10:24) Server (enter for 192.168.100.2:5000):");
var server = ReadLineOrDefault(defaultValue: "192.168.100.2:5000");
Console.WriteLine();

var connection = new HubConnectionBuilder()
    .WithUrl($"http://{server}/chat")
    .WithAutomaticReconnect()
    .Build();

var posBuffer = new ConcurrentStack<string>();
var scriptBuffer = new ConcurrentQueue<string>();
var incomingQueue = new ConcurrentQueue<string>();
var entityUpdateBuffer = new ConcurrentStack<string>();

const string CreatePlayerTwoIfNotExists = "if(!GetEntityExists(\"PlayerTwo\")){CreateEntityAtFirstArea(\"PlayerTwo\", \"MultiplayerSkeleton.ent\", true);}";

connection.On<string>("ReceivePosition", (posMsg) =>
{
    var posRotSplit = posMsg.Split(':');
    var coords = posRotSplit[0].Split(',', StringSplitOptions.TrimEntries);
    var rot = posRotSplit[1].Split(',', StringSplitOptions.TrimEntries);
    var cmd = $"{CreatePlayerTwoIfNotExists};SetEntityPosRot(\"PlayerTwo\", {coords[0]}, {float.Parse(coords[1]) + 1.0f}, {coords[2]}, {rot[0]}, {rot[1]}, {rot[2]});";
    posBuffer.Push(cmd);
});

connection.On<string>("ScriptExec", script =>
{
    Console.WriteLine($"[remote script] {script}");
    scriptBuffer.Enqueue(script);
});

connection.On<string>("EntityGrab", script =>
{
    Console.WriteLine($"[remote entity] {script}");
    entityUpdateBuffer.Push(script);
});

try
{
    await connection.StartAsync();
    Console.WriteLine("Connected to SignalR hub.");
}
catch (Exception ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
    return;
}

Console.Write("Wait time in ms (default 5): ");
var delay = ReadLineIntOrDefault(defaultValue: 5);
Console.WriteLine();

Console.Write("Amnesia game port: (default 5150): ");
var port = ReadLineIntOrDefault(defaultValue: 5150);
Console.WriteLine();

var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", port);

var stream = client.GetStream();
var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
var reader = new StreamReader(stream, Encoding.ASCII);

var welcome = await reader.ReadLineAsync();
Console.WriteLine($"[Amnesia] {welcome}");

var lobbyJoined = false;

connection.On<bool>("OnLobbyJoined", isAuthority =>
{
    if (!isAuthority)
        writer.WriteLine("setlistener");

    lobbyJoined = true;
});

var lastPosRot = "";
var lastReportedDoor = "";
DateTime reportedAt = DateTime.Now;

await connection.InvokeAsync("JoinLobby");

_ = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            if (!lobbyJoined)
                continue;

            var line = await reader.ReadLineAsync();
            if (line == null) break;

            //Console.WriteLine($"[Amnesia][in] {line}");

            if (line.StartsWith("grab_start:")) //grab_start:book03_1:1.36:-0.07,0.02,-0.10:-2.14,0.19,2.99
            {
                var split = line.Split(':');
                await connection.InvokeAsync("ScriptExec", $"SetPropStaticPhysics(\"{split[1]}\", true)");
            }
            else if(line.StartsWith("grab_update:")) // grab_update:book03_1:-5.741,0.946,3.932:-59.647,88.051,2.779
            {
                var split = line.Split(':');
                var entity = split[1];
                var posSplit = split[2].Split(',');
                var rotSplit = split[3].Split(',');

                //exec:SetEntityPosRot("chair_nice01_1", -0.295, 0.699, -0.702, -179.995, -0.000, -180.000)
                var command = $"SetEntityPosRot(\"{entity}\", {posSplit[0]}, {posSplit[1]}, {posSplit[2]}, {rotSplit[0]}, {rotSplit[1]}, {rotSplit[2]});";

                await connection.InvokeAsync("EntityGrab", command);
            }
            else if(line.StartsWith("grab_end:")) // grab_end:book03_1
            {
                var split = line.Split(':');
                await connection.InvokeAsync("ScriptExec", $"SetPropStaticPhysics(\"{split[1]}\", false);AddPropForce(\"{split[1]}\", 1.0, 0.0, 0.0, \"world\")");
            }
            else if(line.StartsWith("grab_throw:")) // grab_throw:book03_1:6.53,-2.93,-6.98
            {
                // not implemented yet
                var split = line.Split(':');
                var posSplit = split[2].Split(',');

                await connection.InvokeAsync("ScriptExec", $"AddPropImpulse(\"{split[1]}\", {posSplit[0]}, {posSplit[1]}, {posSplit[2]}, \"world\");");
            }
            else if (line.StartsWith("RESPONSE:getposrot:"))
            {
                var response = line.Replace("RESPONSE:getposrot:", "");
                if (response != lastPosRot)
                {
                    lastPosRot = response;
                    await connection.InvokeAsync("SendPlayerPos", response);
                }
            }
            else if (line.StartsWith("SCRIPT_CALL:"))
            {
                await connection.InvokeAsync("ScriptExec", line.Replace("SCRIPT_CALL:", string.Empty));
            }
            else if (line.StartsWith("SYNC_DOOR:"))
            {
                line = line.Replace("SYNC_DOOR:", string.Empty);
                var split = line.Split(':', StringSplitOptions.TrimEntries);

                if (split[0] != lastReportedDoor || (DateTime.Now - reportedAt).TotalSeconds >= 1)
                {
                    lastReportedDoor = split[0];
                    reportedAt = DateTime.Now;
                    var syncDoorCmd = $"SetSwingDoorOpenAmount(\"{split[0]}\", {split[1]});";
                    //await connection.InvokeAsync("ScriptExec", syncDoorCmd); // FIXME: Temp disabled does not work well
                }
            }
            else if (line.StartsWith("EVENT:MapChanged:"))
            {
                Console.WriteLine("Map Changed received - not implemented");
            }
            else if (line.StartsWith("RESPONSE:exec:script executed"))
            {
                // Do nothing
            }
            else
            {
                Console.WriteLine($"Unknown Amnesia line: {line}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Reader thread error] {ex.Message}");
    }
});

while (true)
{
    await writer.WriteLineAsync("getposrot");

    await Task.Delay(delay);

    var posCommand = new StringBuilder("exec:");
    if (!posBuffer.IsEmpty)
    {
        _ = posBuffer.TryPop(out var cmd);
        if (cmd is null)
        {
            Console.Error.WriteLine("Failed to dequeue a pos command");
            return;
        }

        posCommand.Append(cmd);
        posBuffer.Clear();
    }

    if (!entityUpdateBuffer.IsEmpty)
    {
        _ = entityUpdateBuffer.TryPop(out var cmd);
        if (cmd is null)
        {
            Console.Error.WriteLine("Failed to dequeue an entity update");
            return;
        }

        posCommand.Append(cmd);
        entityUpdateBuffer.Clear();
    }

    if (posCommand.Length > 5) // length of "exec:"
    {
        writer.WriteLine(posCommand.ToString());
    }

    await Task.Delay(delay);

    if (!scriptBuffer.IsEmpty)
    {
        var sb = new StringBuilder("exec:");

        while (scriptBuffer.TryDequeue(out var script))
        {
            sb.Append(script);
            if (!script.EndsWith(";"))
                sb.Append(';');
        }

        var scriptCommand = sb.ToString();

        Console.WriteLine($"[.NET] {scriptCommand}");
        await writer.WriteLineAsync(scriptCommand);
        // Let background thread handle the response
    }
}

static string ReadLineOrDefault(string defaultValue)
{
    var line = Console.ReadLine();

    return string.IsNullOrWhiteSpace(line) ? defaultValue : line;
}

static int ReadLineIntOrDefault(int defaultValue)
{
    var line = Console.ReadLine();

    if (!int.TryParse(line, out var value))
    {
        Console.WriteLine($"Provided value is not a valid number. Using '{defaultValue}'");
        return defaultValue;
    }

    return value;
}