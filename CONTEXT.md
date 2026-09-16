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
