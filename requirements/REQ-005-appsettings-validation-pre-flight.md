---
id: REQ-005
title: appsettings.json validation pre-flight — fail clearly on malformed config instead of looping the container
status: Proposed
created: 2026-05-09
author: Kalhara Samarasinghe
area: webapi + ops
---

# REQ-005 — appsettings.json validation pre-flight

## Goal

When the WebApi starts and `appsettings.json` is malformed (missing brace, dangling comma, wrong enum spelling, etc.), surface a **clear human-readable error** identifying the file, line, and likely cause — instead of the current behaviour where the container's restart loop spits an unhandled `System.Text.Json.JsonReaderException` buried under .NET hosting infrastructure stack frames, with no indication which file or which edit broke it.

Concretely:

1. **Pre-flight validation** — before `WebApplication.CreateBuilder` runs, parse every `appsettings*.json` we'll bind, surface the file path + line + column on failure, and exit with a one-line summary rather than a 50-line stack trace.
2. **Optional: split per-feature config blocks** — move `ServiceBusConnections` (and any future per-feature dictionaries that carry secrets/keys) into dedicated `appsettings.<feature>.json` files. A malformed addition in one block then can't take down the whole config.

## Why now

- REQ-004's deployment phase exposed this footgun **twice in the same session**. Both incidents were the same root cause: hand-adding a deeply-nested block under `Environments.<env>.ServiceBusConnections` and miscounting the trailing braces. Each occurrence cost 15+ minutes diagnosing the buried .NET stack trace inside a Hyper-V container that wouldn't keep running long enough to inspect.
- The actual JSON error message — `Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed. LineNumber: 179 | BytePositionInLine: 0` — IS in the logs, but it's eight stack frames deep below the `Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder` line. An operator who isn't familiar with ASP.NET Core's hosting flow has to wade through it.
- Fix is small (~50 lines) and the diagnostic surface stays good forever after.

## Current behaviour

```text
Unhandled exception. System.IO.InvalidDataException:
  Failed to load configuration from file 'C:\app\appsettings.json'.
 ---> System.FormatException: Could not parse the JSON file.
 ---> System.Text.Json.JsonReaderException: Expected depth to be zero at
      the end of the JSON payload. There is an open JSON object or array
      that should be closed. LineNumber: 179 | BytePositionInLine: 0.
   at System.Text.Json.ThrowHelper.ThrowJsonReaderException(...)
   at System.Text.Json.Utf8JsonReader.ReadSingleSegment()
   at System.Text.Json.Utf8JsonReader.Read()
   at System.Text.Json.JsonDocument.Parse(...)
   at System.Text.Json.JsonDocument.Parse(ReadOnlyMemory`1 utf8Json, ...)
   at System.Text.Json.JsonDocument.Parse(ReadOnlyMemory`1 json, ...)
   at System.Text.Json.JsonDocument.Parse(String json, ...)
   at Microsoft.Extensions.Configuration.Json.JsonConfigurationFileParser.ParseStream(...)
   at Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider.Load(...)
   --- End of inner exception stack trace ---
   ... 6 more frames ...
   at Program.<Main>$(String[] args) in C:\src\src\AiTestCrew.WebApi\Program.cs:line 22
   at Program.<Main>(String[] args)
```

The container's restart policy then loops on this every few seconds, drowning subsequent diagnostics in repeats of the same trace.

## Desired behaviour

```text
[CONFIG-ERROR] Failed to parse C:\app\appsettings.json:
  Line 179, column 0: Expected closing brace '}' but reached end of file.
  Hint: 5 open '{' tokens were not closed. Did you recently add a block
  that's missing a trailing '}'?

  The container will now exit. After fixing the file, restart with:
    docker compose restart webapi

[Process exited with code 1]
```

Single line of action ("look at line 179"), one line of remediation. No stack trace.

## Scope — what's in

### 1. `ConfigPreFlight.Validate(args, env)` helper

New file: `src/AiTestCrew.WebApi/Configuration/ConfigPreFlight.cs`. Called from `Program.cs` BEFORE `WebApplication.CreateBuilder` runs.

For each file in the standard ASP.NET Core config chain:

- `appsettings.json`
- `appsettings.{environment}.json` (when present)
- Any `appsettings.<feature>.json` introduced by §2 below.

Reads the file as UTF-8 text → calls `JsonDocument.Parse` → on failure, formats a friendly message:

| Detected pattern | Message |
|---|---|
| `Expected depth to be zero at the end of the JSON payload` | `"Line {line}: missing {n} closing braces. Did you recently add a block without closing it?"` |
| `',' is invalid after a property name` | `"Line {line}: trailing comma. Remove the comma after the last property in the object."` |
| `'}' is invalid` (wrong nesting) | `"Line {line}: unexpected '}'. Probably an extra closing brace — check matching '{' depth."` |
| Any other `JsonReaderException` | The exception's `Message` line, prefixed with the file + line + col. |

On any failure: write to stderr, call `Environment.Exit(1)`. **Don't throw** — exit cleanly so Docker's restart-loop telemetry doesn't fill with stack noise.

On success: silent — pre-flight is a guard, not a chatty step.

### 2. (Optional) Split `ServiceBusConnections` into `appsettings.ServiceBus.json`

A separate config file at the repo root with shape:

```json
{
  "TestEnvironment": {
    "ServiceBusConnections": { ... },
    "Environments": {
      "<env>": {
        "ServiceBusConnections": { ... },
        "AllowEventAssertPeek": true
      }
    }
  }
}
```

Loaded explicitly in `Program.cs` via `builder.Configuration.AddJsonFile("appsettings.ServiceBus.json", optional: true, reloadOnChange: false)`. ASP.NET Core's hierarchical merging combines it with the main `appsettings.json`, so persistent code never knows the difference.

**Why optional**: §1 alone already solves the operator-experience problem. §2 is a defensive structural improvement — limits blast radius, keeps high-touch config (Service Bus keys, evolving event-assert flags) in a smaller file, makes Docker volume mounts cleaner. But it adds a config-loading line and a documentation update; if the team prefers a single file, §1 is sufficient.

### 3. Documentation

- `CLAUDE.md` "Where to extend" map — new row: "Adding a new env or feature flag to `appsettings.json` → after editing, save and let the next WebApi start surface the validation result. If the pre-flight rejects, the message names the file + line."
- `docs/deployment.md` — new "Config validation" section explaining the pre-flight + the optional per-feature split.

## Scope — explicitly out

- **Schema validation against `TestEnvironmentConfig`** — orthogonal, larger lift. JSON-shape errors are the 80% case; type-level mismatches (`AllowDbDryRun: "yes"` instead of `true`) currently surface at config-bind time with a different (but readable) message. Out for v1.
- **Hot-reload validation** — `reloadOnChange: true` in dev would be nice, but the cost is per-file watchers + the same validation on every reload. Out.
- **Auto-fix common typos** — non-goal. Surface, don't repair.

## Acceptance criteria

1. **Trailing-comma case.** Adding a `,` after the last property in a deeply-nested block, restart the WebApi → stderr shows one line: `"appsettings.json line N: trailing comma..."`. Container exits cleanly (exit 1, no stack trace).
2. **Missing-brace case.** Same as the bug REQ-004 surfaced twice — append a `ServiceBusConnections` block without closing it → stderr shows `"appsettings.json line N: missing 1 closing brace..."`.
3. **No regression.** A valid `appsettings.json` produces zero pre-flight output (silent on success).
4. **Order.** Pre-flight runs BEFORE `Microsoft.Hosting.Lifetime` writes its first log line. The user sees the config error, not a log noise pile.
5. **(If §2 implemented)** A malformed `appsettings.ServiceBus.json` produces a pre-flight failure that names that file specifically (not the main `appsettings.json`).

## Files most likely touched

**New**
- `src/AiTestCrew.WebApi/Configuration/ConfigPreFlight.cs`
- (If §2) `appsettings.ServiceBus.json` (root) + `src/AiTestCrew.WebApi/appsettings.ServiceBus.json` (per-env build copy)

**Modified**
- `src/AiTestCrew.WebApi/Program.cs` — call `ConfigPreFlight.Validate(args, env)` as the first line of `Main`, before `WebApplication.CreateBuilder`. (If §2) add the explicit `AddJsonFile` line.
- `Dockerfile` — (If §2) `COPY` the new feature config into the publish output.
- `docker-compose.yml` — (If §2) update the `docker-config` mount comment to reflect the per-feature split.
- `CLAUDE.md`, `docs/deployment.md` — documentation.

**Tests**
- `tests/AiTestCrew.WebApi.Tests/ConfigPreFlightTests.cs` — fixture-driven: pass an invalid JSON snippet through `ConfigPreFlight.Validate`, capture stderr, assert the friendly message shape.

## Open questions

1. Is `Environment.Exit(1)` from inside `Main` acceptable, or do we prefer a custom exception type that the host-builder catches and converts to an exit code? (Cleaner from a CLI-tool perspective, but `Environment.Exit` is simpler and the existing code already uses it for fatal startup paths.)
2. Should pre-flight ALSO check for `appsettings.example.json` not being shipped in the production image? (Out for v1 — different concern.)
3. Phase split: ship §1 only as the immediate fix, leave §2 for a follow-up after the team has felt §1's improvement?
