# Prior art for one AI enemy shared between co-op players

Research for [#52](https://github.com/amnesia-spelos/amnesia-multiplayer/issues/52), under the Shared Enemies
map [#51](https://github.com/amnesia-spelos/amnesia-multiplayer/issues/51).

Primary sources where they exist: shipped game code (decompiled `Assembly-CSharp` dumps published by modders),
HPL2 mod source, engine documentation, and developer talks. Each claim names its source. Paths beginning
`src/amnesia/` are in `../amnesia-tdd-tcp` at `a6dcdbd`. Terms follow `CONTEXT.md` (**Shared Enemy**,
**Session Host**, **Joining Player**, **Game Peer**, **Multiplayer Relay**, Avatar, Hold).

Decompiled game code is a third party's dump of a commercial build, not the developer's repository. It shows
what the shipped game does, but line numbers drift between game versions, so each link is pinned to a commit
of the dump.

---

## Answer

Two models recur, and shipped co-op horror games mix them rather than pick one:

1. **Host-authoritative AI, interpolated copies elsewhere.** One machine runs perception, pathing, and the
   state machine; every other machine shows a smoothed copy. Unreal Engine, Source, R.E.P.O., and
   Halo/Destiny-era Bungie titles work this way. The attacked non-host player sees the enemy late by the
   transit time plus their interpolation delay, and the host's AI chases a late copy of that player.
2. **Per-enemy authority handed to the targeted player.** Lethal Company moves network ownership of the enemy
   to the player it chases (`ChangeOwnershipOfEnemy`). That player's machine then runs the AI against their
   real, current position, so the chase and the hit are exact for the one who is in danger. Everyone else,
   including the host, sees the late copy.

In both models the hit is decided in the victim's own game: Lethal Company and R.E.P.O. ignore enemy
collisions with anyone but the local player, apply damage there, and only then tell the others. Death is
per-player with spectating in both. The one HPL2 co-op mod (a split-screen fork, one process) already solved
**target selection** for vanilla Amnesia enemies with two players, and chose **shared death**: either player
dying is game over for both. No networked Amnesia or HPL2 mod with enemies was found.

---

## 1. Host-authoritative AI with interpolated copies

### What it is

| Source | What it shows |
| --- | --- |
| Unreal Engine: AI controllers exist only on the server or listen server. A client holds only its own `PlayerController`, so AI logic runs on the host and clients see replicated pawns. Community answers, consistent with the engine's design: [answers.unrealengine.com/questions/248934](https://answers.unrealengine.com/questions/248934/view.html), [237668](https://answers.unrealengine.com/questions/237668/view.html); issue [UE-209550](https://issues.unrealengine.com/issue/UE-209550) records that "on remote clients (NM_Client), the AIController is never resolvable". | The default in a mainstream engine. |
| Valve Source engine: the server simulates every entity, including NPCs. Clients render other entities through an interpolation buffer, 100 ms by default, and the server lag-compensates players' shots by rewinding *players*. [Source Multiplayer Networking](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking). *This page sat behind a bot wall during this research, so these figures come from the page as previously known and were not re-read.* | The latency structure: a client always sees the world in the past. |
| R.E.P.O. (Photon PUN, 2025): `Enemy.Start` sets `MasterClient = true` only on the Photon master client ([`Enemy.cs:96-103`](https://github.com/Laamy/ClydeMenu/blob/3bbf20895c62e17894d2f46a7f07523fcfdd030d/ClydeMenu/dlls/REPO_Dump/Assembly-CSharp/Enemy.cs#L96-L103)). Vision bails out on non-master clients ([`EnemyVision.cs:54`](https://github.com/Laamy/ClydeMenu/blob/3bbf20895c62e17894d2f46a7f07523fcfdd030d/ClydeMenu/dlls/REPO_Dump/Assembly-CSharp/EnemyVision.cs#L54)), and the NavMesh agent is enabled only on the master ([`EnemyNavMeshAgent.cs:18-24`](https://github.com/Laamy/ClydeMenu/blob/3bbf20895c62e17894d2f46a7f07523fcfdd030d/ClydeMenu/dlls/REPO_Dump/Assembly-CSharp/EnemyNavMeshAgent.cs#L18-L24)). | A co-op horror game with a host-run enemy whose perception and pathing exist on one machine only. |
| Bungie: *I Shot You First: Networking the Gameplay of Halo: Reach* (David Aldridge, GDC 2011, [vault](https://gdcvault.com/play/1014345/I-Shot-You-First-Networking)) and *Shared World Shooter: Destiny's Networked Mission Architecture* (Justin Truman, GDC 2015, [vault](https://gdcvault.com/play/1022247/Shared-World-Shooter-Destiny)). The Destiny abstract covers "rules for host handoff at regional boundaries" and "ungraceful host migrations". | Console co-op with a player-hosted AI simulation and host migration. The talk content is members-only, so no detail is claimed here. |

### Latency for the player being attacked

- **Session Host attacked:** exact. The AI and the victim are in the same process.
- **Joining Player attacked:** two delays stack. (a) The host's AI perceives, paths to, and swings at the
  Joining Player's *Avatar*, which is the Joining Player's Pose from one transit plus the render delay ago. In
  this project that delay is `kRenderDelayMs = 100.0` (`src/amnesia/src/game/AvatarPoseModel.cpp:50`). (b) The
  Joining Player sees the enemy's state one transit plus their own interpolation late. The result is that the
  enemy hits where the Joining Player *was* (sidestepping does not save them), or misses where they *are*, and
  the swing arrives late on their screen. On a LAN the transit is a few milliseconds, so the render delay
  dominates.
- What the industry adds to mask this is shooter-side lag compensation for players' *shots*. It does nothing
  for an NPC's melee against a remote player, which is the case here.

---

## 2. Per-enemy authority that hands off to the targeted player

### Lethal Company (Unity Netcode for GameObjects, 2023)

This is the clearest shipped example, and it is a co-op horror game. All citations are to
[`Timo576/Lethal_company_data@1023e88`](https://github.com/Timo576/Lethal_company_data/tree/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp)
(a decompile, early 2024).

| Mechanism | Source |
| --- | --- |
| **The owner runs the AI; everyone else smooths toward it.** Non-owners stop any search, set `SetClientCalculatingAI(false)`, and `SmoothDamp` the transform toward `serverPosition` with `syncMovementSpeed = 0.22f`. The owner runs `DoAIInterval` every `AIIntervalTime = 0.2f` s. | [`EnemyAI.cs:891-946`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/EnemyAI.cs#L891-L946); fields `:36`, `:69` |
| **Position sync is sparse and reliable.** It is sent only when the enemy has moved more than `updatePositionThreshold = 1f` m since the last send, as a reliable RPC from the owner (owner to server to clients). | [`EnemyAI.cs:314-323`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/EnemyAI.cs#L314-L323), `:61`, `:1190-1200` |
| **Ownership moves to the target.** `ChangeOwnershipOfEnemy(newOwnerClientId)` calls `NetworkObject.ChangeOwnership` on the server, or asks the server by RPC. | [`EnemyAI.cs:1215-1245`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/EnemyAI.cs#L1215-L1245) |
| **Detection happens in the noticed player's own game.** The Thumper (`CrawlerAI`) runs its line-of-sight check on each client. When the client that sees it is the local player, it raises a timer, then sends `BeginChasingPlayerServerRpc` and takes ownership itself. | [`CrawlerAI.cs:141-150`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/CrawlerAI.cs#L141-L150) |
| **Switching targets mid-chase hands ownership on.** When the chasing owner sees a different player, it sets `targetPlayer` and calls `ChangeOwnershipOfEnemy(targetPlayer.actualClientId)`. The same happens on re-acquiring a lost target. | [`CrawlerAI.cs:186-216`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/CrawlerAI.cs#L186-L216); Bracken [`FlowermanAI.cs:78`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/FlowermanAI.cs#L78), `:250`; Jester [`JesterAI.cs:87`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/JesterAI.cs#L87) |
| **Hits are decided by the victim.** `MeetsStandardPlayerCollisionConditions` returns null unless the collider belongs to `localPlayerController`. Each game therefore only reports the enemy touching *its own* player, against *its own* view of the enemy. | [`EnemyAI.cs:157-174`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/EnemyAI.cs#L157-L174) |
| **Damage is applied locally, then announced.** `CrawlerAI.OnCollideWithPlayer` calls `DamagePlayer(40)` on the local player, then `HitPlayerServerRpc` for the shared effects. `DamagePlayer` and `KillPlayer` return early unless `IsOwner` of the *player*. | [`CrawlerAI.cs:434-447`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/CrawlerAI.cs#L434-L447); [`PlayerControllerB.cs:556-566`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/GameNetcodeStuff/PlayerControllerB.cs#L556-L566), `:4088-4095` |
| **Exclusive kill animations are arbitrated by the server.** The Bracken's victim requests `KillPlayerAnimationServerRpc`. The server grants it unless the enemy is already in a kill animation or carrying a body, and otherwise answers `CancelKillAnimationClientRpc`. This is the same "first claim wins, loser is told" shape as this project's Claims. | [`FlowermanAI.cs:494-516`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/FlowermanAI.cs#L494-L516) |
| **One player dying.** The server decrements `livingPlayers`. Only at zero does it set `allPlayersDead` and end the round (`ShipLeaveAutomatically`). Dead players spectate. A mid-round disconnect counts the same way. | [`PlayerControllerB.cs:4170-4175`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/GameNetcodeStuff/PlayerControllerB.cs#L4170-L4175); [`StartOfRound.cs:1016-1032`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/StartOfRound.cs#L1016-L1032) |

### Engine support for it

Unity Netcode's *distributed authority* topology makes this a first-class feature. A `NetworkObject` can be
`Transferable` (ownership passes immediately unless locked) or `RequestRequired` (it must be requested and is
locked after transfer). When an owner leaves, "the ownership and last known state of the subset of objects
owned by the leaving player is transferred" to the remaining clients
([Ownership and authority](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/basics/ownership.html);
[Distributed authority](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.4/manual/terms-concepts/distributed-authority.html)).

Glenn Fiedler's networked-physics scheme for Oculus is the physics analogue, and it is what ADR 0004's Holds
already resemble. Objects have default authority. Interacting takes authority, authority spreads to bodies a
held object touches, and authority returns to default at rest. The host arbitrates with per-object authority
and ownership sequence numbers, and "ownership is stronger than authority"
([Networked Physics in Virtual Reality](https://gafferongames.com/post/networked_physics_in_virtual_reality/)).

### Latency for the player being attacked

- **The targeted player: exact.** Perception, pathing, the attack swing, and the hit test all run against their
  real, current position, in the same game that draws their screen. Juking works the way vanilla
  Amnesia taught them.
- **Everyone else sees the chase late**, by one transit plus the smoothing. That is harmless because they are
  not the one being hit.
- **Costs.**
  - The handoff transfers AI state, not just position. Lethal Company gets this almost free because Netcode
    replicates `currentBehaviourStateIndex` and the subclasses keep little hidden state. The new owner rebuilds
    the rest (`SetClientCalculatingAI(true)`, a fresh search).
  - A brief double-owner or no-owner window at the switch.
  - Hysteresis is needed so that two players near each other do not make ownership ping-pong.
  - The enemy's position snaps when the new owner's local copy, which was only an approximation, becomes the
    truth.

---

## 3. Target selection, hits on remote players, and one player dying

| Concern | Lethal Company | R.E.P.O. | HPL2 local co-op mod |
| --- | --- | --- | --- |
| Who picks the target | The owner. The first detection is made by the noticed player's own client ([`CrawlerAI.cs:141-150`](https://github.com/Timo576/Lethal_company_data/blob/1023e88988e11ac4730f5ea5e54f57a50d49c30e/Assembly-CSharp/CrawlerAI.cs#L141-L150)). | Master client only ([`EnemyVision.cs:54`](https://github.com/Laamy/ClydeMenu/blob/3bbf20895c62e17894d2f46a7f07523fcfdd030d/ClydeMenu/dlls/REPO_Dump/Assembly-CSharp/EnemyVision.cs#L54)). | The single process; rule below. |
| Hit on a non-simulating player | Victim-side: only the local player's collider counts. | Victim-side: `HurtCollider.PlayerHurt` returns unless `_player.photonView.IsMine` ([`HurtCollider.cs:382-395`](https://github.com/Laamy/ClydeMenu/blob/3bbf20895c62e17894d2f46a7f07523fcfdd030d/ClydeMenu/dlls/REPO_Dump/Assembly-CSharp/HurtCollider.cs#L382-L395)). Remote damage goes through `HurtOtherRPC`, which the owner applies only if the hurt position is within 2 m of where it really is ([`PlayerHealth.cs:367-395`](https://github.com/Laamy/ClydeMenu/blob/3bbf20895c62e17894d2f46a7f07523fcfdd030d/ClydeMenu/dlls/REPO_Dump/Assembly-CSharp/PlayerHealth.cs#L367-L395)). | N/A (one process). |
| One player dies | Spectate; the round ends at zero living. | Per-player (not traced further here). | **Shared death**: game over for both. |

**R.E.P.O. is a useful hybrid.** The enemy is host-run (model 1), but the *hit* is tested by the victim's own
game against its interpolated copy of the enemy. It favours the victim's view of the attack without moving
the AI. The 2 m sanity check on `HurtOtherRPC` shows how a remotely decided hit can be bounded by the
victim's real position.

---

## 4. Amnesia and HPL2 prior art

### TheBorn's Local Co-op Mod (split-screen, one process)

[`TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod@728fa81`](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/tree/728fa81450961f7cfc9b2904afeea3f917cf0bb6)
is a split-screen fork of the same `iLuxEnemy` code this project modifies. It is not networked, so it has no
authority question. It is direct prior art for **vanilla-first targeting with two players**, which is the
map's rule.

- **One target pointer per enemy.** Every `gpBase->mpPlayer` use in `iLuxEnemy` became `GetTargetPlayer()`.
  When co-op is off or Player 2 is missing, the target falls back to Player 1
  ([`LuxEnemy.cpp:2424-2441`](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/blob/728fa81450961f7cfc9b2904afeea3f917cf0bb6/amnesia/src/game/LuxEnemy.cpp#L2424-L2441)).
  Vanilla's equivalents are the many `gpBase->mpPlayer` reads in
  `src/amnesia/src/game/LuxEnemy.cpp` (for example `:730`, `:1381`, `:2117-2143`).
- **The target selection rule** (`UpdateTargetPlayer`, run each sense update before sight and detection,
  [`:2491-2566`](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/blob/728fa81450961f7cfc9b2904afeea3f917cf0bb6/amnesia/src/game/LuxEnemy.cpp#L2491-L2566),
  called at [`:710`](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/blob/728fa81450961f7cfc9b2904afeea3f917cf0bb6/amnesia/src/game/LuxEnemy.cpp#L710)):
  - Dead players are never targets.
  - Re-evaluation happens every 0.75 s, "switching every tick would make the enemy dither".
  - When both players can be detected (vanilla see-range, with the lantern, darkness, and crouch rules
    applied per player, then line of sight), the closer one wins if it is under 60 % of the other's distance.
    Otherwise the pick is random on a fresh detection and sticky afterwards.
  - When exactly one can be detected, that one is the target.
  - When neither can be detected: while Idle, Wait, Patrol, or GoHome the target is the nearest player.
    While alerted or hunting, the enemy stays on the current target's trail.
  - On a switch, the old target's terror (heartbeat) contribution is removed and the last known position is
    refreshed to the new target.
- **Perception by any player.** "Seen" checks, the visibility checks that drive the "seen" scares, and
  dispelling a hallucination count *either* player
  ([`:2960-3020`](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/blob/728fa81450961f7cfc9b2904afeea3f917cf0bb6/amnesia/src/game/LuxEnemy.cpp#L2960-L3020),
  [`:2362-2366`](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/blob/728fa81450961f7cfc9b2904afeea3f917cf0bb6/amnesia/src/game/LuxEnemy.cpp#L2362-L2366)).
  Terror and music follow the target only.
- **Shared death.** `cLuxPlayerDeath::Start` redirects Player 2's death into Player 1's, "one player going down
  is game over for both", which runs one fade-and-checkpoint sequence for both
  ([`LuxPlayerHelpers.cpp:2746-2790`](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/blob/728fa81450961f7cfc9b2904afeea3f917cf0bb6/amnesia/src/game/LuxPlayerHelpers.cpp#L2746-L2790)).
  The comments there document a recursion freeze caused by the script "act as" player swap. That is worth
  knowing because Shared Callbacks replay script on both games too.
- The README warns that the game and every custom story "were written for exactly one player"
  ([README](https://github.com/TheBorn/Amnesia-The-Dark-Descent---Local-Co-op-Mod/blob/728fa81450961f7cfc9b2904afeea3f917cf0bb6/README.md)).

### Others

- No networked Amnesia, Penumbra, or HPL2 mod with shared enemies was found. GitHub repository searches for
  "amnesia multiplayer", "amnesia coop", "hpl2 multiplayer", and "penumbra multiplayer" return only this
  repository.
- A 2010 ModDB download titled "Multiplayer mod for Amnesia TDD"
  ([ModDB](https://www.moddb.com/games/amnesia-the-dark-descent/downloads/multiplayer-mod-for-amnesia-tdd))
  exists, but the page refused automated access. Its approach, and whether it touched enemies at all, is
  unverified.

---

## 5. What this means for the Shared Enemies spec

These are observations for the grilling tickets, not decisions.

- **Vanilla attacks hit only `gpBase->mpPlayer`.** `iLuxEnemy::Attack` calls `cLuxMapHelper::ShapeDamage` with
  `abCheckPlayer`, which tests overlap against the local player's character body
  (`src/amnesia/src/game/LuxEnemy.cpp:1786-1800`; `LuxMapHelper.cpp:265`, player check about 30 lines in).
  In the game that runs the enemy, the other player is an Avatar, which `ShapeDamage` does not know about.
  - Model 1 needs the Avatar to become something the enemy can see, reach, and hit, plus a relayed damage
    message.
  - Model 2 needs no hit relay *for the target*, because the target's own game runs the attack against the
    real player. It needs the Avatar only as a *candidate* for target switching.
  - R.E.P.O.'s hybrid (host AI, victim-side hit test) needs the victim's game to run the attack shape against
    its approximate copy of the enemy.
- **The 100 ms Avatar render delay is the dominant latency on a LAN**, and it lands entirely on the
  non-simulating player in model 1. Model 2 moves it onto the spectator, who is not in danger.
- **Model 2 fits ADR 0004's grain.** Holds already are "per-entity authority that moves to the interacting
  player, ordered by the Multiplayer Relay, with Claim Denials for the loser". An enemy owner change could be
  a Claim, with the Relay as the single ordering point, and "first claim wins" mirrors the Bracken's
  kill-animation arbitration.
  - The open cost is transferring the enemy's state machine on handoff.
  - HPL2 already serializes it for save games: `iLuxEnemy_SaveData`, `cLuxEnemyMover_SaveData`,
    `cLuxEnemyPathfinder_SaveData`, `cLuxEnemyPatrolNode_SaveData`, and `cLuxStateMessage_SaveData`
    (`src/amnesia/src/game/LuxEnemy.h:48-117`). That is a ready inventory of what a handoff would carry. It
    is not a ready wire format, given the trusted-LAN rule that messages carry only bounded numbers and
    integer IDs.
- **The non-owning game's copy must not think.** Lethal Company disables AI calculation on non-owners. Here the
  equivalent is driving the enemy like a Peer-Driven Entity, so that the copy plays animations and sounds but
  does not perceive, path, or attack.
- **Target selection already has a vanilla-first answer.** TheBorn's rule uses vanilla detection evaluated per
  player, a 0.75 s retarget interval, closest-under-60 %, sticky while hunting, and terror following the
  target. It satisfies "Targeting is vanilla first" without designing a threat model. Under model 2 it would
  run in the owning game, with the other player represented by the Avatar's (delayed) Pose.
- **One player dying has two precedents.**
  - Shared death (TheBorn), which suits Amnesia's checkpoint design.
  - Per-player death with spectating until everyone is down (Lethal Company), which suits a run-based game.
  - Amnesia's death is a checkpoint reload driven by map script, so per-player death would need an answer for
    what the dead player's game does while the other continues.
