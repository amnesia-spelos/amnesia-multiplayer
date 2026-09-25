# 4. Holds: Relay-ordered Claims over the LAN protocol

## Status

Accepted. Amends the `LanProtocol` invariant stated in ADR 0001, ADR 0002, and ADR 0003, which require further gameplay synchronization to have its own decision record.

## Context

Codename Grasp (issue #26) lets players grab, rotate, and throw props and swing or spin doors, levers, and valves, with the rule from issue #1 that no two players interact with one entity at once, because Amnesia designed no physical interaction for multiple users. Issue #28 had to decide where that exclusivity is arbitrated and what crosses the network.

Research that shaped the decision:

- `amnesia-tdd-tcp` `docs/research/interaction-surface.md` (#44): a grab is a 60 Hz PID force stream, not a joint; Newton's solver state, dropped logic ticks, and PID history make two machines diverge from identical inputs. Relaying inputs is not viable.
- `amnesia-tdd-tcp` `docs/research/physics-determinism.md`: Newton 2.00 is not deterministic across machines, randomness plays no part in physics, and lockstep is infeasible because the two machines never share inputs (the Avatar body is teleported to a delayed Pose; update order follows pointer order).
- `docs/research/sync-seam-cost.md` (#27): an `interactions` Capability is additive at Game Interaction Protocol Version 2; `LanProtocol` has an exactly-once ordered lane and a latest-wins Pose slot; non-Pose messages are capped at 40 per second; entities can be named by map-authored integer IDs.
- The Multiplayer Relay today is the listener half of a single peer-to-peer connection; the Session Host's own traffic bypasses it.

Alternatives considered:

- **The Relay grants Holds before they take effect.** One source of truth, but only the Joining Player pays a round trip on every grab, so the two players feel different games.
- **Optimistic Claims with "the Session Host wins" as the tie-break.** Instant for everyone, but the rule has no answer once two Joining Players contend, so it would be rewritten when a third player arrives.
- **Relaying inputs and re-simulating.** Ruled out by the research above.
- **Per-type state relay** (wheel angle, door openness, lever setters). Five mechanisms, three missing setters, and a double-apply hazard with interact connections.
- **Accepting divergence of props nobody holds.** Cheap, but a thrown prop that knocks a stack leaves the two worlds different wherever it matters for play.

## Decision

A **Hold** is a player's exclusive right to decide the motion of one map-placed entity. It is taken by a **Claim**: by interacting with the entity, or by touching it with the player's own body or with another entity they Hold. It lasts until the entity comes to rest after the Holder is done with it (**Settling**).

- **Claims take effect at once and are ordered by the Relay.** The player's own game grabs immediately; the Claim goes to the Relay, which accepts the first Claim for an entity and answers a later one with a **Claim Denial**, ending the loser's interaction. The Session Host's Game Peer submits its own Claims through the same arbiter, so there is one ordering point regardless of player count. With two players this behaves like the Session Host winning near-simultaneous Claims. The Session Host is an arbiter of order, never a physics authority.
- **The Holder relays resulting state, never inputs.** While interacting and while Settling, the Holder streams the world transform and velocity of every body it Holds. Interaction start and end are relayed separately, because the game's own prop logic keys off whether an entity is being interacted with, which ends at release while the Hold continues.
- **Authority propagates by contact.** A body the Holder streams, or the Holder's own body, touching a free map-placed prop produces a Claim for it, recursively. Contact never takes an entity that already has a Hold.
- **Breaking is the Holder's decision**, relayed as a discrete message with the final transform and velocity.
- **Every Hold ends** when it settles (at rest, or at a Settling cap), when the Holder departs or changes map, when the receiving player changes map, or when either local game Session ends. The entity then resumes local physics from its last streamed state.

On the wire: `LanProtocol` becomes version 5 and gains Claim, Claim Denial, interaction start/end, break, settled, and a body stream. The body stream is one latest-wins message per tick per player at 30 Hz carrying up to 32 bodies, with its own inbound rate bucket, never on the 40-per-second session lane and never behind the Pose gate. Entities are named only by map path plus map-authored integer prop and body IDs; runtime-created entities cannot be Held. The invariant becomes: `LanProtocol` may name an installed Custom Story to start, carry the other player's Pose, and carry Holds on map-placed entities by integer identifier, but never an arbitrary remote script or command.

## Consequences

- Version 4 Game Peers are rejected at admission as incompatible.
- A player whose Claim is denied sees the prop leave their hands. On a LAN the window is a few milliseconds, so this is rare.
- Consequences of a Held entity's motion — gates on interact connections, `ConnectionStateChange`, state callbacks — are reproduced by each machine's own prop logic and map script, once per machine. Scripts that use `RandInt`, depend on timer phase, or assume a single player can diverge; map-state synchronization is left to a future story-authority effort.
- Past the 32-body stream budget, and for props moved without any player (scripted impulses, enemies), each machine simulates alone and may diverge.
- When a Held entity collides with an entity Settling under the other player's Hold, each Holder's machine briefly shows its own collision. Handing a Settling Hold between players would fix it and would need per-entity authority sequence numbers.
- Supporting more than one Joining Player still requires Relay forwarding, sender identifiers, and per-player Avatars, but not a new arbitration design.
