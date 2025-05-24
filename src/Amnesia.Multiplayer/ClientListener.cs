using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Amnesia.Multiplayer;

public class ClientListener : INetEventListener
{
    public void OnConnectionRequest(ConnectionRequest request)
    {
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Console.WriteLine("[Client] error! " + socketError.ToString());
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var message = reader.GetString();
        Console.WriteLine("Received: " + message);
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnPeerConnected(NetPeer peer)
    {
        Console.WriteLine("[Client] connected to: {0}:{1}", peer.Address, peer.Port);

        var dataWriter = new NetDataWriter();
        dataWriter.Put("Hello, peer!");
        peer.Send(dataWriter, DeliveryMethod.ReliableUnordered);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Console.WriteLine("[Client] disconnected: " + disconnectInfo.Reason);
    }
}
