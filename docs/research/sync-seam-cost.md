# The sync seam and the cost of an interaction message

Research for [#27](https://github.com/amnesia-spelos/amnesia-multiplayer/issues/27), under the Codename Grasp
map [#26](https://github.com/amnesia-spelos/amnesia-multiplayer/issues/26).

Primary sources only: the two source trees, their ADRs, and the Protocol Version 2 contract README.
Paths beginning `src/Multimnesia.*` are in this repository; paths beginning `src/amnesia/` or `tests/` are in
`../amnesia-tdd-tcp`. Terms follow `CONTEXT.md` in both repositories.

---

## 1. How a Pose travels end to end

Two hops, each with its own wire language. A Pose crosses four processes: the sending player's game, the
sending Game Peer, the receiving Game Peer, the receiving player's game.

### Hop A1 — the local game publishes the Pose (Game Interaction Protocol, loopback)

| Step | Where |
| --- | --- |
| Game Peer subscribes: `localpose subscribe 30` | `src/Multimnesia.Client/GameInteractionProtocol.cs:141`, issued from `SharedPose.WorkAsync` at `src/Multimnesia.Client/SharedPose.cs:127` (`LocalPoseRateHz = 30`, `SharedPose.cs:11`) |
| The gateway clamps the rate to 1–60 Hz and restarts the subscription | `src/amnesia/src/game/GameInteractionGateway.cpp:192-217` (`kMinimumLocalPoseRate`/`kMaximumLocalPoseRate`, `:100-101`) |
| Each game update samples the local Pose into `cGameInteractionPose` | `SampleLocalPose`, `src/amnesia/src/game/GameInteractionGateway.cpp:221-250`; struct at `GameInteractionGateway.h:85-101` |
| The Pose is serialized to `STATE localpose <timeMs> <teleportCounter> <x> <y> <z> <yaw> <pitch> <crouch> <lantern> <map>` | `cGameInteractionProtocolVersion2::SerializeLocalPose`, `src/amnesia/src/game/GameInteractionProtocolVersion2.cpp:364-372` |
| Delivery, once nothing else is pending | `cImplementation::DeliverLocalPose`, `src/amnesia/src/game/GameInteractionGateway.cpp:503-511` |

### Hop A2 — the Game Peer parses and forwards it

| Step | Where |
| --- | --- |
| Line read and classified as `GameEvent.LocalPoseReported` | `GameInteractionProtocol.ParseEvent`, `src/Multimnesia.Client/GameInteractionProtocol.cs:79-80`; parse at `:93-109` using `ProtocolVersion2Line.TrySplit` (`src/Multimnesia.Client/ProtocolVersion2.cs:36-58`) |
| Carried as `LocalPose` | `src/Multimnesia.Client/ProtocolVersion2.cs:27-28` |
| Dispatched | `src/Multimnesia.Client/LocalGameSession.cs:161` → `SharedPose.HandleLocalPose`, `src/Multimnesia.Client/SharedPose.cs:60-64` |
| Converted to the LAN message `LanMessage.Pose` and offered | `SharedPose.cs:63` → `TcpSessionOperations.SendPose`, `src/Multimnesia.Client/TcpSessionOperations.cs:216-223` → `LanOutbound.OfferPose`, `src/Multimnesia.Client/LanOutbound.cs:36-40` |

### Hop B1 — `LanProtocol` across the LAN

| Step | Where |
| --- | --- |
| `LanMessage.Pose(TimeMs, TeleportCounter, X, Y, Z, Yaw, Pitch, Crouch, Lantern, Map)` | `src/Multimnesia.Contracts/LanProtocol.cs:18-19` |
| Serialized as a 4-byte big-endian length prefix plus UTF-8 JSON, `type: "pose"` | `LanProtocol.WriteAsync`, `src/Multimnesia.Contracts/LanProtocol.cs:36-79` (`MaximumFrameBytes = 4096`, `:29`) |
| Written by the single outbound writer | `TcpSessionOperations.SendOutboundAsync`, `src/Multimnesia.Client/TcpSessionOperations.cs` (`StartOutbound` `:403-409`, `SendOutboundAsync` `:411-423`) |
| Read, structurally validated, rate-checked | `LanProtocol.ReadAsync` → `ReadPose`, `src/Multimnesia.Contracts/LanProtocol.cs:81-118`, `:184-198`; receive loop `TcpSessionOperations.ReceiveChatAsync` |

### Hop B2 — the receiving Game Peer formats `avatarpose` for its own game

| Step | Where |
| --- | --- |
| Inbound Pose handed to the Shared Pose | `src/Multimnesia.Client/LocalGameSession.cs:129-132` → `SharedPose.HandleReceivedPose`, `src/Multimnesia.Client/SharedPose.cs:67-71` |
| The **receiving Game Peer**, not the wire message, composes the line | `GameInteractionProtocol.AvatarPose`, `src/Multimnesia.Client/GameInteractionProtocol.cs:128-139`, called from `SharedPose.cs:135` |
| Avatar lifecycle around it: `avatarcreate partner` / `avatarremove partner` | `GameInteractionProtocol.cs:123-125`, driven by `SharedPose.WorkAsync`, `SharedPose.cs:116-131`; `AvatarIdentifier = "partner"`, `SharedPose.cs:10` |
| The game parses the line into `cGameInteractionPose` | `ParseAvatarCommand`, `src/amnesia/src/game/GameInteractionProtocolVersion2.cpp:186-212` |
| Buffered and rendered 100 ms in the past | `cAvatarPoseModel::AddPose` / `::Sample`, `src/amnesia/src/game/AvatarPoseModel.cpp:55-113`; `kRenderDelayMs = 100.0`, `:48` |
| Applied to the Avatar | `src/amnesia/src/game/LuxAvatarHandler.cpp:53-83` |

ADR 0003 (`docs/adr/0003-shared-pose-over-lan-protocol.md`) states this split explicitly: relaying the raw
`STATE localpose` or `avatarpose` line was *rejected* because "it would let a LAN peer choose the text a Game
Peer writes to its local game."

---

## 2. Coalescing State Updates, and a message that must be delivered exactly once

### The local Pose is coalescing at three separate places

1. **In the game.** `cLocalPoseSubscription::msUndeliveredStateUpdate` holds at most one undelivered State
   Update; a newer sample overwrites it (`src/amnesia/src/game/GameInteractionGateway.cpp:110-112`,
   written at `:247`). It is handed to the transport only when `GetPendingDeliveryByteCount() == 0`
   (`:506`). The contract states it plainly: "A newer Pose replaces any undelivered older one instead of
   queueing behind it… and is never disconnected because of State Updates"
   (`tests/game-interaction-protocol-version-2/README.md`, *Messages*). While play is suspended, an
   unchanged Pose is not resampled at all (`GameInteractionGateway.cpp:239-240`).
2. **In the sending Game Peer.** `LanOutbound` keeps a single `_pose` slot, replaced by `OfferPose`
   (`src/Multimnesia.Client/LanOutbound.cs:25`, `:36-40`), drained only when the ordered session-message
   channel is empty (`:49-58`), and **dropped entirely on shutdown** (`:48`).
3. **In the receiving Game Peer.** `SharedPose._receivedPose` is a single slot replaced by
   `HandleReceivedPose` (`src/Multimnesia.Client/SharedPose.cs:67-71`) and taken with
   `Interlocked.Exchange` (`:133`).

### What the machinery already does with something that must not be dropped

There **is** an exactly-once lane, and the Shared Custom Story Start already uses it. It is the ordered
session-message channel, not the Pose slot:

- `LanOutbound._messages` is a bounded `Channel<OutboundMessage>` (capacity `OutboundChatCapacity = 64`,
  `src/Multimnesia.Client/TcpSessionOperations.cs:20`) with `SingleReader = true`
  (`src/Multimnesia.Client/LanOutbound.cs:13-18`). `ReadAllAsync` drains **every** queued session message
  before it ever looks at the Pose slot (`:53-55`), so a discrete message is never starved or replaced by
  Pose traffic, and Pose traffic never delays it (ADR 0003: Pose sending "never delay[s] Chat Entries or
  session messages behind it").
- Delivery can be awaited: `OutboundMessage.Sent` is a `TaskCompletionSource` completed after the frame is
  written (`LanOutbound.cs:8`, `TcpSessionOperations.cs:419`). `Departure` is the only current user
  (`TcpSessionOperations.cs:195-196`).
- Overflow is **not** silent. `TryWrite` returning false is treated as a fatal peer condition: the chat path
  disposes the connection and notices "The remote Game Peer could not keep up with chat traffic"
  (`TcpSessionOperations.cs:232-239`). So the existing contract is *deliver or end the Multiplayer Session*,
  never *drop*.

On the Game Interaction Protocol hop the equivalent guarantee is structural: every inbound line is popped and
executed in order in the same game update (`src/amnesia/src/game/GameInteractionGateway.cpp:571-582`), and
Responses are flushed in that same update (`:583`). Nothing coalesces except the `localpose` State Update.
`SharedPose` also already has an application-level acknowledgement: `ping` every 10 written Poses, with a cap
of 30 unconfirmed (`SharedPose.cs:15-16`, `:136-139`, `HandlePong` at `:74-80`).

**Consequence for Grasp.** A grab/release belongs on the ordered session-message lane. A held prop's transform
belongs on a coalescing lane of its own, shaped like the Pose slot — *not* on the session-message lane,
because of the rate limit in §5. Reusing `SharedPose`'s worker for both needs care: the pose write is gated by
`CanWritePose` (`SharedPose.cs:57`, `:133`), and a discrete interaction write must not sit behind that gate.

---

## 3. Versioning: what Protocol Version 2 cost, what a Version 3 would cost

### What negotiating Version 2 actually involved

Game side, `amnesia-spelos/amnesia-tdd-tcp#29` ("Negotiate Protocol Version 2 and fix transport latency"),
under ADR 0004 (`docs/adr/0004-negotiated-protocol-version-2.md` there). The ticket's acceptance criteria were
a whole parallel protocol stack, not a number change:

- a Version 2 parser/serializer beside the legacy adapter, selected **per Session**
  (`cImplementation::ParseCommand` / `SerializeResponse`, `src/amnesia/src/game/GameInteractionGateway.cpp:486-498`);
- non-negotiating Sessions byte-for-byte unchanged, with existing contract fixtures passing;
- shared line-format helpers (space-separated fields, path last, 4-decimal "C"-locale numbers, Avatar
  Identifier validation) — `GameInteractionProtocolVersion2.cpp:246-295`;
- Capability gating with a documented rejection (`RequiredCapability` / `not-granted`,
  `GameInteractionGateway.cpp:323-359`);
- `TCP_NODELAY`, same-update flush, an inbound line cap;
- the *full* keyword set, field order and Response shapes for every message frozen in the contract README
  before the behavior tickets landed.

Game Peer side, this repository's [#18](https://github.com/amnesia-spelos/amnesia-multiplayer/issues/18)
("Negotiate Game Interaction Protocol Version 2 with the local game"): extract the local-game session out of
the entry point; make `protocol 2 avatars localpose` the first line on every connect and reconnect with
nothing written before its Response (`GameInteractionProtocol.NegotiateSharedPose`,
`src/Multimnesia.Client/GameInteractionProtocol.cs:120`, written at
`src/Multimnesia.Client/LocalGameSession.cs:144`); model every outcome in `ProtocolNegotiation`
(`src/Multimnesia.Client/ProtocolVersion2.cs:6-24`); degrade to legacy with one SYSTEM notice
(`LocalGameSession.cs:182`).

### What a Version 3 would cost — and why interactions probably do not need one

The negotiation was built so that **new Capabilities do not need a new Protocol Version**:

- `ParseNegotiation` ignores unrecognized Capability names outright
  (`src/amnesia/src/game/GameInteractionProtocolVersion2.cpp:96-114`), and the game answers with the
  *intersection* of requested and supported (`NegotiateProtocol`, `GameInteractionGateway.cpp:180-190`;
  `kSupportedCapabilities`, `:96-98`). The contract README says a Session can request none and still
  negotiate.
- The Game Peer's own check is already capability-shaped, not version-shaped: `GrantsSharedPose` tests
  `Version == 2 && Capabilities.Contains("avatars") && Capabilities.Contains("localpose")`
  (`src/Multimnesia.Client/ProtocolVersion2.cs:15-16`).

So an `interactions` Capability with new keywords is additive at Version 2. The cost is: one enum value in
`eGameInteractionCapability` (`GameInteractionGateway.h:59-64`), entries in `kCapabilityNames` and
`kCommandKeywords` (`GameInteractionProtocolVersion2.cpp:29-51`), a `RequiredCapability` case
(`GameInteractionGateway.cpp:323-335`), a `GetClassification` entry (`GameInteractionGateway.cpp:70-78`),
parse/serialize, adapter methods on `iGameInteractionGameAdapter` (`GameInteractionGateway.h:243-267`),
contract fixtures, and — Game Peer side — extending `NegotiateSharedPose` and adding a second grant predicate
beside `GrantsSharedPose`. An older game simply does not grant it and the feature degrades, exactly as
`AvatarsUnsupportedNotice` does today.

A genuine **Version 3** would cost far more and buys nothing here: `NegotiationOutcomeFor` rejects any version
other than `kSupportedProtocolVersion = 2` with `unsupported-version`
(`GameInteractionGateway.cpp:167-178`, `:95`); the serializer hardcodes `" 2"` in the negotiation Response
(`GameInteractionProtocolVersion2.cpp:346-352`); the whole contract fixture corpus is written against
Version 2. It is only warranted for a **breaking change to an existing message's grammar**. Note the project's
actual precedent for that: the raised lantern *changed Version 2 in place* because Version 2 had not shipped
(ADR 0003, *Amendment: the raised lantern* — `amnesia-spelos/amnesia-tdd-tcp#40`). Version 2 has since
shipped with v0.1.0, so that escape hatch is closed; a breaking change now costs a real Version 3.

### `LanProtocol` versioning and mismatched Game Peers

- One constant: `LanProtocol.CurrentVersion = 4` (`src/Multimnesia.Contracts/LanProtocol.cs:28`), surfaced as
  `SessionNetworkOptions.ProtocolVersion` (`src/Multimnesia.Client/TcpSessionOperations.cs:15`).
- It is exchanged in the handshake and compared for **exact equality in both directions**: the Session Host
  rejects a differing `JoinRequest` with `AdmissionRejected("The Multiplayer Session uses an incompatible
  protocol version.")` and disposes the socket (`TcpSessionOperations.cs:321-326`, message at `:27`); the
  Joining Player only accepts `AdmissionAccepted` when `accepted.ProtocolVersion == _options.ProtocolVersion`
  (`:137`).
- So a mismatched Game Peer is **never admitted**: no Multiplayer Session starts, no Pose flows, nothing
  half-works. ADR 0002 and ADR 0003 both chose this deliberately — "rejected at admission as incompatible
  rather than disconnecting mid-session on an unknown message," and the lantern amendment bumped 3→4 precisely
  so a stale build on one LAN computer fails at admission "instead of being admitted and then dropped at its
  first `pose`, which a mismatched build on one of the two LAN computers would otherwise cause."
- Bumping it is one constant plus the ADR. Every new message type so far bumped it: 1→2 for the Shared Custom
  Story Start (ADR 0002), 2→3 for the Pose, 3→4 for the lantern field (ADR 0003). Interaction messages should
  be expected to bump it to 5.
- History matters for the release script: the maintainer copies `bin/Release/net10.0/` to the second LAN PC
  (`AGENTS.md`), so both computers must carry the same `LanProtocol.CurrentVersion` or admission fails.

---

## 4. The trusted-LAN boundary: what message shape keeps the guarantee

`CONTEXT.md` states the invariant: `LanProtocol` "has no message type that names or executes an arbitrary
remote script or command, so a peer on the LAN cannot use it to run code on the other machine." ADR 0002 and
ADR 0003 restate it as it widened: "`LanProtocol` may name an installed Custom Story to start and may carry
the other player's Pose, but never an arbitrary remote script or command."

The escape hatch this is guarding against is real and sits one hop away: the **legacy** Game Interaction
Protocol has an `exec` Command that runs arbitrary AngelScript through
`iGameInteractionGameAdapter::RunScript` (`src/amnesia/src/game/GameInteractionGateway.cpp:410-417`,
`GameInteractionGateway.h:251`). That connection is loopback-only. An interaction message that carried a
script-reachable string would build a bridge from the LAN to that hatch.

Four properties the two existing precedents share, and which an interaction message must keep:

1. **The receiver composes the local line, never the sender.** ADR 0002: "The receiving Game Peer, not the
   wire message, decides to issue `startcustomstory`." ADR 0003 repeats it for `avatarpose`. In code this is
   `GameInteractionProtocol.AvatarPose(...)` building the string from typed fields
   (`src/Multimnesia.Client/GameInteractionProtocol.cs:128-139`) — the only free-form text on the wire, `Map`,
   goes into a field the game treats as a map path, not a command.
2. **A closed set of typed records, validated on write *and* on read.** `LanMessage` is an abstract record with
   sealed cases (`src/Multimnesia.Contracts/LanProtocol.cs:7-20`); unknown `type` strings throw
   (`:110`); `WriteAsync` validates before serializing and throws on an invalid Pose (`:55-69`);
   `TcpSessionOperations.SendPose` validates again so a bad local Pose cannot kill the session (`:216-223`).
3. **Every field bounded.** `MaximumFrameBytes = 4096` (`:29`); numbers must be finite and
   `|v| <= MaximumPoseMagnitude = 1e9` (`:31`, `IsPoseNumber` `:182`); the map path is capped at
   `MaximumPoseMapScalars = 256` Unicode scalars with all Unicode control categories rejected
   (`:33`, `IsValidField` `:206-216`); identifiers are bounded and exclude `|`, `:` and control characters
   (`CustomStoryIdentifier.IsValid`, used at `:142-149`); enumerations travel as a fixed token set both ways
   (`OutcomeWireName`/`ParseOutcome`, `:151-169`).
4. **Identifiers, not script-reachable names.** This is the one that bites for interactions, and the engine
   already answers it. `cLuxMap` keeps **two** lookups: `GetEntityByName` over a lowercased name map, and
   `GetEntityByID` over an integer map (`src/amnesia/src/game/LuxMap.cpp:620-641`, declared at
   `LuxMap.h:114-115`). The *name* is the script namespace — `cLuxScriptHandler` resolves entities by name for
   script calls (`src/amnesia/src/game/LuxScriptHandler.cpp:243`, `:306`, `:2270`). The *integer ID* is not.
   And the engine's own grab state already identifies exactly what a grab message needs, by integer:
   `iLuxPlayerState_Interact_SaveData` stores `mlCurrentPropId` and `mlCurrentBodyId` as `Int32`
   (`src/amnesia/src/game/LuxPlayerState_Interact.h:34-40`,
   `src/amnesia/src/game/LuxPlayerState_Interact.cpp:115-116`), written from `GetID()` / `GetUniqueID()`
   (`:131-132`) and resolved on load via `GetEntityByID` plus `GetBodyFromID` (`:154-158`).

**Recommended shape.** A grab message carries `{ propId: int32, bodyId: int32 }` — both bounded integers —
and never an entity name or a path. Named-entity fields, free-form text fields, and anything the receiving
Game Peer would splice into a Command string are what would breach the guarantee. If a future message must
name a *file* (as `avatarcreate <entityFile>` can), it should stay on the Game Interaction Protocol hop and be
chosen by the receiving Game Peer, not carried over `LanProtocol`.

Two consequences worth recording: integer entity IDs are **per-map and assigned at load**, so an interaction
message must be scoped to a map the way a Pose already is (`msMapFile` on every Pose,
`GameInteractionGateway.h:100`); and, per ADR 0002 of `amnesia-tdd-tcp`, Avatars are *not* map entities and
scripts cannot reach them, so an Avatar can never be the target of an interaction identifier.

---

## 5. Ordering and loss on each hop, and the grab/release pair

### Hop A (Game Peer ↔ local game, loopback TCP)

- **Ordered.** One socket; the Game Peer serializes every write behind a `SemaphoreSlim(1, 1)`
  (`src/Multimnesia.Client/LocalGameSession.cs:96-106`). The game pops lines in arrival order and executes
  each in the same update (`src/amnesia/src/game/GameInteractionGateway.cpp:571-582`).
- **Lossless except at the extremes.** Loopback TCP does not drop. The three ways a line dies are an inbound
  line over 65,536 bytes (peer disconnected, `GameInteractionGateway.cpp:587-593`; cap at
  `LegacyGameInteractionProtocol.h:12`), a 1 MiB outbound queue overflow in the game
  (`src/amnesia/src/game/GameInteractionTransport.cpp:7`, `:135-141`), and the deliberate coalescing of
  `localpose` State Updates. **Commands do not coalesce.**
- **Latency is bounded by a tick.** The gateway flushes Responses in the update that processed them
  (`GameInteractionGateway.cpp:583`); `TCP_NODELAY` is on both sides
  (`src/Multimnesia.Client/LocalGameSession.cs` loopback socket; contract README *Transport*).
- **Session end is total.** When the Peer disconnects, the gateway ends the Session and removes its Avatars
  (`GameInteractionGateway.cpp:470-484`, `:552-564`); Capabilities and the `localpose` subscription go with
  it. A reconnect renegotiates from scratch (`LocalGameSession.cs:144`).

### Hop B (Game Peer ↔ Game Peer, `LanProtocol` over LAN TCP)

- **Ordered.** One TCP connection per Multiplayer Session, one writer task draining one channel
  (`LanOutbound.ReadAllAsync`, `src/Multimnesia.Client/LanOutbound.cs:49-58`; `SingleReader = true`, `:15`).
  Session messages keep strict FIFO order among themselves and always precede the Pose slot. **Reordering is
  impossible on this hop** — ADR 0003 explicitly rejected a separate UDP channel, so there is no second path
  a message could race down.
- **Lossless for session messages, lossy by design for Poses.** A Pose that has not been written when a newer
  one arrives is discarded (`OfferPose`, `:36-40`), and an unsent Pose is dropped at shutdown (`:48`). Session
  messages are never replaced; they either go out, or the channel fills and the connection is torn down
  (`TcpSessionOperations.cs:232-239`).
- **Rate limits are asymmetric, and this is the main cost for interactions.** Inbound non-Pose messages are
  capped at `MaxInboundMessagesPerWindow = 40` per 1-second window; exceeding it disconnects the peer with
  a notice (`TcpSessionOperations.cs:22-23`, `:463-472`). Poses have a separate token bucket,
  `MaxInboundPoseBurst = 600` refilled at `InboundPosesPerSecond = 60` (`:25-26`, `:457-461`). Heartbeats
  every 2 s (`:13`, `:426-434`) spend from the 40-per-second budget. **A continuous held-prop transform at
  30 Hz would blow that budget and disconnect the sender**; it needs either the Pose lane's bucket or a
  bucket of its own.
- **Liveness.** A read that stalls longer than `HeartbeatTimeout = 10 s` ends the Multiplayer Session
  (`:14`, `:452-453`).

### A grab/release pair specifically

**Neither half can be lost or reordered while the Multiplayer Session lives.** Both hops are single ordered
TCP streams with no coalescing on the discrete lane, so `grab` then `release` arrive in that order or the
connection is already gone. The realistic failure is not a *half*-lost pair — it is *both* halves lost at once,
because every loss mode on both hops is connection-fatal:

| Failure | Effect on a grab/release pair |
| --- | --- |
| LAN connection lost / heartbeat timeout | Multiplayer Session ends; Departure/teardown paths run (`TcpSessionOperations.cs:452-453`, `:496-499`) |
| Session-message channel full (64) | Connection disposed with a notice (`:232-239`) — never a silent drop |
| Game Peer ↔ local game connection lost | Game Session ends, Avatars and subscription dropped (`GameInteractionGateway.cpp:470-484`); reconnect renegotiates |
| Remote player departs | `Departure` handled, presence flips, `SharedPose` removes the Avatar and clears the pending Pose (`SharedPose.cs:116-132`) |
| The *local* game freezes loading | Poses stall behind `MaxUnconfirmedPoses = 30` and are coalesced away (`SharedPose.cs:15`, `:57`, `:133`) — **a discrete message routed through this same gate would stall too, which is the bug to avoid** |

So the design obligation is not retransmission — it is **reconciliation on session end and on Avatar
replacement**. A release implied by a departure must be applied locally, the way `SharedPose` already
recreates a fresh Avatar for a replacement player rather than continuing the departed player's timeline
(`SharedPose.cs:118-125`). The same asymmetry the Pose already handles — teleport counter and map path let
the receiver snap rather than glide across a discontinuity (`AvatarPoseModel.cpp:37-45`, `:62-67`) — argues
for an interaction message carrying enough state to be *self-correcting* rather than purely incremental.

---

## Summary for the design ticket

| Message kind | Lane | Cost |
| --- | --- | --- |
| Grab / release (discrete, exactly once) | `LanOutbound._messages` (ordered, bounded 64, fail-loud) + a new Game Interaction Protocol Command | Cheap. New `LanMessage` case, `LanProtocol.CurrentVersion` 4→5, new ADR (ADR 0002 and 0003 both require one), new Capability on the game side — **no Protocol Version 3** |
| Held-prop transform (continuous, coalescable) | A second latest-wins slot beside `LanOutbound._pose`, with its own inbound token bucket | Cheap on shape, but **must not** use the 40-per-second session lane, and must not sit behind `SharedPose.CanWritePose` |
| Anything naming an entity by name, or carrying script/command text | — | **Breaches the trusted-LAN guarantee.** Use `int32` prop/body IDs, as the engine's own save data does |
| Breaking change to an existing line's grammar | — | Expensive: a real Game Interaction Protocol Version 3. The "amend Version 2 in place" precedent closed when v0.1.0 shipped |
