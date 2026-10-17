# 6. Shared Callbacks replay Map Script registrations

## Status

Accepted. Amends ADR 0005: "Other script changes to a Peer-Driven Entity are not refused yet" is settled below, and the interim rule that each game runs collide callbacks its Peer-Driven Entities trigger ends when Shared Callbacks ship.

Amended by #62 after the 2026-10-11 playtest (#50): item pickups, inventory combines, and Flashback areas become shared kinds, and the inventory rule changes with them.

Amended by #64 after the same playtest: sticky area attachments of a Held entity become a shared kind.

Amended by #77 for Shared Enemies (ADR 0009): a Shared Enemy's triggers are shared like a player's, by the Session Host's game, which simulates it, and a Shared Enemy disabling itself at the end of its patrol becomes a shared kind.

## Context

Issue #43: a Map Script callback runs only in the game of the player who triggered it. In the chapter 1 playtest of #40, the Session Host fitted `cogwheel03_dyn` in `13_machine_room`; their game swapped it for the mounted cogwheel, and the Joining Player's copy stayed loose. Trigger areas, interactions, and item uses drift the two games' maps apart the same way.

The 2026-10-11 playtest (#50) found three more: the `03_archives` key pickup scare and the `02_entrance_hall` flashback ran only for the player who set them off, notes one player picked up never opened for the other, which desynced voice acting, and a script-combined drill left the other player's own copy of the third part in their inventory for good. A prop that stuck to a sticky area in the Holder's game could miss it in the other game, which only follows 30 Hz samples, and then fell under that game's physics and stayed unstuck.

The engine source (`amnesia-tdd-tcp`, `src/amnesia/src/game`) shows where each callback runs:

- Interact callbacks (`SetEntityPlayerInteractCallback`, including the one a grab runs) and use-item callbacks (`AddUseItemCallback`) run only in the acting player's game. A Peer-Driven Entity never runs them.
- Collide callbacks with `"Player"` as parent check only the local player's body. Collide callbacks involving a Peer-Driven Entity run in every game, depending on sample timing (ADR 0005).
- Levers, wheels, and multi-sliders are Holdable; driven motion crosses their limits in every game, so their state-change, `ConnectEntities`, and slider callbacks already run everywhere.
- A sticky area checks each update for bodies inside it, runs its `AttachFunction`, and attaches one it finds. A Peer-Driven Entity's samples put it at different positions in each game, so the check sees it at different times or not at all.
- Buttons and lamps are not Holdable. A press or lighting changes their state in one game only.
- The game can find an interact callback by entity and a use-item callback by item and entity, and a collide callback by walking the parent's callback list, so a registration can be found from identifiers alone.

Alternatives considered:

- **Report effects.** The triggering game reports every state change its callback makes (entity active, door locked, light, health) and the others apply them. Exact under script drift, but it needs hooks on about 100 script functions and separate handling for timers and variables.
- **Relay the callback's function name or script text.** Simplest for the game, but `LanProtocol` would then name code to run, which breaks the Trusted-LAN boundary.
- **Order Shared Callbacks through the Multiplayer Relay like Claims.** Prevents a callback triggered by two players running twice, at the cost of a round trip before every trigger.

## Decision

- **A Shared Callback replays the registration, not the effects.** Every other player's game runs the callback its own Map Script registered for the same entities. Divergence from drifted script state (local variables, `RandInt`) is a known limit, filed as bugs case by case.
- **Shared kinds:** collide callbacks with `"Player"` as parent; collide callbacks involving a Peer-Driven Entity, relayed only by the Holder's game while the other games stop running them; interact callbacks; use-item callbacks; item pickups; inventory combines; Flashback areas; sticky area attachments of a Held entity; collide callbacks involving a Shared Enemy, relayed only by the Session Host's game while the other games' driven copies trigger none; a Shared Enemy disabling itself at the end of its patrol, which runs its `OnAutoDisabled` callback in every game. A button's switched state and a lamp's lit state are shared by their own messages, so each game runs its own connection callbacks.
- **Item pickups are shared.** Picking up a map-placed non-consumable item (`Puzzle`, `Note`, `Diary`, `Lantern`, `HandObject`, `Coins`) picks it up in every game: it leaves the world, enters the inventory, runs its `OnPickup` callback, and opens a note or diary. Consumables (tinderbox, lamp oil, sanity potion, laudanum) stay local, pickup and callback both. An item already gone from a game's world is a no-op there, so two players picking up the same item run its callback once in each game.
- **Combines are shared.** A combine callback runs in every game whose inventory holds both items, named by the two item names. Combining two consumables stays local.
- **Flashback areas are shared.** The receiving game deactivates the area and plays the full flashback. An area already inactive there is a no-op.
- **Sticky attachments are decided by the Holder.** Like collide callbacks with a Peer-Driven Entity, only the Holder's game checks a sticky area for the entity's bodies and reports an attach, naming the area, the entity, and the body. The other games stop checking bodies they drive, and attach their copy, running the area's `AttachFunction`, when told to. The attach ends the entity's Hold in every game. A game whose copy cannot attach, as when its area already holds a body, returns the entity to its own physics, and a game whose player won a Claim on the entity before the attach was relayed ignores it. Detaching follows the next Claim on the attached entity: grabbing it detaches it in the grabbing game, and every other game detaches its copy when it starts driving it.
- **Kept local:** look-at callbacks, timers, prop animation, effect voice, checkpoint, and lantern callbacks, ignite and other entity callback func types besides a shared pickup's `OnPickup` and a Shared Enemy's `OnAutoDisabled`, map lifecycle (`OnStart`, `OnEnter`, `OnLeave`, `OnUpdate`), and level-door map changes. A replayed parent starts its own follow-ups.
- **Inventory.** Non-consumable pickups are shared; consumables and runtime-created items are not. A replayed `RemoveItem` or `GiveItem` acts on each game's own inventory, so a `RemoveItem` of an item the player lacks does nothing.
- **Named by identifier.** A Shared Callback carries the map path, integer entity IDs, a collide state, for a sticky attach an integer body ID, and for use-item and combine bounded item names. The receiving Game Peer composes the command; the game looks up a registration and never runs text it was sent. A missing registration is a no-op with one log line.
- **Every player trigger is shared**, even one a Shared Callback set up, and so is every trigger of a Shared Enemy while another player is on its map. A door's `Break` callback is not a Shared Callback: a door a Shared Enemy breaks is shared as its own event (ADR 0009), and each game runs its own `Break` callback as it breaks its copy. A callback run by a Command from the Game Peer is never shared back. Two players triggering the same one-shot is harmless; a repeating callback may run twice.
- **Player control is the receiver's.** While a Shared Callback runs, the game ignores calls that take control or the camera: `SetPlayerActive`, `TeleportPlayer`, `StartPlayerLookAt`, `MovePlayer*`, `SetPlayer*Mul`. Sanity, music, sounds, and screen effects still play. Two exceptions are part of the shared kind itself, not script control: a shared flashback's own player effects (slowdown, sepia), and a note or diary opening on a shared pickup.
- **A Shared Callback may deactivate or move a Peer-Driven Entity.** Every game runs the same script, so they agree; the Hold ends with the entity.
- **Pending Shared Callbacks.** Each Game Peer keeps, per map, the Shared Callbacks other players sent for maps its game is not on, and applies them in order after its game enters that map. They last the Multiplayer Session and are cleared by a new Shared Custom Story Start.

## Consequences

- `amnesia-tdd-tcp` needs a Version 2 report of shared kinds, a command that runs a callback by registration and marks it as a Shared Callback, skipping collide callbacks with a Peer-Driven Entity in a non-Holder game, button and lamp state commands, and allowing script deactivation of a Peer-Driven Entity.
- `LanProtocol` gains a Shared Callback message and button and lamp state messages; the Trusted-LAN boundary in `CONTEXT.md` is amended when they ship to say a message may name a Map Script registration by identifiers, never a function.
- Entering a map with many Pending Shared Callbacks runs them all at once, with their sounds and sanity effects. Accepted to avoid softlocks.
- Scares and sanity damage reach players who did not trigger them.
- A shared note or diary opens, and its voice plays, for every player on the map, and a shared flashback slows and sepia-tints every player there.
- `LanProtocol` version 8 adds the pickup, combine, and flashback kinds, so a version 7 Game Peer is refused at admission. Version 9 adds the sticky attach kind, so a version 8 Game Peer is refused. Version 13 adds the enemy auto-disable kind, beside enemy door breaks, so a version 12 Game Peer is refused.
- The legacy `SCRIPT_CALL:` report and `exec:` command remain reachable from Version 2 sessions; their removal is tracked separately in `amnesia-tdd-tcp`.
