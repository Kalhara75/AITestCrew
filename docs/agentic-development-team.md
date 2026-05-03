# AITestCrew — Agentic Development Team

## What this is

A five-agent pipeline that takes a single requirement file (`requirements/REQ-NNN-*.md`) and drives it to a feature branch ready for human review, with minimal mid-flight involvement from the requester.

The team is implemented as Claude Code subagents under `.claude/agents/`. Each agent is a markdown file with YAML frontmatter that defines its role, tools, and system prompt. Agents run in their own context and communicate via the parent agent's `Task` tool.

This document explains how the team is structured, why it's structured that way, and how to evolve it. It is the reference for improving the team's capabilities over time.

---

## Why the team exists

Before the team, the workflow for implementing a requirement was:

1. User reads requirement.
2. User asks Claude (in main thread) to implement it.
3. User reviews each step, redirects, fixes, re-runs.
4. User commits, pushes, opens PR.

This put the user in the loop for every decision and consumed main-thread context with implementation noise. The team's purpose is to:

- **Compress the loop**: requirement in → branch ready for review out, with one or two checkpoints.
- **Protect the main context**: implementation churn happens in subagent contexts, not yours.
- **Enforce convention**: codify "the right way" via agent prompts so quality doesn't depend on the user remembering to ask.
- **Keep humans in charge of shared state**: the team never pushes, never opens PRs, never modifies `main`.

---

## Team composition

| Agent | File | Tools | Role |
|---|---|---|---|
| **Feature Coordinator** | `.claude/agents/feature-coordinator.md` | Read, Bash, Glob, Grep, Task, TodoWrite | Pipeline orchestrator. Creates feature branch. Spawns the other four. Pauses for user approval at risky steps. |
| **Implementation Planner** | `.claude/agents/implementation-planner.md` | Read, Glob, Grep, Bash, WebFetch, WebSearch | Read-only. Surveys the codebase, resolves open questions, produces a phase-by-phase plan with cited skills. |
| **Implementer** | `.claude/agents/implementer.md` | Read, Write, Edit, Glob, Grep, Bash, TodoWrite | Executes the plan. Writes code, runs build/tests, commits incrementally. **Does not touch docs** — that's the doc-writer's job. |
| **Doc Writer** | `.claude/agents/doc-writer.md` | Read, Write, Edit, Glob, Grep, Bash | Reads the diff, decides which of `docs/functional.md`, `docs/architecture.md`, `docs/deployment.md`, `docs/data-packs.md`, `docs/recording-troubleshooting.md`, and `CLAUDE.md` need updates, writes them in a separate commit. May skip with "No doc updates required". |
| **Code Reviewer** | `.claude/agents/code-reviewer.md` | Read, Glob, Grep, Bash | Read-only QA. Verifies acceptance criteria, skill conformance, doc conformance, build/test gates. Returns structured report. |

All five use `model: opus` by default — depth over speed for these roles. Drop to `sonnet` if cost/latency matters more than quality.

---

## Pipeline

```
User
  │
  │  "Use feature-coordinator to implement requirements/REQ-001.md"
  ▼
┌──────────────────────────────────────────────────────────────────────┐
│ feature-coordinator                                                  │
│                                                                      │
│  0. Pre-flight: read requirement, verify clean tree on main          │
│  1. git checkout -b feat/req-NNN-<slug>                              │
│  2. Spawn implementation-planner ──────────┐                         │
│                                            ▼                         │
│                                 [plan returned to coordinator]       │
│  3. Summarise plan to user, pause for go-ahead (unless autonomous)   │
│  4. Spawn implementer with plan ───────────┐                         │
│                                            ▼                         │
│                                 [code committed, summary returned]   │
│  5. Spawn doc-writer ──────────────────────┐                         │
│                                            ▼                         │
│                                 [docs committed in separate commit,  │
│                                  or "no doc updates required"]       │
│  6. Spawn code-reviewer ───────────────────┐                         │
│                                            ▼                         │
│                                 [review report returned]             │
│  7. If blockers: re-spawn implementer / doc-writer (max 2 iter)      │
│  8. STOP. Report to user. Wait for explicit "push" approval.         │
└──────────────────────────────────────────────────────────────────────┘
  │
  │  User reviews → "push"
  ▼
git push -u origin <branch>; gh pr create
```

### Phase boundaries

Each phase is **independently committable**. The implementer commits per phase with a descriptive message. This means if the pipeline is interrupted (or a later phase fails), the branch is still in a valid state.

### Iteration cap

The reviewer can find blocking issues. The coordinator will re-spawn the implementer up to **2 times** to fix them. After that, the coordinator stops and surfaces the blockers to the user — better to ask for direction than to thrash.

---

## How existing project skills plug in

The project already has 14 skills under `.claude/commands/`. Each agent in the team is told which skills to consult and when. This is the integration matrix:

### Action skills (codified scaffolding recipes)

When a requirement matches one of these, the planner cites it; the implementer follows it verbatim; the reviewer verifies it was followed.

| Skill | Triggers when... |
|---|---|
| `add-agent.md` | New test agent for a new `TestTargetType` |
| `add-validation.md` | New response validation rule on an existing agent |
| `add-asexml-template.md` | New aseXML transaction template + manifest |
| `add-asexml-verification.md` | New post-delivery UI verification on an existing delivery objective |
| `add-data-pack-script.md` | New startup-time SQL data-pack script |
| `add-delivery-protocol.md` | New aseXML delivery protocol (`IXmlDropTarget`) |
| `tune-deferred-verification.md` | Tuning or debugging deferred-verification timing/retry/stuck-Awaiting |

**Compression path**: when a requirement is *purely* a scaffolding task that maps to one of these, the coordinator may skip the planner and hand the skill recipe directly to the implementer. This is explicit — the coordinator tells the user when it compresses.

### Reference skills (domain knowledge)

The planner reads these before writing the plan; the implementer reads them before touching the domain; the reviewer reads them to validate domain-correctness.

| Skill | Domain |
|---|---|
| `asexml-reference.md` | aseXML pipeline (Generate / Deliver / Verify) |
| `blazor-cloud-reference.md` | Brave Cloud Blazor/MudBlazor UI |
| `bravo-web-reference.md` | Bravo Web Kendo/ASP.NET MVC UI |
| `desktop-winui-reference.md` | WinForms desktop recorder/replay/FlaUI |

These contain load-bearing selectors, timing rules, and pitfalls that aren't visible in the code itself. Drift here doesn't fail the build — only the reference skill catches it.

### Utility skills

| Skill | Used by |
|---|---|
| `run-aitest.md` | Implementer — canonical build/run commands |
| `review-agent.md` | Reviewer — 30+ point checklist when the diff includes a test agent |
| `implement-feature.md` | Planner reads its "Where it lives" layer-mapping table; implementer follows its Step 8 doc-update rule. The rest of `implement-feature.md` is superseded by the pipeline. |

### How citations flow

The planner's output includes a mandatory section:

```
## Skills the implementer must follow
- `.claude/commands/<skill>.md` — <why and which sections apply>
```

The implementer reads each cited skill verbatim. The reviewer's report includes a "Skill conformance" table that explicitly verifies each cited skill was followed.

If the planner doesn't cite a skill, the implementer doesn't read it — so getting the planner's citations right is the keystone of the system.

---

## Safety model

The team operates under "trust the user with shared state":

| Action | Who can do it |
|---|---|
| Create local feature branch | Coordinator (no approval needed) |
| Read files, run tests, run builds | Any agent |
| Edit / write code, config, scripts | Implementer only |
| Edit / write docs (`docs/*`, `CLAUDE.md`) | Doc-writer only |
| Commit | Implementer (code commits) and doc-writer (one docs commit) |
| Push to remote | **User only** (coordinator asks for approval) |
| Open a PR | **User only** (coordinator asks for approval) |
| Modify `main` | Never |
| Force-push, reset --hard, delete branches, skip hooks | Never |

The "go autonomous" mode (where the user pre-authorizes the pipeline) explicitly does **not** authorize push or PR creation. Autonomy applies inside the pipeline; publishing always requires a human "push" reply.

### Trust-but-verify

The coordinator runs `git diff --stat main...HEAD` itself after the implementer reports. The reviewer runs `git diff main...HEAD` and reads the actual diff. Where an agent's intent-summary disagrees with the diff, the diff wins.

---

## How to invoke

Hand the coordinator a requirement file. Example prompt:

> Use the **feature-coordinator** agent to implement `requirements/REQ-001-standardise-test-execution-ui.md`.

Optional modifiers:
- "go autonomous" / "don't check in with me" → coordinator runs the full pipeline without pausing for plan approval, but still stops at push.
- "check with me at each step" → default — coordinator pauses after the plan and again at push.

The coordinator returns a final report with branch name, commits, files changed, plan summary, reviewer's report, and acceptance-criteria coverage. You review locally (`git checkout`, browse the branch, run the dev server), then either reply `push` to publish or send corrections.

---

## Files & locations

```
.claude/agents/
├── feature-coordinator.md       Pipeline orchestrator
├── implementation-planner.md    Read-only planner
├── implementer.md               Code writer
└── code-reviewer.md             Read-only reviewer

.claude/commands/                 Existing 14 skills (referenced by agents)

requirements/                     User-authored REQ-*.md files
└── REQ-001-*.md                  First requirement using this team

docs/
├── claude-development.md         Original Claude integration guide (skills, CLAUDE.md)
└── agentic-development-team.md   This file
```

---

## Design decisions

### Why four agents, not one?

A single all-purpose agent has all tools (read+write+commit+review) and a single context window. That works for small tasks but degrades on larger ones because:
- The context window fills with implementation noise, then the same agent tries to review the work — not enough headroom for an honest second pass.
- One agent can't usefully review its own code; the same biases that produced bugs miss them on review.

Four agents with separated contexts give the reviewer a clean read of the diff, and the planner doesn't see the implementer's improvisations.

### Why is the planner read-only?

The planner's job is to produce a plan. If it had write access, it would start implementing as it planned, and the plan would degrade to a post-hoc justification of whatever was easiest to write. Read-only forces the plan to be specified before code is written.

### Why doesn't the coordinator implement anything itself?

The coordinator's only job is orchestration. If it implemented, it would compete for context with the work it's supposed to coordinate, and the trust-but-verify check (running `git diff` after the implementer) would be self-checking.

### Why is push approval mandatory?

Push and PR creation are the only steps with externally-visible blast radius. Local commits can be amended, branches deleted, files reverted. A pushed PR is visible to the team, may trigger CI, may notify reviewers. Autonomy is fine inside the local sandbox; publication is always human-gated.

### Why cite skills in the plan rather than letting the implementer find them?

If the implementer searches for skills itself, it has to discover what's relevant — and might miss a skill that applies but isn't named in the requirement. By making the planner responsible for citing skills, we put skill-discovery in the agent that has the best codebase context (the planner just surveyed the repo) and the most reasoning headroom.

### Why is the reviewer read-only?

If the reviewer fixed issues itself, the implementer's work and the reviewer's work would blend in the diff and the user couldn't tell what was implemented vs. what was patched. Read-only review keeps the trail clean: implementer's commits + reviewer's report are separate artifacts.

---

## How to evolve the team

### Adding a new agent

If you find a recurring need that doesn't fit the existing four roles (e.g., a security-review specialist, a UI/UX critic, a performance auditor):

1. Create `.claude/agents/<name>.md` with frontmatter (`name`, `description`, `tools`, `model`).
2. Decide the trigger: do you spawn it from the coordinator's pipeline, or invoke it ad-hoc?
3. If pipeline-integrated: edit `feature-coordinator.md` to add a step that spawns the new agent at the right point.
4. If ad-hoc: just leave it as a callable agent. The user (or coordinator on demand) can invoke it.

Example candidates:
- **security-reviewer** — focused review pass for security-sensitive diffs (auth, secrets, SQL injection, etc.). Could be a sibling to `code-reviewer`, spawned when the diff touches `Auth/`, `Endpoints/`, or `*.sql`.
- **doc-writer** — generates user-facing changelog entries / `docs/functional.md` updates from a diff. Could run after the implementer.
- **migration-planner** — when a requirement implies schema or persistence-format changes, this agent plans the migration story before the planner produces the file-level plan.

### Tuning agent behaviour

Each agent's behaviour is its system prompt (the body of its `.md` file). To change behaviour:

- **Make output more terse**: edit the "Tone" or "Report" section.
- **Add a new check**: edit the "What to do" section. For the reviewer, add it to step 4 (quality gates) or 4b/4c/4d (skill/domain/agent checks).
- **Change tool access**: edit the `tools:` line in frontmatter. Adding `WebFetch` lets an agent pull external docs; removing `Bash` makes it pure-static.
- **Change model**: edit `model:` to `sonnet` or `haiku` to trade depth for speed.

After editing, the change is live — no restart needed. Test with a small requirement before relying on it for production work.

### Capturing learnings

When the team produces a bad outcome — wrong implementation, missed skill, false-positive review — the fix usually belongs in **one of the agent prompts**, not in the requirement.

Symptom-to-fix examples:

| Symptom | Likely fix location |
|---|---|
| Planner cited the wrong skill (or none) | `implementation-planner.md` — clarify the trigger criteria for that skill |
| Implementer improvised when a skill was cited | `implementer.md` — strengthen "follow verbatim" language for that skill |
| Reviewer missed a domain rule | `code-reviewer.md` 4c — add the specific rule from the reference skill |
| Doc-writer skipped a doc that should have been updated | `doc-writer.md` step 2 decision table — add the missed signal |
| Doc-writer manufactured content for a non-change | `doc-writer.md` step 2 — strengthen the "if none apply, skip" guidance |
| New `docs/*.md` file added but doc-writer doesn't know about it | `doc-writer.md` step 2 decision table + `code-reviewer.md` 4d — add the new doc to both tables |
| Doc style drifts (new entries don't match existing prose) | `doc-writer.md` step 3 — strengthen "read existing structure" requirement |
| Coordinator pushed without approval | `feature-coordinator.md` Hard rules — should never happen; treat as a bug |
| Coordinator skipped plan-approval gate (spawned implementer without user `approve`) | `feature-coordinator.md` step 2b + Hard rules — strengthen the "STOP" language; verify the autonomous-mode trigger phrases in `# Modes` haven't been broadened |
| Subagent handovers not visible in console | `feature-coordinator.md` "Console visibility" section — agent skipping the `→ HANDING OFF TO` / `← RETURNED FROM` banners; reinforce that they're non-negotiable |
| User had to redirect mid-pipeline more than once | The plan was too vague — strengthen `implementation-planner.md` quality bar |

When in doubt, edit the agent prompt and re-run the same requirement to confirm the fix works.

### Versioning

The agent prompts are checked into git. Treat changes the way you'd treat any code change:
- Commit with a message describing what behaviour changed and why.
- If a change is risky (e.g., relaxing a safety rule), call it out in the PR description.
- Consider tagging the previous version if you're about to make a major restructure.

---

## Open extension ideas

Not implemented, but reasonable next steps:

1. **Per-target-type implementer specialists** — separate `ui-implementer`, `agent-implementer`, `data-pack-implementer` agents, each with deeper context for its domain. The coordinator routes based on requirement keywords.
2. **Auto-PR mode for low-risk requirement classes** — if a requirement is tagged `risk: low` (e.g., a docs-only change or a single-file scaffold), the coordinator could push and open the PR automatically and notify the user instead of pausing.
3. **Requirement-quality gate** — a pre-coordinator agent that reviews the requirement for completeness before the team starts. Today the team trusts the requirement; if it's vague, the planner will produce a vague plan.
4. **Continuous-improvement loop** — after each user review, capture rejected-change reasons and feed them back into the appropriate agent's prompt.
5. **Post-merge follow-up agent** — after the user merges, an agent watches CI / monitors and reports any post-merge regressions back to the user.
6. **Skill-coverage analyzer** — a meta-agent that scans `requirements/` and reports which requirements have no matching skill and might be candidates for new scaffolding skills.

---

## Related docs

- `docs/claude-development.md` — original Claude integration (skills, `CLAUDE.md`).
- `docs/architecture.md` — code-level architecture the agents reason about.
- `CLAUDE.md` (repo root) — auto-loaded into every Claude Code session; agents inherit this context.
- `.claude/commands/*.md` — the 14 skills the agents cite and follow.
- `requirements/REQ-001-*.md` — first requirement built using this team; useful as a worked example.
