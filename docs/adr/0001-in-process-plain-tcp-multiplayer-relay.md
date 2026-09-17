# 1. In-process plain-TCP Multiplayer Relay

## Status

Accepted.

## Context

The original diagnostic baseline ran the Multiplayer Relay as a separate ASP.NET Core / SignalR process (`Multimnesia.Server`), paired with a Game Peer that polled the local game for position and relayed raw `SCRIPT_CALL` traffic. That baseline proved two games could see one another move, but it was explicitly disposable (see the repository's original README) and never intended as the durable architecture.

The durable design (specified in issue #3) replaces movement synchronization with a chat-and-command experience: a player types `/host`, `/join <host>`, or `/leave` in the game's own chat, and the Game Peer negotiates a Multiplayer Session with exactly one other Game Peer over the LAN. This is a smaller, better-understood protocol surface than generic script relay, and it no longer needs a general-purpose web framework to carry it.

## Decision

The Multiplayer Relay runs in-process inside the Game Peer application, not as a separate deployable. `/host` starts a `TcpListener` inside the same process that talks to the local game; there is one deployable, `Multimnesia.Client`.

The LAN wire protocol (`Multimnesia.Contracts.LanProtocol`) is plain TCP carrying length-prefixed, UTF-8 JSON frames with a small closed set of message types (`JoinRequest`, `AdmissionAccepted`, `AdmissionRejected`, `ChatEntry`, `Departure`, `Heartbeat`). It has no message type that names or executes a remote script or command. SignalR, ASP.NET Core hosting, and the separate hub/admission-service pair (`MultiplayerRelayHub`, `SessionAdmission`) are removed entirely, along with position polling, the `PlayerPosition` contract, and raw `SCRIPT_CALL` forwarding.

The Multiplayer Relay's LAN listener may bind to LAN interfaces, but it remains unauthenticated and unencrypted by design: the Session Host controls access at the firewall, on a trusted private network, not the protocol. This is documented as a trusted-LAN boundary, not a temporary gap.

## Consequences

- One published output (`Multimnesia.Client`) is copied to both PCs; there is no separate relay executable to version, configure, or fail independently of the Game Peer.
- The wire protocol's closed message set is easy to reason about and exhaustively test (`LanProtocolTests`, `MultiplayerSessionOperationsTests`): unknown, malformed, oversized, or pre-admission frames are rejected by construction rather than by hub-level convention.
- Losing the separate process means the Multiplayer Relay's lifetime is tied to the Session Host's Game Peer process; this is intentional; the spec's non-goals explicitly exclude host migration and reconnect/resume.
- A future gameplay transport (e.g. movement synchronization) is a deliberately separate concern. Should it arrive, it must not reuse `LanProtocol`'s closed message set or its trusted-LAN posture without its own decision record; this ADR does not preclude that future work, but it does not speculate into it either.
