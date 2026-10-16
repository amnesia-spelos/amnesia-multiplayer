# 7. The Multiplayer Relay forwards to several Joining Players

## Status

Accepted; implementation paused by ADR 0008. Amends ADR 0001, which pairs the Session Host with exactly one other Game Peer, and settles the consequence of ADR 0004 that more than one Joining Player "requires Relay forwarding, sender identifiers, and per-player Avatars".

## Context

The maintainer wants to playtest with three computers: a Session Host and two Joining Players. Every part of the Game Peer assumes one other player today:

- The Multiplayer Relay admits one connection and answers any other `JoinRequest` with "The Multiplayer Session is full." It reads frames from that connection but never forwards them, so a Joining Player hears only the Session Host.
- No `LanProtocol` message says who sent it. Shared Pose drives one Avatar, `partner`. Holds records a Holder as either the local player or "the other" one.
- Inbound budgets are counted per connection and sized for one remote player (#61).

The game side already allows several Avatars per Game Interaction Protocol Session, so the missing parts are in the Game Peer and `LanProtocol`.

Alternatives considered:

- **Full mesh.** Every Game Peer connects to every other one. One hop between Joining Players, but each one has to discover and reach the others through their firewalls, and Claims still need one ordering point (ADR 0004), so the Session Host stays special anyway.
- **Each Game Peer stamps its own identity.** Simpler for the Relay, but a Joining Player, or a mismatched build, could act as another player and end or take over their Holds.
- **A two-player hack for one playtest.** It touches the same modules as real support and would be thrown away.

## Decision

- **Star topology.** Each Joining Player keeps one connection, to the Session Host. The Multiplayer Relay forwards every message a Joining Player sends to every other Joining Player, and the Session Host's own messages go to all of them. Chat Entries, Poses, Hold messages, bodies, Shared Callbacks, and button and lamp states all follow this path. Claims are still ordered by the Relay's arbiter alone.
- **The Relay names the sender.** At admission the Relay assigns each Joining Player a small integer that is unique within the Multiplayer Session. The Session Host is 0. The Relay adds the sender's number to every message it forwards, and it never accepts one from a Joining Player. Receivers key Avatars (Avatar Identifier `player-<n>`), Holders, and Chat Colors by this number.
- **At most four players** (the Session Host and three Joining Players), as a constant. The fifth gets "The Multiplayer Session is full."
- **A Joining Player's departure ends only that player's part.** Their Avatar is removed and their Holds end in every other game, and the Multiplayer Session continues. The Session Host's departure still ends it for everyone (ADR 0001: no host migration).
- **Joining after a Shared Custom Story Start is admitted unchanged.** The late Joining Player starts the Custom Story by hand; replaying the start is left to #23 and amnesia-spelos/amnesia-tdd-tcp#38.

`LanProtocol` becomes version 10 so that a version 9 Game Peer is rejected at admission instead of failing on its first forwarded frame. (This read version 8 when recorded; #62 took version 8 for the pickup, combine, and flashback Shared Callbacks first, and #64 took version 9 for the sticky attach Shared Callback.)

## Consequences

- A Joining Player sees another Joining Player's Pose and bodies after two LAN hops instead of one, which is small next to the game's 100 ms interpolation delay.
- The Session Host's upload grows with each Joining Player, and a Joining Player's inbound link carries the traffic of every other player. #61 settled the Inbound Allowances: the Session Host applies a Sender Allowance sized for one player to each Joining Player and forwards only the messages within it, dropping the frame that exceeds it as it disconnects that player. A Joining Player's Inbound Allowances are fixed at the four-player maximum less one, times one player's, so they never change as players come and go and only catch a misbehaving Session Host. A Joining Player does not count per sender, because its only remedy, disconnecting the Session Host, would end its Multiplayer Session for one other player's fault. Game Peers do not pace what they send: each one-player allowance stays above what one player legitimately sends.
- The Session Host's computer is a single point of failure for every Joining Player, as it already was for one.
- Shared Custom Story Start now reports one outcome per Joining Player.
