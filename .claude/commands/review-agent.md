Review an AITestCrew agent implementation for correctness, pattern consistency, and quality.

Arguments: $ARGUMENTS
Expected format: `<agent name or file path>`
Example: `ApiTestAgent`
Example: `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs`

## What you must do

### Step 1 — Read the target agent

Read the full agent file specified in the arguments. If no agent is specified, read `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs`.

### Step 2 — Read the reference patterns

Read these files to understand what correct looks like:
- `src/AiTestCrew.Agents/Base/BaseTestAgent.cs` — base class contract
- `src/AiTestCrew.Core/Interfaces/ITestAgent.cs` — interface requirements
- `src/AiTestCrew.Core/Models/TestStep.cs` — correct use of `TestStep.Pass/Fail/Err`
- `src/AiTestCrew.Core/Models/TestResult.cs` — correct `TestResult` construction

### Step 3 — Check against these criteria

**Interface compliance**
- [ ] Implements `ITestAgent` via `BaseTestAgent`
- [ ] `CanHandleAsync` returns `true` only for the intended `TestTargetType` values
- [ ] `ExecuteAsync` always returns a `TestResult` — never throws unhandled exceptions
- [ ] `ExecuteAsync` has a top-level `try/catch (Exception ex)` that returns an Error result

**Reuse mode support**
- [ ] Checks `task.Parameters["PreloadedTestCases"]` at the start of `ExecuteAsync`
- [ ] If pre-loaded cases are found, skips discovery and LLM generation entirely
- [ ] Logs a clear message indicating reuse mode is active

**Persistence compatibility**
- [ ] Returns `Metadata["generatedTestCases"]` containing the list of test cases
- [ ] The test case type is compatible with `TestSetRepository` serialisation

**LLM usage**
- [ ] All LLM calls go through `AskLlmAsync` or `AskLlmForJsonAsync` from `BaseTestAgent`
- [ ] Prompts include a JSON schema example so the LLM knows the expected output format
- [ ] JSON responses are cleaned before deserialisation (handled by `AskLlmForJsonAsync`)
- [ ] LLM failure (null return) is handled gracefully — not treated as a crash

**Authentication**
- [ ] Auth is injected from `TestEnvironmentConfig`, not from LLM-generated headers
- [ ] LLM-generated auth headers are removed before injecting real credentials

**Step tracking**
- [ ] Each major phase adds a `TestStep` using `TestStep.Pass/Fail/Err` factories
- [ ] Overall status is derived from steps: errors > failures > passed

**Cancellation**
- [ ] `CancellationToken` is passed through to all async calls
- [ ] `OperationCanceledException` is caught and returns an Error result

**Logging**
- [ ] Uses `Logger` (from `BaseTestAgent`) with structured logging patterns
- [ ] Does not log sensitive data (auth tokens, passwords)
- [ ] Debug-level logs for HTTP traffic, Info-level for key milestones

### Step 4 — Report findings

Produce a structured report with:
1. **Summary** — one paragraph overall assessment
2. **Issues** — list of specific problems found, each with file path and line number
3. **Recommendations** — prioritised list of improvements

Categorise issues as:
- **Critical** — will cause incorrect behaviour or crashes
- **Important** — violates the architecture or pattern conventions
- **Minor** — style, naming, or documentation gaps

### Step 5 — Fix critical issues

For any Critical issues found, implement the fix immediately. For Important and Minor issues, present them as recommendations for the user to decide whether to action.
