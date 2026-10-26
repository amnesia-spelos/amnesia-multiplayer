# 10. Tailscale for internet play

## Status

Accepted.

## Context

ADR 0001 keeps the Multiplayer Relay unauthenticated and unencrypted, and draws the trust boundary at a private network: the Session Host controls access at the firewall, not the protocol. That is enough on a LAN, but players also want to hold a Multiplayer Session between two homes.

`docs/research/internet-play-connectivity.md` compared the ways to do that without the maintainers running any server. The options were:

- **A mesh VPN that players install**: Tailscale, ZeroTier, NetBird, Radmin VPN or Hamachi. Each one puts the players' PCs on one private network.
- **Public port exposure**: port forwarding, UPnP, IPv6 direct, or tunnel services such as playit.gg and ngrok. Each one puts the unauthenticated Multiplayer Relay on the public internet.
- **A built-in transport**, such as Epic Online Services P2P. It needs a product registration and a new protocol decision that adds authentication and encryption.
- **A forwarder we rent**: the SSH-tunnel-through-a-VPS experiment on branch `experiment/vps-forwarding`. It works, but someone has to run and pay for a server.

## Decision

**Tailscale is the recommended way to play outside a LAN.** Each player installs Tailscale. The Session Host shares their own PC with each Joining Player, and then the players use `/host` and `/join` as on a LAN, with the Session Host's Tailscale address.

- **We host nothing.** Tailscale handles NAT traversal and encryption. When a direct connection fails, it relays through Tailscale's own relay servers (DERP), which cannot decrypt the traffic.
- **The Trusted-LAN boundary still holds.** A tailnet is a private network that only invited machines reach. Sharing one PC rather than inviting players into the tailnet limits what each Joining Player can reach to that PC. The shared PC accepts incoming connections but cannot start its own, which is the Session Host's role.
- **The protocol does not change.** The Multiplayer Relay already listens on every interface, `/join` accepts an IPv4 address or a name it resolves through DNS, and Tailscale marks its Windows adapter as a private network, so the existing private-profile firewall rule covers it.
- **We do not recommend public port exposure**, including Tailscale Funnel, which makes a service public. These would break ADR 0001's boundary.
- Other mesh VPNs work the same way for players who prefer them, but we document and test only Tailscale.

## Consequences

- Every player needs a free Tailscale account. Tailscale's free plan limits and terms are outside our control. If they change, this decision is revisited.
- `/host` prints the addresses players should join at, so it has to include the Session Host's Tailscale address (#84).
- A built-in transport remains possible later. It would need its own ADR that adds authentication and encryption, as ADR 0001 requires of any change to the trust boundary.
