# Amnesia Multiplayer

This repository contains the Game Peer application for a trusted-LAN, two-player multiplayer session for Amnesia: The Dark Descent. `Multimnesia.Client` is the Game Peer: one deployable application that connects to the local game, runs an in-process Multiplayer Relay when hosting, and speaks a small versioned LAN protocol to the other Game Peer. See `docs/adr/0001-in-process-plain-tcp-multiplayer-relay.md` for why the Multiplayer Relay is in-process rather than a separate service, and `CONTEXT.md` for the domain vocabulary (Multiplayer Session, Multiplayer Relay, Game Peer, Session Host, Joining Player) used throughout this document.

> [!WARNING]
> The Multiplayer Relay is unauthenticated and unencrypted. Run it only between trusted computers on a trusted, private LAN. Never expose its port to the internet or configure router port forwarding for it.

Players do not build any of this: a release archive contains the modified `Amnesia.exe`, the Game Peer, and the game content, laid out to extract into the Amnesia folder. See its `INSTALL.txt`, and `docs/RELEASING.md` for how that archive is produced.

## Prerequisites

- Windows x64 on both PCs.
- Amnesia: The Dark Descent installed on both PCs.
- The same game version on both PCs.
- The modified `Amnesia.exe` produced by [`amnesia-tdd-tcp`](https://github.com/amnesia-spelos/amnesia-tdd-tcp), with its Game Interaction Protocol listening locally on port 5150. It must support the `chatentry` Command for chat to display; an older `Amnesia.exe` answers it with `WARNING:Unknown command`, and chat from the other player and `SYSTEM` feedback stop appearing. It must include Custom Story start support (`startcustomstory:<id>` and `EVENT:CustomStoryStarted:<id>`); without it, chat still works, but the Session Host's starts are never shared, and starts shared with a Joining Player fail as unsupported. It must also support Game Interaction Protocol Version 2 with the `avatars` and `localpose` Capabilities for the Shared Pose; without them, the rest keeps working and the player is told movement will not be shared. For Entity Interaction Sync it must also grant the `interactions` Capability; without it, the rest keeps working and the player is told moving props will not be shared. For Shared Enemies it must grant `enemies` as well as the Shared Pose Capabilities, support the `enemyattack` Command and Event for the Joining Player to be hit, the `checkpoint` and `playerdeath` Events and `shareddeath` Command for Shared Deaths, and the `enemydoorbreak` Command and Event and the `autodisabled` Shared Callback kind for door breaks and enemy-triggered callbacks. For Shared Callbacks it must also grant the `sharedcallbacks` Capability; without it, the rest keeps working and the player is told triggers, buttons, and lamps will not be shared.
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

- Each codenamed milestone bumps the minor version; fixes to that milestone bump the patch version under the same codename. The current release is `v0.3.0`, codename "Dread". `main` builds `v0.3.1`, a fix to "Dread", which is in development.
- There is no `-alpha` suffix. Instead, GitHub releases are marked pre-release.
- Release titles and archive names include the codename, for example `amnesia-multiplayer-v0.3.1-dread-win-x64.zip`.

The version and codename are defined once, in `src/Directory.Build.props`, and stamped into the executable's file version. When the Game Peer first connects to its local game, chat shows `Amnesia Multiplayer v0.3.1 "Dread" connected.`, and each per-run log file starts with the same version. Game Peers do not compare versions with each other; the `LanProtocol` version check at admission decides compatibility.

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

An Avatar walks, idles, crouches, and looks up and down as its player does. It makes no footsteps, so enemies cannot hear it, but the Session Host's Shared Enemies can see, chase, and attack the Joining Player's Avatar. See [Expected behavior and limitations](#expected-behavior-and-limitations).

## Player commands

Every slash-prefixed chat entry is interpreted as a Game Peer command; commands are never relayed as ordinary chat. Ordinary chat requires no command: while hosting or joined, it is relayed to the other Game Peer without a local echo (the local game already displays what you typed).

- **`/host`** — starts an in-process Multiplayer Relay bound to all IPv4 interfaces on the configured port (`5000` by default) and begins waiting for a Joining Player. Reports the usable private IPv4 addresses to join at.
- **`/join <hostname-or-IPv4>`** — attempts to join a Session Host's Multiplayer Session within a 10-second deadline (configurable). If the hostname resolves to multiple eligible IPv4 addresses, they are tried sequentially within that deadline. IPv6 is unsupported.
- **`/leave`** — leaves a Multiplayer Session you are hosting or have joined, or cancels an in-progress `/join`.
- **`/tp-to-peer`** — places you where the other player stands, from their latest Pose: their feet position, the way they face, and crouching if they crouch. Use it when a door locks or a wall closes behind the player who went first. It works only while both players are on the same map, and needs an `Amnesia.exe` that grants `playerplace`. The other player sees your Avatar snap to them. It takes no argument.

`/host` and `/join` are only valid while not already in a Multiplayer Session; `/leave` is valid while hosting, joining, or joined; `/tp-to-peer` is valid while hosting or joined with the other player present. Invalid transitions and unknown or malformed commands produce an exact SYSTEM chat message rather than switching sessions implicitly.

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

| You use `/tp-to-peer` and are placed at the other player | `Teleported to the other player.` |
| You use `/tp-to-peer` outside a Multiplayer Session, or while still joining | `You are not in a Multiplayer Session.` |
| You use `/tp-to-peer` with nobody else in the Multiplayer Session | `The other player is not in the Multiplayer Session.` |
| You use `/tp-to-peer` before any Pose of the other player arrived | `The other player's position is not known yet.` |
| You use `/tp-to-peer` while the other player is on another map | `The other player is on a different map.` |
| You use `/tp-to-peer` and your game does not grant `playerplace` | `Your game does not support /tp-to-peer; update it to teleport to the other player.` |
| Your game refuses `/tp-to-peer` because you are not in play (paused, in a menu, inventory, or journal, loading, dead, in a flashback, or a script took control) | `Your game refused the teleport: you are not in play.` |
| Your game refuses `/tp-to-peer` as malformed | `Your game refused the teleport: the request was malformed.` |
| Your game does not answer `/tp-to-peer` within 5 seconds | `Your game did not answer the teleport.` |
| You use `/tp-to-peer` again before your game answered the last one | `A teleport is already in progress.` |

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
| `ChatColor` | unset | Chat Color of your Chat Author name on the other player's screen: six hex digits, `RRGGBB`, without `#`. Unset uses the game's default color. Your own messages always appear in the default color on your own screen. Also settable with the short switch `--color`. |

Ordinary play requires no configuration beyond these defaults; no relay URL or bind address needs to be supplied. Override any setting from the command line, for example:

```powershell
.\Multimnesia.Client.exe --GamePeer:RelayPort 5050 --GamePeer:LogLevel Debug
.\Multimnesia.Client.exe --color FF00FF
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

### Release walkthrough (from the release archive)

Run this one from the extracted release archive on two PCs, never from a dev build: it is what makes a release publishable (`docs/RELEASING.md`). It covers the Shared Pose and the release packaging; the steps above still cover chat, sessions, and the Shared Custom Story Start, and the [Entity Interaction Sync walkthrough](#entity-interaction-sync-walkthrough) covers Holds.

1. On both PCs, back up `Amnesia.exe` and extract the archive into the Amnesia folder as its `INSTALL.txt` describes, then add the private-network firewall rule on the PC that will host.
2. On both PCs, double-click `Start Multiplayer.bat` in the Amnesia folder. Confirm the game starts, the Game Peer gets its own console window, and the game's chat shows `Amnesia Multiplayer v0.3.1 "Dread" connected.` — the version each PC is running.
3. On PC A, `/host`. On PC B, `/join <PC A's LAN address>`. On PC A, start "Amnesia Multiplayer Test". Confirm both games load the map.
4. Walk around on both PCs. Confirm each player sees the other as the skeleton Avatar, moving smoothly rather than jumping between positions, turned the way that player is facing, walking, idling, crouching, and looking up and down as that player does, and that walking into the Avatar blocks rather than passing through it.
5. Confirm ordinary chat still works both ways while moving.
6. Trigger a placement that is not ordinary walking — the map start itself, or a `TeleportPlayer` — and confirm the Avatar snaps to the new position rather than gliding across the level to it.
7. On PC B, `/leave`. Confirm PC A's Avatar disappears. `/join` again and have PC A start the Custom Story again; confirm both Avatars come back.
8. With both players in game, close and restart PC B's game (leaving both Game Peers running). Confirm PC B's Game Peer reconnects, and after PC A starts the Custom Story again, both players see each other again.
9. On both PCs, confirm `multiplayer\logs\game-peer-*.log` exists, that its newest file starts with the version, codename, `LanProtocol` version, and OS, and that the session events from this walkthrough are in it.

If either PC's game is an older build without Avatar support, that player sees `Your game does not support Avatars; movement will not be shared.` and chat and Custom Story starts keep working. If the skeleton entity is missing from a game folder, that player sees `The Avatar model is not installed; the other player will be invisible.` Both are degraded modes, not failures of the walkthrough — but a release archive extracted correctly should produce neither.

### Entity Interaction Sync walkthrough

Run this before calling a build's Entity Interaction Sync verified (#40), with the same builds and logs as the Hold endings walkthrough below. It covers each kind of interaction in the test room once; the walkthroughs after it go deeper into Hold endings, contention, contact, and breaks.

1. **Start.** On PC A, `/host`. On PC B, `/join <PC A's LAN address>`. On PC A, start "Amnesia Multiplayer Test". Confirm both players are in `multiplayer-test.map` and see each other's Avatar.
2. **Valve and gate.** B spins the valve (entity 84). A sees the valve turn and the portcullis (entity 85) open. Repeat with A spinning and B watching.
3. **A held prop is out of reach.** B picks up the bottle (entity 72) and keeps holding it. A looks at it: A's crosshair greys and A cannot grab it. B drops it, and once it settles, A can grab it.
4. **Throw.** B throws the bottle. A sees it fly and land where it lands on B's screen.
5. **Topple.** B throws the bottle into the stack of wooden boxes. Both screens show the stack fall the same way and come to rest in the same places.
6. **Break.** B throws the key jar (entity 87) at a wall. It breaks in the same place on both screens.
7. **Door, lever, and slider.** B opens the door (entity 73) partway, pulls the lever (entity 88) to switch the room light on, and moves the slider (entity 90) to change the light's color. Both screens show the door at the same angle, the lever at the same end, the slider in the same notch, and the same light. The lever and slider callbacks run in each game.
8. **Leaving mid-hold.** B picks up a box and keeps holding it, then types `/leave`. On PC A the box falls from where B held it, under A's own physics.

On 2026-10-08 these steps all passed, and the same build was then played through chapter 1 of the main game. Holds shared doors, levers, valves, and props there too. That session found the gaps listed under [Expected behavior and limitations](#expected-behavior-and-limitations).

### Hold endings walkthrough

Run this after any change to how Holds end (#37). Use the Release Game Peer from `src/Multimnesia.Client/bin/Release/net10.0/` on both PCs, built from the same commit, and the game build with `interactions`. Install `mp-test-cs` on both PCs. Each step names who does what. PC B is the Holder unless the step says otherwise. Read Hold endings in the Game Peer log (`logs\game-peer-*.log` next to the exe). Each one is a `HoldEnded` line ending in its reason, for example `The other player's Hold on entity 72 on custom_stories/mp-test-cs/maps/multiplayer-test.map ended: the other player left.`

Setup: on PC A, `/host`. On PC B, `/join <PC A's LAN address>`. On PC A, start "Amnesia Multiplayer Test", and confirm both players are in `multiplayer-test.map` and see each other's Avatar. Pick a small grabbable prop such as the bottle, and note its entity ID from the first `HoldClaimed` line.

1. **Baseline.** B picks up the prop, carries it, and drops it. A sees it move with B and land where B sees it land. Both logs end the Hold with `settled.` Then A picks it up, and B sees it move.
2. **Departure while holding.** B picks up the prop and keeps holding it. B types `/leave`. On PC A the prop falls from where B held it, under A's own physics, and A can pick it up. A's log ends the Hold with `the other player left.`, and so does B's (`The local player's Hold ...`).
3. **Clean slate for a replacement.** B lets go, then `/join`s again without restarting the story. B picks up the same prop. A sees it move. B's log shows a new `HoldClaimed` for it, not a continued Hold. A's log has no `HoldCommandFailed` for that entity.
4. **Crash while holding.** B picks up the prop and keeps holding it, then closes B's Game Peer console window. A sees `A player disconnected.`, either right away or once the heartbeat timeout passes. The prop falls on PC A and A can pick it up, and A's log ends the Hold with `the other player left.` Restart B's Game Peer, `/join` again, and restart the story from PC A.
5. **The Holder leaves the map.** Standing next to the level door, B throws the prop and uses the door straight away, before the prop settles (about 3 seconds). If B's log ends the Hold with `settled.` first, try again. On PC A the prop finishes falling under A's physics and A can pick it up. A's log ends the Hold with `the other player left the map.`, and B's ends B's own with `the local player left the map.` Bring B back by restarting the story from PC A, not through the door: after a level door the map path is spelled differently (amnesia-tdd-tcp#64).
6. **The receiving player leaves the map.** B picks up the prop and keeps holding it, and A walks through the level door. A's log ends the Hold with `the local player left the map.` There is no `HoldCommandFailed` `entityrelease ... wrong-map` line. B drops the prop and lets it settle; otherwise the restart runs into the gap in step 8. Then restart the story from PC A.
7. **The Holder's own entry is gone.** After the story restart that ends step 5, B picks up the prop again. B's log shows a fresh `HoldClaimed` for it, not a continued Hold, and A sees it move.
8. **Known gap, not a failure: reload of the same map.** B picks up the prop and keeps holding it, and A restarts the story. Neither log ends the Hold, and the prop may stop syncing until both Game Peers are restarted. This is tracked in amnesia-tdd-tcp#67. Note what you see, but don't count it as a failure.

### Contention walkthrough

Run this after any change to how Claims are ordered (#36), with the same builds, setup, and logs as the Hold endings walkthrough. Claim Denials are `HoldClaimDenied` lines.

1. **A held prop cannot be taken.** B picks up the prop and keeps holding it. A looks at it: A's crosshair greys and A cannot grab it. Then B lets go and the prop settles, and A can grab it. Repeat with A holding and B looking.
2. **A held prop cannot be taken while Settling.** B throws the prop, and A tries to grab it in flight. A cannot until it lands and both logs end the Hold with `settled.`
3. **Near-simultaneous grabs.** Both players look at the prop, and on a count of three both grab it. Exactly one player ends up holding it, and the other sees it leave their hands, if it reached them at all. Both players see it move with the Holder. The race shows as `HoldClaimDenied` lines. When B lost, A's log has `Denied the other player's Claim ...` and B's has `The local player's Claim ... was denied.` When A lost, A's log has `The local player's grab ... was refused: the other player Holds it.` Repeat a few times; either player may win.

### Contact walkthrough

Run this after any change to how Holds spread by contact (#38), with the same builds, setup, and logs as the Hold endings walkthrough, and a game build that reports contacts (amnesia-tdd-tcp#57). A Hold taken by touch shows as a `HoldClaimed` line ending in `by contact.` The test room has a stack of three wooden boxes and a pyramid of three more; restart the story from PC A to rebuild them between steps.

1. **A thrown prop topples a stack.** B throws the bottle into the stack of three boxes. Both screens show the boxes fall the same way and come to rest in the same places. B's log has a `by contact.` Claim for each box that moved, and both logs end each box's Hold with `settled.`
2. **Walking into a box.** B walks into a box of the pyramid. A sees it move as B pushed it. B's log has a `by contact.` Claim for it. A walking into the box B pushed, after it settles, gives A the Claim the same way.
3. **An Avatar pushes nothing.** A walks into a box while B watches. Only A's log has a `by contact.` Claim; B sees the box move with A's Hold and has no Claim of its own.
4. **Contact never takes a Held prop.** B holds a box, and A throws the bottle at it. The box stays in B's hands on both screens. A's log has no `by contact.` Claim for that box; if the contact raced B's Claim, it shows as `The local player's contact with ... was refused: the other player Holds it.`
5. **Accepted divergence, not a failure.** A prop B threw is still rolling when A throws another into it. Each screen may briefly show its own collision between the two before both settle where their Holders put them.

### Breaks walkthrough

Run this after any change to how breaks are shared (#39), with the same builds, setup, and logs as the Hold endings walkthrough, and a game build that breaks reported props (amnesia-tdd-tcp#58). A shared break ends the Hold with `broke.` in both logs. Restart the story from PC A to restore the props between steps.

1. **A thrown breakable breaks for both.** B picks up the key jar (entity 87) and throws it at a wall. Both screens show it break at the same place, and the sanity potion it holds falls out in both games. Both logs end the Hold with `broke.` A's log has no `HoldCommandFailed` `entitybreak` line.
2. **The debris is cosmetic.** After step 1, A and B each try to pick up a shard. Neither can, and no `HoldClaimed` line appears for one. The shards may come to rest in different places on the two screens. Each player can pick up the potion in their own game.
3. **A driven copy never breaks by itself.** B throws the vase (entity 76) gently, so it hits the wall on B's screen without breaking. On A's screen it does not break either, though its copy hits the same wall. Both logs end the Hold with `settled.`
4. **Swap roles.** Repeat step 1 with A throwing and B watching.

If a game lacks `interactions` (an older build), that player sees `Your game does not support interactions; moving props will not be shared.` once after connecting. Their Game Peer writes no `entity...` or `reportedbodies` lines, and chat and Avatars keep working.

### Chat Color walkthrough

Run this after any change to how chat is displayed or how Chat Colors are shared (#48), with the Release Game Peer built from the same commit on both PCs and a game build that supports `chatentry` (amnesia-tdd-tcp#72).

1. **Start.** On PC A, start the Game Peer with `--color FF00FF`. On PC B, start it with no color. Confirm each game's chat shows the connected line, and that PC A's `/host` prints its address.
2. **A colored name.** On PC B, `/join` PC A. On PC A, send a message. On PC B, PC A's name is magenta and the message is in the default color. On PC A, the message is in the default color, because the game always shows a player's own messages that way.
3. **An uncolored name.** On PC B, send a message. On PC A, PC B's name is in the default color.
4. **No chat failures.** Both Game Peer logs start with `LanProtocol version: 6` and have no `ChatEntryFailed` line.
5. **A bad color is rejected.** Start a Game Peer with `--color #FF00FF`. It logs a `GamePeer:ChatColor` configuration error and exits without connecting.
6. **Mismatched builds fail Admission.** Join from a Game Peer built before #48. The joiner sees `The Multiplayer Session uses an incompatible protocol version.`

On 2026-10-11 these steps all passed.

### Shared Callbacks walkthrough

Run this after any change to how Shared Callbacks are shared (#43), with the Release Game Peer built from the same commit on both PCs and a game build that grants `sharedcallbacks`. Play chapter 1 of the main game together, both players on the same map unless a step says otherwise.

1. **`13_machine_room` cogwheel.** The Session Host fits `cogwheel03_dyn`. In the Joining Player's game, the mounted cogwheel appears, and the loose copy disappears and can't be grabbed. Neither Game Peer log has an `entitybodies not-found` line.
2. **Guiding rods.** One player fits them. The other player's game shows them fitted, and the rods leave both players' inventories.
3. **`03_archives` key.** One player picks up `key_study_1`. Both games play the growl, claw, and door break, the key leaves both worlds and enters both inventories, and neither player can pick it up again.
4. **Voiced note or diary.** One player picks up a note or diary. It opens, and its voice plays, in both games at once.
5. **Combine.** One player combines two parts whose combine callback makes a whole item. Both inventories end up with the whole item and neither keeps the parts. Combining a tinderbox or oil changes only that player's inventory.
6. **`02_entrance_hall` flashback.** One player walks into `AreaFlashBack`. Both players get the full `CH01L02_JourneyToSanctum01` flashback, with voice, subtitles, sepia, and slowdown, and walking into the area afterwards does nothing in either game.
7. **Consumables.** One player picks up a tinderbox, oil, or a potion. It stays in the other player's world.
8. **`03_archives` jar.** The Holder carries `vase02_ghost_1` out of `AreaJar`. The other game runs `CollideJarLeaveArea` once, through the Shared Callback, every time.
9. **Trigger area while together.** One player walks into a scare or music area. Both games run it, and the other player keeps control of their character and camera.
10. **Trigger area on another map.** The Session Host runs ahead to a new map and triggers areas there. When the Joining Player arrives, the Pending Shared Callbacks run on arrival. The Joining Player isn't frozen or teleported, and nothing soft-locks.
11. **Button and lamp.** One player presses a button and lights a candle. Both show in the other game.
12. **Both players trigger the same one-shot area.** It runs once in each game. Two players picking up the same item at nearly the same time also run its pickup callback once in each game.
13. **Sticky area.** In My Lady's Insanity's `Prison_torture` map, the Holder fits `playpipesmall` into one of the `StickySmallArea` areas. It snaps into the same place in both games and stays there, the area's attach callback runs once in each game, and carrying it through the area without letting it attach in the Holder's game never attaches it in the other game. Then the other player grabs it out: it detaches in both games and follows that player's hands.
14. **Locked out.** One player walks through a door and locks it from the other side, by a script or a level whose door locks behind the player. The other player types `/tp-to-peer`. They see `Teleported to the other player.` and stand where the first player stands, facing the same way and crouching if that player crouches. The first player sees their Avatar snap there rather than glide through the door. With the first player on another map, `/tp-to-peer` instead shows `The other player is on a different map.` and nothing moves.

On 2026-10-11 the chapter 1 playtest passed. Two Custom Stories played afterwards, My Lady's Insanity and Quiet, were each ended by a rate-limit disconnect. See the contact Claim loop under [Expected behavior and limitations](#expected-behavior-and-limitations).

On 2026-10-15 the pickup, combine, and flashback steps (2–7 and 12) passed on two PCs, along with the earlier steps. Rate-limit disconnects while players touched rubble came more often than before; they are the same contact Claim loop, not yet addressed.

## Expected behavior and limitations

Two players host, join, chat, start the same Custom Story together, see each other move, share how they move props, doors, levers, valves, and sliders, and face the same Grunts and Brutes as Shared Enemies. What that does *not* cover:

- **Only Custom Story starts are shared.** In the main game, each player starts or loads it themselves (#23). Once both are on the same map, the Shared Pose and Holds work there as in a Custom Story.
- **Map changes and returning to the main menu are not shared.** Only the Session Host's fresh Custom Story start is. Continue, Load Game, death reloads, quitting, and every map transition after the start are local.
- **Changing game settings ends the Multiplayer Session for that player.** Applying settings restarts the game's Game Interaction Protocol server (amnesia-tdd-tcp#66), so the Game Peer loses its local game and has to rejoin. Pausing through the menu or a note does not.
- **An Avatar stays frozen at its last Pose when its player returns to the main menu.** The game sends no Poses from the main menu and reports no map-left event, so the other player keeps seeing a motionless Avatar where that player last stood. Start the Custom Story again to resynchronize.
- **`/tp-to-peer` does not follow the other player to another map.** It places you only on the map you are both on. A player left behind on another map has to reach it themselves.
- **A Shared Enemy may visibly jump when a player arrives on or leaves its map mid-chase.** A Shared Enemy is shared only while both players are on its map (ADR 0009). When the Joining Player arrives, or the Session Host arrives on the Joining Player's map, the Joining Player's own enemy is replaced by the Session Host's and jumps to wherever that one is. When either player leaves, or the Session Host leaves the Multiplayer Session, the Joining Player's enemy regains its own AI from the newest Enemy Pose, slightly ahead of where it was shown, and the Session Host's enemy carries on undisturbed. The two games' enemies then go their own way until the players share the map again.
- **Shared Enemies see the Joining Player but do not hear them.** With a game build supporting `avatarenemysight` and `enemyattack`, the Session Host's enemies see and attack both players while they share a map and keep their current target until losing sight, and the doors they break and the callbacks they trigger are shared. Avatars remain silent. Grunts and Brutes are Shared Enemies: the game loads a Brute as a Grunt with its own model, sounds, and attacks. Kaernks stay separate in each game until amnesia-tdd-tcp#103. A custom enemy is shared only if its entity file's `EntityType` is `Enemy_Grunt`. An enemy-triggered callback or door break while the players are on different maps happens only in the game where it happened.
- **Hallucinations vanish only for the Session Host's player.** A hallucination enemy dissolves when the Session Host's player comes near it; the Joining Player neither triggers its disappearance nor sees the full poof (#83). A chapter 1 Shared Enemy encounter of the main game has not yet had its own walkthrough (#79).
- Avatar footstep sounds.
- **What Entity Interaction Sync does not share.** Holds share how players move map-placed props, doors, levers, valves, and sliders, and breaks of props a player Holds. These are not shared:
  - Consumable items. Tinderboxes, lamp oil, sanity potions, and laudanum are picked up and combined by each player alone, and so is any item a script or a broken prop spawns, such as a potion spilled from a jar. Non-consumable pickups and combines are Shared Callbacks.
  - Map Script callbacks outside the shared kinds. Use-item, interact, player collide and trigger-area, pickup, combine, Flashback area, and sticky area attach callbacks are Shared Callbacks (ADR 0006). Look-at, timer, checkpoint, ignite, and the other kept-local callbacks run only in the game of the player who caused them, and a script that depends on drifted script state may diverge.
  - Breaks a script causes. When a script breaks a prop, for example as a result of an interaction, it breaks only in that game (#44).
  - What enemies hear. Shared Enemies share their attacks, door breaks, and the callbacks they trigger. A prop thrown into water splashes only in the thrower's game, so the other player cannot distract a water lurker that way (amnesia-tdd-tcp#69).
  - Props moved without a player, such as scripted impulses or enemies.
  - Anything past the 32-body budget of a Holder's stream.
  - Runtime-created entities: script spawns, break debris, and contained items. Debris is cosmetic and local.
  - Brief conflicting collisions between two players' Settling props. Each screen may show its own collision before both settle where their Holders put them.
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

### Shared Enemy walkthrough

Use identical Game Peer builds (`LanProtocol` version 13) and game builds granting `enemies`, `avatars`, `localpose`, `interactions`, and `sharedcallbacks`, with support for `avatarenemysight`, `enemyattack`, `shareddeath`, `enemydoorbreak`, and `autodisabled`, on two LAN PCs. Install the current `mp-test-cs` on both PCs. This verifies Shared Enemy playback (#72), targeting the Joining Player (#73), attacks on the Joining Player (#74), Shared Deaths (#75), arriving on or leaving the map mid-chase (#76), door breaks and enemy-triggered callbacks (#77), and the Brute (#78).

1. On PC A, `/host`; on PC B, `/join <PC A's LAN address>`. Start "Amnesia Multiplayer Test" on PC A. Confirm both players are in `multiplayer-test.map` and see each other's Avatar.
2. PC A enters `SpawnGruntArea` (ID 154, centered at `-2 1.875 18.875`). It activates `grunt` (ID 139, initially at `6.25 0 28`). PC B stays near the cabinets, watching the corridor. Confirm PC B sees the same Grunt's position, turning, and animation as PC A, with a small interpolation delay.
3. Let the Grunt patrol: `PathNodeArea_6` (wait 5 s), `_13` (10 s), `_2` (5 s), repeated five times. Then let PC A enter its sight and run along the corridor. Confirm PC B sees it chase PC A. The closet `cabinet_nice_1` (ID 137, `3.25 0 12`) provides cover. Door breaks are checked in step 10.
   For #73, start a fresh encounter with PC A hidden behind the closet or a wall and PC B visible in the lit corridor with their lantern raised. Confirm the Grunt spots and chases PC B on both screens. While PC B stays visible, PC A steps into sight, closer to the Grunt: it must keep chasing PC B. PC B then hides behind the closet or a wall while PC A remains visible: after losing PC B, the Grunt must switch to PC A. Reverse the order in another fresh encounter: a Grunt chasing PC A must keep that target when PC B becomes visible, until PC A hides. Keep enough distance to avoid attacks during these checks.
4. PC B leaves the Multiplayer Session with `/leave`; confirm its enemy regains vanilla simulation. Rejoin on the same map and confirm it follows PC A again. Reload a save of this map on PC B and confirm a fresh driven copy follows the next Enemy Poses.
5. Load a different map on PC B while remaining joined. Confirm its enemies stay vanilla and no Enemy Pose from `multiplayer-test.map` drives an entity there. Return to the test map and confirm sharing resumes. Repeat with PC A changing maps while PC B stays: PC B's Grunt must regain vanilla simulation.
6. Being hit, for #74. `mp-test-cs` has no sanity drain or darkness effects, and while the room light is on (`light_lever`), each game resets its own player to full health every second, so hits can be repeated. Start a fresh encounter with PC A hidden and PC B visible, and let the Grunt reach PC B. Each swing that overlaps PC B on PC B's screen hits PC B the vanilla way: health lost, blood splash, red flash, head spin, a push away from the Grunt, and the hit sound. Damage varies between hits, because each game rolls its own. PC A sees the swing but takes no damage, and its Avatar of PC B does not react. Leave the room light on: turning it off registers step 8's checkpoint callback, and deaths are steps 7 and 8. Then dodge in another fresh encounter: PC B backs away or steps aside as the Grunt swings, and a swing that visibly misses on PC B's screen does nothing. Finally, let the Grunt reach PC A while PC B watches: PC A is hit the vanilla way, and PC B takes nothing.
7. Deaths, for #75. Start the story fresh on PC A, so the room light is off and has never been switched: the map has only the automatic `_auto` checkpoint, without a callback. Spawn the Grunt and let it kill PC B, then in another chase PC A. Each time, only the dead player respawns, after the usual death screen; the Grunt stays active on both screens, keeps hunting the survivor, and after the respawn can see, chase, and hit the respawned player again. Repeat with both players dying close together: both respawn and the Grunt stays. No death kills the other player.
8. Deaths with a checkpoint callback. Turn the room light on and then off with `light_lever` on either PC: this registers checkpoint `light_off` with callback `LightOffDeath` in both games, and turns healing off. From now on any death on this map is a Shared Death, and the callback shows the count it received by turning the room light on in red for `0`, green for `1`, and blue for `2`.
   - Let the Grunt kill PC B. PC A dies at once too, without being hit; both games show the death screen, respawn at the start, and turn the room light red. The checkpoint resets the Grunt and the callback activates it again at its start: on PC B it appears there too and follows PC A's Grunt, rather than staying frozen where it was.
   - Second death, now PC A dying first: both die, and the light turns green on both. This callback registers `light_off_again`, replacing its own checkpoint.
   - Third death, both dying close together: one Shared Death, not two. The light is green on both (the replaced checkpoint's count `1`, as vanilla), with no second death screen or second callback.
   - Fourth death: blue on both. Fifth death (count `3`): the callback changes maps, and both PCs arrive in `alt-map.map`. Nobody is left on the test map, and no delayed death kills anyone in `alt-map.map`.
   Unequal death-screen timing: a game that reaches its checkpoint before the Session Host's episode arrives keeps the death screen up to 3 seconds longer; that is expected. If one game's light shows a different color from the other's, the registrations differed (for example, one game never saw the lever turn off); that game keeps its own count and logs `Shared Death: checkpoint '...' keeps its own count`.
9. Arriving and leaving mid-chase, for #76. Start the story fresh on PC A, spawn the Grunt, and let it chase PC A, keeping clear of attacks.
   - While it chases, PC B leaves with `/leave`. Each player now has a vanilla Grunt: on PC B it stops following PC A's, regains its own AI from PC A's newest Enemy Pose, slightly ahead of where PC B last saw it, waits there, and may then notice and chase PC B. On PC A the chase goes on undisturbed.
   - While PC A is still chased, PC B rejoins with `/join <PC A's LAN address>`. PC B's own Grunt, even one chasing PC B, is taken over by PC A's: it may jump to where PC A's Grunt is, PC B's chase music and screen terror stop, and it then follows PC A's Grunt with the usual small delay. PC B can again be seen, chased, and hit by it.
   - While the chase goes on, PC B loads a save of a different map, as in step 5. PC A's Grunt carries on vanilla and stops pursuing PC B's Avatar. PC B then loads a save of `multiplayer-test.map` made earlier, with the Grunt not yet spawned or elsewhere: once PC B is back on the map, its Grunt jumps to PC A's and follows it.
   - While PC A's Grunt chases PC B, PC A ends the Multiplayer Session with `/leave`. PC B's Grunt regains its own AI from the newest Enemy Pose, slightly ahead of where it was shown, waits there, and may then notice and chase PC B again the vanilla way. PC A keeps its own vanilla Grunt.
10. Door breaks and enemy-triggered callbacks, for #77. `mp-test-cs` prints a `TEST77:` line to each game's `hpl.log` (see [Reporting a problem](#reporting-a-problem)) for each enemy-triggered callback: the Grunt walking into `SpawnGruntArea`, breaking the door `mansion_1` (ID 136, `-2 0 14`), which also shakes the screen briefly, and disabling itself at the end of its patrol. Each check below expects the same lines on both PCs, each once per event. Start the story fresh on PC A for each part, with `mansion_1` closed.
   - The Grunt breaks the door. PC A spawns the Grunt, and both players let it chase them through `mansion_1`, closing the door behind them, until it reaches the closed door. The door bursts on both screens at the same moment, with the break sound and a brief screen shake, and both logs get one `TEST77: mansion_1 broke`. The debris falls differently on each PC; that is expected. The doorway stays open on both PCs, and neither player can grab the door again.
   - The Grunt breaks a door a player Holds. PC B grabs `mansion_1` and holds it shut as the Grunt arrives. The door breaks on both screens and PC B's grab ends. Neither Game Peer log has a `HoldCommandFailed` warning; an `entitybreak not-found` line logged as `HoldEnded` is expected. Repeat in a fresh start with PC A holding the door.
   - Trigger areas. While the Grunt patrols, both players stay hidden, out of its sight. Each time it walks into `SpawnGruntArea`, both logs get one `TEST77: grunt entered SpawnGruntArea`, so both PCs end with the same count.
   - Auto-disable. Spawn the Grunt, then both players walk back to the start room, more than 10 m from it and out of sight, and let it finish its five patrol rounds (about two minutes). It disables itself, vanishing on both screens at once, and both logs get one `TEST77: grunt auto-disabled`. On PC B it does not reappear later.
11. The Brute, for #78. Start the story fresh on PC A, and before entering `SpawnGruntArea`, either player interacts with the cabinet `cabinet_simple_1` (ID 138, `3.25 0 9.75`), for example by grabbing a door. It activates `brute` (ID 158, initially at `2.25 0 28.25`) in both games, and `SpawnGruntArea` no longer spawns the Grunt (spawning the Grunt first likewise leaves the cabinet inert). The Brute patrols the Grunt's patrol nodes in reverse order: `PathNodeArea_2` (wait 5 s), `_13` (10 s), `_6` (5 s), repeated five times. Repeat with the Brute:
   - Steps 2 and 3: PC B sees the same Brute's position, turning, and animation as PC A, it chases either player, and it keeps its target and switches only after losing that player.
   - Step 6: its swings hit only the player they overlap on that player's own screen, with the Brute's hit sound.
   - Steps 7 and 8: without a callback only the dead player respawns and the Brute keeps hunting; after `light_lever` registers `light_off`, a death is a Shared Death, the light shows the same color on both PCs, and the callback reactivates the Brute, not the Grunt, which PC B's copy then follows.
   - Step 10: it breaks `mansion_1` on both screens, and if both players stay out of its sight and more than 10 m away for its five patrol rounds, it disables itself on both screens, and both logs get one `TEST78: brute auto-disabled`.
   For a custom enemy, record its entity file and the `EntityType` of its `<UserDefinedVariables>` element in the report: only `Enemy_Grunt` enemies are Shared Enemies.

Enemy Poses have a separate Inbound Allowance with headroom for 64 active enemies at up to 60 reports per second each and a one-second burst (the fixture uses one at 30 Hz). Custom maps beyond that envelope need a larger allowance. Every enemy has its own newest-only outbound and local playback slot; it cannot replace another enemy's pending sample. An enemy attack is not a sample: it rides the ordered lane under the session message allowance, so none is dropped or replaced, and neither is a door break, and the Joining Player's game decides each hit from the copy as it shows it, about 100 ms behind the Session Host. A death follows ADR 0009's checkpoint rule. The game decides from its own checkpoint whether a death preserves Shared Enemies or is a Shared Death. A Joining Player's Game Peer reports its own callback deaths; the Session Host's Game Peer decides each Shared Death, one at a time until its own checkpoint completes it, and stamps it with its own game's checkpoint count, so both callbacks receive the same count and both games store the same next count. A death that happens during an episode, before the episode reaches that player's game, joins it, so simultaneous deaths are one Shared Death. A Joining Player's death after its episode's checkpoint, while the Session Host is still dying, waits for the Session Host's checkpoint and becomes the next episode. The Session Host relays no Enemy Poses or attacks from an episode until its checkpoint resets its enemies, but it still relays door breaks, because a checkpoint does not mend a broken door, and the Joining Player plays none until its own checkpoint has reset the copies, then releases them, so the next Enemy Pose drives a fresh, active copy. Whether a death is shared depends on the dying player's own checkpoint. A death with a callback kills everyone on the map even when another game's registration differs, so nobody is stranded, and each game whose checkpoint is not the Session Host's keeps its own count. A death in a game without a callback kills nobody else, even when the other game has one. A player on another map is never killed: episodes name their map, and both Game Peers and the game reject one for another map. The map path is the only Map Visit identity, so an episode still in flight when its map reloads, by a save load or a change to the same map, applies to the reloaded visit. A Joining Player's death before the Session Host's game reported its checkpoint is left to the Joining Player's game; the game reports one whenever a Map Visit starts. Hard Mode, which reloads the latest save, and permanent death are outside the Shared Death guarantee. Map presence is learned from player Poses; a remote return to the main menu sends no Map Visit or Pose in the current game protocol, so its departure from the map cannot yet be detected until it reports another map or leaves the Multiplayer Session. Local `mapentered` clears copies even for a reload of the same map.

On 2026-10-17 the maintainer reported that the #73 two-PC chase and sticky-target checks passed without glitches, with target switching feeling very good. Automated checks verify host-only Avatar sight opt-in, map separation and return, departure and rejoin, and Capability gating.

On 2026-10-17 the maintainer reported that the #74 two-PC checks passed: the Grunt hit PC B and PC A with the vanilla effects, damage worked well, and it killed PC B. After PC B's death the Grunt stayed active and kept hunting PC B at the respawn point, as this test map intends. After PC A's death the Grunt was cleared on PC A and PC B's copy stayed frozen until the Multiplayer Session ended, when PC B's game took the Grunt back and it moved again. ADR 0009's death rule will change what a death does to Shared Enemies. Automated checks verify the attack message's validation, host-only ordered sending, map gating, and attack ordering after Enemy Poses.

On 2026-10-17 the maintainer reported that the #75 two-PC checks passed: deaths without a callback and Shared Deaths with one both worked as described. Longer play may still turn up smaller issues. Automated checks verify the death messages' validation and direction, episode numbering and coalescing, the Session Host's count, the pause in Enemy Poses during an episode, and copy reacquisition after the checkpoint.

On 2026-10-17 the maintainer reported that the #76 two-PC checks passed: leaving and rejoining mid-chase, and the Session Host leaving mid-chase, handed the Grunt over and back as described. Automated checks verify driving from the first Enemy Poses when either player arrives on the other's map, the Session Host sending Enemy Poses only while both share the map, and release when the Session Host leaves the map or the Multiplayer Session.

On 2026-10-17 the maintainer reported that the #77 two-PC checks passed: the door the Grunt broke was broken for both players, and the encounter's callbacks ran once in each game. Smaller issues will be reported separately. Automated checks verify the door break message's validation, host-only ordered sending, map gating, relaying during a Shared Death episode, the `autodisabled` kind and its Session Host-only sending, and that a Holder's break of a door already broken is not logged as a failure.

On 2026-10-17 the maintainer reported that a streamed two-PC session went very well, with Shared Enemies working in Custom Stories as well as the main game. Brutes were met in "Dark Saloon" (`servant_brute`) and in "Spelos' Spookfest" (`3040083107`, whose `entities/XDJ_enemies/brute/brute_nomusic.ent` has `EntityType` `Enemy_Grunt`), and behaved like the Grunt. The Session Host's game crashed once in `04_wine_cellar_lab.map`, when the Joining Player used the cuprite while the Session Host had their own selected for use; that is amnesia-tdd-tcp#104, not a Shared Enemy issue. The Kaernk is still to come (amnesia-tdd-tcp#103).
