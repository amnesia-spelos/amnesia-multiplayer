# 5. The Holder decides script breaks of Held entities

## Status

Accepted. Amends ADR 0004: "Breaking is the Holder's decision" now covers breaks a map script causes, and the consequence that each machine's map script reproduces the effects of a Held entity's motion "once per machine" is qualified below.

## Context

Issue #44 reported a prop that a script broke in one player's game while the other player's copy stayed whole. The case was `CollideJarLeaveArea` in `maps/main/ch01/03_archives.hps`: carrying `vase02_ghost_1` out of `AreaJar` runs `SetPropHealth` to 0, plays a sound, and gives 10 sanity damage. The vase breaks in the Holder's hands, which the game reports as `interactionend ... destroyed`, with no `reportbroke` and no final state (issue #42).

A manual test on one PC (recorded carry, replayed into the vase as a Peer-Driven Entity; see the reproduction on #42) showed:

- A Peer-Driven Entity runs entity-area collide callbacks, and a script can break it. The game guards only its contacts (`LuxProp_Object.cpp`), not `SetPropHealth` or `BreakEntity`.
- The other game sees the position outside the area only if the update where the vase first stops overlapping the area is sampled. Sampled bodies go out every 17–33 ms, so about one update in three is not sent, and a fast carry makes a miss likelier.
- So one carry ends two ways in the other game. Either it runs the callback itself, with the sound, the sanity damage, and a break from its own last streamed position. Or it runs nothing, and the copy stays whole, driven, and out of reach.

Issue #43 will share script callbacks between games, by relaying them or by reporting their effects. Either way, an effect that reaches the other game by two routes (its own map script, the Hold messages, and a relayed callback) happens twice or in two different ways.

Alternatives considered:

- **Whichever game breaks first.** Either game may break its copy, and the Game Peers ignore a second break. The debris starts from different places, and the outcome still depends on sample timing.
- **Each game breaks its own copy.** The Hold just ends. The two games differ whenever only one game's script fires, which the test shows is a matter of timing.
- **Breaking from the last streamed state** instead of an exact final state. The state can lag by one report, and for a prop with several bodies, the streamed body may not be the break body (`BreakEntityAlignBody`).
- **Not running collide callbacks that involve a Peer-Driven Entity in the other game now.** Effects would never depend on timing, but until #43 ships, a Held prop placed on a trigger area would no longer open a gate or advance a puzzle in the other player's game.

## Decision

- **Only the Holder's game breaks a Held entity, whatever causes the break.** The game refuses script changes to the health of a Peer-Driven Entity (`SetPropHealth`, `AddPropHealth`, `BreakEntity`, and the like), as it already refuses breaks from its contacts. The other game's copy breaks only by `entitybreak`.
- **A break while held carries its exact final state.** The game sends `reportbroke` with the break body's final state right after `interactionend ... destroyed`, so `Holds` relays every break of a Held entity on the same path.
- **Other script changes to a Peer-Driven Entity are not refused yet.** A change to its motion is overwritten by the next streamed sample. Whether a script may deactivate it, or move it outright, belongs to #43, which decides whose script is authoritative.
- **Until #43, each game still runs its own callbacks**, including ones its Peer-Driven Entities trigger. With the refusal above, a break no longer depends on sample timing; the callback's other effects still may.
- **Every effect reaches the other game by exactly one route.** When #43 relays callbacks or their effects, the other game stops running collide callbacks that involve a Peer-Driven Entity, a callback started by a Command from the Game Peer (`entitybreak`, a relayed callback) is never relayed back, and the break callback that a break runs in each game is never relayed.

## Consequences

- This needs a change to the game in `amnesia-tdd-tcp`: the refusal and the `reportbroke` after `destroyed`. `LanProtocol` is unchanged; `broke` already carries a final state.
- A script that breaks a prop the other player Holds has no effect in that game. The break arrives from the Holder.
- Until #43, a callback that a Held entity triggers may still play its sound, scare, or sanity damage in the other game or not, depending on sample timing. #43 owns which effects belong to one player and which to both.
- A script break of a prop nobody Holds is an effect of a callback, so #43 decides how it is shared.
