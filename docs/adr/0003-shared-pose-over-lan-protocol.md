# 3. Shared Pose over the LAN protocol

## Status

Accepted. Amends the `LanProtocol` invariant stated in ADR 0001 and ADR 0002. Amended below for the raised lantern (issue #24).

## Context

Spike 8 of issue #1 requires both players to see each other move in real time. `amnesia-tdd-tcp` (issue amnesia-spelos/amnesia-tdd-tcp#28) provides this on its side of the loopback link: a Game Interaction Protocol Version 2 Session with the `localpose` Capability streams the local player's Pose as `STATE localpose` State Updates, and one with the `avatars` Capability drives Avatars with `avatarpose`, which the game buffers and interpolates 100 ms in the past from sender-timestamped samples (its ADR 0003).

ADR 0001 and ADR 0002 require any gameplay synchronization to be recorded in its own decision before it uses `LanProtocol`. A Pose stream is the first continuous gameplay traffic: 30 small messages per second in each direction, where only the newest one matters.

Alternatives considered:

- A separate UDP channel for Poses. Rejected for now: on a trusted LAN, TCP retransmission and head-of-line blocking are rare and small next to the game's 100 ms render delay, and a second channel would need its own admission, liveness, and teardown alongside the existing session connection. `amnesia-tdd-tcp` rejected a separate real-time channel for similar lifecycle reasons (its ADR 0004).
- Relaying the raw `STATE localpose` or `avatarpose` line. Rejected: it would let a LAN peer choose the text a Game Peer writes to its local game.

## Decision

`LanProtocol` gains one closed, bounded message type and its version becomes 3:

- `pose { timeMs, teleportCounter, x, y, z, yaw, pitch, crouch, map }`, sent by either admitted Game Peer, carrying the fields of its local game's latest `STATE localpose` unchanged in meaning.

Every field is validated structurally (numeric ranges, finite numbers, a bounded map path without control characters). The receiving Game Peer, not the wire message, decides to issue `avatarpose` for the Avatar it created for the other player, formatting the line itself. The invariant becomes: `LanProtocol` may name an installed Custom Story to start and may carry the other player's Pose, but never an arbitrary remote script or command.

Pose sending is latest-wins: an unsent Pose is replaced by a newer one rather than queued, and never delays Chat Entries or session messages behind it. `NoDelay` is set on every Game Peer socket so small frames are not held back by Nagle's algorithm.

## Consequences

- Version 2 Game Peers are rejected at admission as incompatible.
- Pose traffic shares the session connection's liveness: a lost connection ends both the Multiplayer Session and the Shared Pose together, with no separate channel to fail independently.
- If TCP proves inadequate on real networks (for example Wi-Fi loss causing visible stalls), moving Poses to a datagram channel requires a new decision record that supersedes this one; the `pose` message shape is chosen so it could be carried unchanged.
- Further gameplay synchronization (props, interactions, scripts) still requires its own decision record.

## Amendment: the raised lantern

amnesia-spelos/amnesia-tdd-tcp#40 added a `<lantern>` flag after `<crouch>` in both `STATE localpose` and `avatarpose`, changing Protocol Version 2 in place because it had not been released. The `pose` message becomes `pose { timeMs, teleportCounter, x, y, z, yaw, pitch, crouch, lantern, map }`, with `lantern` a boolean required when read, and `LanProtocol` becomes version 4. The bump makes a version 3 Game Peer fail admission as incompatible instead of being admitted and then dropped at its first `pose`, which a mismatched build on one of the two LAN computers would otherwise cause.
