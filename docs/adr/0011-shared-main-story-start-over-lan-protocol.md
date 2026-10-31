# 11. Shared Main Story Start over the LAN protocol

## Status

Accepted. Amends ADR 0002, whose invariant lets `LanProtocol` name only an installed Custom Story to start.

## Context

Issue #23 asks that when the Session Host starts a new game of the Main Story from the main menu, every Joining Player's game starts it too, as a Custom Story start already does. `amnesia-tdd-tcp` (amnesia-spelos/amnesia-tdd-tcp#38) added the `startmainstory` Command, answered `RESPONSE:startmainstory:starting` or `RESPONSE:startmainstory:not in main menu`, and reports every fresh Main Story start, including one from the menu, as `EVENT:MainStoryStarted`. amnesia-spelos/amnesia-tdd-tcp#122 makes the game send that Event before it loads the start map, so the Game Peer can relay it while the Session Host's game is still loading.

ADR 0002 lets one Game Peer cause a state-changing Command on another game, but only to start an installed Custom Story named by its Custom Story Identifier. Starting the Main Story is a different start, so it needs its own decision.

Alternatives considered:

- **Treat the Main Story as one more Custom Story Identifier.** Rejected: the Main Story has no identifier, and a reserved name would let a Custom Story folder with that name collide with it.
- **Leave Main Story starts unshared.** Rejected: the Main Story is what the players want to play together (ADR 0008).

## Decision

`LanProtocol` gains two closed message types, and its version becomes 15:

- `main-story-started {}`, sent only by the Session Host's Game Peer when its local game reports a fresh Main Story start while a Joining Player is admitted. It carries no fields.
- `main-story-start-outcome { outcome }`, sent only by a Joining Player's Game Peer in reply, with `outcome` one of `started`, `not-in-main-menu`, `unavailable`. `not-found` and `invalid` are malformed here, because the Main Story cannot be missing or invalid.

The Session Host's Game Peer relays the start as soon as it reads the Event, never waiting for `MapChanged` or any later Event, so the games load together. The receiving Game Peer decides to issue `startmainstory`; the message names no script or Command. Hard mode is not shared: a Joining Player always starts in normal mode. As with a Custom Story, a Joining Player's own start stays local, and a start the Game Peer reproduced for the Session Host is not relayed back.

The invariant becomes: `LanProtocol` may name an installed Custom Story to start, or ask for the Main Story to start, but never an arbitrary remote script or command.

## Consequences

- Version 14 Game Peers are rejected at admission as incompatible, instead of being disconnected on the first Main Story start.
- A trusted-LAN peer can now make the other game start a new game of the Main Story while it is in the main menu. This is accepted under the existing trusted-LAN boundary, as ADR 0002 accepted it for Custom Stories.
- The Game Peer handles Custom Story and Main Story starts with the same code (`SharedStoryStart`), so both follow the same rules for a Joining Player's own start and for a start reproduced for the Session Host.
- Continue, Load Game, and hard mode are still not shared.
