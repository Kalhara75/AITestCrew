---
id: REQ-024
title: Chat assistant catalog exposes the aseXML template registry so it can answer "what transaction types are supported?"
status: Proposed
created: 2026-05-19
author: Kalhara Samarasinghe
author-note: A QA engineer asked the in-app assistant *"What are the MIL transaction types supported by the system?"* and got back *"That information isn't available in the current catalog — the catalog exposes endpoint codes, API stacks, modules, and test sets, but doesn't include a list of MIL transaction types. You may want to check the MIL module documentation or the Bravo DB directly for supported transaction types."* The information is in fact present in the running process — `TemplateRegistry` (loaded from `templates/asexml/**/*.manifest.json` at startup) holds every supported `TransactionType`, transaction group, template id, description, and field spec. It is just not surfaced in `ChatIntentService.BuildCatalogAsync`. This REQ closes that gap so QAs can ask the assistant about supported aseXML transaction types and (a) get a correct answer and (b) know which template ids and required fields to use when authoring an `aseXml`/`aseXmlDeliver` post-step.
area: webapi
related: REQ-004 (Event_AzureServiceBus post-step — same catalog-extension pattern for the Service Bus connection list), REQ-017 (Xray import — uses the catalog to map fragments to step kinds; benefits from richer template metadata when an Xray fragment names a transaction type), aseXML feature reference (`docs/functional.md`, `/asexml-reference` skill)
---

# REQ-024 — Chat assistant catalog exposes the aseXML template registry

## Goal

After REQ-024 ships, the in-app chat assistant can correctly answer questions like:

- *"What MIL transaction types are supported by the system?"*
- *"Which aseXML templates do we have for MeterDataNotification?"*
- *"What fields does template MDN-NEM12-30min need?"*
- *"Can I drop a MeterFaultAndIssueNotification?"*

It answers these by reading a new catalog field (`aseXmlTemplates` / `aseXmlTransactionTypes`) that mirrors the in-memory `TemplateRegistry`. The change is purely additive to the catalog — no new endpoint, no new agent, no schema migration.

Specifically:

1. `BuildCatalogAsync` includes two new fields:
   - `aseXmlTransactionTypes` — a flat, deduplicated, alphabetically-sorted array of every distinct `Manifest.TransactionType` across the loaded templates. This is the answer to *"what transaction types are supported?"* with zero further inference required by the LLM.
   - `aseXmlTemplates` — an array, one entry per loaded template, with `templateId`, `transactionType`, `transactionGroup`, `description`, and a compact `userFields` summary (token name → `{ required, example, description, format }`) for the user-supplied fields. Auto/const fields are omitted from the catalog to keep the payload small; the LLM never has to fill them.
2. The system prompt grows a short "aseXML template catalog rules" block that tells the LLM:
   - "MIL transaction types" / "aseXML transaction types" / "B2B transaction types" all resolve to `catalog.aseXmlTransactionTypes`.
   - When the user asks for a template by transaction type, filter `catalog.aseXmlTemplates` by `transactionType`.
   - Never invent a `templateId` or `transactionType` — only use values that appear literally in the catalog.
   - When authoring an `aseXml` / `aseXmlDeliver` post-step (existing `confirmCreatePostStep` flow), pick a `templateId` from `catalog.aseXmlTemplates` and supply only the `userFields` keys listed there.
3. When the registry is empty (no `templates/asexml/` directory or zero manifests loaded), both fields are emitted as empty arrays — not omitted — so the LLM can distinguish "no templates installed" from "the catalog forgot to include them" and answer the user honestly.

## Why now

### Concrete defect — the assistant lies about what it knows

Reproduction (screenshot in PR description):

1. Open the chat assistant on any page.
2. Ask *"What are the MIL transaction types supported by the system?"*.
3. Assistant replies: *"That information isn't available in the current catalog — the catalog exposes endpoint codes, API stacks, modules, and test sets, but doesn't include a list of MIL transaction types. You may want to check the MIL module documentation or the Bravo DB directly for supported transaction types."*

The reply is technically truthful about the catalog's contents but materially misleading. The information **is** in the running process — `TemplateRegistry._byId` is populated at startup from `templates/asexml/**/*.manifest.json`. The QA engineer has no way to know that the assistant could trivially answer this question if the catalog were extended. They follow the suggested workaround ("check the MIL module documentation or the Bravo DB directly") and waste time grepping the repo for transaction types.

### Why this is high-value for QA

QAs onboarding to the project ask exactly this question first: *"what kinds of transactions can I generate and deliver?"* Today they have to:

- Open `/asexml-reference` skill text (not visible without the CLI), or
- `ls templates/asexml/` on the file system, or
- Read `docs/functional.md`'s aseXML section and cross-reference with what's actually loaded.

The assistant is supposed to be the discoverability surface that removes those steps. Closing this gap turns a friction moment for every new QA into a one-shot answer.

### Why the workaround is bad

Today's workarounds for *"what transaction types exist?"* are:

- **Read the file system**: `ls templates/asexml/` on the WebApi host. QAs typically don't have shell access to the WebApi machine; even if they do, the folder names don't tell them which template ids exist within each transaction type.
- **Read the docs**: `docs/functional.md` mentions transaction types in prose but doesn't list every supported template. Drift between docs and registry is possible.
- **Read the code**: only an engineer with repo access could do this.
- **Trial and error**: ask the assistant to author an `aseXml` post-step with a guessed transaction type; if rendering fails, infer the supported types from the error. Slow and gives no confidence about coverage.

None of these match the assistant's promise of "ask me about your test suite and I'll tell you".

### Why this is the natural next step

The catalog already follows this pattern for two analogous registries:

- **Endpoint codes** — sourced from each environment's Bravo DB (`mil.V2_MIL_EndPoint`), exposed as `catalog.endpointsByEnv` (`ChatIntentService.cs:259-272`). The system prompt has explicit rules at lines 851-854.
- **Service Bus connections** — sourced from `TestEnvironmentConfig.Environments.<env>.ServiceBusConnections`, exposed as `catalog.serviceBusConnectionsByEnv` (`ChatIntentService.cs:307-322`). System prompt rules at line 855.

aseXML templates are a third in-memory registry of the same shape — discovered at startup, immutable for the process lifetime, used by the LLM to validate generated step payloads. The extension follows the existing pattern exactly.

## Current behaviour

Trace of *"What MIL transaction types are supported?"* against the current codebase:

1. UI posts `POST /api/chat/message` with the user message.
2. `ChatIntentService.ProcessAsync` resolves the conversation and calls `BuildCatalogAsync` (`ChatIntentService.cs:185`).
3. `BuildCatalogAsync` returns an anonymous object with `modules`, `environments`, `defaultEnvironment`, `apiStacks`, `defaultStack`, `defaultModule`, `endpointsByEnv`, `serviceBusConnectionsByEnv`, `agents`, `currentTestSet`, `postStepConfig` (lines 324-337). **No aseXML fields.**
4. The catalog is JSON-serialised into the system prompt at line 866.
5. The LLM, doing its job, answers truthfully: the catalog has no field listing transaction types, so it tells the user that and suggests external sources.

Meanwhile, `TemplateRegistry` (registered as a singleton at `Program.cs:121-124`) has `LoadFrom` already run with the templates path from `TestEnvironmentConfig.AseXml.TemplatesPath`. Its `All()` method (line 85) returns every `LoadedTemplate` with full `Manifest` data. Two other services already consume this registry — `AseXmlGenerationAgent` (line 125-131) and `AseXmlDeliveryAgent` (line 141-150) — so its lifecycle is well-established.

## Desired behaviour

### Catalog additions

`BuildCatalogAsync` is extended to project the registry into the catalog. New fields appear at the same level as `endpointsByEnv` / `serviceBusConnectionsByEnv`:

```jsonc
{
  // ... existing fields ...

  "aseXmlTransactionTypes": [
    "MeterDataNotification",
    "MeterFaultAndIssueNotification"
  ],

  "aseXmlTemplates": [
    {
      "templateId": "MDN-NEM12-30min",
      "transactionType": "MeterDataNotification",
      "transactionGroup": "MTRD",
      "description": "MDN delivering NEM12 30-minute interval data.",
      "userFields": {
        "To": { "required": true, "example": "RETAILER01", "description": "Recipient participant id." },
        "CsvIntervalData": { "required": true, "format": "nem12", "description": "NEM12 CSV body (300 / 400 / 500 records)." }
      }
    },
    {
      "templateId": "MDN-NEM12-5min",
      "transactionType": "MeterDataNotification",
      "transactionGroup": "MTRD",
      "description": "MDN delivering NEM12 5-minute interval data.",
      "userFields": { /* ... */ }
    },
    {
      "templateId": "MFN-OneInAllIn",
      "transactionType": "MeterFaultAndIssueNotification",
      "transactionGroup": "OWNX",
      "description": "MFN OneInAllIn — single fault notification.",
      "userFields": { /* ... */ }
    }
  ]
}
```

`userFields` is derived by iterating `Manifest.Fields` and including **only** entries with `Source == "user"`. Auto-generated (`MessageID`, timestamps, transaction ids) and constant (`From`, fixed schema versions) fields are excluded — they're not relevant to the LLM when authoring a post-step, and excluding them keeps the catalog payload small. Each entry surfaces `required` (bool, default false), `example` (string, optional), `description` (string, optional), and `format` (string, optional, e.g. "nem12").

### Projection helper

A small private helper on `ChatIntentService`:

```csharp
private static object BuildAseXmlTemplateCatalog(TemplateRegistry registry)
{
    var templates = registry.All()
        .OrderBy(t => t.Manifest.TransactionType, StringComparer.OrdinalIgnoreCase)
        .ThenBy(t => t.Manifest.TemplateId, StringComparer.OrdinalIgnoreCase)
        .Select(t => new
        {
            templateId = t.Manifest.TemplateId,
            transactionType = t.Manifest.TransactionType,
            transactionGroup = t.Manifest.TransactionGroup,
            description = t.Manifest.Description,
            userFields = t.Manifest.Fields
                .Where(kvp => string.Equals(kvp.Value.Source, "user", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        required = kvp.Value.Required,
                        example = kvp.Value.Example,
                        description = kvp.Value.Description,
                        format = kvp.Value.Format
                    },
                    StringComparer.OrdinalIgnoreCase)
        })
        .ToArray();

    var transactionTypes = templates
        .Select(t => t.transactionType)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return new { transactionTypes, templates };
}
```

This helper is called once from `BuildCatalogAsync` and its result is destructured into the two top-level catalog fields. Empty registry → both arrays empty (not null), so the LLM has an unambiguous signal.

### Constructor change

`ChatIntentService` adds one constructor parameter: `TemplateRegistry templateRegistry`. The DI registration at `Program.cs:144-149` already resolves `TemplateRegistry`, so the new wiring is a one-line addition in the constructor argument list. To keep test-friendliness consistent with the existing `agentRepo` / `convRepo` optional-nullable pattern, the new parameter is **non-optional** — `TemplateRegistry` is always registered (it loads to an empty cache if the templates folder is missing) so there is no scenario where it's legitimately absent. Tests that instantiate the service directly must construct one via `TemplateRegistry.LoadFrom("nonexistent", logger)` (which returns an empty registry without throwing).

### System prompt additions

A new bullet block inserted before the existing "Universal rules" section (`ChatIntentService.cs:857`), styled after the endpoint-catalog rules at lines 851-855:

```
aseXML template catalog rules:
- catalog.aseXmlTransactionTypes is the deduplicated list of supported aseXML transaction types — the answer to "what transaction types are supported?", "what MIL transaction types do we have?", or "what B2B transaction types can I generate?". These three phrasings are equivalent in this codebase; "MIL transaction types" refers to the aseXML types that flow through the MIL endpoint catalog.
- catalog.aseXmlTemplates is the per-template detail: each entry has templateId, transactionType, transactionGroup, description, and userFields (the user-supplied fields with required/example/description/format). Auto-generated and constant fields are intentionally omitted.
- When the user asks "what templates exist for <transactionType>?", filter catalog.aseXmlTemplates by transactionType (case-insensitive).
- When the user asks "what fields does template <templateId> need?", read userFields from the matching template entry. List required fields first.
- When authoring an aseXml / aseXmlDeliver post-step via confirmCreatePostStep, only use a templateId that appears literally in catalog.aseXmlTemplates. Only fill keys that appear in that template's userFields — never invent additional fields, never re-emit auto/const fields.
- If catalog.aseXmlTransactionTypes is empty, no templates are installed on this server. Tell the user plainly rather than guessing; suggest they ask an operator to add templates under templates/asexml/.
- "MIL endpoints" (catalog.endpointsByEnv) and "MIL transaction types" (catalog.aseXmlTransactionTypes) are different things — endpoints are per-env delivery destinations (from Bravo DB), transaction types are the aseXML payload schemas (from templates/asexml/). Don't conflate them.
```

The final bullet is important because the QA's original question used "MIL" which is ambiguous — endpoints and transaction types are both "MIL"-coloured concepts in this codebase. The bullet keeps the LLM from answering with the endpoint list when the user really wanted transaction types (or vice versa).

### No new endpoint

The catalog is internal — assembled per request, embedded in the system prompt. No `GET /api/asexml/templates` endpoint is added in this REQ. If a future feature needs the template list at the HTTP boundary (e.g. an aseXML authoring UI that wants a transaction-type dropdown), it can be added as a separate concern; the existing pattern of internal-only catalog is preserved.

### Catalog payload size

Today's typical catalog is ~1–4 KB serialised. Adding the template registry adds roughly:

- 2 templates × ~200 bytes per template entry ≈ 400 bytes today.
- At 50 templates (realistic ceiling for the foreseeable future), ~10 KB.

This is well within the LLM context budget (the system prompt is already ~5–8 KB). No truncation or paging required. If the registry later grows past ~200 templates, the projection could be made conditional on the user's question intent — but that's premature today.

## Files to touch

| File | Why |
|---|---|
| `src/AiTestCrew.WebApi/Services/ChatIntentService.cs` | Add `TemplateRegistry` constructor dependency. Extend `BuildCatalogAsync` to project the registry into two new catalog fields via a new private `BuildAseXmlTemplateCatalog` helper. Insert the "aseXML template catalog rules" block into the system prompt before "Universal rules". |
| `src/AiTestCrew.WebApi/Program.cs` | One-line update to the `ChatIntentService` registration to pass `sp.GetRequiredService<TemplateRegistry>()` through. (The registry is already registered at line 121-124; no new singleton needed.) |
| `docs/architecture.md` | Add `aseXmlTransactionTypes` + `aseXmlTemplates` to the "Chat Assistant" section's catalog-fields list (mirrors the existing entries for `endpointsByEnv` and `serviceBusConnectionsByEnv`). |
| `docs/functional.md` | Under the chat-assistant section (or the aseXML feature reference), add a one-paragraph note that QAs can ask the assistant about supported transaction types and templates. |
| `CLAUDE.md` | No change — the file already documents `ChatIntentService` as the catalog assembler; the new fields are details, not architectural shifts. |

No schema migration. No API contract change. No frontend change (the chat UI already renders whatever the assistant returns).

## Acceptance criteria

1. **Catalog includes both new fields when templates are loaded.** With `templates/asexml/MeterDataNotification/MDN-NEM12-30min.{xml,manifest.json}` and `templates/asexml/MeterFaultAndIssueNotification/MFN-OneInAllIn.{xml,manifest.json}` present at startup, the JSON payload generated by `BuildCatalogAsync` (introspected via a focused unit test that calls the method directly, or via a logging breakpoint on the catalog JSON) contains:
   - `aseXmlTransactionTypes` as a sorted array containing exactly `["MeterDataNotification", "MeterFaultAndIssueNotification"]`.
   - `aseXmlTemplates` as an array with at least one entry per template, each with the five documented fields and a non-empty `userFields` dict for any template whose manifest has `user`-source fields.
2. **Auto/const fields are excluded from userFields.** Given a template manifest with a mix of `source: "auto"` (e.g. `MessageID`), `source: "const"` (e.g. `From`), and `source: "user"` (e.g. `To`, `CsvIntervalData`) entries, the `userFields` dict in the catalog contains only the `user`-source keys. `MessageID` and `From` are absent.
3. **Empty registry produces empty arrays, not nulls.** If `TestEnvironment.AseXml.TemplatesPath` points to a non-existent or empty folder, `aseXmlTransactionTypes` and `aseXmlTemplates` are both `[]` (empty arrays). They are present in the JSON, not omitted.
4. **Assistant answers "what transaction types are supported?" correctly.** End-to-end through `POST /api/chat/message`: a request with body `{ "message": "What MIL transaction types are supported by the system?" }` returns a `reply` that names each transaction type from `aseXmlTransactionTypes`. The reply does not include the previous "this information isn't available" phrasing. No `actions` are emitted (informational only). Verified via integration test or manual exercise.
5. **Assistant answers template-detail questions.** Request: `"What fields does the MDN-NEM12-30min template need?"`. Reply lists the user-supplied fields (e.g. `To`, `CsvIntervalData`) with required/example info. No invented fields.
6. **Assistant doesn't conflate endpoints with transaction types.** Request: `"What MIL endpoints are configured in sumo-retail?"`. Reply lists codes from `endpointsByEnv["sumo-retail"]`, not transaction types. Request: `"What MIL transaction types do we support?"`. Reply lists transaction types, not endpoint codes. The system prompt's "MIL endpoints vs MIL transaction types" disambiguation bullet drives this.
7. **Existing catalog behaviour is unchanged.** The previous JSON shape — all of `modules`, `environments`, `defaultEnvironment`, `apiStacks`, `defaultStack`, `defaultModule`, `endpointsByEnv`, `serviceBusConnectionsByEnv`, `agents`, `currentTestSet`, `postStepConfig` — is still present at the same nesting and in the same shape. The new fields are additive only.
8. **No DI failure on startup.** With or without templates present, the WebApi starts cleanly. `dotnet run --project src/AiTestCrew.WebApi` logs the existing "aseXML template registry loaded N template(s)" line and then serves `POST /api/chat/message` without 500 errors.
9. **Authoring an aseXML post-step still uses literal template ids.** Existing system-prompt rule "Only use ids/keys/codes that appear literally in the catalog" continues to apply. A `confirmCreatePostStep` with `target: "AseXml_Generate"` produced by the assistant references a `templateId` that is present in `catalog.aseXmlTemplates`. Verified by a manual exercise: ask the assistant *"after the WinForms test drop an MFN aseXML file"* and confirm the emitted card's `aseXml.templateId` is `MFN-OneInAllIn` (or whichever template covers that transaction type), not a hallucinated id.
10. **Documentation reflects reality.** `docs/architecture.md`'s "Chat Assistant" catalog-fields list mentions the new fields. `docs/functional.md` mentions the new question shape under chat-assistant usage. A new QA reading either doc learns that they can ask the assistant about transaction types.

## Scope — what's out

- **A standalone HTTP endpoint** (`GET /api/asexml/templates`). The catalog is internal-only today, and no consumer outside the assistant needs this list at the HTTP boundary yet. If/when an aseXML authoring UI lands, it can add its own endpoint.
- **Hot-reload of the registry.** Templates load once at startup (`Program.cs:121-124`); the assistant inherits whatever was loaded then. Adding a new template requires a WebApi restart. This is the existing behaviour and is out of scope.
- **Validation that the catalog template list matches what's on disk.** The registry already logs any per-file load failures (`TemplateRegistry.cs:54-66`). No new health surface is added.
- **A frontend dropdown showing transaction types.** The catalog is consumed by the LLM, not the UI directly. A future aseXML authoring dialog could read from the same projection (or a new endpoint) — out of scope for this REQ.
- **Surfacing auto/const fields.** Excluded from `userFields` deliberately. If an LLM action ever needs to know that, say, `MessageID` is auto-generated, the system prompt already documents that — and the QA-facing assistant flow doesn't need to know.
- **Per-environment template availability.** Templates are global (loaded once into the registry); they're not scoped per customer environment. If a future requirement scopes templates per env, the projection will need an `aseXmlTemplatesByEnv` shape — but today there is no such concept.
- **Localisation / multi-language descriptions.** Manifest `description` is whatever the template author wrote (English). The assistant relays it verbatim.
- **Translating user phrases to template ids deterministically.** The LLM is responsible for mapping natural language (e.g. "MFN", "metering fault notification") to a `templateId` using `description` + `transactionType`. No new heuristics or aliases are added.
- **Surfacing template body XML or sample renders.** Only manifest metadata is in the catalog. The actual XML body stays in `LoadedTemplate.Body` (used by the renderer) and is not LLM-visible. Sample-rendering is a separate, out-of-scope feature.

## Risks / notes

- **Catalog growth.** At ~200 bytes per template, 50 templates adds ~10 KB to the system prompt. Today's catalog is small; this is well within budget. If the registry grows past ~200 templates, paging or intent-conditional inclusion may be worth considering — but premature today. Acceptance is fine as-is.
- **"MIL" ambiguity.** The codebase uses "MIL" for both delivery endpoints (`mil.V2_MIL_EndPoint`) and the broader transaction layer (in informal speech). The system-prompt disambiguation bullet (final bullet of the new block) is the primary mitigation. If users still report confusion, the prompt can be tightened — but the bullet should cover the common cases.
- **Auto/const fields invisible to the assistant.** If a future post-step needs the LLM to know about an auto-generated field (e.g. to capture `MessageID` and assert on it), the projection would need extending. Today this isn't needed — captures happen on the delivery side, not from the manifest. Documented for future maintainers.
- **`TemplateRegistry` is loaded once at startup.** If a user adds a template file at runtime and asks the assistant, they get stale info until the WebApi restarts. This is the existing behaviour for every consumer of the registry; this REQ doesn't change it. The dashboard could grow a "templates loaded" panel with a manual-reload button, but that's a separate concern.
- **Empty `userFields` is possible.** A template whose manifest has only `auto` + `const` fields would surface `userFields: {}`. Acceptable — the LLM should still see the template id, transaction type, and description. The empty dict signals "user fills nothing; the renderer fills everything".
- **Naming consistency with existing fields.** `endpointsByEnv` and `serviceBusConnectionsByEnv` are per-env. `aseXmlTransactionTypes` and `aseXmlTemplates` are global (not per-env). The naming reflects that — no `ByEnv` suffix. This is correct given the registry's global scope today.
- **System-prompt drift risk.** The new bullets sit alongside ~400 lines of existing prompt text. Adding more rules increases the risk of contradictions or LLM confusion. The proposed bullets are short (7 lines) and explicitly cross-reference the existing endpoint-catalog rules — low drift risk. If a regression appears in some other intent (e.g. confirmRun no longer fires correctly), bisect by reverting the prompt block and re-introducing it incrementally.
- **Test surface.** A unit test on `BuildCatalogAsync` (or a focused integration test that exercises `POST /api/chat/message` with a stub `IChatCompletionService` that echoes the system prompt) is the right level. End-to-end LLM testing is unstable; the acceptance criteria deliberately mix shape-based asserts (AC#1-3, #7) with manual LLM checks (AC#4-6, #9).

## How this lands with the existing system

REQ-024 is a small, single-file change in the WebApi service layer. The lift is:

1. One new constructor parameter on `ChatIntentService` (mechanical DI plumbing).
2. One new private helper (~30 lines).
3. Two new fields on the anonymous catalog object (additive).
4. One new bullet block in the system prompt (~7 lines of natural-language rules).
5. Two documentation paragraphs.

There is no schema change, no migration, no new endpoint, no agent change, no UI change, no contract version bump. Test coverage is a single new unit test on the catalog projection plus a manual end-to-end LLM check.

The blast radius is contained: the only consumer of `BuildCatalogAsync` is `ProcessAsync` in the same class, and the only behaviour change is "the LLM has more information available to it". No existing rule contradicts the new bullets; the endpoint-catalog rules at lines 851-855 are the closest pattern and the new block is styled identically.

## Demonstration script (for the reviewer)

1. **Verify "before" baseline.** With `main`'s code, open the chat assistant. Ask *"What are the MIL transaction types supported by the system?"*. Confirm the assistant responds with the "this information isn't available" phrasing (matches the screenshot in the REQ).
2. **Apply the change.** Switch to the REQ-024 branch. Restart the WebApi (`dotnet run --project src/AiTestCrew.WebApi`).
3. **Re-ask the same question.** Verify the assistant now lists each transaction type loaded from `templates/asexml/`. Spot-check at least two — `MeterDataNotification` and `MeterFaultAndIssueNotification` for the default seed templates.
4. **Drill into a transaction type.** Ask *"What templates do we have for MeterDataNotification?"*. Verify the reply names `MDN-NEM12-30min` (and `MDN-NEM12-5min` if present) with their descriptions.
5. **Drill into a template's fields.** Ask *"What fields does the MDN-NEM12-30min template require?"*. Verify the reply lists `To` and `CsvIntervalData` (or whatever is `Required: true` in the manifest) and mentions the NEM12 format hint.
6. **Disambiguation check.** Ask *"What MIL endpoints exist in sumo-retail?"*. Verify the reply lists endpoint **codes** (from `endpointsByEnv`), not transaction types. Then ask *"What MIL transaction types exist?"* and verify it lists transaction types, not endpoint codes. The two questions should produce disjoint, correct answers.
7. **Empty-registry check.** Temporarily rename the `templates/asexml/` folder to `templates/asexml.bak/`. Restart WebApi. Ask the same question. Verify the assistant says no templates are installed (rather than fabricating a list or repeating the old "not in the catalog" excuse). Restore the folder.
8. **Authoring exercise.** Ask the assistant *"after the WinForms test drop an MFN aseXML file"* in a test set that has a WinForms parent step. Verify the emitted `confirmCreatePostStep` card has `target: "AseXml_Deliver"` (or `AseXml_Generate`) and a `templateId` that appears in `catalog.aseXmlTemplates`. No hallucinated ids.
9. **Regression spot-checks.** Confirm existing intents still work: ask *"What modules do we have?"* (lists modules), *"What environments are configured?"* (lists environments), *"What Service Bus connections exist in sumo-retail?"* (lists keys from `serviceBusConnectionsByEnv`). No regressions in unrelated intents.
10. **Documentation review.** Open `docs/architecture.md`'s Chat Assistant section and `docs/functional.md`'s chat-assistant subsection. Confirm both mention the new catalog fields and the new question shape.
