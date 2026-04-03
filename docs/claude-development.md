# AITestCrew — Claude-Assisted Development Guide

## Overview

AITestCrew is set up for AI-assisted development using [Claude Code](https://claude.ai/code). A set of project-specific slash commands (skills) and a persistent context file are included in the repository so Claude understands the architecture and can make correct changes without needing re-explanation each session.

---

## How It Works

### `CLAUDE.md` — Persistent Project Context

`CLAUDE.md` at the repository root is automatically loaded into every Claude Code session opened in this directory. It gives Claude:

- The solution structure and strict dependency rules
- Key file locations and their responsibilities
- Run mode commands
- Agent pattern conventions (reuse mode, persistence, auth injection)
- Naming and serialisation conventions
- A reference to the available slash commands

You do not need to explain the project to Claude each time — it reads `CLAUDE.md` at the start of every session.

**Keep `CLAUDE.md` up to date** when you add significant new components, change conventions, or add new run modes. It is the single source of truth Claude uses for architectural decisions.

---

## Slash Commands

Slash commands live in `.claude/commands/`. They are invoked with `/command-name <arguments>` in Claude Code and expand into detailed, project-aware instructions that guide Claude through a specific development task.

### `/add-agent`

Scaffolds a complete new test agent.

```
/add-agent <TargetType> "<description>"
```

**Examples:**
```
/add-agent UI_Web_MVC "Tests ASP.NET MVC pages via HTTP and validates rendered HTML"
/add-agent Database "Validates database state after API operations using direct SQL queries"
/add-agent BackgroundJob_Hangfire "Triggers and monitors Hangfire background jobs"
```

**What it does:**
1. Reads `BaseTestAgent`, `ApiTestAgent`, and `ITestAgent` to understand the patterns
2. Creates `{TargetType}TestCase.cs` and `{TargetType}TestAgent.cs` in a new `Agents/{TargetType}Agent/` folder
3. Registers the new agent in `Program.cs`
4. Handles reuse mode compatibility (`PreloadedTestCases` + `generatedTestCases` in `Metadata`)
5. Updates `SaveTestSetAsync` in the orchestrator if needed
6. Builds the solution and fixes any errors
7. Updates `docs/functional.md` and `docs/architecture.md`

---

### `/run-aitest`

Builds and runs the test suite.

```
/run-aitest <arguments>
```

**Examples:**
```
/run-aitest "Test the /api/products endpoint"
/run-aitest --list
/run-aitest --reuse test-the-api-products-endpoint
/run-aitest --rebaseline "Test the /api/products endpoint"
/run-aitest
```

Passing no arguments builds only (does not run). Always builds first and reports any compilation errors before running.

---

### `/add-validation`

Adds a new validation rule to an agent's response validation logic.

```
/add-validation <agent> "<rule description>"
```

**Examples:**
```
/add-validation api "fail if response body contains a stack trace or exception message"
/add-validation api "fail if Content-Type header is missing from the response"
/add-validation api "warn if response time exceeds 3 seconds"
```

**What it does:**
1. Determines whether the rule is best implemented as rule-based (fast, no LLM cost) or LLM-based (reasoning required)
2. Adds the check to `ValidateResponseAsync` in the correct location
3. Extends `ApiTestCase` with a new property if per-test configuration is needed
4. Builds the solution
5. Updates the Validation section in `docs/functional.md`

---

### `/implement-feature`

Implements any new feature or enhancement.

```
/implement-feature "<feature description>"
```

**Examples:**
```
/implement-feature "add parallel task execution using the existing MaxParallelAgents setting"
/implement-feature "add a --delete <id> CLI flag to remove a saved test set"
/implement-feature "retry failed test cases up to 2 times before marking them as failed"
/implement-feature "export test results as a JUnit XML file after each run"
```

**What it does:**
1. Reads `docs/architecture.md` and the relevant source files
2. Identifies the correct layer for the change (Runner, Orchestrator, Agents, Core)
3. Enforces the dependency rules — no upward references
4. Implements minimally — no speculative abstractions
5. Handles test set compatibility for any model changes
6. Builds, fixes errors, and updates docs

---

### `/review-agent`

Reviews an agent implementation against a 15-point quality checklist.

```
/review-agent <agent name or file path>
```

**Examples:**
```
/review-agent ApiTestAgent
/review-agent src/AiTestCrew.Agents/ApiAgent/ApiTestAgent.cs
/review-agent UiWebMvcTestAgent
```

**What it checks:**
- Interface compliance (`ITestAgent`, `CanHandleAsync`, `ExecuteAsync`)
- Reuse mode support (`PreloadedTestCases` bypass, `generatedTestCases` in Metadata)
- Persistence compatibility
- LLM usage patterns (via `BaseTestAgent` helpers only)
- Authentication injection
- Step tracking (`TestStep.Pass/Fail/Err`)
- Cancellation token propagation
- Logging conventions

Automatically fixes any Critical issues found. Reports Important and Minor issues as recommendations.

---

## Common Development Workflows

### Adding a new test agent type

```
/add-agent UI_Web_MVC "Tests ASP.NET MVC pages by sending HTTP requests and validating HTML responses"
```

Then verify it works:
```
/run-aitest "Test the /Home page renders correctly"
```

---

### Adding a feature to the existing API agent

```
/implement-feature "capture response time per test case and fail if it exceeds a configurable threshold"
```

---

### Running a regression check on saved tests

```
/run-aitest --list
```
Pick the test set ID, then:
```
/run-aitest --reuse test-the-api-products-endpoint
```

---

### Refreshing test cases after an API change

```
/run-aitest --rebaseline "Test the /api/products endpoint"
```

---

### Reviewing code quality after manual edits

```
/review-agent ApiTestAgent
```

---

## File Reference

| File | Purpose |
|---|---|
| `CLAUDE.md` | Auto-loaded project context for every Claude Code session |
| `.claude/commands/add-agent.md` | Slash command: scaffold a new agent |
| `.claude/commands/run-aitest.md` | Slash command: build and run the test suite |
| `.claude/commands/add-validation.md` | Slash command: add a validation rule |
| `.claude/commands/implement-feature.md` | Slash command: implement any feature |
| `.claude/commands/review-agent.md` | Slash command: quality review of an agent |

---

## Keeping Claude Context Current

Claude reads `CLAUDE.md` at the start of each session. Update it when you:

- Add a new agent type (add to the key files table and agent pattern section)
- Add a new run mode (add to the run modes table)
- Change a core convention (update the conventions section)
- Add a new slash command (add to the commands table)

The slash command files in `.claude/commands/` contain detailed instructions that reference specific file paths and line numbers. When refactoring moves code significantly, update the relevant command file so future invocations reference the correct locations.

---

## Tips

- **Start a session** by describing what you want to change — Claude reads `CLAUDE.md` automatically and will reference the right files without you listing them.
- **Use `/implement-feature` for anything not covered by a specific command** — it enforces the architectural rules and handles docs updates.
- **After a large change**, run `/review-agent <AgentName>` to catch any regressions against the pattern conventions.
- **Slash commands are plain Markdown** — open the files in `.claude/commands/` to read or edit the instructions Claude follows.
