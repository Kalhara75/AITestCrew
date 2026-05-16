---
id: REQ-015
title: Agent installer that preserves local appsettings and Playwright auth state across upgrades
status: Proposed
created: 2026-05-16
author: Kalhara Samarasinghe
author-note: surfaced during REQ-011 rollout — every time we shipped a new agent ZIP to BRLAP110 the QA had to re-edit appsettings.json (ApiKey, WinFormsAppPath) and re-run --auth-setup for every env/surface, because unzipping wiped both.
area: publish + distribution + ops
---

# REQ-015 — Agent installer that preserves local appsettings and Playwright auth state across upgrades

## Goal

Replace the "unzip and pray" agent distribution model with a small installer that performs an **in-place upgrade**: it overwrites binaries and merges admin-managed config additions, but it preserves the two things only the QA's machine knows — their personal `ApiKey` (and a handful of other per-machine fields) and their cached Playwright storage-state files for every customer environment.

When the requirement lands, a QA can drop a new agent build on top of an existing install (or run `install.cmd` from a freshly unzipped pack pointed at the existing folder), and:

- The new binaries replace the old ones.
- Their `appsettings.json` keeps every value they edited (ApiKey, WinFormsAppPath, ServerUrl if locally overridden), but **gains** any new keys / env blocks / URL changes the admin published in this build.
- Their auth-state JSON files (one per environment × surface) are left exactly where they are — no need to re-run `--auth-setup` against four envs every Friday.
- They get a one-screen "Upgrade complete — preserved N fields, added M fields, kept K auth-state files" summary.

## Why now

REQ-011 (server-side LLM proxy) shipped this week. Rolling it out to BRLAP110 surfaced a latent operational defect:

1. Build a new pack with `.\publish.ps1 -Runner`.
2. Copy `AITestCrew-Agent.zip` to BRLAP110.
3. Unzip → overwrite the existing folder.
4. QA's `ApiKey` is now `""` (sanitised pack default). Agent fails to register.
5. QA's `WinFormsAppPath` is now whatever the admin's example file says — wrong for the QA's box. Desktop tests blow up.
6. The four auth-state JSON files under `auth-state/` are gone — Playwright drops out at the SSO redirect on the first run.

Every push of a new build re-imposes 15+ minutes of manual setup per QA per machine. The team is testing through five envs × two web surfaces = ten auth-state files to recreate, and we have three QA boxes today. That's an hour of re-auth per release, and it scales linearly with both the QA headcount and the envs we onboard — REQ-014 (central execution pool) only makes this worse.

Today's workaround is "rename your appsettings.json before unzipping, then merge by hand" plus "copy the `auth-state/` folder out and back in." That's not a system; it's a checklist that gets skipped under release pressure.

## Current behaviour

`publish.ps1 -Runner` produces `publish/runner/` and zips it as `AITestCrew-Agent.zip`. The QA's documented path (README.txt embedded in the zip) is:

```
1. Unzip the file.
2. Open appsettings.json and set ApiKey + (optional) WinFormsAppPath.
3. (Optional) AiTestCrew.Runner.exe --auth-setup --target UI_Web_Blazor --environment <envKey>
4. Double-click start-agent.cmd.
```

For a fresh box that works. For an upgrade on a box that already ran through steps 2–3 last release, **every file in the zip overwrites its counterpart on disk** — including:

- `appsettings.json` (QA's edits → gone)
- Any auth-state files the QA placed under a relative path inside the install dir (gone)
- The Playwright browser cache if it sits inside the install dir (forced re-install — annoying but recoverable)

Auth state files are typically configured at per-env paths like:

```json
"Environments": {
  "TASN": {
    "BraveCloudUiStorageStatePath": "auth-state/TASN-bravo-cloud-ui.json",
    "LegacyWebUiStorageStatePath":  "auth-state/TASN-legacy-web-ui.json"
  }
}
```

`publish.ps1` preserves these paths in the sanitised config but does not preserve the **files** they point to — the zip extraction wipes anything that was there. The same is true for `WinFormsAppPath`, which is per-machine: the admin's value is irrelevant on a QA's laptop.

## Scope — what's in

### 1. New installer script: `install.ps1`

A self-contained PowerShell script lives at the root of the agent pack. It is the single supported entry point for installing or upgrading an agent.

**Invocation:**

```powershell
# First install (target directory does not exist or is empty)
.\install.ps1 -InstallPath "C:\AITestCrew-Agent"

# Upgrade in place (target directory contains an existing install)
.\install.ps1 -InstallPath "C:\AITestCrew-Agent"
# Detects existing install, runs the preserve + merge flow automatically.

# Explicit re-deploy without preserving anything (rare — for a corrupted install)
.\install.ps1 -InstallPath "C:\AITestCrew-Agent" -CleanReinstall
```

Defaults:
- If `-InstallPath` is omitted, defaults to `$env:LOCALAPPDATA\AITestCrew\Agent`. This gives a stable, predictable upgrade target so the QA doesn't have to remember where they put the last one.
- The script always runs from the pack's root — it knows its own source folder via `$PSScriptRoot`.

The flow:

1. **Detect mode** — `Test-Path "$InstallPath\AiTestCrew.Runner.exe"` decides between *first install* and *upgrade*.
2. **First install** — copy every file from `$PSScriptRoot` (excluding `install.ps1` itself) into `$InstallPath`. Print the one-time setup checklist (set `ApiKey`, run `--auth-setup`, drop into `shell:startup`). Identical to today's experience for new boxes.
3. **Upgrade** — see sections 2 and 3 below.

### 2. Preserve list (upgrade flow)

Before any files are overwritten, the installer captures the following from the existing install:

**Files preserved 1:1:**
- Every file under `<InstallPath>/auth-state/**` (recursive). Files keep their relative paths and are restored after the new pack is dropped in.
- `<InstallPath>/agent-id.txt` if present (REQ-013 stable agent identity — also per-machine).
- `<InstallPath>/logs/**` if the directory exists (running agents may be writing to it).

**JSON fields preserved in `appsettings.json`:**

Top-level `TestEnvironment` fields:
- `ApiKey` — QA's personal auth key.
- `ServerUrl` — kept only if the new pack's value is empty AND the local value is non-empty (admin re-publishes will normally pre-fill this, but a QA who set it manually should not lose their setting).
- `AgentName` — optional override for the registration display name.

Per-env (`TestEnvironment.Environments.<key>`):
- `WinFormsAppPath` — desktop binary location, per-machine.
- Any field whose name ends in `Path` and whose existing value is a non-empty absolute or relative path the QA might have edited. Heuristic: if `path != incomingPath && path is not null`, keep `path`. This catches custom auth-state paths and any future per-machine path field without a hard-coded list.

Anything not in the preserve list is **replaced** by the incoming value. Admin-managed values (env URLs, AAD account names, default storage-state paths under the convention) flow through automatically.

### 3. Merge semantics

The installer parses both the incoming and existing `appsettings.json`. The merge algorithm operates on the `TestEnvironment` subtree:

```
result = deep-clone(incoming)
foreach preserved field F in existing:
    if F exists in incoming (so admin still considers it relevant):
        set result.F = existing.F
    else:
        # Admin removed this field on the server side — drop it.
        # Log it in the upgrade report so QA sees the removal.
        log "removed: $F"
```

Map / dictionary values (`Environments`, `DbConnections`, etc.) are merged key-by-key, not blanket-replaced:

```
result.Environments = incoming.Environments     # baseline = admin's layout
foreach env E in existing.Environments:
    if E exists in incoming.Environments:
        # Apply per-env preserve list inside E
        foreach preserved field G in existing.Environments[E]:
            if G exists in incoming.Environments[E]:
                set result.Environments[E].G = existing.Environments[E].G
```

The output is written via the same `ConvertTo-Json -Depth 100` path `publish.ps1` already uses, so formatting matches.

**Atomic write:** the merged file is written to `<InstallPath>/appsettings.json.new`, then renamed over `appsettings.json`. The pre-merge file is copied to `<InstallPath>/.backup/appsettings.json.<timestamp>` (kept indefinitely — small file, cheap insurance).

### 4. Binary / static file replacement

After the preserve step has snapshot-ted everything to memory + the `.backup/` folder:

1. Stop any running `AiTestCrew.Runner.exe` if found (`Get-Process AiTestCrew.Runner | Stop-Process`). Print which PID got killed. Required because the EXE is locked while running.
2. Delete `<InstallPath>` contents **except** the auth-state files, the backup folder, and `logs/` (held aside in step 2).
3. Copy `$PSScriptRoot\*` (excluding `install.ps1`) into `<InstallPath>`.
4. Restore the held-aside files in their original locations.
5. Write the merged `appsettings.json`.

The Runner process is **not** auto-restarted. The QA either had `start-agent.cmd` running and will start it again, or has the shell:startup shortcut and will pick it up at next login. Auto-restart in v1 is over-scoped — a follow-up if the team asks for it.

### 5. Upgrade report

On stdout, after the upgrade completes:

```
AITestCrew Agent — upgrade complete
  Install path:     C:\AITestCrew-Agent
  Previous build:   v1.4.2 (2026-05-09)
  New build:        v1.5.0 (2026-05-16)

  Preserved 4 settings:
    - TestEnvironment.ApiKey
    - TestEnvironment.Environments.TASN.WinFormsAppPath
    - TestEnvironment.Environments.NEMA.BraveCloudUiStorageStatePath
    - TestEnvironment.AgentName

  Preserved 8 auth-state files (auth-state/*.json).
  Backup written to: C:\AITestCrew-Agent\.backup\appsettings.json.2026-05-16_14-22-03

  Removed 1 setting that no longer exists in the new pack:
    - TestEnvironment.LegacyTotpSecret  (replaced by per-env TOTP — see new EnvironmentParameters block)

Next:
  - Start the agent: C:\AITestCrew-Agent\start-agent.cmd
  - Or wait for it to auto-start at next login if you've already added it to shell:startup.
```

Three-line summary is also written to `<InstallPath>/.last-install.log` for support diagnostics.

### 6. Build metadata in the pack

`publish.ps1 -Runner` writes a new `<runnerOut>/build-info.json`:

```json
{
  "version": "1.5.0",
  "buildDate": "2026-05-16T14:11:00Z",
  "commit": "1d98b2f",
  "branch": "main"
}
```

The installer reads this to populate the "Previous build / New build" lines of the report. If `build-info.json` is missing from the existing install (i.e. it pre-dates this REQ), the installer reports `Previous build: unknown` and proceeds.

### 7. README + zip layout

The zip layout becomes:

```
AITestCrew-Agent.zip
├── install.ps1                  ← entry point
├── install.cmd                  ← thin wrapper that calls install.ps1 with -ExecutionPolicy Bypass
├── README.txt                   ← updated — see below
├── build-info.json              ← from §6
├── AiTestCrew.Runner.exe
├── appsettings.json             ← sanitised baseline (installer merges over existing)
├── start-agent.cmd
├── playwright.ps1
├── ...all other Runner pack files...
└── (nested directories as today)
```

`install.cmd` is the user-friendly entry point that handles the PowerShell execution-policy headache in one place:

```cmd
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
```

README opening line changes from *"Unzip and edit appsettings.json"* to:

> **First install**: double-click `install.cmd`. It will install the agent into `%LOCALAPPDATA%\AITestCrew\Agent`, then walk you through setting your `ApiKey` and running `--auth-setup`.
>
> **Upgrade**: same — double-click `install.cmd`. The installer detects your existing install, keeps your `ApiKey` and authenticated sessions, and only replaces the binaries.

### 8. Tests

- **publish.ps1 smoke** — a Pester test in `tests/Scripts.Tests/Publish.Tests.ps1` (NEW test project) that runs `publish.ps1 -Runner -OutputDir <tmp>` and asserts the zip contains `install.ps1`, `install.cmd`, `build-info.json` at the expected locations.
- **Install merge unit tests** — `tests/Scripts.Tests/InstallMerge.Tests.ps1` that exercises the JSON merge function in isolation: a fixtures folder with `existing.json` + `incoming.json` pairs, expected merge outputs, and edge cases (incoming removes a key, incoming adds a new env, existing has a per-env override the incoming doesn't).
- **End-to-end smoke (manual, captured in PR description):** install a baseline pack into a temp folder, edit `ApiKey` and run `--auth-setup` for one env, drop a new pack on top via `install.cmd`, observe: `ApiKey` is preserved, auth-state file is still on disk, the binary's `--version` reports the new build.

## Scope — what's out

- **Rollback to the previous version.** The `.backup/` folder holds the old `appsettings.json` only, not old binaries. Full rollback is a *much* bigger feature (versioned install slots, symlink swap). If we need it, propose it as REQ-016.
- **Auto-restart of the running agent post-upgrade.** Out of scope; the upgrade report tells the QA to restart it manually. We can add a `-Restart` flag in a follow-up once we have a clean way to wait for the new EXE's file lock to be available.
- **Service mode install** (running the Runner as a Windows Service). Today's flow is interactive (start-agent.cmd, shell:startup shortcut). Service-mode install is a separate ask — track in `docs/deployment.md` only if the team requests it.
- **Cross-platform support.** Agents only run on Windows today (Playwright + Win UI Automation are Windows-only). The installer stays PowerShell-on-Windows in v1.
- **MSI / signed installer.** PowerShell is enough; the team's pain is the manual merge, not the absence of a Windows Installer package. Revisit if IT compliance requires a signed package.
- **GUI installer.** Out of scope. The console output is sufficient for the audience (QAs comfortable with `start-agent.cmd`).
- **Downgrade detection.** If the QA points the installer at an *older* pack than the existing install, install anyway and log a `WARNING: downgrade from v1.5.0 to v1.4.2`. We don't prevent it — sometimes you genuinely want to roll back during a bad release.

## Acceptance criteria

1. Running `.\install.cmd` against an empty target folder performs a first install identical in observable behaviour to "unzip the old pack manually": same files end up in the target, same `start-agent.cmd` boot-straps the agent.
2. Running `.\install.cmd` against a folder that already has an `AiTestCrew.Runner.exe` performs an upgrade: existing `ApiKey`, existing `WinFormsAppPath` overrides under each env, and every file under `auth-state/` survive byte-for-byte; new binaries are in place; new admin-published settings (e.g. a new env block, a renamed URL) take effect.
3. The upgrade report names every preserved setting and every preserved auth-state file (count + relative paths). A QA reading the report can verify nothing they cared about was clobbered.
4. The pre-upgrade `appsettings.json` is copied to `.backup/appsettings.json.<timestamp>` before being overwritten. The `.backup/` directory survives subsequent upgrades (never cleaned by the installer — supports diagnostics over time).
5. `install.ps1 -CleanReinstall` skips the preserve step and does a full overwrite. Confirmed by an interactive `[y/N]` prompt that lists what would be lost (auth-state file count, preserved-field names) before proceeding.
6. If the Runner process is running at upgrade time, the installer stops it (printing the PID), upgrades, and leaves it stopped. Re-running `start-agent.cmd` brings it back. The installer does **not** silently kill an unrelated process — it matches by full path (`Get-Process AiTestCrew.Runner | Where-Object Path -eq "$InstallPath\AiTestCrew.Runner.exe"`).
7. The merge code refuses to write a malformed `appsettings.json`: if the merge produces JSON that fails `ConvertFrom-Json` round-trip, the installer aborts with a clear error and leaves the original file untouched. The new binaries are also rolled back (the staged-files step happens after the merge is validated, not before).
8. `publish.ps1 -Runner` emits a zip whose top-level contents satisfy the layout in §7 (presence of `install.ps1`, `install.cmd`, `build-info.json`). Asserted by the Pester test in §8.
9. The unit test suite for the merge function covers at least: (a) preserve a top-level field, (b) preserve a per-env field, (c) admin adds a new env — survives the merge as-is, (d) admin removes a field — gets logged as `removed`, (e) admin renames an env key — old env block is dropped from the merge result (admin's intent wins) and the removal is reported.

## Open questions (none blocking)

- Should the installer's default target be `%LOCALAPPDATA%\AITestCrew\Agent` (per-user, no admin rights needed) or `%ProgramFiles%\AITestCrew\Agent` (system-wide, needs UAC)? Proposal: per-user by default; document the system-wide path for "auto-start at machine boot via Scheduled Task" use cases in `docs/deployment.md`.
- Do we want the install path to be embedded in the agent's heartbeat so the WebApi can show "Agent BRLAP110 is running v1.5.0 from `C:\Users\jess\AppData\Local\AITestCrew\Agent`"? Useful for support diagnostics. Probably yes, but it's a thin addition to the existing `/api/agents/heartbeat` payload — track as a follow-on once §6 lands.
- Should `.backup/` be retention-pruned? Each backup is a few KB; even after a year of weekly releases the folder is < 1 MB. Lean towards "no pruning" until someone complains.
- Is there a case for shipping the installer separately (e.g. a one-page bootstrap script the team posts internally, which pulls the latest pack from a known URL)? Out of scope here, but `install.ps1` is structured so a future "online bootstrapper" can call it with `-Source <url>` without rewrites.

## File-level impact preview

| File | Change |
|---|---|
| `install.ps1` | **NEW** — script root for first-install + upgrade. Lives in the runner pack only (not the repo root). |
| `install.cmd` | **NEW** — execution-policy wrapper. |
| `publish.ps1` | Emit `install.ps1`, `install.cmd`, and `build-info.json` into `$runnerOut` before zipping. Update README block to reference `install.cmd` as the entry point. |
| `src/AiTestCrew.Runner/Properties/build-info.template.json` | **NEW** — template the script substitutes commit/branch/date into at publish time. |
| `tests/Scripts.Tests/Publish.Tests.ps1` | **NEW** — Pester test for pack contents. |
| `tests/Scripts.Tests/InstallMerge.Tests.ps1` | **NEW** — Pester tests for the JSON merge function. |
| `tests/Scripts.Tests/fixtures/` | **NEW** — `existing.json` + `incoming.json` + `expected.json` triples per merge scenario. |
| `docs/qa-quickstart.md` | Update Step 1 to "double-click `install.cmd`" instead of "unzip + edit appsettings.json". |
| `docs/deployment.md` | New short section "Upgrading an agent" under the Local Agent Setup chapter, covering `install.cmd` + `-CleanReinstall` + the `.backup/` folder. |
| `CLAUDE.md` "Where to extend" table | New row: *Adding a new field that QAs edit per-machine* → add to the preserve list in `install.ps1` (heuristic catches most `*Path` fields automatically). |

## Done means

A QA on BRLAP110 receives a Slack DM saying *"new agent build is up, run install.cmd from the attached zip,"* double-clicks it, sees ~20 lines of upgrade report, presses any key to dismiss, and re-launches `start-agent.cmd`. Total time: under a minute. No re-auth. No appsettings re-edit. The team's release cadence stops being gated on QA re-setup time.
