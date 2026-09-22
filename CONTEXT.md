# Multiplayer Coordination

This context covers coordination between separate Amnesia game instances participating in a shared multiplayer experience. Terms owned by the Game Interaction Protocol retain the meanings defined by `amnesia-tdd-tcp`.

## Language

**Multiplayer Session**:
A shared multiplayer experience coordinated for a group of participating game instances.
_Avoid_: Lobby, room

**Multiplayer Relay**:
The process that coordinates a Multiplayer Session and relays multiplayer traffic among its Game Peers.
_Avoid_: Server, SignalR server, hub

**Game Peer**:
The multiplayer application instance paired with one locally running game and participating in a Multiplayer Session.
_Avoid_: Client, multiplayer client

**Session Host**:
The player whose computer runs the Multiplayer Relay and whose Game Peer is admitted first to the Multiplayer Session.
_Avoid_: Server, authority

**Joining Player**:
The second player whose Game Peer joins the Session Host's Multiplayer Session over the local network.
_Avoid_: Client, guest

**Shared Custom Story Start**:
The Session Host's fresh start of a Custom Story, reproduced on the Joining Player's game within the same Multiplayer Session.
_Avoid_: Story sync, map sync, remote start

**Shared Pose**:
A player's Pose, continuously reproduced as an Avatar in the other player's game within the same Multiplayer Session.
_Avoid_: Movement sync, position sync, remote player, ghost

## Trusted-LAN boundary

The Game Interaction Protocol connection between a Game Peer and its local game is loopback-only and never crosses the network. The Multiplayer Relay's LAN listener is intended for a private, firewalled network between trusted computers: it is unauthenticated, so the Session Host must control access at the firewall, not the protocol. The LAN wire protocol (`LanProtocol`) carries only bounded, structurally validated Chat Entries, Join/Admission negotiation, Heartbeats, Departure notices, Shared Custom Story Starts with their outcomes, and Poses for the Shared Pose. A Shared Custom Story Start may name an installed Custom Story by its Custom Story Identifier, and a Pose carries only numbers, two flags, and a map path that the receiving Game Peer formats into its own `avatarpose` line, but the protocol has no message type that names or executes an arbitrary remote script or command, so a peer on the LAN cannot use it to run code on the other machine.
