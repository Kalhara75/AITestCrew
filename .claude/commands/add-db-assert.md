Scaffold a DB Assert post-step attached to an existing parent test step.

A DB Assert (`Db_SqlServer` post-step) runs a single read-only SELECT against a
configured DB and asserts the result via row-count or per-column rules — and
optionally captures returned values as `{{Token}}` for sibling post-steps.
This is the parallel of `/add-asexml-verification` for non-UI assertions.

Arguments: $ARGUMENTS
Expected format: `<moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`

Examples:
- `aemo-b2b mfn-delivery-tests deliver-mfn AseXmlDeliver 0 "confirm Jobs has a row for the just-delivered MessageID with Status='Processed' and Payload.OrderId={{OrderId}}, capture JobId"`
- `bravo-smoke jobs-checks search-job WebUi 0 "verify a Jobs row exists for the searched MessageID"`

`parentKind` must be one of: `Api`, `WebUi`, `DesktopUi`, `AseXml`, `AseXmlDeliver`.
`parentStepIndex` is 0-based into the parent objective's matching step list.

## What you must do

DB Asserts are authored without a UI recorder — they're SQL + structured
assertions. The skill's job is to draft, validate against the live DB via the
dry-run endpoint, then PUT the post-step.

### Step 1 — Confirm the prerequisites

1. Module + test set + objective exist. Read `modules/<moduleId>/<testSetId>.json`
   and verify the test objective with id `<objectiveId>` is present. If the
   user gave the objective name instead, resolve to the slug.

2. Parent step exists at `<parentKind>` index `<parentStepIndex>`. The carrier
   field per kind:
   - `Api` → `apiSteps[i]`
   - `WebUi` → `webUiSteps[i]`
   - `DesktopUi` → `desktopUiSteps[i]`
   - `AseXml` → `aseXmlSteps[i]`
   - `AseXmlDeliver` → `aseXmlDeliverySteps[i]`

3. The DB connection key the user mentions (or `BravoDb` by default) is
   configured for the test set's environment. Hit
   `GET /api/db-check/connections?envKey=<key>` and check the returned `keys`.

4. The active env's `AllowDbDryRun` is true. The dry-run will return 403 if it's
   disabled — surface that to the user and stop.

### Step 2 — Read the engine's contracts

Skim these before drafting so you understand what's allowed:

- `src/AiTestCrew.Storage/DbAgent/DbCheckStepDefinition.cs` — persistence shape
  (Sql, ConnectionKey, ExpectedRowCount, ColumnAssertions, Captures, TimeoutSeconds).
- `src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs` — the 14 supported operators.
- `src/AiTestCrew.Agents/DbAgent/DbCheckSqlGuardrails.cs` — SELECT-only,
  no semicolons, no INSERT/UPDATE/DELETE/DDL, no EXEC, no SELECT INTO.
- `src/AiTestCrew.Agents/DbAgent/ColumnAssertionEvaluator.cs` — operator semantics
  (NULL fidelity, JSONPath extraction, numeric/date tolerance).

### Step 3 — Draft the SQL + assertions + captures

Start from the user's natural-language description. Apply these rules:

**SQL**:
- ONE `SELECT` statement. No semicolons. No `INSERT`, `UPDATE`, `DELETE`,
  `MERGE`, `TRUNCATE`, `DROP`, `ALTER`, `CREATE`, `EXEC`, `EXECUTE`,
  `INTO`, `SHUTDOWN`, `GRANT`, `REVOKE`. CTEs (`WITH …`) are allowed.
- Use `{{Token}}` placeholders (double curly braces) for any value coming from
  the parent step's render context. Common tokens for aseXML delivery parents:
  `{{NMI}}`, `{{MessageID}}`, `{{TransactionID}}`, `{{Filename}}`. For UI/API
  parents the available tokens are env params + any siblings' captures.
- Use `TOP 1` (or equivalent) when the assertion is on the first row — the
  agent only inspects the first row by design.

**Assertions**:
- Use `expectedRowCount` for "confirm at least N rows exist".
- Use `columnAssertions` for per-column rules. Pick the right operator:
  - `Equals` / `NotEquals` for exact string match (default `ignoreCase: true`).
  - `Contains` / `StartsWith` / `EndsWith` for substring matches.
  - `Regex` when the user said "matches pattern".
  - `GreaterThan` / `LessThan` / `Between` for thresholds (works for numbers
    AND dates — the evaluator falls back automatically).
  - `EqualsNumeric` with `toleranceDelta` for decimal columns.
  - `EqualsDate` with `toleranceSeconds` for timestamp columns (handles culture
    + UTC suffix variations).
  - `IsNull` / `IsNotNull` for null-aware checks.
- For JSON columns (Bravo's `Payload`, `RawPayload`, `Document`, anything
  `nvarchar(max)`-shaped containing JSON), set `jsonPath` (e.g. `"$.OrderId"`)
  rather than substring-matching the whole column.

**Captures**:
- If the user wants to use a column value in a later post-step, emit a
  `captures` entry: `{ "column": "JobId", "as": "JobId", "required": true }`.
- For JSON-extracted values: `{ "column": "Payload", "jsonPath": "$.OrderId", "as": "OrderId" }`.
- `as` is the bare token name — never wrap in braces. Siblings reference it
  as `{{JobId}}`.

### Step 4 — Validate via dry-run

Hit the WebApi:

```bash
curl -X POST http://localhost:5050/api/db-check/dry-run \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <your-key>" \
  -d '{
    "envKey": "<envKey or null>",
    "connectionKey": "<key>",
    "sql": "<your drafted SELECT>",
    "parameters": { "MessageID": "<a real recent MessageID>" }
  }'
```

The response carries `columns` (with SQL types) and the first 5 rows. Use this
to:
1. Confirm the SQL compiles + runs.
2. Confirm the columns the user mentioned actually exist in the result set
   (the `assertion.column` values must match exactly).
3. For JSONPath assertions, confirm the column's value really is JSON and the
   path resolves.

**On dry-run failure**: print the column list the dry-run *did* see and a
suggested SQL correction. Do NOT silently retry with a different shape — ask
the user to confirm or correct.

### Step 5 — PUT the post-step

Build a `VerificationStep` payload (the post-step shape) and PUT it via the
generic post-step CRUD endpoint:

```bash
curl -X PUT "http://localhost:5050/api/modules/<m>/testsets/<ts>/objectives/<o>/post-steps/<parentKind>/<parentStepIndex>" \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <your-key>" \
  -d '{ ... post-step JSON ... }'
```

The post-step JSON shape:

```json
{
  "description": "<short human label>",
  "target": "Db_SqlServer",
  "waitBeforeSeconds": 0,
  "role": "Verification",
  "dbCheck": {
    "name": "<human label>",
    "connectionKey": "BravoDb",
    "sql": "<the drafted SELECT>",
    "columnAssertions": [ … ],
    "captures": [ … ],
    "timeoutSeconds": 15
  }
}
```

Pick `waitBeforeSeconds` per the deferred-vs-inline rules in the chat-prompt
docs (mirrors `/add-asexml-verification`'s wait policy):
- 0 / a few seconds for "right after".
- > 30s when Bravo needs time to process the parent (delivery → consume →
  Jobs row appears). The exact deferral threshold is in
  `appsettings.json → TestEnvironment.AseXml.VerificationDeferThresholdSeconds`.

### Step 6 — Confirm the change

After PUT, re-read the objective and verify the new post-step appears under
the right parent step's `postSteps[]`. Tell the user the dry-run results +
final post-step shape, plus a suggested next step (run the test set in
Reuse mode against the env, or run only the new post-step via the
`/api/runs` `verifyStepFilter`).

## What can go wrong

- **Guardrail rejects the SQL.** Most common: a stray semicolon or
  `INSERT INTO` smuggled into a CTE. Re-draft.
- **Column not in result set.** The assertion's `column` must EXACTLY match
  one of the names from the dry-run's `columns[]`.
- **JSONPath returns nothing.** Either the column isn't JSON (use a string
  operator instead) or the path is wrong. Inspect a row in the dry-run output
  and adjust.
- **Token not in context.** A `{{MessageID}}` reference fails silently with
  the literal `{{MessageID}}` showing up in the SQL. Lenient mode logs WARN;
  on the next deferred sibling the same token will surface unsubstituted.
  Confirm the parent step actually emits the token (delivery → yes; an
  arbitrary API or UI step → only if it shipped env params or earlier
  captures bound it).
- **Captures didn't fire.** Captures only run on green assertions — a single
  failing assertion blocks the whole capture step. If the user wanted captures
  even on partial mismatch, redesign to make the failing assertion optional
  (or split into two post-steps).

## Don't

- Don't hand-edit the persisted JSON file — go through the API.
- Don't substitute `Captures.As` from any context — the agent intentionally
  doesn't substitute it.
- Don't switch to a different DB connection key without confirming with the
  user; rerouting connections silently is a foot-gun.
- Don't emit the legacy `expectedColumnValues` dict — always use
  `columnAssertions`. The deserialiser shim handles old persisted JSON, but
  new actions must use the new shape.
