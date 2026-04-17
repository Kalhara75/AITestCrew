# AITestCrew — Claude-Assisted Development Guide

## Overview

AITestCrew is set up for AI-assisted development using [Claude Code](https://claude.ai/code). A set of project-specific slash commands (skills) and a persistent context file are included in the repository so Claude understands the architecture and can make correct changes without needing re-explanation each session.

---

## How It Works

### `CLAUDE.md` — Persistent Project Context

`CLAUDE.md` at the repository root is automatically loaded into every Claude Code session opened in this directory. It gives Claude:

- The solution structure and strict dependency rules
- Key file locations and their responsibilities
- Run mode commands
- Agent pattern conventions (reuse mode, persistence, auth injection)
- Naming and serialisation conventions
- A reference to the available slash commands

You do not need to explain the project to Claude each time — it reads `CLAUDE.md` at the start of every session.

**Keep `CLAUDE.md` up to date** when you add significant new components, change conventions, or add new run modes. It is the single source of truth Claude uses for architectural decisions.

---

## Slash Commands

Slash commands live in `.claude/commands/`. They are invoked with `/command-name <arguments>` in Claude Code and expand into detailed, project-aware instructions that guide Claude through a specific development task.

### Action Skills (scaffold / implement)

### `/add-agent`

Scaffolds a complete new test agent.

```
/add-agent <TargetType> "<description>"
```

**Examples:**
```
/add-agent UI_Web_MVC "Tests ASP.NET MVC pages via HTTP and validates rendered HTML"
/add-agent Database "Validates database state after API operations using direct SQL queries"
/add-agent BackgroundJob_Hangfire "Triggers and monitors Hangfire background jobs"
```

**What it does:**
1. Reads `BaseTestAgent`, `ApiTestAgent`, and `ITestAgent` to understand the patterns
2. Creates `{TargetType}TestCase.cs` and `{TargetType}TestAgent.cs` in a new `Agents/{TargetType}Agent/` folder
3. Registers the new agent in `Program.cs`
4. Handles reuse mode compatibility (`PreloadedTestCases` + `generatedTestCases` in `Metadata`), returns ONE `TestResult` per objective
5. Updates `SaveTestSetAsync` in the orchestrator if needed
6. Builds the solution and fixes any errors
7. Updates `docs/functional.md` and `docs/architecture.md`

---

### `/run-aitest`

Builds and runs the test suite.

```
/run-aitest <arguments>
```

**Examples:**
```
/run-aitest "Test the /api/products endpoint"
/run-aitest --list
/run-aitest --reuse test-the-api-products-endpoint
/run-aitest --rebaseline "Test the /api/products endpoint"
/run-aitest
```

Passing no arguments builds only (does not run). Always builds first and reports any compilation errors before running.

---

### `/add-validation`

Adds a new validation rule to an agent's response validation logic.

```
/add-validation <agent> "<rule description>"
```

**Examples:**
```
/add-validation api "fail if response body contains a stack trace or exception message"
/add-validation api "fail if Content-Type header is missing from the response"
/add-validation api "warn if response time exceeds 3 seconds"
```

**What it does:**
1. Determines whether the rule is best implemented as rule-based (fast, no LLM cost) or LLM-based (reasoning required)
2. Adds the check to `ValidateResponseAsync` in the correct location
3. Extends `ApiTestDefinition` with a new property if per-test configuration is needed
4. Builds the solution
5. Updates the Validation section in `docs/functional.md`

---

### `/implement-feature`

Implements any new feature or enhancement.

```
/implement-feature "<feature description>"
```

**Examples:**
```
/implement-feature "add parallel task execution using the existing MaxParallelAgents setting"
/implement-feature "add a --delete <id> CLI flag to remove a saved test set"
/implement-feature "retry failed test cases up to 2 times before marking them as failed"
/implement-feature "export test results as a JUnit XML file after each run"
```

**What it does:**
1. Reads `docs/architecture.md` and the relevant source files
2. Identifies the correct layer for the change (Runner, Orchestrator, Agents, Core)
3. Enforces the dependency rules — no upward references
4. Implements minimally — no speculative abstractions
5. Handles test set compatibility for any model changes
6. Builds, fixes errors, and updates docs

---

### `/add-asexml-template`

Scaffolds a new aseXML transaction template (XML + manifest pair). No C# changes required.

```
/add-asexml-template <TransactionType> <templateId> "<description>"
```

**Examples:**
```
/add-asexml-template CustomerDetailsNotification CDN-MoveIn "Customer Details Notification for move-in events"
/add-asexml-template MeterDataNotification MDN-Actual "Meter Data Notification with actual interval reads"
```

**What it does:**
1. Finds or uses a provided representative sample
2. Reads engine contracts (reference template, manifest, `AseXmlRenderer.cs`, `FieldGenerators.cs`)
3. Creates the XML template at `templates/asexml/{TransactionType}/{templateId}.xml` with `{{Token}}` placeholders
4. Creates the manifest at `templates/asexml/{TransactionType}/{templateId}.manifest.json` with field sources (`auto`/`user`/`const`)
5. Verifies token-to-manifest parity
6. Builds and smoke-tests with both Generate and Deliver objectives
7. Optionally adds new auto-field generators in `FieldGenerators.cs`
8. Updates documentation

---

### `/add-asexml-verification`

Scaffolds a post-delivery UI verification attached to an existing aseXML delivery test case.

```
/add-asexml-verification <moduleId> <testSetId> <objectiveId> <target> "<verification-name>" [waitSeconds]
```

**Examples:**
```
/add-asexml-verification aemo-b2b mfn-delivery deliver-mfn UI_Web_Blazor "MFN Process Overview shows 'One In All In'" 30
/add-asexml-verification aemo-b2b mfn-delivery deliver-mfn UI_Web_MVC "Legacy MFN Search grid row exists"
```

**What it does:**
1. Confirms prerequisites: objective has run successfully, auth state cached for target
2. Reads engine contracts (`VerificationStep.cs`, `VerificationRecorderHelper.cs`, `TokenSubstituter.cs`)
3. Launches the recorder via CLI with auto-parameterisation context
4. Records against real, already-processed data (search by NMI/MessageID)
5. Reviews captured output and verifies `{{Token}}` substitutions
6. Replays to confirm tokens substitute correctly with fresh values

Verifications are owned by a single delivery case and use `{{Token}}` substitution so the same steps work with different MessageIDs.

---

### `/add-delivery-protocol`

Scaffolds a new `IXmlDropTarget` implementation for a new delivery protocol.

```
/add-delivery-protocol <scheme> "<description>"
```

**Examples:**
```
/add-delivery-protocol as2 "Applicability Statement 2 inbound delivery"
/add-delivery-protocol smb "SMB/CIFS network share delivery"
/add-delivery-protocol httppost "HTTP POST multipart upload"
```

**What it does:**
1. Confirms the endpoint shape works with `BravoEndpointResolver`
2. Reads engine contracts (`IXmlDropTarget`, `SftpDropTarget`, `FtpDropTarget`, `DropTargetFactory`)
3. Picks a NuGet package if needed
4. Creates the drop-target class at `src/AiTestCrew.Agents/AseXmlAgent/Delivery/{Scheme}DropTarget.cs`
5. Wires it into `DropTargetFactory` with scheme detection + dispatch
6. Registers DI only if non-logger dependencies are needed
7. Smoke-tests against a real endpoint
8. Updates documentation

**Rules:** Never log passwords, verify file landed, respect cancellation tokens, return `BytesWritten`.

---

### `/review-agent`

Reviews an agent implementation against a 15-point quality checklist.

```
/review-agent <agent name or file path>
```

**Examples:**
```
/review-agent ApiTestAgent
/review-agent src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs
/review-agent UiWebMvcTestAgent
```

**What it checks:**
- Interface compliance (`ITestAgent`, `CanHandleAsync`, `ExecuteAsync`)
- Reuse mode support (`PreloadedTestCases` bypass, `generatedTestCases` in Metadata)
- Persistence compatibility
- LLM usage patterns (via `BaseTestAgent` helpers only)
- Authentication injection
- Step tracking (`TestStep.Pass/Fail/Err`)
- Cancellation token propagation
- Logging conventions

Automatically fixes any Critical issues found. Reports Important and Minor issues as recommendations.

---

### Reference Skills

These are read-only reference guides. Consult them before modifying the relevant subsystem.

### `/asexml-reference`

End-to-end reference for the aseXML subsystem — the three-phase pipeline (Generate → Deliver → Verify), data model, extension points, and CLI cheat sheet.

```
/asexml-reference
```

**Key content:**
- Phase 1 (Generate): renders XML from template + manifest via `AseXmlGenerationAgent`
- Phase 2 (Deliver): uploads to Bravo inbound drop location via SFTP/FTP (`AseXmlDeliveryAgent`)
- Phase 3 (Verify): runs post-delivery UI checks with `{{Token}}` substitution from render context
- Manifest field sources: `auto` (generators), `user` (runtime), `const` (hardwired)
- Built-in generators: `messageId`, `transactionId`, `nowOffset`, `today`
- Invariants and skill mapping table

---

### `/bravo-web-reference`

Reference guide for the Bravo Web (ASP.NET MVC + Kendo UI) application stack.

```
/bravo-web-reference
```

**Key content:**
- Kendo UI widget structures (PanelBar, Window, Grid)
- Selector rules for PanelBar headers, Grid hyperlinks, modal dismissal
- Login page selectors
- Lessons learned: `input` events (not `change`), `href*=` (contains match), Grid dynamic hrefs, overlay handling

Consult before modifying Playwright recorder/replay logic or selector generation for MVC targets.

---

### `/blazor-cloud-reference`

Reference guide for the Brave Cloud (Blazor/MudBlazor) application stack.

```
/blazor-cloud-reference
```

**Key content:**
- MudBlazor component structures (NavGroup, NavLink, Button, DataGrid, Select, Dialog)
- Selector rules (text-based preferred, avoid `type="button"`)
- `click-icon` action for SVG icons with fingerprint + occurrence index
- StorageState persistence for session reuse
- SPA navigation timing: `WaitForLoadStateAsync(NetworkIdle)` resolves too early — use `wait-for-stable`
- 1920×1080 viewport required (both MVC and Blazor replay)

Consult before modifying Playwright recorder/replay logic or selector generation for Blazor targets.

---

### `/desktop-winui-reference`

Reference guide for the WinForms Desktop UI recording and replay engine (FlaUI/UI Automation).

```
/desktop-winui-reference
```

**Key content:**
- Technology: FlaUI.UIA3, Windows hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`), message pump
- Element selector model: five-field composite (AutomationId, Name, ClassName, ControlType, TreePath) with cascading priority
- Recording architecture: hooks, message pump requirement (critical), element resolution, Ctrl+V paste handling
- Replay architecture: three-strategy click (InvokePattern, element.Click(), Mouse.Click), window transition detection, assertion polling
- Common issues and solutions
- Adding new step actions and selector strategies

Consult before modifying desktop recorder, replay, or element resolution logic.

---

## Common Development Workflows

### Starting the Web UI for development

```bash
# Terminal 1 — API server
dotnet run --project src/AiTestCrew.WebApi

# Terminal 2 — React dev server (hot reload)
cd ui && npm run dev
```

Open `http://localhost:5173`. The WebApi runs on port 5050; the React dev server proxies API calls to it.

The Vite dev server proxies `/api` and `/screenshots` requests to the WebApi (configured in `ui/vite.config.ts`), so no CORS configuration is needed during development.

In production, run `.\build-all.ps1` to build the React app into `src/AiTestCrew.WebApi/wwwroot/` — the WebApi serves both API and UI from a single process.

### Adding a new REST API endpoint

1. Create or edit a file in `src/AiTestCrew.WebApi/Endpoints/`
2. Register the route group in `src/AiTestCrew.WebApi/Program.cs`
3. If the endpoint should be accessible without authentication, add it to the exemption list in `src/AiTestCrew.WebApi/Middleware/ApiKeyAuthMiddleware.cs`
4. Add corresponding TypeScript types in `ui/src/types/index.ts`
5. Add API client functions in `ui/src/api/`
6. Create or update React pages/components in `ui/src/pages/` and `ui/src/components/`

### Adding a new React page

1. Create the page in `ui/src/pages/YourPage.tsx`
2. Add a route in `ui/src/App.tsx`
3. Add a nav link in `ui/src/components/Layout.tsx` if it should appear in the header

---

### Adding a new test agent type

```
/add-agent UI_Web_MVC "Tests ASP.NET MVC pages by sending HTTP requests and validating HTML responses"
```

Then verify it works:
```
/run-aitest "Test the /Home page renders correctly"
```

---

### Adding a feature to the existing API agent

```
/implement-feature "capture response time per test case and fail if it exceeds a configurable threshold"
```

---

### Recording reusable login/setup steps

Record setup steps (e.g. login) that run before every test case in a test set:

```
dotnet run --project src/AiTestCrew.Runner -- --record-setup --module sdr --testset nmi-search
```

Perform login in the browser, click **Save & Stop**. The steps are saved to the test set's `setupSteps` field. On replay, they run before each test case automatically. Setup steps can also be viewed and edited in the web dashboard.

### Blazor SSO auth setup (with 2FA)

For Blazor apps using Azure AD SSO with 2FA, save the auth state first:

```
dotnet run --project src/AiTestCrew.Runner -- --auth-setup
```

Complete the SSO + 2FA flow manually in the visible browser. The session is saved and reused for all subsequent recordings and test runs. Then record with `--target UI_Web_Blazor`:

```
dotnet run --project src/AiTestCrew.Runner -- --record --module security --testset user-search --case-name "Search users" --target UI_Web_Blazor
```

Consult `/blazor-cloud-reference` for MudBlazor DOM patterns and selector rules.

---

### Running a regression check on saved tests

```
/run-aitest --list
```
Pick the test set ID, then:
```
/run-aitest --reuse test-the-api-products-endpoint
```

---

### Deploying for multi-user access

1. Configure `StorageProvider: "Sqlite"` and `SqliteConnectionString` in appsettings.json
2. Migrate existing data: `dotnet run --project src/AiTestCrew.Runner -- --migrate-to-sqlite`
3. Build and deploy:
   - Docker: `docker compose up -d --build`
   - Self-contained: `.\publish.ps1 -OutputDir C:\deploy` then run `AiTestCrew.WebApi.exe`
4. Create the first user: `curl -X POST http://server:5050/api/users -H "Content-Type: application/json" -d '{"name": "Admin"}'`
5. Share the API key with team members

### Setting up a QA engineer's local Runner

Configure the Runner's appsettings.json to point at the shared server:
```json
{
  "TestEnvironment": {
    "ServerUrl": "http://team-server:5050",
    "ApiKey": "atc_..."
  }
}
```

Recording commands now sync to the shared server:
```bash
dotnet run --project src/AiTestCrew.Runner -- --record --module sec --testset users --case-name "Search" --target UI_Web_Blazor
```

---

### Refreshing test cases after an API change

```
/run-aitest --rebaseline "Test the /api/products endpoint"
```

---

### Recording a desktop (WinForms) test case

```
dotnet run --project src/AiTestCrew.Runner -- --record --module desktop --testset calc --case-name "Basic Add" --target UI_Desktop_WinForms
```

Interact with the desktop app. FlaUI hooks capture clicks and keystrokes. Steps are saved with a five-field selector composite (AutomationId > Name > ClassName+ControlType > TreePath). Consult `/desktop-winui-reference` for element resolution rules.

---

### aseXML: Generating a B2B transaction

```bash
dotnet run --project src/AiTestCrew.Runner -- --module aemo-b2b --testset mfn-tests "Generate an MFN for NMI 4103035611 ..."
```

Renders XML from a template + manifest to `output/asexml/`. Use `/add-asexml-template` to scaffold new transaction types. Consult `/asexml-reference` for the full pipeline overview.

---

### aseXML: Delivering to a Bravo endpoint

```bash
# List available endpoints
dotnet run --project src/AiTestCrew.Runner -- --list-endpoints

# Deliver
dotnet run --project src/AiTestCrew.Runner -- --module aemo-b2b --testset mfn-delivery --endpoint GatewaySPARQ --obj-name "Deliver MFN" "Deliver MFN for NMI 4103035611 to GatewaySPARQ ..."
```

Renders the XML then uploads to the endpoint's inbound drop location via SFTP or FTP. Use `/add-delivery-protocol` to add new transport protocols.

---

### aseXML: Adding a post-delivery UI verification

```bash
# Ensure auth state is cached for the target UI
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor

# Record the verification
dotnet run --project src/AiTestCrew.Runner -- --record-verification --module aemo-b2b --testset mfn-delivery --objective "Deliver MFN" --target UI_Web_Blazor --verification-name "MFN Process Overview shows 'One In All In'" --wait 30
```

Records UI steps that verify the delivery was processed. Literals from the delivery context (NMI, MessageID, etc.) are auto-parameterised into `{{Token}}` placeholders so verifications work with fresh data. Use `/add-asexml-verification` for guided scaffolding.

---

### Legacy MVC auth setup

For Legacy MVC targets, save forms auth state before recording:

```bash
dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC
```

---

### Reviewing code quality after manual edits

```
/review-agent ApiTestAgent
```

---

## File Reference

| File | Purpose |
|---|---|
| `CLAUDE.md` | Auto-loaded project context for every Claude Code session |
| `.claude/commands/add-agent.md` | Slash command: scaffold a new agent |
| `.claude/commands/run-aitest.md` | Slash command: build and run the test suite |
| `.claude/commands/add-validation.md` | Slash command: add a validation rule |
| `.claude/commands/implement-feature.md` | Slash command: implement any feature |
| `.claude/commands/review-agent.md` | Slash command: quality review of an agent |
| `.claude/commands/add-asexml-template.md` | Slash command: scaffold an aseXML transaction template |
| `.claude/commands/add-asexml-verification.md` | Slash command: scaffold a post-delivery UI verification |
| `.claude/commands/add-delivery-protocol.md` | Slash command: scaffold a new delivery protocol |
| `.claude/commands/asexml-reference.md` | Reference: aseXML Generate → Deliver → Verify pipeline |
| `.claude/commands/bravo-web-reference.md` | Reference: Kendo UI DOM patterns and selector rules |
| `.claude/commands/blazor-cloud-reference.md` | Reference: MudBlazor DOM patterns, SPA timing, and selector rules |
| `.claude/commands/desktop-winui-reference.md` | Reference: FlaUI desktop recording/replay and element resolution |
| `src/AiTestCrew.Storage/` | Persistence layer (repos, models, SQLite implementations) |
| `src/AiTestCrew.WebApi/Program.cs` | WebApi DI wiring and endpoint registration |
| `src/AiTestCrew.WebApi/Middleware/ApiKeyAuthMiddleware.cs` | API key authentication |
| `src/AiTestCrew.WebApi/Endpoints/` | REST API endpoint definitions |
| `src/AiTestCrew.WebApi/Endpoints/UserEndpoints.cs` | User management CRUD |
| `src/AiTestCrew.Runner/RemoteRepositories/` | HTTP-based repo implementations for remote mode |
| `ui/src/App.tsx` | React Router route definitions |
| `ui/src/api/` | TypeScript API client functions |
| `ui/src/pages/` | React page components |
| `ui/src/components/` | Reusable React UI components (incl. ConfirmDialog, MoveObjectiveDialog) |
| `ui/src/contexts/AuthContext.tsx` | API key auth state and login/logout |
| `ui/src/pages/LoginPage.tsx` | API key login form |
| `ui/src/types/index.ts` | TypeScript interfaces matching API responses |
| `Dockerfile` | Multi-stage Windows container build |
| `docker-compose.yml` | Docker Compose for deployment |
| `publish.ps1` | Self-contained publish script |
| `.env.example` | Environment variable template |

---

## Keeping Claude Context Current

Claude reads `CLAUDE.md` at the start of each session. Update it when you:

- Add a new agent type (add to the key files table and agent pattern section)
- Add a new run mode (add to the run modes table)
- Change a core convention (update the conventions section)
- Add a new slash command (add to the commands table)

The slash command files in `.claude/commands/` contain detailed instructions that reference specific file paths and line numbers. When refactoring moves code significantly, update the relevant command file so future invocations reference the correct locations.

---

## Tips

- **Start a session** by describing what you want to change — Claude reads `CLAUDE.md` automatically and will reference the right files without you listing them.
- **Use `/implement-feature` for anything not covered by a specific command** — it enforces the architectural rules and handles docs updates.
- **After a large change**, run `/review-agent <AgentName>` to catch any regressions against the pattern conventions.
- **Slash commands are plain Markdown** — open the files in `.claude/commands/` to read or edit the instructions Claude follows.
