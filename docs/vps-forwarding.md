# Experiment: Multiplayer Session over a VPS

**Branch:** `experiment/vps-forwarding`. **Status:** worked on first real test (2026-09-25); product code is unchanged. No VPS is running between tests.

## Question

How does the current implementation (ADR 0001: in-process Multiplayer Relay, plain-TCP `LanProtocol`) behave over a real internet path instead of a LAN?

## Setup

The Multiplayer Relay still runs inside the Session Host's Game Peer. A Vultr Debian VPS is only a dumb TCP forwarder: the Session Host opens a reverse SSH tunnel to it, and the Joining Player runs `/join <vps-ip>`. Traffic goes Joining Player → VPS → Session Host, so the Session Host needs no router port forwarding.

```
Joining Player ──TCP 5000──▶ VPS (sshd, GatewayPorts) ══ssh -R══▶ Session Host 127.0.0.1:5000 (Relay)
```

Access control is a Vultr firewall group: TCP 22 only from the Session Host's public IP, TCP 5000 only from listed Joining Player IPs. This keeps the Trusted-LAN boundary approximately true: the VPS plus its firewall stands in for the private network.

## Prerequisites (Session Host PC)

- `vultr-cli.exe` next to the repository (or `VULTR_CLI` set), and `VULTR_API_KEY` set.
- SSH key held in the Bitwarden SSH agent, public half at `~/.ssh/multimnesia_vps.pub`. The tunnel uses Windows `ssh.exe`, because Git Bash's `ssh` cannot see the Bitwarden agent.

## What persists between tests

Deleting the VPS is the only way to stop billing (Vultr bills stopped instances too), and cloud-init rebuilds it in under two minutes, so no snapshot is kept. These free resources stay in the Vultr account and `up.ps1` reuses them:

- SSH key `multimnesia-vps`.
- Firewall group with description `multimnesia-vps`: TCP 22 from the Session Host's public IP, TCP 5000 from each Joining Player IP allowed so far.

## Running a test

1. `.\scripts\vps\up.ps1 -JoinerIp <joining-player-public-ip>`: allows that IP and creates the VPS (`vc2-1c-1gb` in `fra`, Debian 13; $5/mo cap, billed hourly; the listed free plan was refused for this account in `fra`). Without `-JoinerIp` it allows this PC's own public IP. Rerunning only adds missing rules and leaves a running VPS alone, so it also admits another IP mid-session.
2. Start the game and Game Peer, type `/host`.
3. `.\scripts\vps\tunnel.ps1` and keep the window open.
4. Joining Player: `/join <vps-ip>` (printed by both scripts).
5. **Always:** `.\scripts\vps\down.ps1` afterwards to stop billing. Confirm with `vultr-cli instance list`.

The Joining Player finds their public IP with `curl.exe -s https://api.ipify.org` and checks the path with `Test-NetConnection <vps-ip> -Port 5000`. Ping always fails: the firewall group allows no ICMP.

## Troubleshooting

- **Join times out and the host log shows no handshake:** the Vultr firewall dropped it because the Joining Player's public IP is not allowed (home IPs change). Rerun `up.ps1 -JoinerIp <ip>`; rule changes take up to a minute.
- **`up.ps1`/`tunnel.ps1` cannot SSH:** the Session Host's own public IP changed; rerunning `up.ps1` adds a port 22 rule for the current one. Remove stale rules with `vultr-cli firewall rule delete <group-id> <rule-number>`.
- **`REMOTE HOST IDENTIFICATION HAS CHANGED`:** Vultr reused an IP from an earlier VPS; run `ssh-keygen -R <vps-ip>`.
- **Unexpected `HandshakeRejected` in the host log:** every `Test-NetConnection` to the VPS reaches the relay and is logged that way; harmless.

## Results

**2026-09-25:** Session Host and Joining Player on different home networks, VPS in Frankfurt. Once the Joining Player's IP was allowed, joining, Shared Custom Story Start, Shared Pose and Holds all worked as on the LAN, with no problems reported. The VPS ran about two hours and cost $0.02 (Vultr pending charges, before VAT).

## Next

If this becomes a feature, a `/host` mode that dials the forwarder itself (no manual `ssh`) and any change to the Trusted-LAN boundary need their own decisions. Worth watching on later runs: Avatar stalls from TCP head-of-line blocking under packet loss, Claim Denial frequency at internet latency, and heartbeat timeouts on poor connections.
