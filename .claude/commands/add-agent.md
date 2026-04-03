Scaffold a new test agent for AITestCrew.

Arguments: $ARGUMENTS
Expected format: `<TargetType> "<description>"`
Example: `UI_Web_MVC "Tests ASP.NET MVC pages via HTTP and validates rendered HTML"`

## What you must do

You are adding a new agent to the AITestCrew framework. Follow the exact patterns established by the existing `ApiTestAgent`. Do not deviate from the architecture.

### Step 1 — Understand the target type

The `TestTargetType` enum is in `src/AiTestCrew.Core/Models/Enums.cs`. The agent must return `true` from `CanHandleAsync` for one or more of these values. If the requested target type does not exist in the enum yet, add it there first.

### Step 2 — Read existing patterns

Before writing any code, read these files in full:
- `src/AiTestCrew.Agents/Base/BaseTestAgent.cs` — the base class all agents extend
- `src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs` — the reference implementation
- `src/AiTestCrew.Agents/ApiAgent/ApiTestCase.cs` — how agent-specific models are defined
- `src/AiTestCrew.Core/Interfaces/ITestAgent.cs` — the interface contract
- `src/AiTestCrew.Runner/Program.cs` — how agents are registered in DI

### Step 3 — Create agent models

Create `src/AiTestCrew.Agents/{TargetType}Agent/{TargetType}TestCase.cs` with:
- A `{TargetType}TestCase` class representing one LLM-generated test case for this agent type
- A `{TargetType}Validation` class for the LLM validation verdict (with `Passed`, `Reason`, `Issues`)

Model these closely on `ApiTestCase` and `ApiValidation` — same property naming conventions, `set` accessors, default values.

### Step 4 — Create the agent class

Create `src/AiTestCrew.Agents/{TargetType}Agent/{TargetType}TestAgent.cs`:

```csharp
public class {TargetType}TestAgent : BaseTestAgent
{
    public override string Name => "{TargetType} Agent";
    public override string Role => "<role description appropriate for this test type>";

    public {TargetType}TestAgent(Kernel kernel, ILogger<{TargetType}TestAgent> logger, ...)
        : base(kernel, logger) { }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target is TestTargetType.{TargetType});

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        // Phase 1: optionally discover/load context (equivalent to DiscoverEndpointAsync)
        // Phase 2: LLM generates test cases (equivalent to GenerateTestCasesAsync)
        // Phase 3: execute each test case (equivalent to ExecuteTestCaseAsync)
        // Phase 4: determine overall status (hasFails/hasErrors pattern)
        // Phase 5: SummariseResultsAsync
        // Return TestResult with Metadata["generatedTestCases"] = testCases
    }
}
```

The `Metadata["generatedTestCases"]` key is required so the orchestrator can persist the test cases for reuse mode. The value must be `List<{TargetType}TestCase>`.

### Step 5 — Register the agent in DI

Register the agent in **both** `src/AiTestCrew.Runner/Program.cs` and `src/AiTestCrew.WebApi/Program.cs`. Add registrations immediately after the `ApiTestAgent` registrations, following the same two-line pattern:

```csharp
builder.Services.AddSingleton<{TargetType}TestAgent>(sp => new {TargetType}TestAgent(
    sp.GetRequiredService<Kernel>(),
    sp.GetRequiredService<ILogger<{TargetType}TestAgent>>(),
    /* additional constructor args */
));
builder.Services.AddSingleton<ITestAgent>(sp => sp.GetRequiredService<{TargetType}TestAgent>());
```

**Important:** Both Runner and WebApi must have identical agent registrations so the same test types work from CLI and web UI.

### Step 6 — Persistence support

The orchestrator's `SaveTestSetAsync` method uses `r.Metadata["generatedTestCases"]` with a cast to `List<ApiTestCase>`. This cast will fail for other agent types. Update `SaveTestSetAsync` in `src/AiTestCrew.Orchestrator/TestOrchestrator.cs` to handle the new type — either by checking the agent name and casting appropriately, or by introducing a common interface for persisted test cases.

Also update `ApiTestAgent.cs`'s reuse path: `task.Parameters["PreloadedTestCases"]` is checked against `List<ApiTestCase>`. A similar check must be added for the new agent.

### Step 7 — Build and verify

Run `dotnet build` from the solution root. Fix any compilation errors before proceeding.

### Step 8 — Update documentation

- Add the new agent to the "Planned Future Capabilities" or "Active Agents" section of `docs/functional.md`
- Add it to the project responsibilities table in `docs/architecture.md`
- Update the "How Agents Are Registered and Routed" section if the pattern changed

### Architecture constraints to respect

- Do NOT modify `ITestAgent` — the interface is stable
- Do NOT change the `TestTask`, `TestResult`, or `TestStep` models
- Keep the new agent in `AiTestCrew.Agents`, not in `Orchestrator` or `Runner`
- All LLM calls must go through `AskLlmAsync` or `AskLlmForJsonAsync` from `BaseTestAgent`
- Auth injection pattern (if applicable): inject from `TestEnvironmentConfig`, never from LLM-generated headers
