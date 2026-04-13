Scaffold a new aseXML transaction template.

The template is consumed by BOTH agents — `AseXmlGenerationAgent` (renders XML to disk, `AseXml_Generate`) and `AseXmlDeliveryAgent` (renders + uploads to a Bravo endpoint, `AseXml_Deliver`). A single `{templateId}.xml` + `{templateId}.manifest.json` pair covers both workflows; no separate delivery template is needed.

Arguments: $ARGUMENTS
Expected format: `<TransactionType> <templateId> "<description>"`
Examples:
- `CustomerDetailsNotification CDN-MoveIn "Customer Details Notification for move-in events"`
- `MeterFaultAndIssueNotification MFN-PlannedMaintenance "MFN for planned maintenance outages"`
- `OneWayNotification OWN-SupplyRestored "One-way notification when supply is restored"`

## What you must do

Adding a new aseXML template is a **content-only change** by design. No agent, orchestrator, persistence, or DI changes should be needed — if you find yourself editing those files, stop and reconsider.

### Step 1 — Find a representative sample

Before writing a template you must have a representative aseXML sample for this transaction type. Ask the user for one if you don't have it (ideal: a real captured message from the target environment, something the Bravo platform is known to accept). Note the namespace, header shape, transaction wrapper, and all transaction-type-specific elements.

If the user pastes a sample into the conversation, save it temporarily so you can diff against your output.

### Step 2 — Read the engine's contracts

Read these in full before authoring the template:

- `templates/asexml/MeterFaultAndIssueNotification/MFN-OneInAllIn.xml` — reference template body with `{{tokens}}`
- `templates/asexml/MeterFaultAndIssueNotification/MFN-OneInAllIn.manifest.json` — reference manifest with every `source` type demonstrated
- `src/AiTestCrew.Agents/AseXmlAgent/Templates/AseXmlRenderer.cs` — the token regex, field resolution rules, and error conditions
- `src/AiTestCrew.Agents/AseXmlAgent/Templates/FieldGenerators.cs` — the closed set of built-in auto generators

Do not guess — the renderer enforces strict invariants and unmatched tokens / missing manifest entries fail hard.

### Step 3 — Create the template body

Path: `templates/asexml/{TransactionType}/{templateId}.xml`

Rules:
- Copy the sample verbatim, then replace every varying piece of content with a `{{TokenName}}` placeholder.
- Token syntax is literal: `{{FieldName}}` — no spaces inside the braces matter (`{{ x }}` works) but stick to `{{FieldName}}` for consistency. Tokens must be valid C# identifier-ish names (`[A-Za-z_][A-Za-z0-9_]*`).
- Tokens go in **text content and attribute values only** — never in element or attribute names. The renderer XML-escapes substituted values, so `&`, `<`, `>`, `"`, `'` in user-supplied values are safe.
- Keep attributes like `xmlns:*` and `xsi:schemaLocation` exactly as the sample has them — those are not variable.
- The Header block (`<From>`, `<To>`, `<MessageID>`, `<MessageDate>`, `<TransactionGroup>`, `<Priority>`, `<SecurityContext>`, `<Market>`) is common to all aseXML transactions — copy its shape from the MFN template.
- The transaction body inside `<Transaction>...</Transaction>` is where the transaction-type-specific elements go.

Every `{{Token}}` in the body MUST have a corresponding entry in the manifest (step 4). Unknown tokens cause a render failure at run time.

### Step 4 — Create the manifest

Path: `templates/asexml/{TransactionType}/{templateId}.manifest.json`

Schema (consult `TemplateManifest.cs` and `FieldSpec.cs`):

```json
{
  "templateId": "<must match the filename without .xml>",
  "transactionType": "<e.g. CustomerDetailsNotification — used by LLM to pick templates>",
  "transactionGroup": "<OWNX | DIGI | ... — the <TransactionGroup> header value>",
  "description": "<one-sentence purpose — shown to the LLM when picking templates>",
  "fields": {
    "<TokenName>": { "source": "auto" | "user" | "const", ...spec-fields... }
  }
}
```

**Choose `source` for each token:**

| Source   | When to use                                                                       | Required spec fields                                    |
|----------|-----------------------------------------------------------------------------------|---------------------------------------------------------|
| `auto`   | Value is generated every run (IDs, timestamps). Never user-editable.              | `generator`, plus `pattern` (id generators) or `offset` (time generators) |
| `user`   | Value varies per test case and the user/LLM supplies it from the objective.       | `required` (bool), optionally `example` (string)        |
| `const`  | Value is hardwired in the template (sender codes, hardwired workflow values).     | `value`                                                 |

**Built-in generators** (names are case-insensitive):

| generator       | Purpose                                                  | Spec                     | Output example                |
|-----------------|----------------------------------------------------------|--------------------------|-------------------------------|
| `messageId`     | Message header ID                                        | `pattern` with `{rand8}` | `MSRINB-MFN-HN4YU4L3-DD`      |
| `transactionId` | Transaction ID (on the `<Transaction>` element)          | `pattern` with `{rand8}` | `AURORAP-TXN-BE-9WZBP7NI`     |
| `nowOffset`     | ISO-8601 timestamp with a fixed timezone offset          | `offset` e.g. `+10:00`   | `2026-04-13T15:20:57+10:00`   |
| `today`         | Date-only equivalent of `nowOffset`                      | `offset` e.g. `+10:00`   | `2026-04-13`                  |

**Need a generator that doesn't exist?** Don't pick a close-ish one and hope — stop and follow Step 7 below.

**Tips for writing the manifest:**
- Group fields in the same order they appear in the XML — makes diffs readable.
- Provide an `example` for every `user` field. The LLM uses it as a hint and (Phase 1.5) the UI will use it as a placeholder. Pick values that match what a real message would contain (not `foo`/`bar`).
- Mark a `user` field `required: true` if the XML is invalid without it, `false` otherwise. Required-but-missing causes a failing render step with the field name — that's good; the test author gets a clear signal.
- For `const` fields, use exactly the value that appears in the real-world sample. If you're not sure, ask — don't invent a value.

### Step 5 — Verify token ↔ manifest parity

Cross-check:
- Every `{{TokenName}}` in the template body appears as a key in `manifest.fields`.
- Every key in `manifest.fields` appears at least once in the template body (or is intentionally const-only for future use — rare).
- `templateId` in the manifest matches the `.xml` filename exactly.

If these don't match, the renderer will either:
- Fail with *"references undeclared token(s) not in the manifest"* (body has tokens the manifest doesn't list), or
- Silently resolve but leave the field unused (manifest has keys the body doesn't reference).

### Step 6 — Build and smoke-test

Rebuild once so the new template+manifest pair is copied into each project's `bin/templates/asexml/...` (the existing `<Content>` glob in `AiTestCrew.Runner.csproj` and `AiTestCrew.WebApi.csproj` handles this — you do NOT need to touch the csproj files):

```bash
dotnet build src/AiTestCrew.Runner/AiTestCrew.Runner.csproj
```

Then run the two relevant objectives — prove the template works with BOTH agents:

```bash
cd src/AiTestCrew.Runner/bin/Debug/net8.0-windows

# Pre-reqs (only if the module / test set don't exist)
./AiTestCrew.Runner.exe --create-module "AEMO B2B"
./AiTestCrew.Runner.exe --create-testset aemo-b2b "<TransactionType> scenarios"

# 1. GENERATE — phrase the objective with a verb like "generate/produce/render"
#    so the LLM picks AseXml_Generate. Output lands in output/asexml/<ts>_<taskId>/.
./AiTestCrew.Runner.exe --module aemo-b2b --testset <slug> \
  --obj-name "<short label>" \
  "<full generation objective naming the transaction type + all required user fields>"

# 2. DELIVER — phrase with "deliver/send/submit to <EndPointCode>" so the LLM picks
#    AseXml_Deliver. Output lands in output/asexml/<ts>_<taskId>_deliver/, and a real
#    SFTP/FTP upload happens. Pre-req: AseXml.BravoDb.ConnectionString in appsettings.json.
./AiTestCrew.Runner.exe --module aemo-b2b --testset <slug>-delivery \
  --endpoint <EndPointCode> \
  --obj-name "<short label>" \
  "Deliver <transaction type> ... to <EndPointCode> ..."
```

Inspect the generated artefacts in both runs and confirm:

1. **Layout matches the real sample** (element order, namespaces, nesting).
2. **User fields are your values**, verbatim.
3. **Const fields are hardwired** exactly as in the manifest.
4. **Auto fields are fresh** (`MessageID`, `TransactionID`, timestamps each changed vs. the sample).
5. **Well-formed XML** — the renderer runs `XDocument.Parse` internally, so a malformed output would have failed the step.
6. **No stray `{{Tokens}}` left** in the output — those only survive if a manifest entry is missing, which should have been a render error.
7. **Delivery — remote file lands at the right path** as reported by the `upload[1]` step. If the endpoint has `IsOutboundFilesZiped = 1`, a `package[1]` step appears and the uploaded filename is `{MessageID}.zip` containing a single `{MessageID}.xml` entry.

Running only the generation half is acceptable when the Bravo DB / SFTP endpoint isn't reachable from your machine — but note that in the review so the next person can finish the delivery smoke.

### Step 7 — (Optional) Add a new auto-field generator

Only if an existing generator cannot express the required value (e.g. a sequenced counter, a checksum-derived ID, a UUID format not covered by `{rand8}`):

1. Add a new method to `src/AiTestCrew.Agents/AseXmlAgent/Templates/FieldGenerators.cs`, keeping the function pure and deterministic in interface (random allowed internally).
2. Add an arm to the `Generate(FieldSpec spec)` `switch` that dispatches to your new method, matching on a lowercase generator name.
3. Document the generator in `docs/architecture.md` and in this skill's generator table (step 4 above) in the same edit.
4. Keep the generator set closed — resist adding one unless a real template needs it.

### Step 8 — Update documentation

Add a row for the new transaction type / template in `docs/functional.md`. Specifically, extend the aseXML section to mention which transaction types ship with templates — keep the list accurate, it's a pointer users rely on.

If you added a generator in step 7, also update the generators table in `docs/architecture.md` and in the "aseXML templates" section of `docs/functional.md`.

### Step 9 — Do NOT do these things

- Do NOT modify `AseXmlGenerationAgent.cs`, `AseXmlDeliveryAgent.cs`, `AseXmlRenderer.cs`, `TemplateRegistry.cs`, or the step definition classes to support a specific transaction type. If those files need to change, you're introducing coupling the engine was designed to avoid — stop and explain why.
- Do NOT modify the orchestrator, persistence, DI, or `TestTargetType` enum for a new template.
- Do NOT write template XML programmatically, with Razor, or via string concatenation elsewhere. The template file IS the source of truth.
- Do NOT hardcode auto-generated values (MessageID, TransactionID, timestamps) — they must go through generators so each run is unique.
- Do NOT commit `bin/.../output/asexml/` artefacts. `.gitignore` should exclude the `output/` directory. If you see generated XML staged, unstage it.
- Do NOT put sender codes or environment-specific URLs in a template without confirming with the user — those are often per-deployment and may need promoting from `const` to `user` in the future.
- Do NOT add endpoint-specific knowledge to a template (e.g. "OnlyForGatewaySPARQ"). Endpoint selection is a property of the test case (`AseXmlDeliveryTestDefinition.EndpointCode`), not the template — the same template body must be usable against any endpoint. Endpoints themselves are managed in the Bravo DB's `mil.V2_MIL_EndPoint` table, not in templates or code.

### Architecture constraints to respect

- Templates are pure content — the renderer logic lives in `AseXmlRenderer.cs` and handles all substitution, escaping, and validation. Both `AseXmlGenerationAgent` and `AseXmlDeliveryAgent` call the same renderer, so one template works for both.
- Generators are the only source of auto values. Don't compute `MessageID`/timestamps yourself.
- The LLM writes field values, never XML. If you find yourself asking the LLM to emit the XML body, you've broken the design — the determinism of the renderer is the core value proposition.
- Adding a new transaction type does not change the public surface of either agent (their `TestTargetType`s, `CanHandleAsync`, step persistence shape). If the target type needs to change, you're building a different agent — not extending the template set.
- Adding a new delivery protocol (AS2, HTTP POST, SMB) is NOT a template change — it's a new `IXmlDropTarget` implementation + a `DropTargetFactory` switch arm. Out of scope for this skill.
