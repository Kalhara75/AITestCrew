# AITestCrew — QA Quickstart

A 30-minute onboarding for a new QA engineer joining the team. By the end you'll have logged in to the dashboard, installed an agent on your laptop, and recorded + run your first test.

If you get stuck on a specific step, jump to **[Where to get help](#where-to-get-help)** at the bottom.

---

## Before you start

Your team admin will hand you three things:

1. **Dashboard URL** — e.g. `http://aitestcrew.team.internal:5050`
2. **Your personal API key** — looks like `atc_8f3a9b7c2d1e4f6a8b3c9d7e2f1a4b...`. This is your login credential; keep it as private as a password.
3. **The agent install zip** — `AITestCrew-Agent.zip` (~250 MB). This is what turns your laptop into a worker.

You'll need:
- Windows 10/11
- ~1 GB free disk space (for the agent + Playwright browser)
- Network access to the dashboard URL

---

## Step 1 — Log in to the dashboard (2 min)

1. Open the dashboard URL in your browser.
2. You'll see a login page. Paste your `atc_...` API key and submit.
3. You should land on the **Modules** page — a list of the test modules your team has set up (security, sdr, etc.). If you see this, you're authenticated.

The browser stores your key in `localStorage` so you only paste it once per browser. **Sign out** from the top-right menu clears it.

---

## Step 2 — Install your agent (10 min)

The dashboard server can run API and aseXML tests on its own. **Web UI and desktop UI tests need an agent on a machine with a real desktop session** — that's why your laptop becomes a worker.

1. **Run `install.cmd`** from the zip folder (double-click or from a terminal). It installs the agent to `C:\Tools\AITestCrew\` by default. If `C:\Tools` doesn't yet exist on your machine, the very first install needs admin once — right-click `install.cmd` → **Run as administrator**. Subsequent upgrades never need elevation. Custom path: `install.cmd -InstallPath D:\Tools\AITestCrew`.

2. **Edit `appsettings.json`** in the install folder. You only need to fill **one** field:
   ```json
   "ApiKey": "atc_8f3a9b7c..."
   ```
   That's it. `ServerUrl` is already pre-filled, every team environment (URLs, AAD accounts, storage state paths) is baked in, and all secrets the admin had locally (passwords, DB connection strings, Service Bus keys, LLM API keys) have been **stripped** before the zip was built. Your agent's LLM calls (test generation, run summaries) are routed through the server — **you do not need an LLM API key**. The agent talks to the server over HTTP and authenticates browser sessions interactively in Step 3.

   **Optional — only if you'll run desktop (WinForms) tests:** find the env you'll use under `Environments` and point `WinFormsAppPath` at where the desktop app is installed on **your** machine, e.g.:
   ```json
   "WinFormsAppPath": "C:\Bravo\BravoWin.exe"
   ```
   Most QAs leave this blank — desktop testing is a smaller slice of the work.

   **Also (desktop QAs only) — disable Bravo's auto-updater.** Bravo Win checks for new DLLs on every launch and, when it finds an update, pops a **Bravo Startup** dialog asking the user to **Update / Close / Launch**. That dialog blocks the agent — recordings stop at the prompt, replays time out waiting for the main window. Turn the check off **once per machine**:

   1. Open `csla.config` in the same folder as `BravoWin.exe` (e.g. `C:\Bravo\csla.config`).
   2. Find this line under `<appSettings>`:
      ```xml
      <add key="EnableAutoUpdate" value="true"/>
      ```
   3. Change `true` to `false` and save:
      ```xml
      <add key="EnableAutoUpdate" value="false"/>
      ```

   Launch Bravo manually once to confirm the startup dialog no longer appears — you should go straight to the login screen. Re-enable the flag (or run a regular Bravo install) when you need to pull a newer build; the test agent only cares that the dialog is absent at run time.

   > **Upgrading later:** when the admin sends a new zip, just run `install.cmd` from the new zip folder. Your `ApiKey`, saved browser sessions (`auth-state/`), and `WinFormsAppPath` are automatically preserved.

3. **Install the Playwright browser** (only if you'll record/run web tests — most QAs do):
   ```powershell
   cd C:\Tools\AITestCrew
   powershell -ExecutionPolicy Bypass -File .\playwright.ps1 install chromium
   ```
   The `-ExecutionPolicy Bypass` is needed because Windows blocks scripts unzipped from the internet by default. If you'd rather fix this once instead of typing it every time, run **as your user** (not admin):
   ```powershell
   Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
   Get-ChildItem -Recurse C:\Tools\AITestCrew | Unblock-File
   ```
   After that, `.\playwright.ps1 install chromium` works directly. If `Set-ExecutionPolicy` errors out with *"overridden by a policy defined at a more specific scope"*, your machine is locked down by IT Group Policy — fall back to `npx playwright install chromium` (needs Node.js) or ask IT to whitelist the agent folder.

4. **Start the agent** by double-clicking `start-agent.cmd`. You should see:
   ```
   Registered as 72ad9b3c1a5e (YOUR-LAPTOP)
   Capabilities: UI_Web_Blazor, UI_Web_MVC, UI_Desktop_WinForms
   Polling for jobs — press Ctrl+C to stop.
   ```

5. **Verify on the dashboard.** Open the **Agents** panel on the Modules page — your laptop should appear as **Online**.

> **Auto-start at login (optional):** press <kbd>Win</kbd>+<kbd>R</kbd>, type `shell:startup`, drop a shortcut to `start-agent.cmd` in there. Next login, the agent starts on its own.

---

## Step 3 — Authenticate against the test apps (5 min, web QAs only)

Browser tests can't pause for a login prompt every run, so you authenticate **once** per app and the agent caches the session.

In the agent folder, open a PowerShell terminal and run, for each app you'll touch:

```powershell
# Blazor (Brave Cloud)
.\AiTestCrew.Runner.exe --auth-setup --target UI_Web_Blazor --environment <envKey>

# Legacy MVC
.\AiTestCrew.Runner.exe --auth-setup --target UI_Web_MVC --environment <envKey>
```

A browser opens. Log in the way you normally would (Azure SSO, MFA, etc.). When you reach the app's home page, **close the browser** — your session is saved to a file next to the agent (`bravecloud-auth-state.<env>.json`).

Replace `<envKey>` with the environment your team uses (e.g. `sumo-retail`). Ask the admin which one applies to your work. If the team uses multiple envs, repeat the command per env.

> **What if I get re-prompted later?** Storage state lives ~8 hours by default. The system also auto-recovers transparent re-logins in most cases — see `docs/architecture.md → Seamless Authentication Recovery`.

> **Auth tiles are scoped to your machine.** The dashboard only shows auth-state tiles for agents you own. If you see an empty auth panel, that means all your agents have fresh sessions — not that something is broken.

---

## Step 4 — Record your first test (10 min)

The fastest way to learn the system is the **Assistant** — it speaks plain English and drives the same flows the CLI does.

1. From the dashboard, click **Assistant** in the top nav.
2. Type:
   > *Show me the modules in this project*

   The assistant lists them with click-through links.
3. Pick one (say `security`) and type:
   > *Create a new test set called "smoke" in the security module*

   You'll get a **Confirm create** card. Click **Create**.
4. Then:
   > *Record a new test case in security/smoke called "Open users page" targeting Blazor UI*

   A **Confirm record** card appears. Click **Start recording**. The dashboard hands the recording job to your agent — a browser opens on your laptop.
5. Click around in the browser the way you'd manually verify the page works. Close the browser when done. The agent saves the recording and uploads it to the server.
6. Back in the assistant, ask:
   > *Run security/smoke*

   A **Confirm run** card appears. The **Run on** dropdown already shows your own machine — your machine is the default; you only touch the dropdown when you want to run on someone else’s agent. Click **Run**. Watch the result in the Executions view.

That's the end-to-end loop: **log in → record → run → see result**. Everything else in the system is variations on this pattern.

---

## What to learn next

You've just done the simplest flow. The system has a lot more — API tests, database assertions, Service Bus events, full aseXML B2B delivery flows. The intended way to learn these is **incrementally through the Assistant**.

**Open `docs/qa-assistant-curriculum.md`** — it's a staged list of prompts that walk you from "Stage 1: Orient" through "Stage 5: aseXML deferred verification". Spend ~30 min per stage as you encounter the relevant tests in your work. Don't try to do them all at once.

---

## Where to get help

| Problem | Where to look |
|---|---|
| Dashboard won't accept my key | Ask admin to confirm your user is active (`GET /api/users` from their box) |
| Agent shows Offline on dashboard | Check the agent terminal window — usually `ServerUrl` typo or firewall |
| `Missing X-Api-Key header` | `ApiKey` in `appsettings.json` is wrong or blank |
| Playwright browser not found | `.\playwright.ps1 install chromium` in the agent folder |
| Desktop test hangs at "Bravo Startup" / Update dialog | Set `EnableAutoUpdate="false"` in `csla.config` next to `BravoWin.exe` — see Step 2 desktop setup |
| Recording captured the wrong selector | [`docs/recording-troubleshooting.md`](recording-troubleshooting.md) — the recorder's known sharp edges and the diagnostic playbook |
| "Awaiting Verification" stuck forever (aseXML) | Run `/tune-deferred-verification` in the assistant, or read [`docs/architecture.md → Deferred Post-Delivery Verification`](architecture.md) |
| Anything else | Ping the team channel and link your assistant conversation — the admin can replay your thread |

**Reference docs (skim these as needed; don't read end-to-end on day 1):**

- [`docs/qa-assistant-curriculum.md`](qa-assistant-curriculum.md) — staged prompts from simple to complex (your main learning path)
- [`docs/recording-troubleshooting.md`](recording-troubleshooting.md) — when a recording doesn't replay
- [`docs/functional.md`](functional.md) — feature reference and the full CLI command catalogue
- [`docs/deployment.md`](deployment.md) — agent setup deep-dive (the install zip's README is the short version)
