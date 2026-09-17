# Amnesia Multiplayer

This repository contains the Game Peer application for a trusted-LAN, two-player multiplayer session for Amnesia: The Dark Descent. `Multimnesia.Client` is the Game Peer: one deployable application that connects to the local game, runs an in-process Multiplayer Relay when hosting, and speaks a small versioned LAN protocol to the other Game Peer. See `docs/adr/0001-in-process-plain-tcp-multiplayer-relay.md` for why the Multiplayer Relay is in-process rather than a separate service, and `CONTEXT.md` for the domain vocabulary (Multiplayer Session, Multiplayer Relay, Game Peer, Session Host, Joining Player) used throughout this document.

> [!WARNING]
> The Multiplayer Relay is unauthenticated and unencrypted. Run it only between trusted computers on a trusted, private LAN. Never expose its port to the internet or configure router port forwarding for it.

## Prerequisites

- Windows x64 on both PCs.
- Amnesia: The Dark Descent installed on both PCs.
- The same game version, map, and starting position on both PCs. Map synchronization is not provided.
- The modified `Amnesia.exe` produced by [`amnesia-tdd-tcp`](https://github.com/amnesia-spelos/amnesia-tdd-tcp), with its Game Interaction Protocol listening locally on port 5150.
- For development, the .NET 10 SDK. Published self-contained builds do not require a separately installed .NET runtime.

The Game Interaction Protocol connection between the Game Peer and its local game is loopback-only and never crosses the network. Only Multiplayer Relay traffic crosses the LAN.

## Build, test, and publish

From the repository root:

```powershell
dotnet restore .\src\Multimnesia.sln
dotnet build .\src\Multimnesia.sln --configuration Release --no-restore
dotnet test .\src\Multimnesia.sln --configuration Release --no-build
dotnet publish .\src\Multimnesia.Client\Multimnesia.Client.csproj --configuration Release --runtime win-x64 --self-contained true --output .\artifacts\game-peer
```

This is an ordinary directory publish. Trimming and single-file publishing are intentionally not enabled. Copy `artifacts\game-peer` to both PCs.

## Setup

1. On both PCs, install and start the modified game, load the same base-game map, and move each player to the agreed starting position.
2. On both PCs, start `Multimnesia.Client.exe`. It connects to the local game over the Game Interaction Protocol and remains a console application whose console is used only for operational logs; all player interaction happens through the game's own chat.
3. If the local game connection cannot be established, the Game Peer keeps running and retries with bounded backoff, logging the initial failure, restrained retry status, and recovery. Hosting and joining are unavailable until that connection succeeds.

## Player commands

Every slash-prefixed chat entry is interpreted as a Game Peer command; commands are never relayed as ordinary chat. Ordinary chat requires no command: while hosting or joined, it is relayed to the other Game Peer without a local echo (the local game already displays what you typed).

- **`/host`** — starts an in-process Multiplayer Relay bound to all IPv4 interfaces on the configured port (`5000` by default) and begins waiting for a Joining Player. Reports the usable private IPv4 addresses to join at.
- **`/join <hostname-or-IPv4>`** — attempts to join a Session Host's Multiplayer Session within a 10-second deadline (configurable). If the hostname resolves to multiple eligible IPv4 addresses, they are tried sequentially within that deadline. IPv6 is unsupported.
- **`/leave`** — leaves a Multiplayer Session you are hosting or have joined, or cancels an in-progress `/join`.

`/host` and `/join` are only valid while not already in a Multiplayer Session; `/leave` is valid while hosting, joining, or joined. Invalid transitions and unknown or malformed commands produce an exact SYSTEM chat message rather than switching sessions implicitly.

### Lifecycle outcomes

| Situation | SYSTEM feedback |
|---|---|
| Joining Player leaves gracefully (seen by Session Host) | `A player left.` |
| Joining Player connection lost or times out (seen by Session Host) | `A player disconnected.` |
| Session Host uses `/leave` (seen by Joining Player) | `The host ended the Multiplayer Session.` |
| Session Host connection lost or times out (seen by Joining Player) | `Connection to the host was lost.` |
| You leave after joining | `You left the Multiplayer Session.` |
| You leave after hosting | `Hosting stopped.` |
| A third Game Peer attempts to join an occupied session | `The Multiplayer Session is full.` |

When the Joining Player leaves or disconnects, the Session Host keeps hosting and can admit a replacement. When the Session Host leaves, the Multiplayer Session ends and the Joining Player returns to being unconnected from any session.

## Configuration

Safe defaults work for both Game Peers on one PC. Configuration can be supplied in an optional `appsettings.json` beside the executable; command-line values override the JSON file.

```json
{
  "GamePeer": {
    "GameHost": "127.0.0.1",
    "GamePort": 5150,
    "RelayPort": 5000,
    "JoinTimeoutSeconds": 10,
    "LogLevel": "Information"
  }
}
```

| Setting | Default | Purpose |
|---|---|---|
| `GameHost` | `127.0.0.1` | Loopback address of the local Game Interaction Protocol. Must be a loopback address. |
| `GamePort` | `5150` | Local Game Interaction Protocol port. |
| `RelayPort` | `5000` | TCP port the in-process Multiplayer Relay listens on when hosting, and connects to when joining. |
| `JoinTimeoutSeconds` | `10` | Overall deadline for a `/join` attempt, including trying every resolved address. |
| `LogLevel` | `Information` | Minimum severity console-logged: `Debug`, `Information`, `Warning`, or `Error`. `Debug` additionally logs remote endpoints. |

Ordinary play requires no configuration beyond these defaults; no relay URL or bind address needs to be supplied. Override any setting from the command line, for example:

```powershell
.\Multimnesia.Client.exe --GamePeer:RelayPort 5050 --GamePeer:LogLevel Debug
```

## Firewall

Allow inbound TCP traffic to the configured Multiplayer Relay port (5000 by default) on the Session Host's **private** network profile only. Do not open the Game Interaction Protocol port (5150 by default) in Windows Firewall: it is local, loopback-only traffic and must never be exposed to the LAN. Do not create public-network firewall rules or router port forwarding for the Multiplayer Relay port; it is unauthenticated and intended only for a trusted private LAN.

## Troubleshooting

- **`Hosting failed: ...`** — the configured `RelayPort` is already in use on this PC, or the account lacks permission to bind it. Choose a different port with `--GamePeer:RelayPort`.
- **`Could not resolve the destination: ...`** — the hostname given to `/join` did not resolve. Use the Session Host's printed LAN IPv4 address instead.
- **`The host did not resolve to an IPv4 address.`** — the destination only resolved to IPv6 addresses, which are unsupported.
- **`Joining the Multiplayer Session timed out.`** — no reachable Game Peer accepted the connection within `JoinTimeoutSeconds`. Confirm the Session Host is hosting, the address and port are correct, and the private-network firewall rule from the section above is in place.
- **`The Multiplayer Session uses an incompatible protocol version.`** — both PCs must run the same `Multimnesia.Client` build.
- **`The Multiplayer Session is full.`** — a Multiplayer Session already has a Session Host and a Joining Player; wait for a slot to free up.
- **Local-game connection errors** — confirm the modified `Amnesia.exe` from `amnesia-tdd-tcp` is running and listening on `GamePort` before starting the Game Peer; it retries automatically once the game becomes reachable.
- Set `LogLevel` to `Debug` to see per-connection endpoints and handshake attempts while diagnosing a LAN issue; never share `Debug` logs outside your own troubleshooting, as they include peer IP addresses.

## Two-computer verification walkthrough

This is the record of the manual walkthrough required before considering a build release-ready. Repeat it after any change to session orchestration, the LAN protocol, or the Multiplayer Relay.

1. Start the modified game and `Multimnesia.Client.exe` on both PCs (PC A and PC B).
2. On PC A, type `/host`. Confirm the SYSTEM message lists a usable LAN address and port.
3. On PC B, type `/join <PC A's LAN address>`. Confirm PC A sees `A player is attempting to join.` then `A player joined.`, and PC B sees a successful-join SYSTEM message.
4. On PC A, send an ordinary chat message. Confirm it appears on PC B without a duplicate local echo on PC A.
5. On PC B, send an ordinary chat message. Confirm it appears on PC A without a duplicate local echo on PC B.
6. On PC B, type `/leave`. Confirm PC B sees `You left the Multiplayer Session.` and PC A sees `A player left.`, while PC A remains hosting.
7. On PC B, `/join` PC A again to confirm a replacement Joining Player is admitted after a graceful departure.
8. On PC A, type `/leave`. Confirm PC A sees `Hosting stopped.` and PC B sees `The host ended the Multiplayer Session.`.
9. On PC A, `/host` again; on PC B, `/join` again. Then close PC A's Game Peer process (or disconnect its network) without `/leave`. Confirm PC B eventually reports `Connection to the host was lost.` once the heartbeat timeout elapses, demonstrating recovery from Session Host loss without requiring a graceful shutdown.

A build is considered verified only when every step above produces the documented outcome on real hardware, in addition to the automated test suite passing.

## Expected behavior and limitations

This milestone is a chat-and-session vertical slice. It deliberately excludes:

- movement or other game-state synchronization;
- arbitrary script execution;
- UDP or another gameplay transport;
- authentication, encryption, or internet exposure;
- IPv6;
- reconnect/resume or host migration;
- multiple simultaneous Multiplayer Sessions;
- more than one Joining Player;
- discovery or matchmaking;
- persistent player identity;
- chat persistence (this milestone does not persist logs either; operators may redirect console output when diagnosing a session).

The architecture does not preclude any of the above being built later, but none of it is in scope now.
