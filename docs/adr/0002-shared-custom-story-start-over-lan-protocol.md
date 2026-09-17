# 2. Shared Custom Story Start over the LAN protocol

## Status

Accepted. Amends the `LanProtocol` invariant stated in ADR 0001.

## Context

Spike 6 of issue #1 requires that when the Session Host starts a Custom Story, the Joining Player's game starts the same Custom Story. `amnesia-tdd-tcp` (issue amnesia-spelos/amnesia-tdd-tcp#26) exposes `startcustomstory:<id>` and reports every fresh start, including menu starts, as `EVENT:CustomStoryStarted:<id>`.

ADR 0001 states that `LanProtocol` has no message type that names or executes a remote command, and that a future gameplay transport must not reuse `LanProtocol` without its own decision record. Reproducing a start on another machine necessarily lets one Game Peer cause a state-changing Command on the other game.

Alternatives considered:

- Treat the start as session coordination already covered by ADR 0001. Rejected: it silently weakens a stated invariant.
- Build a separate gameplay channel now. Rejected: speculative; no gameplay synchronization exists yet.

## Decision

`LanProtocol` gains two closed, bounded message types and its version becomes 2:

- `CustomStoryStarted { identifier }`, sent only by the Session Host's Game Peer after its local game reports a fresh Custom Story start while a Joining Player is admitted.
- `CustomStoryStartOutcome { identifier, outcome }`, sent only by the Joining Player's Game Peer in reply, with `outcome` one of `started`, `not-found`, `invalid`, `not-in-main-menu`, `unavailable`.

The identifier is validated as a Custom Story Identifier (non-empty, bounded length, no `|`, `:`, or control characters). The receiving Game Peer, not the wire message, decides to issue `startcustomstory`; the LAN message names an installed Custom Story, never a script or arbitrary Command. The invariant becomes: `LanProtocol` may name an installed Custom Story to start, but never an arbitrary remote script or command.

## Consequences

- Version 1 Game Peers are rejected at admission as incompatible rather than disconnecting mid-session on an unknown message.
- A trusted-LAN peer can now make the other game start any installed Custom Story while it is in the main menu. This is accepted under the existing trusted-LAN boundary.
- Any further gameplay synchronization (movement, props, scripts) still requires its own decision record; this ADR does not open `LanProtocol` to general Command relay.
