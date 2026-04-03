Build and run the AITestCrew test suite.

Arguments: $ARGUMENTS

## Argument forms

- `"<objective>"` — Normal mode: generate test cases, save, run
- `--list` — List all saved test sets
- `--reuse <id>` — Re-run a saved test set without LLM generation
- `--rebaseline "<objective>"` — Regenerate test cases, overwrite saved, run
- *(no arguments)* — Build only, do not run

## What you must do

### Step 1 — Build

Always build before running. Run from the solution root:

```
dotnet build
```

If the build fails, stop here and report the errors. Do not attempt to run with a broken build.

### Step 2 — Run

Run from the `src/AiTestCrew.Runner` project directory using the provided arguments:

```
dotnet run --project src/AiTestCrew.Runner -- <arguments>
```

Examples:
```
dotnet run --project src/AiTestCrew.Runner -- "Test the /api/products endpoint"
dotnet run --project src/AiTestCrew.Runner -- --list
dotnet run --project src/AiTestCrew.Runner -- --reuse test-the-api-products-endpoint
dotnet run --project src/AiTestCrew.Runner -- --rebaseline "Test the /api/products endpoint"
```

### Step 3 — Report output

After running, summarise:
- Overall PASSED/FAILED status
- Number of tasks and steps that passed/failed
- Any errors encountered
- The saved test set ID (if Normal/Rebaseline mode) for future reuse

### Notes

- The runner requires a valid `appsettings.json` in `src/AiTestCrew.Runner/` with `LlmApiKey`, `ApiBaseUrl`, and `AuthToken` configured.
- Saved test sets are written to the `testsets/` directory next to the compiled binary (typically `src/AiTestCrew.Runner/bin/Debug/net8.0/testsets/`).
- Execution history is written to `executions/{testSetId}/{runId}.json` in the same directory.
- Full request/response logs are written to `logs/testrun_{timestamp}.log` in the same directory.
- The WebApi project shares the Runner's `appsettings.json` and data directories. Tests run via the Web UI produce results visible from both CLI and UI.
