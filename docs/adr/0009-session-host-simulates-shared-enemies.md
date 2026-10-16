# 9. The Session Host's game simulates Shared Enemies

## Status

Accepted. Amends ADR 0004, whose Session Host "is an arbiter of order, never a physics authority": for Shared Enemies, the Session Host's game is the authority. Holds are unchanged.

## Context

A Shared Enemy must be one threat to both players (#51). The research behind #54 settled what can carry it:

- amnesia-spelos/amnesia-tdd-tcp#82: an enemy is a self-moving character body steered by A*, with unseeded `rand()` throughout its AI, so two games cannot simulate it and agree. A driven copy works on the Avatar recipe from a small Pose-rate stream. Today an attack never hits an Avatar, and a door an enemy breaks spawns runtime entities.
- amnesia-spelos/amnesia-tdd-tcp#81: every sense reads one local player into one set of target flags, and Avatars make no sound.
- amnesia-spelos/amnesia-tdd-tcp#83: a death loads a checkpoint without a map reload, and `LoadCheckPoint` resets every enemy on the map in the dying player's game only.
- amnesia-spelos/amnesia-tdd-tcp#84: Shared Callback replay of enemy Map Script functions mostly agrees; two functions read the local player.
- #52: shipped co-op games run the AI on the host or hand it to the targeted player's game, and decide hits in the victim's own game.

Alternatives considered:

- **The targeted player's game simulates, handing off when the target changes.** No latency for the player being chased, but the AI's path, state, and timers must move between games mid-chase, and the other player must still be perceivable as an Avatar.
- **Every game simulates, with corrections.** Ruled out by the unseeded randomness.

## Decision

- **The Session Host's game simulates every Shared Enemy.** Every other game drives a copy from the Session Host's stream, as it drives an Avatar, and the copy has no AI of its own. The authority never changes hands while both players are on the map.
- **Targeting is vanilla, over every player.** Each sense considers the local player and every Avatar. The enemy keeps its current target until it loses that player, then takes whichever player it perceives best. The enemy sees Avatars but does not hear them yet; hearing is a later ticket.
- **The victim's game decides a hit.** The Session Host sends an attack event, and each game tests it against its own player, from what that game shows. The Session Host's own player is hit the vanilla way.
- **A single death never resets a Shared Enemy.** The dead player loads their checkpoint; the enemy loses that target and keeps hunting the survivor. The Session Host's `LoadCheckPoint` must not reset Shared Enemies while the other player is alive; a copy ignores the reset. When both players are dead, the vanilla reset applies.
- **Only while both players are on the map.** A player alone on a map has the vanilla enemy in their own game. When the other player arrives, the Session Host's enemy takes over and a Joining Player's own enemy is replaced by the copy, which may visibly jump; when a player leaves, the one who stays keeps or regains a vanilla enemy.
- **A door an enemy breaks is an event.** Every other game breaks its own copy of the door, and the debris stays local.
- **Enemy-triggered callbacks run once.** A copy fires no Map Script callbacks. The Session Host relays the enemy-triggered callbacks that change shared world state as Shared Callbacks (ADR 0006).
- **On the wire**, a Shared Enemy is an **Enemy Pose** stream (enemy, position, rotation, animation) at Pose rate, with its own Inbound Allowance and one newest-only outbound slot per enemy, plus attack and door-break events on the ordered lane. The `LanProtocol` invariant and the Trusted-LAN boundary are extended when those messages are built.
- **The Grunt comes first**, then the Brute, then the Kaernk.

## Consequences

- A Joining Player sees the enemy about 100 ms late, the game's interpolation delay. Deciding hits in the victim's game keeps a hit honest to what that player saw, at the cost of a lagging player dodging slightly more easily.
- The Session Host's game must let enemies perceive and target an Avatar, and test attacks against it: a game-side change in `amnesia-tdd-tcp`.
- An arrival or departure mid-chase is the one handoff, and it may show a visible jump.
- The Session Host's death no longer resets the enemy, which vanilla `LoadCheckPoint` would do for both players, so dying is not an escape for the survivor.
