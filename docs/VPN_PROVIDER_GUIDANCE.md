# VPN Provider Guidance (ledger 25.04)

How to shape or limit traffic when a VPN client is active. The generic problem: the VPN tunnel hides the
remote IPs behind the tunnel interface, and double-counting inflates stats.

## Generic method
1. Open **Activity**, locate the VPN client process (e.g. `Surfshark`, `NordVPN`, `ExpressVPN`).
2. Apply a **limit** to that process to shape the whole machine's traffic through the tunnel (ledger 25.03).
3. Tag it with **"Ignore Traffic"** to bypass stats and stop VPN double-counting (ledger 25.02 / 20.07.a).
4. If limits must still apply to the VPN process, use **"Ignore Limits"** instead so DNS/DHCP traffic never stalls (20.07.b).

## Provider-specific notes
- **SurfShark**: process `Surfshark.exe`; kill switch adds extra WFP filters — apply limits on the process, not the interface.
- **NordVPN**: process `NordVPN.exe`; use *Ignore Traffic* tag + per-process limit on the tunnel app.
- **ExpressVPN**: process `expressvpn.exe`; forwarded connections appear under the Forwarded node (17.x) — filter with
  "Is Forwarded traffic" (17.04).

## Remote IPs behind the tunnel (25.01)
When a VPN is active, remote addresses resolve to tunnel endpoints. That is expected; geolocation and WHOIS
reflect the tunnel exit, not the destination.
