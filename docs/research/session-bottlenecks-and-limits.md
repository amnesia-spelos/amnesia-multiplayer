# Bottlenecks and limits of a Multiplayer Session, at two and four players

Research question: what are the theoretical bottlenecks and maximums of the Multiplayer Session architecture, which of
them a realistic playthrough could hit, and how they change when the Multiplayer Relay forwards to up to three Joining
Players (ADR 0007, [#67](https://github.com/amnesia-spelos/amnesia-multiplayer/issues/67)). Written after #61 landed
(`a32ce16`), before #67 is implemented.

Primary sources only: the two source trees, their ADRs and `CONTEXT.md`, the Protocol Version 2 contract README, and
GitHub issues for the status of known problems. The code is the authority. Paths beginning `src/Multimnesia.*` are in
this repository; paths beginning `src/amnesia/` or `tests/` are in `../amnesia-tdd-tcp`. This builds on
`docs/research/sync-seam-cost.md` and does not repeat it. Two of its numbers are out of date: `MaximumFrameBytes` is
now 32 KiB, not 4096, and `LanProtocol.CurrentVersion` is 9, not 4 (`src/Multimnesia.Contracts/LanProtocol.cs:84-86`).

Message sizes in §1.3 were measured, not estimated. A throwaway harness, not committed, serialized each message with the
real `LanProtocol.WriteAsync` and `GameInteractionProtocol` formatters. Unless a row says otherwise, it used the map path
`custom_stories/mp-test-cs/maps/multiplayer-test.map` (52 characters) and numbers at the game's 4-decimal precision.
The Game Peer parses them from 4-decimal lines (`src/Multimnesia.Client/ProtocolVersion2.cs:69-74`), so they stay that
short in JSON.

---

## 1. The two hops

### 1.1 Hop A: game ↔ Game Peer (Game Interaction Protocol, loopback TCP)

| Property | Value | Where |
| --- | --- | --- |
| Game update rate, which paces all game-side I/O | 60 Hz logic tick | amnesia-tdd-tcp `docs/research/interaction-surface.md:78`; gateway runs in `cLuxSocketServer::Update`, `src/amnesia/src/game/LuxSocketServer.cpp:268-273` |
| Bytes the game reads per update | ≤ 64 KiB | `kMaximumBytesReceivedPerUpdate`, `src/amnesia/src/game/GameInteractionTransport.cpp:8`, loop `:155-173` |
| Bytes the game sends per update | ≤ 64 KiB | `kMaximumBytesSentPerUpdate`, `GameInteractionTransport.cpp:9`, `Flush` `:175-195` |
| Game's outbound queue to the Game Peer | 1 MiB, then the game **disconnects** the Game Peer | `GameInteractionTransport.cpp:7`, `:136-144` |
| Longest inbound line | 65,536 bytes, then **disconnect** | `src/amnesia/src/game/LegacyGameInteractionProtocol.h:12`; `GameInteractionGateway.cpp:842-845`; contract README `tests/game-interaction-protocol-version-2/README.md:256` |
| `localpose` / `reportedbodies` rate | Clamped to 1–60 Hz; the Game Peer asks for 30 | `GameInteractionGateway.cpp:150-151`, `:262-264`; `SharedPose.LocalPoseRateHz`, `src/Multimnesia.Client/SharedPose.cs:11`; `Holds.ReportedBodiesRateHz`, `src/Multimnesia.Client/Holds.cs:19` |
| State Update backlog | At most one per kind. Handed to the transport only when nothing else is pending, and a newer one replaces it | `GameInteractionGateway.cpp:161-163`, `:747-760`; contract README `:109` |
| Events (`interactions`, `sharedcallbacks`) | Queued as they happen, never coalesced | `cGameInteractionGateway::Report`, `GameInteractionGateway.cpp:852-872` |
| Game reading while a map loads | None: the gateway runs only in a game update, and a story start "blocks while its start map loads" | `GameInteractionGateway.cpp:763-771`; noted in `SharedPose.cs:13-14` |
| Game Peer reading the game | Never awaits a handler; each line is dispatched and the next one read | `src/Multimnesia.Client/LocalGameSession.cs:186-217` |
| Game Peer writing the game | One `SemaphoreSlim(1,1)` write gate for every writer, with unbounded waiters | `LocalGameSession.cs:107-118` |
| Avatar Pose writes | At most 30 unconfirmed; a `ping` every 10, and the newest received Pose replaces older ones | `SharedPose.cs:15-16`, `:57`, `:133-139` |
| `entitybodies` writes | **Not gated by a pong.** One latest-wins slot, written as soon as the gate frees | `Holds.cs:49`, `:463-479` |
| Hold and Shared Callback lines | Unbounded `ConcurrentQueue`s, written in order | `Holds.cs:48`; `src/Multimnesia.Client/SharedCallbacks.cs:40-41`, `:199-216` |

### 1.2 Hop B: Game Peer ↔ Game Peer (`LanProtocol`, LAN TCP)

| Property | Value | Where |
| --- | --- | --- |
| Framing | 4-byte big-endian length, then UTF-8 JSON; written as **two** stream writes per frame | `src/Multimnesia.Contracts/LanProtocol.cs:207-211` |
| Largest frame | 32,768 bytes; the writer and the reader both throw past it | `LanProtocol.cs:86`, `:205`, `:219-220` |
| Bodies per message | ≤ 32 (`MaximumBodies`); Holds leaves an entity out whole rather than split it | `LanProtocol.cs:93`, `:336-338`; `Holds.cs:197-203` |
| Ordered session lane | Bounded channel, capacity 64 (`OutboundChatCapacity`), only ever written with `TryWrite` | `src/Multimnesia.Client/TcpSessionOperations.cs:22`, `:455-461`; `src/Multimnesia.Client/LanOutbound.cs:13-18`, `:29-34` |
| Pose and Bodies | One latest-wins slot each, written only when the ordered lane is empty | `LanOutbound.cs:25-26`, `:37-48`, `:57-66` |
| Writer | One task per connection, awaiting each frame | `TcpSessionOperations.cs:463-476` |
| Heartbeat | Every 2 s on the ordered lane; the loop silently stops if the lane is full | `TcpSessionOperations.cs:13`, `:478-489` |
| Read timeout | 10 s per read | `TcpSessionOperations.cs:14`, `:508-509` |
| Receive loop | Reads one frame, checks its Inbound Allowance, then **awaits** the callback before reading the next | `TcpSessionOperations.cs:506-565` |
| Admission | One Joining Player today (`_admittedConnection`); 8 concurrent handshakes | `TcpSessionOperations.cs:23`, `:55-58`, `:383-399` |

### 1.3 Measured sizes

| Message | Bytes (LAN frame incl. prefix) | Game line (bytes incl. LF) |
| --- | --- | --- |
| Pose | 207 | `avatarpose`: 128 |
| Pose, 256-scalar map needing JSON escapes, numbers at the 1e9 bound | 1,758 | — |
| Bodies, 1 body | 299 | `entitybodies`: 181 |
| Bodies, 8 bodies | 1,622 | — |
| Bodies, 32 bodies | 6,158 | 3,468 |
| Bodies, 32 bodies, every number at the bound | 8,955 | 7,206 (with a 256-scalar CJK map) |
| Bodies, as above plus a 256-scalar map needing escapes | 10,440 (32% of the 32 KiB frame) | — |
| Claim / Settled / Interaction / Broke | 113 / 96 / 125 / 254 | — |
| Shared Callback (player collide / use item) | 139 / 144 | — |
| Heartbeat | 24 | — |
| Chat Entry (typical / 256-scalar message) | 102 / 335 | — |

So no legitimate message comes near the 32 KiB frame limit or the 65,536-byte line limit. Both stay theoretical limits.

### 1.4 What each queue does when it fills

| Queue or limit | On overflow | Where |
| --- | --- | --- |
| Game outbound queue (1 MiB) | Game disconnects the Game Peer; the Game Session ends and the Game Peer reconnects | `GameInteractionTransport.cpp:138-141`; `LocalGameSession.cs` `RunReconnectingAsync` |
| Game State Update slot | Replaced by the newer sample | `GameInteractionGateway.cpp:747-760` |
| Game sample buffers (256 per Avatar, 256 per driven body) | Oldest dropped | `AvatarPoseModel.cpp:7`, `:74`; `PeerDrivenEntityModel.cpp:7`, `:116` |
| Game Peer write gate | Blocks the writer, and anything awaiting it | `LocalGameSession.cs:107-118` |
| `LanOutbound` ordered lane (64) | Hold, world, and chat sends **dispose the connection** with a notice. Heartbeats stop silently, so the other side times out | `TcpSessionOperations.cs:262-292`, `:485` |
| `LanOutbound` Pose / Bodies slot | Replaced | `LanOutbound.cs:37-48` |
| Pending Shared Callbacks (4,096) | The change is dropped and logged | `SharedCallbacks.cs:15`, `:165-172` |
| Inbound Allowance | `Flood` disconnect with the "message rate limit" notice | `TcpSessionOperations.cs:521-526` |
| Frame over 32 KiB, malformed frame | Protocol-violation disconnect | `LanProtocol.cs:219-220`; `TcpSessionOperations.cs:574-578` |

---

## 2. Inbound Allowances against real traffic

### 2.1 The values

The Session Host checks each Joining Player against one player's values, its Sender Allowance. A Joining Player
checks its connection against `MaxPlayers − 1 = 3` times those values, whether or not three other players are present
(`TcpSessionOperations.cs:24-36`, `:496-503`; ADR 0007 *Consequences*; `CONTEXT.md:67-73`).

| Kind | Session Host, per Joining Player | Joining Player, from the Session Host |
| --- | --- | --- |
| Pose | burst 600, refill 60/s | burst 1,800, refill 180/s |
| Bodies | burst 600, refill 60/s | burst 1,800, refill 180/s |
| Hold messages | burst 96, refill 20/s | burst 288, refill 60/s |
| Everything else (chat, heartbeat, Shared Callbacks, button/lamp, story start) | 40 per fixed 1-s window | 120 per fixed 1-s window |

The buckets are `InboundAllowance` (`src/Multimnesia.Client/InboundAllowance.cs:4-17`). The fixed window restarts
on the first message after it expires (`TcpSessionOperations.cs:518-519`), so up to 2 × 40 can pass in a little over
a second across a window boundary.

### 2.2 What one player legitimately sends

| Stream | Rate | Bytes/s |
| --- | --- | --- |
| Pose, while on a map | ≤ 30/s (game sends only on change while paused or in menus, contract README `:108`) | 30 × 207 = **6,210** |
| Bodies, while Holding or Settling | ≤ 30/s | 30 × 299 = 8,970 (1 body) … 30 × 6,158 = **184,740** (32 bodies) |
| Heartbeat | 0.5/s | 12 |
| Hold messages, cave-in worst case | 32 Claims + 32 Settled in about 3 s (Settling cap `kSettlingCapMs = 3000`, `src/amnesia/src/game/LocalInteractionReportModel.cpp:5`) | 32 × 113 + 32 × 96 = 6,688 bytes per burst |
| Hold messages, observed | At most 11 in any second on a 20+-prop cave-in, 2026-10-16 (#60 closing comment) | — |

A 32-body Bodies stream is short-lived in practice. It comes from a contact cascade, whose entities Settle within 3 s.

### 2.3 Headroom

A bucket of burst *B* and refill *R* stays full under any steady rate *r* ≤ *R*. A sustained *r* > *R* empties it in
*B* / (*r* − *R*) seconds.

| Kind | 2 players, Session Host | 2 players, Joining Player | 4 players, Session Host (per Joining Player) | 4 players, Joining Player (3 senders) |
| --- | --- | --- | --- | --- |
| Pose | 30 vs 60/s → 2× | 30 vs 180/s → 6× | 30 vs 60/s → 2× | 90 vs 180/s → 2× |
| Bodies | ≤ 30 vs 60/s → 2× | ≤ 30 vs 180/s → 6× | ≤ 30 vs 60/s → 2× | ≤ 90 vs 180/s → 2× |
| Hold burst | 64 vs 96 → 32 spare | 64 vs 288 | 64 vs 96 → 32 spare | 3 × 64 = 192 vs 288 → 96 spare |
| Hold sustained | 11 observed vs 20/s | 11 vs 60/s | 11 vs 20/s | 33 vs 60/s |
| Other | ~0.5/s + human-paced vs 40 | vs 120 | vs 40 | ~1.5/s + human-paced vs 120 |

What this means:

- **Pose and Bodies cannot plausibly go over.** The game clamps both subscriptions at 60 Hz and the Game Peer asks
  for 30. Before the Relay forwards anything, the sender's `LanOutbound` keeps only the newest Pose and Bodies, so
  only frames already in socket buffers can arrive in a burst. A 600 burst is 20 s of Poses.
- **Holds have the tightest margin.** After one full cascade (64 messages), 32 tokens remain, and the bucket refills
  to 96 in 64 / 20 = 3.2 s. A **second full cascade from the same player within 1.6 s** (32 + 20*t* ≥ 64 → *t* ≥
  1.6 s) disconnects that player. A loop at 70 messages/s still disconnects in 96 / (70 − 20) ≈ 1.9 s, as #60
  intended. Since amnesia-tdd-tcp#85 (closed, `a4ce041`), a prop is Claimed by contact only once the contact moves
  it, and the 2026-10-16 playtest stayed at 11/s. Two big cascades back to back is possible (kicking through rubble),
  but it has not been observed.
- **The 40-per-window "other" allowance has not been measured against Shared Callbacks.** A Held entity moving
  back and forth across a trigger area sends an `entity-collide` enter/leave pair each time. Neither source tree
  bounds how often that can happen. The 2026-10-11 and 2026-10-15 playtests had no `Flood` other than the Hold loop
  (`README.md:301-303`), so this is *unclear*, not *observed*.
- **The Joining Player's 3× values hold with 2× headroom at four players**, and 6× at two. As ADR 0007 says, they
  catch only a misbehaving Session Host.
- **A theoretical bug, not tracked:** the allowances read `DateTime.UtcNow` (`TcpSessionOperations.cs:499`, `:511`),
  and a negative elapsed time *subtracts* tokens (`InboundAllowance.cs:12`). A backwards wall-clock step of more than
  burst / refill disconnects the next message for `Flood`. That is 10 s for Pose and Bodies, and 4.8 s for Holds, at
  either player count. A forward step only refills the bucket. A monotonic clock (`Stopwatch`) would remove it.

---

## 3. The Session Host's fan-out under #67

### 3.1 Bandwidth

In ADR 0007's star, the Session Host sends each of the *N* − 1 Joining Players its own stream plus the forwarded
streams of the other *N* − 2. That is (*N* − 1)² player-streams up. A Joining Player receives *N* − 1 streams and
sends 1. Per-stream figures come from §2.2. Scenario **W** is walking (6.2 KB/s). Scenario **C** is a 32-body
cascade on top of walking (191 KB/s, for ≤ 3 s).

| Case | 2 players, each direction | 4 players, Session Host up (9 streams) | 4 players, Joining Player down (3 streams) |
| --- | --- | --- | --- |
| Everyone W | 6.2 KB/s (0.05 Mbit/s) | 9 × 6.2 = 56 KB/s (0.45 Mbit/s) | 18.6 KB/s |
| One Joining Player C (forwarded ×2, to the Session Host ×1) | 191 KB/s (1.5 Mbit/s) | 7 × 6.2 + 2 × 191 = 425 KB/s (3.4 Mbit/s) | ≤ 2 × 6.2 + 191 = 203 KB/s |
| Session Host C (×3) | 191 KB/s | 6 × 6.2 + 3 × 191 = 610 KB/s (4.9 Mbit/s) | 203 KB/s |
| Everyone C at once (pathological) | 191 KB/s | 9 × 191 = 1,719 KB/s (13.8 Mbit/s) | 573 KB/s (4.6 Mbit/s) |

Frames on the Session Host at four players: in, 3 × (30 + 30) = 180/s; out, up to 9 × (30 + 30) = 540/s, plus
heartbeats. Each frame is two stream writes with Nagle off (`LanProtocol.cs:209-210`; `LanOutbound.cs:74-79`), so it
may leave as two TCP segments, a 4-byte header and its payload. That is up to about 1,080 packets/s for Poses and
Bodies. The sources do not measure per-packet overhead or Wi-Fi airtime. Writing the prefix and payload in one call
would halve the packet count.

On a wired LAN none of this is close to a limit. On Wi-Fi the 4.9 Mbit/s realistic peak is probably fine, but no
playtest has measured it, so this is *unclear*.

### 3.2 Where forwarded Poses and Bodies go: the main #67 risk

Today `LanOutbound` has **one** Pose slot and **one** Bodies slot per connection (`LanOutbound.cs:25-26`). Its only
other path is the 64-entry ordered lane, whose overflow ends the connection (`TcpSessionOperations.cs:262-276`).
#61 explicitly left "keeping only the newest Pose or Bodies per sender in the Relay's outbound queue" out of scope:
"forward as read; revisit only if a playtest shows backlogs". The two mechanisms that exist today both fail under
forwarding:

- **Into the existing slots:** the Session Host's own Pose and two forwarded senders' Poses replace each other in
  one slot. Whichever was offered last wins. Avatars stutter or starve, depending on timing.
- **Into the ordered lane:** each Joining Player's outbound receives 2 forwarded senders × (30 Pose + ≤ 30 Bodies)
  = 60–120 frames/s. If that player's link or receive loop stalls, the 64 entries fill in 64 / 60 ≈ **1.1 s**
  (walking) or 64 / 120 ≈ **0.5 s** (both Holding), after whatever the socket send buffer absorbs. The next
  `TryWrite` then fails. Under today's ordered-lane rule that disconnects the player. With two players, the same
  lane fills at 0.5 heartbeats/s, about 128 s, so the 10-s heartbeat timeout governs. A Wi-Fi hiccup of about a
  second, harmless today, would drop a Joining Player.

The fix that matches the existing design is a latest-wins Pose and Bodies slot **per sender** in each `LanOutbound`.
#68 already makes the *receiving* Pose slot per sender, but nothing tracks the *Relay's outbound* side.

### 3.3 Head-of-line effect of one slow Joining Player

- Each admitted connection has its own `LanOutbound` and writer task (`TcpSessionOperations.cs:455-461`). A slow
  socket stalls only its own writer.
- `LanOutbound` exposes only `TryWrite` (`LanOutbound.cs:29-34`). The channel's `FullMode.Wait` (`:17`) never
  applies, so a forwarding receive loop cannot block on a full queue. A slow Joining Player therefore cannot
  head-of-line the others. What it risks is its own disconnect, as in §3.2. This holds only while #67 keeps
  forwarding non-blocking. An awaited write on the forwarding path would let one slow Joining Player stall the
  forwarding of every sender's receive loop.
- Within one connection, the writer drains every ordered message before the Pose, and the Pose before the Bodies
  (`LanOutbound.cs:61-64`). A Pose waits behind at most the ordered backlog and one Bodies frame on the wire. At
  100 Mbit/s a 6,158-byte Bodies frame takes 6,158 × 8 / 10⁸ ≈ 0.5 ms, small next to the 100 ms render delay.

### 3.4 Can the receive loop's awaits back up TCP?

The loop awaits each callback before reading the next frame (`TcpSessionOperations.cs:528-557`). On the Game Peer, the
Pose, Bodies, Hold, and world callbacks return at once (`LocalGameSession.cs:144-168`), and so do the story-start
callbacks, deliberately (`:130`, `:138`). Two still await the local game: **Chat Entries** (`_receiveChat` →
`DisplayAsync` → write gate, `LocalGameSession.cs:119`, `:129`) and **notices** (`_notice`, for example
`TcpSessionOperations.cs:364`, `:412`, `:524`).

Those awaits block only when the write gate is held by a write the loopback socket cannot take. That needs the game to
read nothing (a map load, §1.1) *and* the socket buffers to be full. Avatar Poses cannot fill them, because they are
capped at 30 unconfirmed. **`entitybodies` lines can.** `Holds` keeps writing the other player's bodies to a frozen
game as fast as the gate allows (`Holds.cs:463-479`), and it learns the local map changed only from `mapentered`,
which the game sends after the load (contract README `:139`). A level change while another player Holds props on the
map being left is the plausible trigger. One body is 30 × 181 ≈ 5.4 KB/s; 32 bodies is 30 × 3,468 ≈ 104 KB/s. The
sources do not give the loopback buffer size, so whether a 5–20 s load fills it is *unclear*.

If it does fill, a chat line or notice that arrives during the load stalls that connection's receive loop. That is not
a disconnect: the 10-s timeout covers only `ReadAsync`. TCP backpressure then reaches the sender. Its Poses and Bodies
coalesce, and only its ordered lane (64) can overflow. Under #67, the same stall would also hold back forwarding from
that sender to everyone else, if forwarding sits after the callbacks in the loop. A pong-style gate on `entitybodies`,
as `SharedPose` has, would remove the dependency.

Separately, the Session Host awaits `"A player is attempting to join."` *before* starting the handshake timer
(`TcpSessionOperations.cs:364-366`). A stalled write gate there delays admission until the joiner's 10-s `JoinTimeout`
expires (`:11`). This is minor and unchanged by #67.

### 3.5 CPU

Every inbound frame is parsed with `JsonDocument` (`LanProtocol.cs:226`), and every outbound frame is serialized from
the record (`:102`). If #67 stamps the sender number by re-serializing, that is up to about 540 serializations/s of
≤ 6 KB at four players, plus 180 parses/s. Neither tree has a measurement. Nothing points to a CPU bottleneck at
these rates, but this is not verified.

---

## 4. Game-side ceilings

| Ceiling | Value | Where | Meaning for #67–#69 |
| --- | --- | --- | --- |
| Bodies a game reports | 32 (`kMaxBodies`); an entity that does not fit stays under local physics | `src/amnesia/src/game/LocalInteractionReportModel.h:79`, `.cpp:30`, `:189`; contract README `:125` | Per *reporting* game. Four players can each Hold up to 32 bodies, so a game may drive up to 3 × 32 = 96. Each `entitybodies` line carries ≤ 32 (`GameInteractionProtocolVersion2.cpp:26`). No cap exists on driven entities (`PeerDrivenEntityModel.h:107`). The cost of driving 96 bodies is unmeasured. Accepted divergence past 32: ADR 0004, `README.md:322` |
| Avatars per Game Interaction Protocol Session | 16, then `limit` | `GameInteractionGateway.cpp:178`, `:477-479`; contract README `:94` | 3 needed. Each Avatar has its own pose model and sender clock (`LuxAvatarHandler.h:84`; `AvatarPoseModel.h:70`) |
| Game Peer → game throughput | ≤ 64 KiB/update × 60 ≈ 3.75 MiB/s | §1.1 | At 4 players, worst case 3 × (30 × 128 + 30 × 3,468) ≈ 324 KB/s ≈ 8% of it |
| Line length | 65,536 | §1.1 | Largest real line ≈ 7.2 KB |
| **Peer-Driven Entity playback clock** | **One** `cSenderClock` and one newest-sender-time for **all** driven entities. A sample more than 1 s behind the newest clears every buffered sample (`kResyncMs = 1000`) | `PeerDrivenEntityModel.h:107-111`; `PeerDrivenEntityModel.cpp:79-82`, `:131-136`, `:255-260`; `src/amnesia/src/game/SenderClock.h:23-30`, `:37`; contract README `:166` | **Assumes one other Holder.** `entitybodies` time is the Holder's own engine game time (`LuxInteractionReportHandler.cpp:167`, `:214-217`), so two machines' clocks differ by however long each game has run. With two other players Holding at once, each line from the Holder with the earlier clock clears both Holders' samples, and the alignment flips between their offsets on every line. Driven props of both Holders would hitch or freeze. Avatars are unaffected because they have a clock each. **Not tracked:** #66 expects "no game-side change", and #69 does not mention it |

Game Peer assumptions of exactly one other player are all tracked by #67–#70. They are the single
`_admittedConnection` (`TcpSessionOperations.cs:55-58`), the `partner` Avatar and its single received-Pose slot
(`SharedPose.cs:10`, `:26`), the `Local`/`Other` Holder and its single received-Bodies slot (`Holds.cs:22`, `:49`),
and `LanOutbound`'s single slots (§3.2, where only the Relay side is untracked).

A design note for #68: `MaxUnconfirmedPoses = 30` was sized as "about a second's worth" for one Avatar
(`SharedPose.cs:13-15`). If one counter covers three Avatars' lines, that is about 0.33 s of lines. The Avatars would
pause, losing nothing, during any game hitch longer than that.

---

## 5. Latency

| Source | Value | Where |
| --- | --- | --- |
| Nagle | Off on all four sockets: both LAN ends, the Game Peer's loopback, and the game's | `LanOutbound.cs:73-80`; `LocalGameConnectionRunner.cs:37`; `GameInteractionTransport.cpp:115-119` |
| Game tick quantization | Samples taken and delivered once per 60 Hz update; Responses flushed in the same update | `GameInteractionGateway.cpp:836-840` |
| Render delay | 100 ms for Avatars and for Peer-Driven Entities | `AvatarPoseModel.cpp:50`; `PeerDrivenEntityModel.cpp:56` |
| Clock alignment | Adopts a lower-latency sample at once; drifts toward higher latency at 5% per sample | `SenderClock.h:23-30`, `:38` |
| `LanOutbound` priority | Ordered backlog, then Pose, then Bodies; ≈ 0.5 ms per Bodies frame at 100 Mbit/s | §3.3 |
| Pose gate | Avatar updates pause while 30 written Poses await a pong | `SharedPose.cs:15`, `:57` |
| Claim resolution | Instant locally; a Joining Player's Claim is ordered after a LAN round trip | ADR 0004 *Consequences* |
| Joining Player ↔ Joining Player (#67) | Two LAN hops plus Relay processing | ADR 0007 *Consequences* |
| Dead-peer detection | Heartbeat every 2 s; up to 10 s to notice a crash or a pulled cable | `TcpSessionOperations.cs:13-14`; `README.md:183` |

Under normal LAN conditions, the only latency that is visible by design is the 100 ms render delay.

---

## 6. Ranked limits

Ordered by how likely a realistic three- or four-PC playthrough is to hit each one.

| # | Limit | Value | Where | Realistic? | Consequence | Already tracked? |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Relay outbound for forwarded Poses and Bodies | 1 slot per kind, or 64 ordered entries | `LanOutbound.cs:13-26`; `TcpSessionOperations.cs:22`, `:262-276` | **Yes**, once #67 forwards | Shared slot: Avatars replace each other. Ordered lane: ~0.5–1.1 s stall on one Joining Player's link disconnects them | Partly: #61 defers it ("revisit only if a playtest shows backlogs"). #68 covers only the receive side |
| 2 | Shared Peer-Driven Entity clock | 1 clock for all Holders, `kResyncMs = 1000` | `PeerDrivenEntityModel.h:107-111`; `SenderClock.h:37` | **Yes**, with #69, whenever two other players Hold at once | Props driven for both Holders hitch or freeze | **No** (#66 expects no game-side change) |
| 3 | Hold allowance at the Session Host | burst 96, refill 20/s | `TcpSessionOperations.cs:33-34` | Unclear: needs two full cascades within 1.6 s; 11/s observed | `Flood` disconnect of that player | #60 closed; amnesia-tdd-tcp#85 closed |
| 4 | Unpaced `entitybodies` writes to a game loading a map | Writes as fast as the gate frees; loopback buffer size unknown | `Holds.cs:463-479`; `LocalGameSession.cs:107-119` | Unclear | Chat or notice awaits stall a receive loop, and under #67 its forwarding too; disconnect only if the sender's ordered lane fills | No |
| 5 | "Other" fixed window | 40/s per Joining Player | `TcpSessionOperations.cs:24`, `:518-519` | Unclear: Shared Callback rates from a Held prop at an area edge are unmeasured; never seen | `Flood` disconnect | No |
| 6 | Session Host upload | 0.45–4.9 Mbit/s realistic, 13.8 pathological, ~1,080 packets/s | §3.1 | No on wired LAN; unclear on Wi-Fi | Stalls, which with risk 1 unaddressed become disconnects | ADR 0007 notes upload growth |
| 7 | Pose gate shared by 3 Avatars | 30 lines ≈ 0.33 s at 90/s | `SharedPose.cs:15` | Possible during hitches | Brief Avatar pauses, nothing lost | No (#68 design detail) |
| 8 | Session Host as single point of failure; 10-s dead-peer detection | 2 s / 10 s | `TcpSessionOperations.cs:13-14` | Yes, by design | Everyone's session ends with the Session Host's | ADR 0001, ADR 0007 |
| 9 | 32 bodies per Holder | 32 | `LocalInteractionReportModel.h:79`; `LanProtocol.cs:93` | Yes (big piles) | Excess props simulate locally and may diverge | ADR 0004; `README.md:322` |
| 10 | Two writes per frame with Nagle off | 2 segments per frame | `LanProtocol.cs:209-210` | Harmless on LAN | Double packet rate | No |
| 11 | Wall-clock step in Inbound Allowances | Backward step > 10 s (Pose) / 4.8 s (Hold) | `InboundAllowance.cs:12`; `TcpSessionOperations.cs:499`, `:511` | No (needs a clock step) | Spurious `Flood` disconnect | No |
| 12 | Pending Shared Callbacks | 4,096 | `SharedCallbacks.cs:15` | No ("far beyond a chapter's worth"; fills ~3× faster at four players) | Change dropped and logged | ADR 0006 |
| 13 | Joining Player Inbound Allowances | 3 × one player | `TcpSessionOperations.cs:498-503` | No: 2× headroom at four players | `Flood` disconnect of the Session Host | #61 (done) |
| 14 | LAN frame / game line / game queues | 32 KiB / 65,536 B / 1 MiB / 64 KiB per update | §1 | No: largest real message ≈ 10.4 KB; game I/O at 4 players ≈ 8% | Disconnect | Designed bounds |
| 15 | Avatars per game; concurrent handshakes | 16; 8 | `GameInteractionGateway.cpp:178`; `TcpSessionOperations.cs:23` | No: 3 and ≤ 3 needed | `limit` / rejected attempt | — |

Doc drift: the README still lists "Standing against a pile of props can disconnect the session", tracked in
amnesia-tdd-tcp#85 and #60 (`README.md:313`). Both are closed, and the 2026-10-16 cave-in playtest saw no
disconnect (#60 comment).

## Verdict

The two-player session as shipped has no realistic throughput or queue ceiling. Every message is a fraction of its
frame and line limits. Poses and Bodies coalesce at every stage, and the Inbound Allowances sit at 2× or more above
what one player sends. The one real failure seen in playtests, the contact re-Claim loop, is fixed at its source
(amnesia-tdd-tcp#85), and #60 gave a single cave-in its own headroom. The remaining two-player risks are narrow and
unobserved: two big cascades back to back, or chat arriving while a game loads a map with another player's props
streaming in.

Four players are where the real risks are, and they are design gaps, not capacity. Bandwidth stays modest, under
5 Mbit/s up from the Session Host in a busy realistic moment. The two risks to settle before the three-PC playtest
(#71):

1. **The Relay's outbound needs a latest-wins Pose and Bodies slot per sender.** Otherwise #67 either makes Avatars
   replace each other or turns a one-second network hiccup into a disconnect.
2. **The game's Peer-Driven Entity playback has one sender clock.** Two other players Holding props at once would make
   both Holders' props hitch. This needs a game-side change (one clock per Holder, keyed by something the Game Peer
   passes), which #66 currently says should not be needed.
