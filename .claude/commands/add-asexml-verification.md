Scaffold a post-delivery UI verification attached to an existing aseXML delivery test case.

A verification records UI steps (Legacy MVC, Blazor, or WinForms) that run AFTER the XML has been delivered and Bravo has had time to process it. Values from the current run's XML render (`{{NMI}}`, `{{MessageID}}`, `{{TransactionID}}`, `{{Filename}}`, any template field) are injected into every UI step at playback — so the same verification works across every run with fresh IDs.

Arguments: $ARGUMENTS
Expected format: `<moduleId> <testSetId> <objectiveId> <target> "<verification-name>" [waitSeconds]`
Examples:
- `aemo-b2b mfn-delivery-tests deliver-mfn-one-in-all-in UI_Web_Blazor "MFN Process Overview shows 'One In All In'" 30`
- `aemo-b2b mfn-delivery-tests deliver-mfn-one-in-all-in UI_Web_MVC "Legacy MFN Search grid row exists"`
- `aemo-b2b mfn-delivery-tests deliver-mfn-one-in-all-in UI_Desktop_WinForms "Desktop admin tool shows transaction"`

## What you must do

Recording a verification is a **capture + auto-parameterise** operation. You should never hand-write or hand-edit verification steps — the recorder does the heavy lifting. If you find yourself about to write `{{Token}}` manually in step JSON, stop and reconsider.

### Step 1 — Confirm the prerequisites

A verification can only be recorded when the target objective has **already run successfully at least once**. Why: the recorder pulls the live `MessageID` / `TransactionID` / `Filename` / `EndpointCode` from the latest successful run so literals you type during recording can be auto-substituted with `{{Token}}` placeholders.

Before launching, confirm with the user:

1. Which **module** + **test set** + **objective (Id slug OR display Name)** to attach the verification to. The CLI accepts either form case-insensitively. If unclear, read `modules/<moduleId>/<testSetId>.json` and list the `testObjectives[].id` + `.name` values where `aseXmlDeliverySteps.length > 0`.
2. That objective has at least one execution run with status `Passed` in `executions/<testSetId>/*.json`. If not, tell the user to run the delivery first.
3. Which **target** surface the verification is for: `UI_Web_MVC` (uses `LegacyWebUiUrl`), `UI_Web_Blazor` (uses `BraveCloudUiUrl`), or `UI_Desktop_WinForms` (uses `WinFormsAppPath`). The matching config entry in `appsettings.json` must be populated.
4. **Auth state is cached for the target.** The recorder opens already-logged-in when the matching storage state exists — `LegacyWebUiStorageStatePath` for MVC, `BraveCloudUiStorageStatePath` for Blazor. If either path isn't configured, the CLI prints a hint and the recording will start unauthenticated (capturing a login flow into the verification — undesirable). Run `--auth-setup --target <UI_*>` first to cache the state:
   - `dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_MVC`
   - `dotnet run --project src/AiTestCrew.Runner -- --auth-setup --target UI_Web_Blazor`

### Step 2 — Read the engine's contracts

Skim these before launching the recorder so you understand what the tooling will do:

- `src/AiTestCrew.Agents/AseXmlAgent/VerificationStep.cs` — persistence shape (Description, Target, WaitBeforeSeconds, WebUi or DesktopUi).
- `src/AiTestCrew.Agents/AseXmlAgent/Recording/VerificationRecorderHelper.cs` — the auto-parameterisation rules (min length 4, longest-match-first, exact substring, first-key-wins).
- `src/AiTestCrew.Core/Utilities/TokenSubstituter.cs` — playback substitution (`{{Token}}` grammar, lenient unknown-token behaviour).

### Step 3 — Launch the recorder

Use the CLI — not a bare `PlaywrightRecorder.RecordAsync` call:

```bash
dotnet run --project src/AiTestCrew.Runner -- --record-verification \
  --module <moduleId> \
  --testset <testSetId> \
  --objective <objectiveId> \
  --target UI_Web_MVC|UI_Web_Blazor|UI_Desktop_WinForms \
  --verification-name "<descriptive name>" \
  [--wait <seconds>] \
  [--delivery-step-index <n>]
```

At launch, the CLI prints the auto-parameterise context:

```
Recording verification for deliver-mfn-one-in-all-in → target UI_Web_Blazor
Auto-parameterise context (9 keys):
  {{DateIdentified}} = 2025-05-04
  {{EndpointCode}}   = GatewaySPARQ
  {{Filename}}       = MSRINB-MFN-HN4YU4L3-DD.xml
  {{MessageID}}      = MSRINB-MFN-HN4YU4L3-DD
  {{MeterSerial}}    = 060738
  {{NMI}}            = 4103035611
  ...
```

Note these values. Anything longer than 4 characters that matches a context value will become a `{{Token}}`.

### Step 4 — Record against the real, already-processed data

The trick: the recorder is pointing at a system where the **prior run's** data lives. If the last run's MessageID was `MSRINB-MFN-HN4YU4L3-DD`, search for that MessageID in the UI — you'll find the actual record that Bravo processed. Assert its properties there.

**Do**:
- Search by NMI, MessageID, or TransactionID — whichever the UI offers.
- Click into the record, inspect its detail page, add `assert-text` steps for fields the test should verify (`ReasonForNotice`, transaction status, fault codes, etc.).
- Add `assert-url-contains` or `assert-title-contains` steps that anchor navigation (e.g. the URL should contain the MessageID after clicking a row).

**Don't**:
- Type values that aren't in the context (those stay as literals and won't update on next run).
- Assert on timestamps or volatile UI elements that change per session.
- Navigate away and back — keep the flow linear.

Save & Stop (web) or press S (desktop) when done.

### Step 5 — Review the captured + parameterised output

The CLI prints the saved step count and the test set JSON path. Open the JSON and scan the newly appended `verifications[]` entry under the target delivery case:

```json
"postDeliveryVerifications": [
  {
    "description": "MFN Process Overview shows 'One In All In'",
    "target": "UI_Web_Blazor",
    "waitBeforeSeconds": 30,
    "webUi": {
      "steps": [
        { "action": "fill", "selector": "input[name='nmiSearch']", "value": "{{NMI}}" },
        { "action": "click", "selector": "button:has-text('Search')" },
        { "action": "assert-text", "selector": "td.message-id", "value": "{{MessageID}}" },
        { "action": "assert-text", "selector": "td.reason-for-notice", "value": "One In All In" }
      ]
    }
  }
]
```

Verify:
- `{{NMI}}` and `{{MessageID}}` appear where the recorder substituted them.
- Any literal you expected to be a token but isn't — either the value was shorter than 4 chars, or it didn't exactly match a context value. Use the web UI edit dialog (next step) to tidy up, or edit the JSON manually if you prefer.
- Selectors are stable (aria-label / text-based rather than deep CSS trees). If a selector looks brittle, the edit dialog lets you rewrite it before replay.

### Step 5a — Tidy the recording in the web UI (optional but recommended)

Open the test set in the web UI (`http://localhost:5173` → module → test set). Under the delivery case, the **Post-delivery UI verifications** panel now shows your new verification. You can:

- **Expand the row (▸)** to see each step with action / selector / value / timeout, `{{Tokens}}` highlighted in indigo.
- **Click the pencil ✎** on Web UI verifications to open the same **Edit Web UI Test Case** dialog used for standalone web UI tests. From there you can:
  - Delete captured login-flow steps (though running `--auth-setup --target UI_Web_*` before recording avoids capturing them in the first place).
  - Rewrite a brittle selector.
  - Reorder / add / remove steps.
  - Insert `{{Token}}` placeholders the auto-parameteriser missed (e.g. for values shorter than 4 chars).
  - Edit the Name / Description / Start URL / screenshot-on-failure flag.
- **Click the trash icon 🗑** to delete the whole verification and re-record from scratch.

Desktop (WinForms) verifications support view + delete only — no edit dialog yet. Delete-and-re-record is the workflow for fixing desktop verifications.

### Step 6 — Replay to confirm

Run the delivery test case again:

```bash
dotnet run --project src/AiTestCrew.Runner -- --reuse <testSetId> --module <moduleId>
```

Expect:
- Fresh delivery (`render` → `resolve-endpoint` → optional `package` → `upload`).
- `wait[1.1]` step for the configured delay.
- `verify[1.1] <child steps>` with `{{MessageID}}` substituted to the NEW message id produced by this run. UI assertions pass because Bravo processed the fresh file.
- If you added multiple verifications, each gets its own `wait[1.N]` + `verify[1.N]` block.

If a verification fails, check the per-step `Detail` and the Playwright / WinForms screenshot directory for post-mortem.

### Step 7 — Do NOT do these things

- Do NOT hand-edit verification step Values to invent `{{Tokens}}` that aren't in the render context. Run the delivery again, read the surfaced context, and use only what's there.
- Do NOT attach verifications to non-delivery objectives (the CLI blocks this, but don't try to work around it by editing JSON — the delivery agent is the only code path that runs verifications).
- Do NOT record verifications against data that the system hasn't processed yet. You'll capture literals the system won't match on replay.
- Do NOT commit Playwright traces / screenshots / temp output under `bin/.../output/`. Those are run artefacts, gitignored by the repo root `.gitignore`.
- Do NOT add verification-specific fields to `WebUiStep` or `DesktopUiStep`. The same step types serve both standalone UI agents and verifications; divergence is a smell.
- Do NOT wire verifications to trigger through `TestTask.DependsOn` — it's declared but intentionally unused. The delivery agent's sibling-dispatch model is what makes verifications "steps of the same test case".

### Architecture constraints to respect

- A verification is **owned by** one `AseXmlDeliveryTestDefinition.PostDeliveryVerifications` entry. It cannot be shared across delivery cases (YAGNI) and can't be attached to any other target type.
- The same recorded steps work against fresh MessageIDs on every run because of `{{Token}}` substitution in `TokenSubstituter`. Don't bypass that — write steps that are parameterisable by design.
- Wait strategy is fixed-delay only in the current milestone. If a real smarter-wait need emerges (SFTP-pickup polling, Bravo DB status polling), extend `VerificationStep` with an optional richer strategy rather than adding parallel fields.
- Playback runs the existing UI agent (`LegacyWebUiTestAgent`, `BraveCloudUiTestAgent`, `WinFormsUiTestAgent`) unchanged — sibling dispatch via `ExecuteAsync(syntheticTask)`. Do NOT refactor UI agent internals to "support verifications" — that breaks the abstraction.
