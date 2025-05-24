using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace Amnesia.Multiplayer;

public class ServerListener : INetEventListener
{
    public NetManager? Server { get; set; }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        var acceptedPeer = request.AcceptIfKey("gamekey");
        Console.WriteLine("[Server] ConnectionRequest. Ep: {0}, Accepted: {1}",
            request.RemoteEndPoint,
            acceptedPeer != null);
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Console.WriteLine("[Server] code: " + socketError + " error: " + socketError.ToString());
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (Server is null)
        {
            Console.Error.WriteLine("[Server] Server was null while OnPeerConnected");
            return;
        }

        //foreach (var netPeer in Server)
        //{
        //    if (netPeer.Address == peer.Address)
        //        continue;
        //}
        peer.Send(reader.GetRemainingBytes(), deliveryMethod); // Echo back
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        Console.WriteLine("[Server] ReceiveUnconnected: {0}", reader.GetString(100));
    }

    public void OnPeerConnected(NetPeer peer)
    {
        if (Server is null)
        {
            Console.Error.WriteLine("[Server] Server was null while OnPeerConnected");
            return;
        }

        Console.WriteLine("[Server] Peer connected: " + peer);
        foreach (var netPeer in Server)
        {
            if (netPeer.ConnectionState == ConnectionState.Connected)
                Console.WriteLine("ConnectedPeersList: id={0}, ep={1}", netPeer.Id, netPeer);
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Console.WriteLine("[Server] Peer disconnected: " + peer + ", reason: " + disconnectInfo.Reason);
    }
}
