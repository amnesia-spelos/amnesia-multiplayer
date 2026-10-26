# Internet play without a server we host

Research question: how can players hold a Multiplayer Session over the internet when the maintainers run no server at
all, not even the rented forwarder of `docs/vps-forwarding.md` (on branch `experiment/vps-forwarding`)? Which options can we recommend to the public now, and
which could become a built-in option later?

Primary sources only: this repository's code, ADRs and `CONTEXT.md`, and vendors' own documentation, pricing pages,
licences, source repositories and SDK headers. Every external claim links the page it came from. Pages were read on
**2026-10-26**. Free tiers and prices change often, so treat every limit below as "as of 2026-10-26". Claims I could not
confirm in a primary source are marked **(unconfirmed)**. Terms follow `CONTEXT.md`.

Outcome: ADR 0010 adopts Tailscale as the recommended way to play outside a LAN.

---

## Recommendation

**What to tell players now: put both PCs on one private virtual network, then `/host` and `/join` as on a LAN.** A
mesh VPN keeps the Trusted-LAN boundary true. Only computers you admitted can reach the Multiplayer Relay, traffic is
encrypted in the tunnel, and NAT traversal and relay fallback are the VPN vendor's job. We host nothing. In order:

1. **Tailscale** (best default). Free Personal plan: up to 6 users, unlimited devices
   ([pricing](https://tailscale.com/pricing)). WireGuard-encrypted, and it falls back to DERP relays that cannot decrypt
   traffic when a direct path fails ([DERP](https://tailscale.com/kb/1232/derp-servers)), so CGNAT and double NAT work.
   *How-to:* everyone installs Tailscale and signs in. The Session Host either invites each Joining Player into their
   tailnet as a user, or **shares only their own PC** ([sharing](https://tailscale.com/kb/1084/sharing)). Sharing fits
   best: the recipient sees only that one machine, and a shared machine "can receive incoming connections … but cannot
   start connections", which is exactly the Session Host's role. The Session Host types `/host`. Each Joining Player
   types `/join <host's 100.x address>`, or its MagicDNS name, because `/join` resolves names through DNS
   (`TcpSessionOperations.cs:90`, `:145-154`). Note: `/host` does not print the 100.x address (see Repo facts below), so
   the Session Host copies it from the Tailscale app.
2. **ZeroTier**. The free plan has 10 devices, one network and one admin, for non-commercial use
   ([pricing](https://www.zerotier.com/pricing/)). End-to-end encrypted, and it relays through ZeroTier's roots when
   no direct path forms ([relaying FAQ](https://docs.zerotier.com/faq/relaying/)). *How-to:* the Session Host creates
   a network in ZeroTier Central. Each player installs ZeroTier One (needs admin rights:
   [FAQ](https://docs.zerotier.com/faq)) and joins by network ID. The Session Host authorizes each node. Then `/host`,
   and `/join <host's ZeroTier IP>`. Whether a Joining Player needs a ZeroTier account just to join by network ID is
   **(unconfirmed)**.
3. **Radmin VPN** (lowest friction for Windows-only groups, but closed source). It's free, with no ads and no paid
   features, and it "does not limit the number of gamers" ([home](https://www.radmin-vpn.com/)). You join a network by
   name and password, with no account described ([help](https://www.radmin-vpn.com/help/)). It claims end-to-end
   256-bit AES ([security](https://www.radmin-vpn.com/security/)) and falls back to a relay connection when a direct one
   fails ([help](https://www.radmin-vpn.com/help/)). *How-to:* both install it, the Session Host creates a network with
   a password, the Joining Player joins it, then `/join <host's 26.x address>`.

Mention as acceptable but not preferred: **NetBird** (open-source, WireGuard, cloud free plan for 5 users and 100
machines: [pricing](https://netbird.io/pricing)) and **Hamachi** (free for 5 computers per network, which fits the
4-player maximum of ADR 0007: [vpn.net](https://vpn.net/)).

**Do not recommend** public port-exposure tunnels (playit.gg, ngrok TCP, bore, Pinggy, Localtonet), router port
forwarding, UPnP or IPv6 direct for the current protocol. Each puts the unauthenticated, unencrypted Multiplayer Relay on
the public internet (ADR 0001, `CONTEXT.md` "Trusted-LAN boundary"). The only tunnel variants that stay inside the
boundary need software on both ends: frp STCP/XTCP with a pre-shared key, and Cloudflare Tunnel with WARP or
`cloudflared access tcp`. They are harder than a mesh VPN and still need someone's server or account. The next
closest is a playit.gg tunnel with an IP allow-list firewall.

**For a future built-in "just works" option:** the only candidates that need no server of ours are **Epic Online
Services P2P**, which is free with Epic-run NAT traversal and relays, and **iroh**, whose public relays are free but
rate-limited and without uptime guarantees. Both still mean depending on, and registering with, a third party. Valve's
GameNetworkingSockets is open source, but it has no relay without Steam and needs a signaling service we would host.
Whatever is chosen, **a built-in internet option needs its own ADR that replaces the Trusted-LAN boundary**: peers
must be authenticated and traffic encrypted before the Multiplayer Relay accepts anyone it did not admit at a
firewall. EOS P2P (only logged-in Product Users, addressed by ID) and iroh (QUIC, dialed by public key) supply part of
that. Plain `LanProtocol` over a public port supplies none.

---

## Repo facts that shape the answer

| Fact | Where | Consequence |
| --- | --- | --- |
| The Multiplayer Relay listens on `IPAddress.Any`, port `RelayPort` (default 5000) | `src/Multimnesia.Client/TcpSessionOperations.cs:124`, `src/Multimnesia.Client/GamePeerOptions.cs:10` | It listens on every interface, the VPN adapter included. Nothing to configure per VPN. |
| `/join` resolves the destination via DNS and keeps only IPv4 addresses | `TcpSessionOperations.cs:90`, `:153-154` | Overlay IPv4 addresses and MagicDNS-style names work. IPv6-only paths (incl. "IPv6 direct") do not. |
| `/host` prints only addresses in 10/8, 172.16/12 and 192.168/16 | `TcpSessionOperations.cs:130`, `:693-705` | Tailscale and NetBird (100.64.0.0/10: [Tailscale](https://tailscale.com/kb/1015/100.x-addresses), [NetBird](https://docs.netbird.io/about-netbird/how-netbird-works)), Hamachi (25.x: [GoTo](https://support.goto.com/hamachi/help/the-logmein-hamachi-client-hamachi-c-hamachi-aboutclients)) and Radmin (26.x, inferred from its firewall instructions: [help](https://www.radmin-vpn.com/help/)) addresses are **not printed**. `/join` still accepts them, but players must find the address in the VPN app. Filed as #84 for the Tailscale range. |
| Protocol is unauthenticated and unencrypted by design; trust boundary is a private firewalled network | `docs/adr/0001-in-process-plain-tcp-multiplayer-relay.md:19`, `CONTEXT.md` "Trusted-LAN boundary" | Overlay networks keep the boundary. Public exposure breaks it. |
| Up to three Joining Players designed (ADR 0007), two players for now (ADR 0008) | `docs/adr/0007-multiplayer-relay-forwards-to-several-joining-players.md`, `docs/adr/0008-two-players-until-shared-enemies.md` | Every free tier below covers four players. |
| Largest `LanProtocol` frame is 32 KiB | `src/Multimnesia.Contracts/LanProtocol.cs:120` | Message-based transports with small packets (EOS: 1170 bytes) would need fragmentation. |
| The VPS experiment worked at ≈170 ms round trip | `docs/vps-forwarding.md` "Results" | Relayed overlay paths (DERP, ZeroTier roots) of similar latency should be playable. TCP head-of-line blocking under loss is still unmeasured. |

---

## 1. Mesh VPNs / virtual LANs players install themselves

### 1.1 Comparison (as of 2026-10-26)

| Tool | Account needed? | Both install? | Free limit | CGNAT / double NAT | Relay fallback | Open source | Windows friction |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Tailscale** | Yes, every player ([sharing](https://tailscale.com/kb/1084/sharing)) | Yes | 6 users, unlimited devices, non-commercial ([pricing](https://tailscale.com/pricing)) | Yes, via DERP | DERP (and peer relays), WireGuard end-to-end ([DERP](https://tailscale.com/kb/1232/derp-servers)) | Client yes, control server no; Headscale replaces it (below) **(client licence unconfirmed)** | Windows 10+ ([install](https://tailscale.com/kb/1022/install-windows)); admin rights to install **(unconfirmed; only a forum answer)** |
| **ZeroTier** | Network admin yes; joiners **(unconfirmed)** | Yes | 10 devices, 1 network, 1 admin, non-commercial ([pricing](https://www.zerotier.com/pricing/)) | Yes, relayed via roots ([relaying](https://docs.zerotier.com/faq/relaying/)) | Root servers; slow TCP relay if UDP is blocked | Mostly MPL, some "source available" non-free parts ([LICENSE](https://github.com/zerotier/ZeroTierOne/blob/dev/LICENSE.txt)) | "Administrative Rights" required ([FAQ](https://docs.zerotier.com/faq)) |
| **NetBird** | Yes (cloud) | Yes | 5 users, 100 machines ([pricing](https://netbird.io/pricing)) | Yes | Relay service (Coturn, now WebSocket relay) ([how it works](https://docs.netbird.io/about-netbird/how-netbird-works)) | Client BSD-3, server AGPLv3, self-hostable ([repo](https://github.com/netbirdio/netbird)) | Windows supported **(admin/driver unconfirmed)** |
| **Radmin VPN** | No (network name + password) ([help](https://www.radmin-vpn.com/help/)) | Yes | Free, "does not limit the number of gamers" ([home](https://www.radmin-vpn.com/)); a 150-per-network cap appears only on third-party sites **(unconfirmed)** | Yes, presumably via its relay | "Relay/TCP connection … when a direct connection cannot be established" ([help](https://www.radmin-vpn.com/help/)) | No (Famatech) | Windows 7–11 only |
| **Hamachi** | Sign-up shown, requirement **(unconfirmed)** | Yes | 5 computers per network ([vpn.net](https://vpn.net/)) | Yes **(relay mechanism unconfirmed)** | **(unconfirmed)** | No | Windows/Mac/Linux; latest Windows client 2.3.0.111, 2024-04-18, per search snippet of GoTo docs **(unconfirmed)** |
| **EasyTier** | No central account; shared public nodes ([repo](https://github.com/EasyTier/EasyTier)) | Yes | Free | Yes; "works with NAT4-NAT4" | Community "shared nodes" relay | LGPL-3.0 | Installer needs admin; driver files ([repo](https://github.com/EasyTier/EasyTier)) |
| **n2n** | No; needs a supernode | Yes | Free | Via supernode | Supernode relays | GPL-3.0 ([repo](https://github.com/ntop/n2n)) | Someone must host a supernode with a public port; ntop's public one is advised against |
| **Headscale** | Your own server | Yes (Tailscale clients) | Free | As Tailscale | Tailscale's DERP or your own | BSD-3 ([repo](https://github.com/juanfont/headscale)) | **Re-introduces "someone hosts a server"**: the control server |

### 1.2 Tailscale details

- **Free plan**: "Up to 6 users", "Unlimited user devices", non-commercial, "Free forever"
  ([pricing](https://tailscale.com/pricing)). The page carries no "as of" date.
- **Node sharing** ([kb/1084](https://tailscale.com/kb/1084/sharing)): invite by email or link (unused links expire
  after 30 days). The recipient can accept "from any Tailscale account" and sees "only the shared machine … and
  nothing else". Shared machines are quarantined: they "can receive incoming connections … but cannot start
  connections". That matches the Session Host, who only accepts. Each Joining Player still needs their own (free)
  Tailscale account. Whether share recipients count against the 6-user limit is not stated **(unconfirmed)**.
  Inviting players into the host's tailnet instead counts them as users, and without ACLs gives them reach to all the
  host's devices, so sharing is the safer instruction.
- **DERP** ([kb/1232](https://tailscale.com/kb/1232/derp-servers)): used to set up a direct link, and as a relay
  only "when other connection types aren't possible due to hard NAT, firewalls". "It's impossible for a DERP server to
  decrypt your traffic." No bandwidth limits or prices are stated for DERP on this page.
- **Funnel vs Serve** ([kb/1223](https://tailscale.com/kb/1223/funnel)): Funnel exposes a local service to the public
  internet, on ports 443, 8443 and 10000 only, TLS-only, with bandwidth limits. Serve keeps it tailnet-only. **Funnel
  would make the relay public, so never use it for the Multiplayer Relay.** Plain tailnet access needs neither: the
  relay's port is already reachable over the tailnet.
- **Addresses**: 100.64.0.0/10 ([kb/1015](https://tailscale.com/kb/1015/100.x-addresses)), which `/host` does not
  print (Repo facts).

### 1.3 ZeroTier details

- Free plan: "10 devices, one network", "One network admin", "Personal, non-commercial use"
  ([pricing](https://www.zerotier.com/pricing/)). The page also distinguishes accounts created before and after
  2025-11-05 ("Legacy" vs "New" Central), so older accounts may have different limits **(details unconfirmed)**.
- Relaying: a peer in RELAY state goes through ZeroTier's roots. It is slower and can lose packets. A slow TCP relay
  is the fallback when UDP is blocked ([relaying FAQ](https://docs.zerotier.com/faq/relaying/)). Relayed packets stay
  end-to-end encrypted (per the same docs, via search snippet of [docs.zerotier.com/relay](https://docs.zerotier.com/relay/)
  **(page not read directly)**).
- Self-hosted controller: possible via the controller built into ZeroTier One. I found only third-party guides, no
  official page **(unconfirmed)**. Self-hosting the controller is "we host a server" again in any case.
- Overlay addresses come from the pool the network admin picks. Private pools such as 10.x would be printed by
  `/host` **(pool choices unconfirmed)**.

### 1.4 Radmin VPN and Hamachi

- Radmin VPN is free with no paid tier ([home](https://www.radmin-vpn.com/)). Its help page suggests 26.0.0.0/8 for
  firewall trust rules ([help](https://www.radmin-vpn.com/help/)). Its servers belong to Famatech
  ([security](https://www.radmin-vpn.com/security/)), and key management is not documented. Closed source, Windows only.
- Hamachi: "free for up to 5 computers in your network". Paid plans are Standard $49/yr (6–32), Premium $199/yr
  (33–256) and Multi-Network $299/yr ([vpn.net](https://vpn.net/)). The page footer says 2003–2018, so it may be stale.
  GoTo's docs say each client gets one 25.x.x.x IPv4 address
  ([GoTo](https://support.goto.com/hamachi/help/the-logmein-hamachi-client-hamachi-c-hamachi-aboutclients)). 25/8 is
  not a private range, which is one more reason `/host` won't print it.

---

## 2. Tunnel / port-exposure services

All of these make a port on the Session Host's PC reachable through the vendor's edge. The column that matters is
**"Who can connect?"**

### 2.1 Comparison (as of 2026-10-26)

| Service | Raw TCP | Free tier | Who can connect? | Fits Trusted-LAN boundary? |
| --- | --- | --- | --- | --- |
| **playit.gg** | Yes, on Free ([pricing](https://playit.gg/pricing)) | 4 ports; Premium 16 ([premium](https://playit.gg/support/playit-premium/)); Premium $3/mo ([pricing](https://playit.gg/pricing)) | Anyone, unless a firewall allows only listed IPs ([premium](https://playit.gg/support/playit-premium/)). Whether Free has any rules is not stated **(unconfirmed)**. | Only with an IP allow-list, same posture as the VPS `-JoinerIp` mode |
| **ngrok** | Yes; Free TCP needs a verified credit card, which isn't charged ([pricing](https://ngrok.com/pricing)) | Free plan | Anyone; IP restrictions are listed under a paid governance add-on ([pricing](https://ngrok.com/pricing)) | No (on Free) |
| **Cloudflare Tunnel, public hostname + `cloudflared access tcp`** | Yes, "Arbitrary TCP" ([docs](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/non-http/cloudflared-authentication/arbitrary-tcp/)) | Needs a Cloudflare account and **a site (domain) on Cloudflare** | Only clients running `cloudflared` that pass an **Access policy** (SSO) | Yes, but the Session Host needs a domain and every Joining Player needs `cloudflared` |
| **Cloudflare Tunnel, private network + WARP** | Private routes carry "non-HTTP resources" ([docs](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/private-net/)) | Zero Trust free plan; user count **(unconfirmed)** | Only devices enrolled in the host's Zero Trust org | Yes; effectively a heavier mesh VPN |
| **frp (STCP / XTCP)** | Yes | Free software, Apache-2.0 ([repo](https://github.com/fatedier/frp)) | STCP: only visitors running `frpc` with the same `secretKey`; XTCP: P2P, same key | Yes, but **needs an `frps` server with a public IP**: our VPS again |
| **bore** | Yes | Free public server `bore.pub`, MIT ([repo](https://github.com/ekzhang/bore)) | Anyone at `bore.pub:<port>`; the secret only controls who may *create* tunnels, and traffic isn't encrypted | No |
| **Pinggy** | Yes, TCP free ([home](https://pinggy.io/)) | 60-minute tunnel timeout, new address each time | Anyone; an IP whitelist exists, plan **(unconfirmed)** | No (unless whitelisting is available) |
| **Localtonet** | Yes ([home](https://localtonet.com/)) | 1 tunnel, 1 GB/month, 30-minute timeout | Anyone; IP whitelist only on pay-as-you-go ($2/month per tunnel) | Only on a paid plan |
| **Tailscale Funnel** | TLS only, ports 443/8443/10000 ([kb/1223](https://tailscale.com/kb/1223/funnel)) | Included | Anyone | No. Use the plain tailnet instead (§1.2). |

### 2.2 Notes

- A public endpoint recreates what the VPS experiment called `-OpenRelay`: scanners can connect, take a join slot, or
  fill the log with rejected handshakes (`docs/vps-forwarding.md`, "Before the session"). The protocol cannot run code
  on either machine (`CONTEXT.md`), but it does nothing to keep a stranger out of the Multiplayer Session.
- An IP allow-list (playit.gg Premium firewall, Localtonet paid) recreates the `-JoinerIp` posture, with the same
  snag that home IPs change and must be asked for before every session.
- Free playit.gg IPs may be on public blocklists, per an operator reply on Trustpilot, not official docs
  **(unconfirmed)**.

---

## 3. Embeddable NAT traversal / relay libraries (future built-in option)

### 3.1 Comparison (as of 2026-10-26)

| Library / service | Who runs rendezvous and relay | Cost | Encryption / peer identity | .NET | Re-introduces "we host"? |
| --- | --- | --- | --- | --- | --- |
| **Epic Online Services P2P** | Epic: NAT punch-through, then "Epic relay servers" ([`eos_p2p.h`](https://github.com/SpecialKO/SpecialK/blob/main/depends/include/EOS/eos_p2p.h), SDK 1.14 header mirror) | Free, any engine/store, per Epic's FAQ (search snippet of [epicgames.com/help](https://www.epicgames.com/help/en-US/epic-accounts-c5719348850459/general-support-c5719341353627/what-are-epic-online-services-a5720272025755); page returned 403) **(unconfirmed directly)** | Peers addressed by `EOS_ProductUserId` after Connect login; encryption **(unconfirmed)** | Official C# SDK from the Developer Portal ([Epic article](https://onlineservices.epicgames.com/news/setting-up-a-c-solution-for-eos-in-visual-studio-2019), via search snippet) | No server, but **we register a product with Epic and ship its client credentials** |
| **Valve GameNetworkingSockets (open source)** | Signaling: **ours**. STUN: public. TURN: ours, or none ([README_P2P](https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/README_P2P.md)) | Free, BSD-3 ([repo](https://github.com/ValveSoftware/GameNetworkingSockets)) | AES-GCM-256, Curve25519 | Third-party C# binding (ValveSockets-CSharp) | **Yes**: a signaling service, plus TURN or failing hard-NAT pairs |
| **Steam Datagram Relay (Steamworks)** | Valve | Not stated; "contact us" ([SDR](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay)) | Steam auth | Steamworks.NET etc. | No server, but needs our own AppID (see 3.3) |
| **iroh** | n0's public relays (US, EU, Singapore) ([pricing](https://www.iroh.computer/pricing)) | Community $0: "Rate-limited traffic", "No uptime guarantees"; dedicated $199/region/month on top of Pro $19 | QUIC, dial by public key ([repo](https://github.com/n0-computer/iroh)) | Rust; only generic FFI bindings (iroh-ffi), no stated C# | No for hobby use; production relays are paid or self-hosted |
| **libp2p (dotnet-libp2p)** | Public relays/bootstraps would be needed | Free (OSS) | Noise | TCP, QUIC, Noise basic; Circuit Relay v2 in progress; hole punching "help wanted" ([repo](https://github.com/NethermindEth/dotnet-libp2p)) | Not usable yet without our own relay |
| **WebRTC data channels (SIPSorcery)** | Signaling: ours; STUN: public; TURN: ours or a paid provider | Library free ([repo](https://github.com/sipsorcery-org/sipsorcery)) | DTLS (WebRTC) | .NET Framework 4.6.1+ and all .NET | **Yes**: signaling server. Licence is BSD-3 *with an added use restriction*, worth a legal look. |
| **Photon (Realtime)** | Exit Games | Free 20 CCU, "DEVELOPMENT ONLY" ([pricing](https://www.photonengine.com/pricing)) | Vendor | **(unconfirmed for .NET 10)** | Vendor-hosted, but the free tier is not for production |
| **Unity Relay** | Unity | First 50 average monthly CCU free; 3 GiB/CCU up to 150 GiB/month ([pricing](https://unity.com/products/gaming-services/pricing)) | Vendor | Engine/auth dependency **(unconfirmed)** | Vendor-hosted; we'd own a Unity Gaming Services project |

### 3.2 Epic Online Services in detail

From the SDK 1.14 headers (mirrored verbatim in a public repo, since `dev.epicgames.com` would not render for me):

- "These functions will attempt to punch through NATs, but will fallback to using Epic relay servers if a direct
  connection cannot be established" ([`eos_p2p.h:9`](https://github.com/SpecialKO/SpecialK/blob/main/depends/include/EOS/eos_p2p.h)).
- Relay control: `EOS_RC_NoRelays`, `EOS_RC_AllowRelays` (default) and `EOS_RC_ForceRelays`. ForceRelays "will hide IP
  Addresses from peers" ([`eos_p2p_types.h:364-373`](https://github.com/SpecialKO/SpecialK/blob/main/depends/include/EOS/eos_p2p_types.h)).
- Packets are at most `EOS_P2P_MAX_PACKET_SIZE 1170` bytes, with reliable-ordered delivery available (`eos_p2p_types.h:14`,
  `:64-68`). `LanProtocol` frames go up to 32 KiB, so an adapter would need fragmentation, or EOS P2P would carry only
  a stream shim.
- **No Epic account needed:** `EOS_Connect_CreateDeviceId` lets "a new user … start playing without requiring to login
  to the game using any user identity"; later starts log in with `EOS_ECT_DEVICEID_ACCESS_TOKEN`
  ([`eos_connect.h:75-93`](https://github.com/SpecialKO/SpecialK/blob/main/depends/include/EOS/eos_connect.h)).
- What we'd still take on: a product registration with Epic and acceptance of its licence terms **(terms not read)**,
  client ID/secret shipped in the Game Peer, a native DLL, and a way for players to exchange Product User IDs instead
  of IPs (EOS Lobbies/Sessions could do it; also Epic-hosted).
- Epic's P2P doc page and service pages did not render for my fetcher (JS-only or login redirect), so details beyond
  the headers are **(unconfirmed)**: encryption in transit, relay bandwidth limits, and product registration steps.

### 3.3 Steam: what is and isn't possible

- SDR access is tied to an app: Valve publishes certificates that mark "your key as trusted for your app(s)", and
  non-Steam players can use it only if the game ships on Steam and has a game coordinator. Valve says "we cannot
  promise that this service will always be available to non-Steam players"
  ([SDR](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay)).
- Open-source GameNetworkingSockets "does not support accessing the relay network". Steam's authentication, signaling
  and SDR "are only available on Steam" ([repo](https://github.com/ValveSoftware/GameNetworkingSockets)). Its P2P
  README says TURN is "relatively costly … you probably will need to run your own, or just fail connections that
  cannot pierce NAT" ([README_P2P](https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/README_P2P.md)).
- **AppID 480 (Spacewar)** is Valve's example game ([example](https://partner.steamgames.com/doc/sdk/api/example)).
  That page gives no explicit "testing only" rule. The "test without a partner membership, not for production" framing
  comes from third-party guides **(unconfirmed in Valve docs)**. Shipping on 480 would put our players in a shared
  namespace with every other hobby project, with no admin access.
- **AppID 57300 (Amnesia: The Dark Descent)** belongs to Frictional Games. Using it would mean running our networking
  under another publisher's app, and SDR certificates are issued per partner app (above). I found no Valve page
  addressing mods doing this, so "not legitimate without Frictional's involvement" is my reading, **not a quoted rule**.
- So a Steam-relay option needs **our own AppID** (Steam Direct; fee not checked here) or Frictional's cooperation.

### 3.4 Shared caveats

- STUN servers: the GNS README says "there are publicly-available ones" and its example uses Google's. I found no
  Google terms for public use of `stun.l.google.com` **(unconfirmed)**.
- Any library that needs a signaling or TURN server re-introduces exactly the hosting responsibility this research is
  trying to avoid. Of the options here, only EOS and iroh's community relays avoid it, and both depend on a vendor's
  goodwill and free tier.

---

## 4. UPnP / NAT-PMP and IPv6 direct

| Option | Cost | Works when | Why it conflicts with the posture |
| --- | --- | --- | --- |
| UPnP IGD / NAT-PMP / PCP auto port forward | Zero | Router supports and enables it; the Session Host is **not** behind CGNAT (a mapping on the home router doesn't open the carrier's NAT) **(general networking fact, not sourced here)** | It opens TCP 5000 to the **entire internet**: the `-OpenRelay` failure mode, every session, without the host noticing |
| IPv6 direct | Zero | Both have IPv6, and the host's router firewall allows the inbound connection **(not sourced here)** | Same public exposure if the firewall is opened. It also doesn't work today: `/join` rejects non-IPv4 results (`TcpSessionOperations.cs:153-154`) and the outbound socket is `AddressFamily.InterNetwork` (`src/Multimnesia.Client/LanOutbound.cs:100`). |

Both would be acceptable only after the protocol gains authentication (e.g. a session secret or key exchange) and
encryption, which ADR 0001 explicitly does not provide.

---

## Could not confirm

- Whether Tailscale share recipients count toward the 6-user limit; Tailscale client licence; admin rights for the
  Windows install.
- Whether a ZeroTier joiner needs an account; ZeroTier's Legacy vs New account limits; official self-hosted
  controller docs; address pool choices.
- Radmin VPN's per-network cap (150 appears only on third-party sites) and relay operator; Hamachi's relay mechanism
  and whether an account is mandatory.
- NetBird Windows install requirements.
- playit.gg firewall rules on the Free plan; which Pinggy plan has IP whitelisting; Cloudflare Zero Trust free user
  count.
- EOS: encryption in transit, relay limits, product registration terms (Epic docs did not render); the "free, any
  store" claim was seen only as a search snippet.
- A Valve statement on AppID 480 or a base game's AppID being off-limits for mods.
- Google STUN terms; Photon .NET SDK compatibility; Unity Relay's engine/auth dependency.
