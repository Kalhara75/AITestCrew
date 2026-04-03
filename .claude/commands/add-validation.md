Add a new validation rule to an AITestCrew agent.

Arguments: $ARGUMENTS
Expected format: `<agent> "<rule description>"`
Example: `api "fail if response time exceeds 2 seconds"`
Example: `api "fail if the response body contains a stack trace"`

## What you must do

### Step 1 — Identify the validation type

There are two types of validation in AITestCrew:

**Rule-based** (fast, no LLM cost — preferred):
- Deterministic checks that can be evaluated without LLM reasoning
- Examples: status code, string contains/not-contains, response time threshold, header presence
- Lives in the first section of `ValidateResponseAsync` in `ApiTestAgent.cs`, before the LLM call
- Returns early with `Passed = false` if the check fails (skips the LLM call)

**LLM-based** (uses tokens, runs only if rule checks pass):
- Checks that require reasoning or contextual understanding
- Examples: "does this look like real data?", "are field types semantically correct?"
- Lives in the prompt sent to `AskLlmForJsonAsync<ApiValidation>` inside `ValidateResponseAsync`

Determine which type fits the requested rule before writing any code.

### Step 2 — Read the existing validation code

Read `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` in full, focusing on `ValidateResponseAsync` (around line 319). Understand:
- The exact structure of rule-based checks (they append to the `issues` list)
- The early return pattern: `if (issues.Count > 0) return new ApiValidation { Passed = false, ... }`
- What data is available: `tc` (the test case), `response` (HttpResponseMessage), `body` (string), `statusCode` (int)

### Step 3 — Implement the rule

**For rule-based validation**, add the new check inside `ValidateResponseAsync` before the `if (issues.Count > 0)` early return:

```csharp
// <Description of the check>
if (<condition that indicates failure>)
{
    issues.Add("<clear failure message>");
}
```

Available data:
- `tc` — `ApiTestCase` (method, endpoint, expectedStatus, expectedBodyContains, etc.)
- `response` — `HttpResponseMessage` (headers, status code, content)
- `body` — `string` (full response body)
- `statusCode` — `int`
- `stepSw` — `Stopwatch` (if you need elapsed time; add it to the method if not present)

**For LLM-based validation**, add a new check point to the prompt string in `ValidateResponseAsync`. The prompt asks the LLM to evaluate several numbered checks — add a new numbered item.

Do not change the `ApiValidation` model or the prompt's JSON output format — only add to the list of things the LLM is asked to check.

### Step 4 — Update ApiTestCase if needed

If the new validation requires configuration per test case (e.g. a max response time threshold per test), add a nullable property to `ApiTestCase` in `src/AiTestCrew.Agents/ApiAgent/ApiTestCase.cs`. Use a sensible default value so existing saved test sets are not broken.

### Step 5 — Build and verify

Run `dotnet build` from the solution root. The build must pass with 0 errors before finishing.

### Step 6 — Update documentation

Add the new validation rule to the "Validation" section in `docs/functional.md` under the appropriate list (rule-based or LLM-based).
