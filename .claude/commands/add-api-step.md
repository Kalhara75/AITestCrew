Scaffold an API post-step attached to an existing parent test step.

An API post-step (`Api` post-step) fires an HTTP request as part of a
post-parent verification chain — useful for checking side-effects via API
(e.g. confirm a resource was created, verify a status endpoint changed),
chaining captures from one call into the next, or asserting on API response
content without involving the LLM validator.

Arguments: $ARGUMENTS
Expected format: `<moduleId> <testSetId> <objectiveId> <parentKind> <parentStepIndex> "<NL description>"`

Examples:
- `bravo-smoke jobs-checks submit-job Api 0 "verify GET /api/jobs/{{JobId}} returns 200 with status='Submitted'"`
- `aemo-b2b mfn-delivery-tests deliver-mfn AseXmlDeliver 0 "confirm POST /api/verifications returns 201 and capture the VerificationId"`
- `bravo-smoke orders-tests create-order WebUi 0 "assert GET /api/orders/{{OrderId}} has totalAmount>0"`

`parentKind` must be one of: `Api`, `WebUi`, `DesktopUi`, `AseXml`, `AseXmlDeliver`.
`parentStepIndex` is 0-based into the parent objective's matching step list.

## What you must do

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

3. The API stack and module key for the target test set are configured. These
   come from the test set's `apiStackKey` and `apiModule` fields. If the user
   specifies a different stack, note it.

4. The active env's `AllowApiDryRun` is true (default). The dry-run will return
   403 if disabled — surface that to the user and stop.

### Step 2 — Read the engine's contracts

Skim these before drafting:

- `src/AiTestCrew.Storage/ApiAgent/ApiTestDefinition.cs` — persistence shape
  (Method, Endpoint, Headers, QueryParams, Body, ApiAssertions, Captures,
  PostSteps, TimeoutSeconds). Also see `NormaliseLegacyFields` for the legacy
  → structured shim.
- `src/AiTestCrew.Storage/ApiAgent/ApiAssertion.cs` — assertion model
  (Source, HeaderName, JsonPath, Operator, Expected, Expected2, IgnoreCase,
  ToleranceSeconds, ToleranceDelta).
- `src/AiTestCrew.Storage/ApiAgent/ApiCapture.cs` — capture model
  (Source, HeaderName, JsonPath, As, Required).
- `src/AiTestCrew.Storage/ApiAgent/ApiAssertionSource.cs` — Status | Header | Body | BodyText.
- `src/AiTestCrew.Storage/DbAgent/AssertionOperator.cs` — the 14 shared operators
  (same operators used by DB and Event Assert agents).
- `src/AiTestCrew.Agents/ApiAgent/ApiAssertionEvaluator.cs` — evaluator semantics.

### Step 3 — Draft the API step + assertions + captures

Start from the user's natural-language description. Apply these rules:

1. **Method/Endpoint**: derive from the description. If the user gives a path
   with `{{Token}}` variables, preserve them — they are resolved at runtime
   from prior captures or environment parameters.

2. **Assertions over LLM**: always prefer structured `apiAssertions` over the
   legacy `expectedStatus`/`expectedBodyContains` fields. When `apiAssertions`
   is non-empty the LLM validator is skipped entirely.

3. **Source selection**:
   - Status code check → `Status` source.
   - Response header check → `Header` source + `headerName`.
   - JSON body field check → `Body` source + `jsonPath` (JSONPath expression).
   - Full body substring → `BodyText` source.

4. **JSONPath syntax** (same as DB Assert): `$.field`, `$.nested.field`,
   `$.array[0].field`. Use `EqualsNumeric` for numeric comparisons,
   `EqualsDate` for ISO-8601 dates.

5. **Captures**: always emit captures when the user's description mentions
   "capture", "bind", "save", or the downstream step needs a `{{Token}}`
   value. Name the token clearly (e.g. `JobId`, `VerificationId`).

6. **Token substitution**: all fields (endpoint, headers, body, assertion
   expected values) undergo `{{Token}}` substitution at runtime using the
   environment parameters + captured tokens from prior steps.

### Step 4 — Validate via dry-run

Hit `POST /api/api-step/dry-run` with the drafted step's fields. Use the
test set's stack/module/env to configure the request:

```json
{
  "envKey": "<envKey or null for default>",
  "stackKey": "<testSet.apiStackKey>",
  "moduleKey": "<testSet.apiModule>",
  "method": "GET",
  "endpoint": "/api/...",
  "headers": {},
  "queryParams": {},
  "body": null,
  "parameters": {"Token1": "sample-value"}
}
```

Check:
- Status code is as expected.
- JSONPath expressions resolve successfully on the real response body.
- Capture paths return non-null values.

If the dry-run fails with 403: `AllowApiDryRun` is disabled — report it to
the user and offer to persist the step without validation.

If the dry-run succeeds: confirm assertions against the live response and
report the match.

### Step 5 — Persist the post-step

The post-step is appended to the parent step's `postSteps` list. The parent
step is identified by `<parentKind>` + `<parentStepIndex>`.

Use `PUT /api/modules/<moduleId>/test-sets/<testSetId>/objectives/<objectiveId>`
with the full updated objective JSON (read → mutate → write pattern).

The post-step shape:

```json
{
  "target": "API_REST",
  "api": {
    "method": "GET",
    "endpoint": "/api/resource/{{Id}}",
    "headers": {},
    "queryParams": {},
    "body": null,
    "apiAssertions": [
      {
        "source": "Status",
        "operator": "Equals",
        "expected": "200"
      },
      {
        "source": "Body",
        "jsonPath": "$.status",
        "operator": "Equals",
        "expected": "Active",
        "ignoreCase": true
      }
    ],
    "captures": [
      {
        "source": "Body",
        "jsonPath": "$.id",
        "as": "ResourceId",
        "required": true
      }
    ],
    "timeoutSeconds": 30
  }
}
```

### Step 6 — Confirm with the user

Show a summary:
- Method + endpoint
- Assertions (source, operator, expected) — one per line
- Captures (token name, source, path) — one per line
- Dry-run status (passed/failed/skipped)

Ask the user to confirm before persisting if the dry-run was skipped.

## Assertion operator reference

| Operator       | Usage                                    |
|---|---|
| `Equals`       | Case-insensitive string equality (default) |
| `NotEquals`    | Negation of Equals                        |
| `Contains`     | Substring match                           |
| `NotContains`  | Substring absent                          |
| `StartsWith`   | Prefix match                              |
| `EndsWith`     | Suffix match                              |
| `Regex`        | .NET regex match                          |
| `GreaterThan`  | Numeric/date comparison                   |
| `LessThan`     | Numeric/date comparison                   |
| `Between`      | Numeric/date range (Expected..Expected2)  |
| `IsNull`       | Value is null / missing / empty           |
| `IsNotNull`    | Value is present and non-empty            |
| `EqualsNumeric`| Exact numeric equality with optional delta (ToleranceDelta) |
| `EqualsDate`   | ISO-8601 date equality with optional tolerance (ToleranceSeconds) |
