using Amnesia.Multiplayer;
using LiteNetLib;

var isServer = args.Any(arg => arg.Equals("--server", StringComparison.OrdinalIgnoreCase));

Console.WriteLine("Amnesia: Multiplayer");
Console.WriteLine("====================");
Console.WriteLine(isServer ? "[SERVER]" : "[CLIENT]");

const int Port = 9050;
const string Server = "localhost";
var key = Console.ReadKey();

if (isServer)
{
    var serverListener = new ServerListener();
    var server = new NetManager(serverListener);
    if (!server.Start(Port))
    {
        Console.WriteLine("Server start failed");
        return;
    }
    serverListener.Server = server;
    PollUntilExitPressed(server);
}
else
{
    var clientListener = new ClientListener();
    var client = new NetManager(clientListener);
    client.Start();
    client.Connect(Server, Port, "gamekey");
    PollUntilExitPressed(client);
}

static void PollUntilExitPressed(NetManager manager)
{
    Console.WriteLine("Press any key to stop and exit...");
    do
    {
        manager.PollEvents();
        Thread.Sleep(15);
    }
    while (!Console.KeyAvailable);

    Environment.Exit(0);
}
