# Amnesia Multiplayer

This repository contains the Game Peer application for a trusted-LAN, two-player multiplayer session for Amnesia: The Dark Descent. `Multimnesia.Client` is the Game Peer: one deployable application that connects to the local game, runs an in-process Multiplayer Relay when hosting, and speaks a small versioned LAN protocol to the other Game Peer. See `docs/adr/0001-in-process-plain-tcp-multiplayer-relay.md` for why the Multiplayer Relay is in-process rather than a separate service, and `CONTEXT.md` for the domain vocabulary (Multiplayer Session, Multiplayer Relay, Game Peer, Session Host, Joining Player) used throughout this document.

> [!WARNING]
> The Multiplayer Relay is unauthenticated and unencrypted. Run it only between trusted computers on a trusted, private LAN. Never expose its port to the internet or configure router port forwarding for it.

Players do not build any of this: a release archive contains the modified `Amnesia.exe`, the Game Peer, and the game content, laid out to extract into the Amnesia folder. See its `INSTALL.txt`, and `docs/RELEASING.md` for how that archive is produced.

## Prerequisites

- Windows x64 on both PCs.
- Amnesia: The Dark Descent installed on both PCs.
- The same game version on both PCs.
- The modified `Amnesia.exe` produced by [`amnesia-tdd-tcp`](https://github.com/amnesia-spelos/amnesia-tdd-tcp), with its Game Interaction Protocol listening locally on port 5150. It must include Custom Story start support (`startcustomstory:<id>` and `EVENT:CustomStoryStarted:<id>`); with an older `Amnesia.exe`, chat still works, but the Session Host's starts are never shared, and starts shared with a Joining Player fail as unsupported. It must also support Game Interaction Protocol Version 2 with the `avatars` and `localpose` Capabilities for the Shared Pose; without them, the rest keeps working and the player is told movement will not be shared.
- The same Custom Story installed on both PCs under the same folder name (its Custom Story Identifier). Custom Story versions are not compared.
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

To produce a release archive instead of a bare Game Peer folder, run `.\scripts\package-release.ps1`; `docs/RELEASING.md` holds the full release checklist.

## Versioning

Game Peer versions follow SemVer as `0.MINOR.PATCH` while the project is in alpha:

- Each codenamed milestone bumps the minor version; fixes to that milestone bump the patch version under the same codename. The current release is `v0.1.0`, codename "Whisper".
- There is no `-alpha` suffix. Instead, GitHub releases are marked pre-release.
- Release titles and archive names include the codename, for example `amnesia-multiplayer-v0.1.0-whisper-win-x64.zip`.

The version and codename are defined once, in `src/Directory.Build.props`, and stamped into the executable's file version. When the Game Peer first connects to its local game, chat shows `Amnesia Multiplayer v0.1.0 "Whisper" connected.` Game Peers do not compare versions with each other; the `LanProtocol` version check at admission decides compatibility.

## Setup

1. On both PCs, install this repository's game content: the `resources` folder mirrors the game root, so copy `resources\custom_stories\mp-test-cs` and `resources\entities\multiplayer\skeleton_spelos` to the same relative paths under the Amnesia folder, keeping their folder names. The test Custom Story appears in the Custom Stories menu as "Amnesia Multiplayer Test"; the entity is the skeleton Avatar model. (A release archive already contains both.)
2. On both PCs, start the modified game and stay in the main menu.
3. On both PCs, start `Multimnesia.Client.exe`. It connects to the local game over the Game Interaction Protocol and remains a console application whose console is used only for operational logs (also written to a log file, see [Reporting a problem](#reporting-a-problem)); all player interaction happens through the game's own chat.
4. If the local game connection cannot be established, the Game Peer keeps running and retries with bounded backoff, logging the initial failure, restrained retry status, and recovery. Hosting and joining are unavailable until that connection succeeds.

## Starting a Custom Story together

Follow this order:

1. The Session Host types `/host`.
2. The Joining Player types `/join <Session Host's address>`, waits for the successful-join SYSTEM message, and stays in the main menu.
3. The Session Host opens the Custom Stories menu, selects the Custom Story (for example "Amnesia Multiplayer Test"), and presses Start.

The Session Host's Game Peer shares this Shared Custom Story Start with the Joining Player's Game Peer, which starts the same Custom Story on the Joining Player's game. Each game then loads the story independently. The Multiplayer Session survives loading; chat sent while a game is loading may appear afterwards or be lost, and chat works normally once both players are in game.

- Late joiners are not caught up. Only starts made while a Joining Player is joined are shared; a start made before anyone joins is not relayed. If the Joining Player joined too late, both players return to the main menu and the Session Host starts again.
- The Joining Player's own starts are not shared. A Custom Story the Joining Player starts themselves stays local.
- Returning to the main menu, quitting, Continue, Load Game, death reloads, and map changes are not shared.
- If the Joining Player's game cannot start the Custom Story, both players see why in chat and the Multiplayer Session stays up.

## Seeing each other move

Once both players are in the Custom Story, each Game Peer streams its own player's Pose to the other, which shows it as an Avatar — the skeleton model from `resources\entities\multiplayer\skeleton_spelos` — in its own game. This is the Shared Pose. It needs nothing from the players: it starts when the second player is admitted and stops when either leaves.

- The Avatar moves smoothly between the Poses that arrive, faces where that player is facing, and has collision: you bump into the other player rather than walking through them.
- A teleport or a map start snaps the Avatar into place instead of gliding it there.
- The Shared Pose is active only in a two-player Multiplayer Session. Hosting alone or merely joined, nothing is sent.
- It requires an `Amnesia.exe` that supports Avatars. If yours does not, you see `Your game does not support Avatars; movement will not be shared.` once, and chat and Custom Story starts keep working.
- If the skeleton entity is not installed, you see `The Avatar model is not installed; the other player will be invisible.` and the Multiplayer Session continues.

Avatars are not animated: an Avatar slides upright and makes no footsteps, and enemies ignore it. See [Expected behavior and limitations](#expected-behavior-and-limitations).

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
| Session Host starts a Custom Story while a Joining Player is joined (seen by Session Host) | `Starting Custom Story <id> for the other player.` |
| Joining Player's game starts the Session Host's Custom Story (seen by Joining Player) | `The host started Custom Story <id>.` |
| Custom Story is not installed on the Joining Player's PC (seen by both) | `Custom Story <id> could not start for the Joining Player: it is not installed.` |
| Joining Player's installed Custom Story is invalid (seen by both) | `Custom Story <id> could not start for the Joining Player: the installed Custom Story is invalid.` |
| Joining Player's game is not in the main menu, or is still handling a previous start (seen by both) | `Custom Story <id> could not start for the Joining Player: their game is not in the main menu.` |
| Joining Player's game lacks Custom Story start support or does not reply within 5 seconds (seen by both) | `Custom Story <id> could not start for the Joining Player: their game does not support Custom Story starts or did not respond.` |
| Joining Player starts a Custom Story themselves (seen by Joining Player) | `Only the Session Host's Custom Story starts are shared.` |

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
| `LogLevel` | `Information` | Minimum severity logged to the console and the log file: `Debug`, `Information`, `Warning`, or `Error`. `Debug` additionally logs remote endpoints. |

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
- **`Custom Story <id> could not start for the Joining Player: it is not installed.`** — install the Custom Story on the Joining Player's PC under the same folder name as on the Session Host's PC.
- **`Custom Story <id> could not start for the Joining Player: the installed Custom Story is invalid.`** — the Joining Player's copy of the Custom Story is damaged or incomplete; reinstall it from the same source as the Session Host's copy.
- **`Custom Story <id> could not start for the Joining Player: their game is not in the main menu.`** — the Joining Player must be in the main menu when the Session Host presses Start.
- **`Custom Story <id> could not start for the Joining Player: their game does not support Custom Story starts or did not respond.`** — the Joining Player's `Amnesia.exe` predates Custom Story start support, or their game did not reply within 5 seconds. Install a current build from `amnesia-tdd-tcp`; the Joining Player's Game Peer console logs any unrecognized reply.
- **Local-game connection errors** — confirm the modified `Amnesia.exe` from `amnesia-tdd-tcp` is running and listening on `GamePort` before starting the Game Peer; it retries automatically once the game becomes reachable.
- Set `LogLevel` to `Debug` to see per-connection endpoints and handshake attempts while diagnosing a LAN issue; never share `Debug` logs outside your own troubleshooting, as they include peer IP addresses.

## Reporting a problem

When reporting a problem, attach both logs from each affected PC:

- **Game Peer log**: every run writes `logs\game-peer-yyyyMMdd-HHmmss.log` beside `Multimnesia.Client.exe`, named after the run's local start time. The newest 10 are kept. Each file starts with the Game Peer version and codename, the `LanProtocol` version, and the OS version, followed by the same entries as the console at the configured `LogLevel`. If the `logs` folder cannot be written, for example because the Game Peer is in a read-only folder, the Game Peer says so on its console and keeps running without a log file.
- **Game log**: the game writes its own `hpl.log` to `Documents\Amnesia\Main\hpl.log` in your user profile. It is overwritten on every game start, so copy it before restarting the game.

At the default `Information` level the Game Peer log contains no IP addresses. Logs recorded at `Debug` do; see the warning under [Troubleshooting](#troubleshooting).

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

For the Shared Custom Story Start, install `mp-test-cs` on both PCs and start each step with both games in the main menu unless the step says otherwise:

10. On PC A, `/host`, then start "Amnesia Multiplayer Test" before PC B joins. Confirm nothing is relayed and PC A sees no Custom Story SYSTEM message. Return PC A to the main menu.
11. On PC B, `/join` PC A. On PC A, start "Amnesia Multiplayer Test". Confirm PC A sees `Starting Custom Story mp-test-cs for the other player.`, PC B sees `The host started Custom Story mp-test-cs.`, both games load `multiplayer-test.map` at `PlayerStartArea_1`, and ordinary chat works both ways in game.
12. Remove `mp-test-cs` from PC B's `custom_stories` folder, then start it on PC A. Confirm both see `Custom Story mp-test-cs could not start for the Joining Player: it is not installed.` and the session remains. Reinstall it on PC B.
13. With PC B joined, start "Amnesia Multiplayer Test" on PC B. Confirm it stays local: PC A's game stays in the main menu and PC B sees `Only the Session Host's Custom Story starts are shared.`
14. Leave PC B in the Custom Story it started in step 13, and start "Amnesia Multiplayer Test" on PC A. Confirm both see `Custom Story mp-test-cs could not start for the Joining Player: their game is not in the main menu.` and the session remains.

A build is considered verified only when every step above produces the documented outcome on real hardware, in addition to the automated test suite passing.

### Whisper walkthrough (from the release archive)

Run this one from the extracted release archive on two PCs, never from a dev build: it is what makes a release publishable (`docs/RELEASING.md`). It covers the Shared Pose and the release packaging; the steps above still cover chat, sessions, and the Shared Custom Story Start.

1. On both PCs, back up `Amnesia.exe` and extract the archive into the Amnesia folder as its `INSTALL.txt` describes, then add the private-network firewall rule on the PC that will host.
2. On both PCs, double-click `Start Multiplayer.bat` in the Amnesia folder. Confirm the game starts, the Game Peer gets its own console window, and the game's chat shows `Amnesia Multiplayer v0.1.0 "Whisper" connected.` — the version each PC is running.
3. On PC A, `/host`. On PC B, `/join <PC A's LAN address>`. On PC A, start "Amnesia Multiplayer Test". Confirm both games load the map.
4. Walk around on both PCs. Confirm each player sees the other as the skeleton Avatar, moving smoothly rather than jumping between positions, turned the way that player is facing, and that walking into the Avatar blocks rather than passing through it.
5. Confirm ordinary chat still works both ways while moving.
6. Trigger a placement that is not ordinary walking — the map start itself, or a `TeleportPlayer` — and confirm the Avatar snaps to the new position rather than gliding across the level to it.
7. On PC B, `/leave`. Confirm PC A's Avatar disappears. `/join` again and have PC A start the Custom Story again; confirm both Avatars come back.
8. With both players in game, close and restart PC B's game (leaving both Game Peers running). Confirm PC B's Game Peer reconnects, and after PC A starts the Custom Story again, both players see each other again.
9. On both PCs, confirm `multiplayer\logs\game-peer-*.log` exists, that its newest file starts with the version, codename, `LanProtocol` version, and OS, and that the session events from this walkthrough are in it.

If either PC's game is an older build without Avatar support, that player sees `Your game does not support Avatars; movement will not be shared.` and chat and Custom Story starts keep working. If the skeleton entity is missing from a game folder, that player sees `The Avatar model is not installed; the other player will be invisible.` Both are degraded modes, not failures of the walkthrough — but a release archive extracted correctly should produce neither.

## Expected behavior and limitations

Whisper is a vertical slice: two players host, join, chat, start the same Custom Story together, and see each other move. What that slice does *not* cover:

- **Custom Stories only.** The main game is not supported.
- **Map changes and returning to the main menu are not shared.** Only the Session Host's fresh Custom Story start is. Continue, Load Game, death reloads, quitting, and every map transition after the start are local.
- **An Avatar stays frozen at its last Pose when its player returns to the main menu.** The game sends no Poses from the main menu and reports no map-left event, so the other player keeps seeing a motionless Avatar where that player last stood. Start the Custom Story again to resynchronize.
- **Enemies ignore the Joining Player.** Enemy AI is aware only of the player in its own game.
- Avatar animation, head pitch, crouch visuals, and footstep sounds: an Avatar slides upright and does not animate.
- Props, doors, levers, inventory, and scripts: every world interaction stays local to the game it happened in.
- Catch-up for late joiners, sharing the Joining Player's own starts, and checking that both PCs have the same Custom Story version.
- Arbitrary script execution.
- UDP or another gameplay transport.
- Authentication, encryption, or internet exposure.
- IPv6.
- Reconnect/resume of a Multiplayer Session, or host migration. (The Game Peer does reconnect to its own local game.)
- Multiple simultaneous Multiplayer Sessions, and more than one Joining Player.
- Discovery or matchmaking.
- Persistent player identity.
- Chat persistence.
- Auto-update: a new version means extracting a new archive.

The architecture does not preclude any of the above being built later, but none of it is in scope now.
