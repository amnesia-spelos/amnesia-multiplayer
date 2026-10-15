# Multiplayer Coordination

This context covers coordination between separate Amnesia game instances participating in a shared multiplayer experience. Terms owned by the Game Interaction Protocol retain the meanings defined by `amnesia-tdd-tcp`.

## Language

**Multiplayer Session**:
A shared multiplayer experience coordinated for a group of participating game instances.
_Avoid_: Lobby, room

**Multiplayer Relay**:
The process that coordinates a Multiplayer Session: it admits Game Peers and orders their Claims.
_Avoid_: Server, SignalR server, hub

**Game Peer**:
The multiplayer application instance paired with one locally running game and participating in a Multiplayer Session.
_Avoid_: Client, multiplayer client

**Session Host**:
The player whose computer runs the Multiplayer Relay.
_Avoid_: Server, authority

**Joining Player**:
A player whose Game Peer joins the Session Host's Multiplayer Session over the local network; a Multiplayer Session may have several.
_Avoid_: Client, guest

**Shared Custom Story Start**:
The Session Host's fresh start of a Custom Story, reproduced on every Joining Player's game within the same Multiplayer Session.
_Avoid_: Story sync, map sync, remote start

**Shared Pose**:
A player's Pose, continuously reproduced as an Avatar in every other player's game within the same Multiplayer Session.
_Avoid_: Movement sync, position sync, remote player, ghost

**Hold**:
A player's exclusive right to decide the motion of one map-placed entity in the shared world, taken by interacting with it or by touching it with their body or another entity they Hold, and lasting until it comes to rest after the Holder is done with it. While it lasts, no other player can interact with that entity.
_Avoid_: Lock, ownership, grab lock

**Holder**:
The player who has a Hold.
_Avoid_: Owner, controller

**Claim**:
A player's taking of a Hold on an entity, effective at once in their own game and confirmed or denied by the order in which the Multiplayer Relay receives it.
_Avoid_: Grab request, lock request

**Claim Denial**:
The Multiplayer Relay's answer to a Claim that arrived after another player's Claim on the same entity, which ends the losing player's interaction with it.
_Avoid_: Revocation, rejection

**Settling**:
The part of a Hold during which the Holder is not interacting with the entity but still decides where it goes, until it comes to rest: after the Holder lets go, or from the start of a Hold taken by touch.
_Avoid_: Cooldown, flight, release window

**Shared Callback**:
A Map Script callback that a player triggers in their own game, run in every other player's game too, as the callback that game's own Map Script registered for the same entities. Picking up a map-placed non-consumable item, combining two inventory items, setting off a Flashback area, and a sticky area attaching an entity the player Holds are Shared Callbacks too: every other game makes the same pickup, combine, flashback, or sticky attach for itself. Consumable pickups and combines stay local.
_Avoid_: Callback sync, remote script, trigger relay

**Pending Shared Callback**:
A Shared Callback for a map a player's game is not on, kept until that game enters the map.
_Avoid_: Queued trigger, deferred callback

**Shared Enemy**:
A map-placed enemy that is one threat to every player in a Multiplayer Session, able to hunt and attack any of them, rather than a separate copy in each player's game.
_Avoid_: Synced monster, remote enemy, AI sync

## Trusted-LAN boundary

The Game Interaction Protocol connection between a Game Peer and its local game is loopback-only and never crosses the network. The Multiplayer Relay's LAN listener is intended for a private, firewalled network between trusted computers: it is unauthenticated, so the Session Host must control access at the firewall, not the protocol. The LAN wire protocol (`LanProtocol`) carries only bounded, structurally validated Chat Entries, each with an optional author Chat Color of exactly six hex digits, Join/Admission negotiation, Heartbeats, Departure notices, Shared Custom Story Starts with their outcomes, Poses for the Shared Pose, Holds: Claims, Claim Denials, interaction starts and ends, breaks, settles, and the Holder's body stream, and Shared Callbacks with button and lamp states. A Chat Color is only digits that the receiving Game Peer formats into its own `chatentry` line, a Shared Custom Story Start may name an installed Custom Story by its Custom Story Identifier, and a Pose carries only numbers, two flags, and a map path that the receiving Game Peer formats into its own `avatarpose` line. A Hold message names a map-placed entity only by a map path and integer prop and body IDs, and carries only bounded numbers, unit quaternions, a flag, and at most 32 bodies; the receiving Game Peer composes every `entity...` line it writes to its own game. A Shared Callback names a Map Script registration, an item to pick up, a Flashback area, or a sticky area and the body it attached only by a map path, integer entity and body IDs, a collide state, and for an item use or a combine one or two item names of 1 to 64 printable ASCII characters without spaces; a button or lamp state names its entity by a map path and an integer ID and carries one flag. The receiving Game Peer composes every `sharedcallback`, `buttonstate`, and `lampstate` line it writes to its own game, and the game runs only the callback its own Map Script registered for those identifiers, picks up only its own copy of the named item, plays only its own Flashback area's flashback, and attaches only its own copy of the named body to its own sticky area. A shared pickup or combine changes the receiving player's own inventory, never one a message describes. So `LanProtocol` may name an installed Custom Story to start, carry the other player's Pose, carry Holds on map-placed entities by integer identifier, and name a Map Script registration, an item to pick up, a Flashback area, a sticky area and the body it attached, a button, or a lamp by identifier, but it has no message type that names or executes a function, a script, or an arbitrary remote command, so a peer on the LAN cannot use it to run code of its choosing on the other machine.
