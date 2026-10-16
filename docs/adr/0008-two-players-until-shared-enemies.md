# 8. Two players until Shared Enemies are done

## Status

Accepted. Amends ADR 0007: its design stands, but its implementation (#66, from #67 on) is paused until the Shared Enemies map (#51) is done.

## Context

ADR 0007 decided how the Multiplayer Relay forwards traffic between up to three Joining Players, because the maintainer wanted to playtest with three computers. Only #60 and #61, the Inbound Allowances, have been built since.

Shared Enemies (#51) are the last large piece of the Main Story experience, and every decision they need gets harder with each extra player: who the enemy chooses between, who decides a hit, what a death does, who it hears. Testing them needs one person per player for every walkthrough.

`docs/research/session-bottlenecks-and-limits.md` found that two players hit no capacity limit in a realistic playthrough. The gaps it found for four players are design gaps, not capacity limits: each Joining Player's outbound queue keeps one Pose slot and one Bodies slot, so forwarded Avatars would overwrite each other, and the game's Peer-Driven Entity playback has one sender clock, so props driven for two other Holders at once would hitch.

Two-player co-op is the game; more players are a bonus.

Alternatives considered:

- **Finish four players first.** Every Shared Enemy decision would have to be designed and playtested for three or four players before anything ships.
- **Abandon four players.** Throws away ADR 0007's settled design and #61's sizing, which cost nothing to keep.

## Decision

- **The Multiplayer Session ships for two players through Shared Enemies.** #66 and its tickets stay open and paused; they resume after #51.
- **Code stays list-shaped.** New code models "the other players" as a list that happens to have one entry, never as "the other player", so resuming #66 extends lists rather than rewriting singular ones.
- **ADR 0007 still holds** for when the work resumes, including `MaxPlayers = 4` in the Joining Player's Inbound Allowances, which stays as #61 built it.
- **Shared Enemies are designed for two players**, with a list of perceived players rather than a second one, and are revisited for more when #66 resumes.

## Consequences

- A playtest needs two computers and two people.
- The four-player design gaps the research found are recorded on #67 and in a game-side ticket under #66, so they are not lost while the work is paused.
- When #66 resumes, Shared Enemies need a check for three or four players: targeting among several Avatars, and the Session Host's upload of every enemy's stream to every Joining Player.
