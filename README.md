# Amnesia Multiplayer diagnostic prototype

This repository contains a disposable two-player LAN diagnostic baseline for Amnesia: The Dark Descent. `Multimnesia.Server` is the Multiplayer Relay and `Multimnesia.Client` is the Game Peer. It exists to prove that two games can see one another move; it is not the intended long-term multiplayer architecture.

> [!WARNING]
> The Multiplayer Relay is unauthenticated and can relay raw game scripts. Run it only between trusted computers on a trusted LAN. Never expose its port to the internet or configure router port forwarding for it.

## Prerequisites

- Windows x64 on both PCs.
- Amnesia: The Dark Descent installed on both PCs, including the base-game entity `entities/ornament/arabic_statue/arabic_statue.ent`.
- The same game version, map, and starting position on both PCs.
- The modified `Amnesia.exe` produced by [`amnesia-tdd-tcp`](https://github.com/amnesia-spelos/amnesia-tdd-tcp), with its legacy Game Interaction Protocol listening locally on port 5150.
- For development, the .NET 10 SDK. Published self-contained builds do not require a separately installed .NET runtime.

The Game Interaction Protocol must remain bound to loopback. Only Multiplayer Relay traffic crosses the LAN.

## Build, test, and publish

From the repository root:

```powershell
dotnet restore .\src\Multimnesia.sln
dotnet build .\src\Multimnesia.sln --configuration Release --no-restore
dotnet test .\src\Multimnesia.sln --configuration Release --no-build
dotnet publish .\src\Multimnesia.Server\Multimnesia.Server.csproj --configuration Release --runtime win-x64 --self-contained true --output .\artifacts\relay
dotnet publish .\src\Multimnesia.Client\Multimnesia.Client.csproj --configuration Release --runtime win-x64 --self-contained true --output .\artifacts\game-peer
```

These are ordinary directory publishes. Trimming and single-file publishing are intentionally not enabled. Copy `artifacts\relay` to the Session Host PC and `artifacts\game-peer` to both PCs.

## Configuration

Safe defaults work for both processes on one PC. Configuration can be supplied in an optional `appsettings.json` beside an executable. Command-line values override the JSON file.

Multiplayer Relay defaults:

```json
{
  "Relay": {
    "ListenAddress": "127.0.0.1",
    "Port": 5000
  }
}
```

For LAN use, the Session Host must explicitly listen on its LAN address. For example:

```powershell
.\Multimnesia.Server.exe --Relay:ListenAddress 192.168.1.25 --Relay:Port 5000
```

The effective listen address and port are printed at startup. Prefer the Session Host's specific LAN address over `0.0.0.0`.

Game Peer defaults:

```json
{
  "GamePeer": {
    "RelayUrl": "http://127.0.0.1:5000/chat",
    "GameHost": "127.0.0.1",
    "GamePort": 5150,
    "PollIntervalMilliseconds": 15,
    "RemotePlayerEntity": "entities/ornament/arabic_statue/arabic_statue.ent"
  }
}
```

The Session Host can use those defaults except for the Relay URL when a non-loopback relay address is configured. The Joining Player supplies the Session Host's LAN address:

```powershell
.\Multimnesia.Client.exe --GamePeer:RelayUrl http://192.168.1.25:5000/chat
```

`GameHost` accepts only a loopback IP address. `RemotePlayerEntity` must remain relative to the game installation. Do not configure either with a machine-specific absolute path.

## Firewall

Allow inbound TCP traffic to the configured Multiplayer Relay port (5000 by default) on the Session Host's private network profile only. Do not open port 5150 in Windows Firewall: it is local Game Interaction Protocol traffic and must never be exposed to the LAN. Do not create public-network rules or router port forwarding.

## Manual two-PC test

1. On both PCs, install and start the modified game, load the same base-game map, and move each player to the same agreed starting position. Map synchronization is not provided.
2. On the Session Host PC, start `Multimnesia.Server.exe` with an explicit LAN listen address. Confirm the printed endpoint and security warning.
3. On the Session Host PC, start `Multimnesia.Client.exe` with a Relay URL that matches that endpoint. It should report local-game and relay connection success, the **Session Host** role, then `Waiting for the other Game Peer...`.
4. On the Joining Player PC, start `Multimnesia.Client.exe` with the same Relay URL. It should report the **Joining Player** role. Both Game Peers should print `Multiplayer Session ready.`.
5. Move and rotate both players simultaneously for at least one minute. Each game should create exactly one `arabic_statue.ent` representation named `RemotePlayer`, and that representation should continuously follow the other player without sustained freezes, unbounded lag, duplication, or swapped local/remote identity.
6. Start a third Game Peer. It should exit with a clear message that the Multiplayer Session already has two Game Peers.
7. Stop either Game Peer with Ctrl+C. It should exit without an unhandled exception, and the other Game Peer should report the remote disconnection. Stop the relay with Ctrl+C as well.
8. After the disconnected Game Peer has exited, another Game Peer may take the free admission place for further diagnostics.

## Expected behavior and limitations

The prototype synchronizes only player position and rotation. Experimental `SCRIPT_CALL` relay remains enabled, but is not required for acceptance. It does not synchronize maps, interpolate movement, animate or collide remote players, synchronize sound, recover connections, migrate the Session Host, authenticate users, support multiple Multiplayer Sessions, or support more than two players. Grab, throw, door, and legacy `setlistener` handling are deliberately absent.

Startup failures distinguish local-game connection failure from Multiplayer Relay connection failure. A relay-lifetime admission component owns the two Game Peer slots, so admission state is not lost between transient SignalR hub instances.
