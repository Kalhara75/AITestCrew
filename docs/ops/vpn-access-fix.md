# AITestCrew - VPN access fix for the WebApi (Windows container)

This document covers:
1. The symptom and how to reproduce it
2. Root cause - Windows containers + HNS NAT
3. The fix that's in place on brwks221
4. How to verify everything is working
5. Debug checklist if it regresses
6. Rollback
7. Knock-on impact on QA agent setup
8. Cleaner long-term alternative

---

## Symptom

VPN-connected machines could not reach `http://brwks221:5050/`, even though:

- `http://localhost:5050/` worked fine on `brwks221` itself.
- A separate Linux-based tool on the same host (`C:\Brave-GitHub\bravecloud-tools-codemerge`, port 3200) was reachable from VPN clients without any extra config.
- The Windows Firewall on `brwks221` already had `Allow Inbound TCP 5050` rules covering all profiles.
- `ping brwks221` from the VPN client worked - so the VPN tunnel itself was healthy.

From a VPN client:

```powershell
Test-NetConnection brwks221 -Port 5050   # TcpTestSucceeded : False
Test-NetConnection brwks221 -Port 3200   # TcpTestSucceeded : True
```

---

## Root cause

AITestCrew on `brwks221` runs as a **Windows container** via Docker Desktop (verified with `docker info --format '{{.OSType}}'` returning `windows`). Windows containers publish ports via **HNS NAT** (Host Networking Service on a Hyper-V virtual switch). The differences vs. a normal listener matter here:

| Aspect | Linux/native listener (port 3200) | Windows container HNS NAT (port 5050) |
|---|---|---|
| TCP socket on host | Yes - `0.0.0.0:3200 LISTENING` | None - just a NAT rule |
| Visible in `netstat` `LISTENING` | Yes | No |
| Visible in `Get-NetTCPConnection` | Yes | No |
| Bound to all NICs incl. VPN | Yes | No - typically host primary NIC only |
| Reachable on host loopback | Yes | Yes (NAT translates `127.0.0.1:5050` to the container) |
| Reachable from VPN client | Yes | No |

That last row is the bug: HNS NAT doesn't advertise the published port on the VPN adapter, so VPN traffic hitting `brwks221:5050` is silently dropped. The same firewall rules that work for 3200 are powerless because there's no socket to deliver to in the first place.

Diagnostic confirming the asymmetry on the host:

```powershell
netstat -ano | findstr ":3200"
# TCP    0.0.0.0:3200    0.0.0.0:0    LISTENING    <pid>     <- real socket

netstat -ano | findstr ":5050"
# Only outbound 127.0.0.1:* -> 127.0.0.1:5050 lines, no LISTENING <- HNS NAT, not a socket
```

---

## The fix (in place on brwks221)

We tried a 5050 -> 5050 `netsh interface portproxy` first; it failed silently because HNS had already claimed port 5050 at a lower layer of the network stack and the portproxy couldn't bind `0.0.0.0:5050`. **The working configuration uses a different external port (5051)** that forwards into the HNS-published `127.0.0.1:5050`:

```powershell
# 1. Register the portproxy rule (persists across reboots in the registry)
netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=5051 connectaddress=127.0.0.1 connectport=5050

# 2. Open the firewall for the new external port
New-NetFirewallRule -DisplayName "AITestCrew 5051 (portproxy)" `
  -Direction Inbound -Protocol TCP -LocalPort 5051 -Action Allow `
  -Profile Domain,Private,Public

# 3. Ensure iphlpsvc (IP Helper) is running with auto-start.
#    iphlpsvc is the service that actually creates the portproxy listener -
#    netsh only registers the rule. Without iphlpsvc running, the rule sits
#    dormant and netstat shows no listener on 5051.
Set-Service iphlpsvc -StartupType Automatic
Start-Service iphlpsvc

# 4. After adding any portproxy rule, iphlpsvc usually needs a kick:
Restart-Service iphlpsvc
```

End-state for VPN users: **`http://brwks221:5051/`**. Local users on `brwks221` itself can still use either `:5050` (HNS NAT, loopback only) or `:5051` (portproxy, works on any NIC). LAN users should also use `:5051` for consistency.

---

## How to verify

On `brwks221` (elevated PowerShell):

```powershell
# Rule registered
netsh interface portproxy show v4tov4
# Expect: 0.0.0.0  5051  127.0.0.1  5050

# Listener now visible (this is the proof the shim is actually serving)
netstat -ano | findstr ":5051"
# Expect a "TCP  0.0.0.0:5051 ... LISTENING <pid>" line

# Helper service healthy
Get-Service iphlpsvc | Format-Table Name,Status,StartType
# Expect: iphlpsvc  Running  Automatic

# Firewall rule present
Get-NetFirewallPortFilter -Protocol TCP | Where-Object LocalPort -eq 5051 |
  ForEach-Object { Get-NetFirewallRule -AssociatedNetFirewallPortFilter $_ } |
  Format-Table DisplayName,Enabled,Profile,Action,Direction -AutoSize
```

From a **VPN-connected client**:

```powershell
Test-NetConnection brwks221 -Port 5051
# Expect: TcpTestSucceeded : True
```

Then load `http://brwks221:5051/` in a browser - the AITestCrew dashboard should render.

---

## Debug checklist if VPN access breaks again

Walk these in order on `brwks221`:

1. **Is the portproxy rule still registered?**
   ```powershell
   netsh interface portproxy show v4tov4
   ```
   If the `0.0.0.0:5051 -> 127.0.0.1:5050` line is missing, re-add it (step 1 of "The fix" above).

2. **Is iphlpsvc running?**
   ```powershell
   Get-Service iphlpsvc
   ```
   If `Stopped`: `Start-Service iphlpsvc`. If `Disabled`: `Set-Service iphlpsvc -StartupType Automatic` then start.

3. **Does the listener actually exist?**
   ```powershell
   netstat -ano | findstr ":5051"
   ```
   If no `LISTENING` line, the rule is registered but iphlpsvc hasn't reconciled it. Fix:
   ```powershell
   Restart-Service iphlpsvc
   ```

4. **Does the container itself still answer on loopback?**
   ```powershell
   Invoke-WebRequest http://localhost:5050/ -UseBasicParsing | Select-Object StatusCode
   ```
   If this fails, the problem is upstream of the shim - the container is down or crashed. Check `docker ps` and `docker compose logs`.

5. **From the VPN client - is the network path itself healthy?**
   ```powershell
   ping brwks221
   Test-NetConnection brwks221 -Port 3200   # known-good port for comparison
   Test-NetConnection brwks221 -Port 5051
   ```
   If `ping` and 3200 work but 5051 fails, the host is reachable and the issue is squarely on the 5051 listener. If 3200 also fails, the VPN/route is broken - not an AITestCrew problem.

---

## Rollback

If the shim ever needs to be removed:

```powershell
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=5051
Remove-NetFirewallRule -DisplayName "AITestCrew 5051 (portproxy)"
```

VPN access will revert to broken; LAN/local access via `:5050` is unaffected.

---

## Knock-on impact on QA agent setup

The runner pack produced by `publish.ps1 -Runner` ships a templated `appsettings.json` with a `ServerUrl` placeholder. For QA engineers on VPN that field must be:

```
"ServerUrl": "http://brwks221:5051"
```

Not `:5050`. Same applies to any docs in `docs/qa-quickstart.md` that hand out the dashboard URL. A wrong port here surfaces later as a mysterious "agent won't register / Failed to register agent" error - the agent is hitting a black hole.

If you ever run a QA engineer onboarding fresh, double-check their `ServerUrl` first when debugging.

---

## Cleaner long-term alternative

The portproxy shim works but is a workaround. The native fix is to **stop running AITestCrew as a Windows container** and run the published self-contained build directly:

```powershell
.\publish.ps1                           # publishes to .\publish\
cd .\publish
.\AiTestCrew.WebApi.exe                 # Kestrel binds http://+:5050 = 0.0.0.0:5050
```

Or install as a Windows service per the trailing commands in `publish.ps1`.

Why this fixes it: Kestrel binds a real TCP socket on `0.0.0.0:5050`, identical to how the port-3200 tool works. No HNS, no NAT, no shim needed. VPN, LAN, and local all reach `:5050` directly.

Trade-offs of moving off containers:

- Lose the container's hard isolation (process runs as a host user / service account, sees host disks).
- `docker compose` no longer manages restart-on-crash - use a Windows service (`sc.exe create`) or NSSM.
- `docker-config/`, `docker-auth-state/`, `docker-backups/` volume mounts collapse into plain folders next to the exe; update `appsettings.json` paths accordingly.
- Updates become "stop service, replace folder, start service" instead of "docker compose up -d --build".

For now the portproxy is fine. Revisit the native-exe path if Docker Desktop's Windows-containers mode gets retired by Microsoft (rumoured to be deprecated) or if more ports start needing per-port shims.
