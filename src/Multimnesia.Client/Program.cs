using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;

Console.Write("(14:49) Server (enter for 192.168.100.2:5000):");
var server = Console.ReadLine();
Console.WriteLine();

if (string.IsNullOrEmpty(server))
    server = "192.168.100.2:5000";

var connection = new HubConnectionBuilder()
    .WithUrl($"http://{server}/chat")
    .WithAutomaticReconnect()
    .Build();

var posBuffer = new ConcurrentStack<string>();
var scriptBuffer = new ConcurrentQueue<string>();
var incomingQueue = new ConcurrentQueue<string>();

connection.On<string>("ReceivePosition", (posMsg) =>
{
    var posRotSplit = posMsg.Split(':');
    var coords = posRotSplit[0].Split(',', StringSplitOptions.TrimEntries);
    var rot = posRotSplit[1].Split(',', StringSplitOptions.TrimEntries);
    var cmd = $"exec:SetEntityPosRot(\"PlayerTwo\", {coords[0]}, {float.Parse(coords[1]) + 1.0f}, {coords[2]}, {rot[0]}, {rot[1]}, {rot[2]});";
    posBuffer.Push(cmd);
});

connection.On<string>("ScriptExec", script =>
{
    Console.WriteLine($"[remote script] {script}");
    scriptBuffer.Enqueue(script);
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

Console.Write("Wait time in ms (default 50): ");
var delay = int.Parse(Console.ReadLine() ?? "50");
Console.WriteLine();

var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 5150);

var stream = client.GetStream();
var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
var reader = new StreamReader(stream, Encoding.ASCII);

var welcome = await reader.ReadLineAsync();
Console.WriteLine($"[Amnesia] {welcome}");

var lastPosRot = "";
var lastReportedDoor = "";
DateTime reportedAt = DateTime.Now;

// Start background reader loop
_ = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;

            //Console.WriteLine($"[Amnesia][in] {line}");

            // only forward responses
            if (line.StartsWith("RESPONSE:getposrot:"))
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

    if (!posBuffer.IsEmpty)
    {
        _ = posBuffer.TryPop(out var cmd);
        if (cmd is null)
        {
            Console.Error.WriteLine("Failed to dequeue a pos command");
            return;
        }

        await writer.WriteLineAsync(cmd);
        // Let background thread handle the response
        posBuffer.Clear();
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


/* OLD IMPL */

//Console.Write("(10:44) Server (enter for 192.168.100.2:5000):");
//var server = Console.ReadLine();
//Console.WriteLine();

//if (string.IsNullOrEmpty(server))
//    server = "192.168.100.2:5000";

//var connection = new HubConnectionBuilder()
//    .WithUrl($"http://{server}/chat") // Must match server URL
//    .WithAutomaticReconnect()
//    .Build();

///// <summary>
///// Stores pos info, takes latest, clears after. LOSSY
///// </summary>
//var posBuffer = new ConcurrentStack<string>();

///// <summary>
///// Stores important scripts, all executed per frame. NON-LOSSY, MERGED
///// </summary>
//var scriptBuffer = new ConcurrentQueue<string>();

//var stopwatch = new Stopwatch();
//stopwatch.Start();
//connection.On<string>("ReceivePosition", (posMsg) =>
//{
//    stopwatch.Stop();
//    Console.WriteLine($"ReceivePosition after ({stopwatch.ElapsedMilliseconds}ms)");
//    stopwatch.Restart();
//    var posRotSplit = posMsg.Split(':');
//    var coords = posRotSplit[0].Split(',', StringSplitOptions.TrimEntries);
//    var rot = posRotSplit[1].Split(',', StringSplitOptions.TrimEntries);
//    var cmd = $"exec:SetEntityPosRot(\"PlayerTwo\", {coords[0]}, {float.Parse(coords[1]) + 1.0f}, {coords[2]}, {rot[0]}, {rot[1]}, {rot[2]});";
//    posBuffer.Push(cmd);
//});

//connection.On<string>("ScriptExec", scriptBuffer.Enqueue);

//try
//{
//    await connection.StartAsync();
//    Console.WriteLine("Connected to SignalR hub.");
//}
//catch (Exception ex)
//{
//    Console.WriteLine($"Connection failed: {ex.Message}");
//    return;
//}

//Console.Write("Wait time in ms: ");
//var delayString = Console.ReadLine();
//Console.WriteLine();

//var delay = int.Parse(delayString);

//var client = new TcpClient();

//await client.ConnectAsync("127.0.0.1", 5150);

//var stream = client.GetStream();

//var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
//var reader = new StreamReader(stream, Encoding.ASCII);

//var welcome = await reader.ReadLineAsync();
//Console.WriteLine($"[Amnesia] {welcome}");
//var lastPosRot = "";

//while (true)
//{
//    await writer.WriteLineAsync("getposrot"); // RESPONSE:getposrot:1.75, -0.01, 2.45:-1000.0, 0.0, 0.0
//    var response = await reader.ReadLineAsync();

//    response = response.Replace("RESPONSE:getposrot:", string.Empty);

//    if (response != lastPosRot)
//    {
//        lastPosRot = response;
//        await connection.InvokeAsync("SendPlayerPos", response);
//    }

//    await Task.Delay(delay);

//    if (!posBuffer.IsEmpty)
//    {
//        Console.WriteLine($"Buffered instructions: {posBuffer.Count}");
//        _ = posBuffer.TryPop(out var cmd);
//        if (cmd is null)
//        {
//            Console.Error.WriteLine("Failed to dequeue a pos command");
//            return;
//        }

//        Console.WriteLine($"[.NET] {cmd}");
//        await writer.WriteLineAsync(cmd);
//        var posResponse = await reader.ReadLineAsync();

//        Console.WriteLine($"[Amnesia] {posResponse}");

//        posBuffer.Clear();
//    }

//    if (!scriptBuffer.IsEmpty)
//    {
//        var sb = new StringBuilder("exec:");

//        while (scriptBuffer.TryDequeue(out var script))
//        {
//            sb.Append(script);
//            if (!script.EndsWith(";"))
//                sb.Append(';');
//        }

//        var scriptCommand = sb.ToString();

//        Console.WriteLine($"[.NET] {scriptCommand}");
//        await writer.WriteLineAsync(scriptCommand);
//        var scriptResponse = await reader.ReadLineAsync();
//        Console.WriteLine($"[Amnesia] {scriptResponse}");
//    }
//}

/* COMMAND & LISTEN */
//while (true)
//{
//    Console.Write("Game Command: ");
//    var command = Console.ReadLine();
//    Console.WriteLine();

//    if (command == "exit")
//        break;

//    if (command == "listen")
//    {
//        while (true)
//        {
//            var r = await reader.ReadLineAsync();
//            Console.WriteLine($"[Amnesia] {r}");
//        }
//    }

//    await writer.WriteLineAsync(command);
//    var response = await reader.ReadLineAsync();
//    Console.WriteLine($"[Amnesia] {response}");
//}
