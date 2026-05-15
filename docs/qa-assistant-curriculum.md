# AITestCrew — Assistant Curriculum

A staged, prompt-driven path from "first day" to "comfortable with everything." Work through one stage at a time, ideally as you encounter the relevant feature in real work — not all in one sitting.

Every example below is a prompt you can paste verbatim into the **Assistant** page (`/assistant` on the dashboard). The assistant never makes changes silently — it always shows a **confirmation card** with a Run / Create / Record button, so you can review what it's about to do before clicking.

> **Tip:** before each prompt, glance at what kind of card you expect to appear. If you get a different card or no card at all, the assistant probably misread your intent — rephrase, or include more context (module / test set names).

---

## Stage 1 — Orient (read-only, no risk)

**Goal:** learn what's in the project. The assistant only reads — no buttons to click yet.

| Prompt | What you'll see |
|---|---|
| *Show me the modules in this project* | A `showData` card listing modules with click-through `navigate` links to each. |
| *What test sets are in the security module?* | List of test sets + their target stacks / endpoints. |
| *What does the user-management test set contain?* | The test set's objectives and step counts. |
| *What environments are configured?* | All customer environments (e.g. `sumo-retail`, `ams-metering`). |
| *What endpoints exist on sumo-retail?* | aseXML B2B endpoints per env — codes like `NEMMCO`, `RETAILER1`, etc. |
| *Which agents are online right now?* | Connected agents + their capability tags (`UI_Web_Blazor`, etc.). |

By the end of Stage 1 you should know your team's module names, your default environment, and which capabilities your agent reports.

---

## Stage 2 — Your first UI test (one recording, one run)

**Goal:** create a test set, record one UI test case into it, and run it. This is the most common loop.

> **Why no AI-generated UI tests?** UI tests must be **recorded** — only API tests can be AI-generated. If you ask the assistant to *"generate a UI test"*, it will redirect you to the record flow.

| Prompt | Card | What clicking does |
|---|---|---|
| *Create a new test set called "smoke" in the security module* | `confirmCreate` | Creates the test set. |
| *Record a new test case in security/smoke called "Open users page" targeting Blazor UI* | `confirmRecord` | Dispatches a recording job to your agent. A browser opens on **your laptop**. Click around, then close the browser. The agent uploads the recording. |
| *Run security/smoke* | `confirmRun` (mode: Reuse) | Re-runs the recorded test. UI tests get claimed by your agent — watch the run banner switch from *Queued* to *Running on YOUR-LAPTOP*. |

**Things you'll naturally hit at this stage:**
- The agent's terminal window will scroll as it picks up the job. Don't close it.
- If the run fails on a selector, open the execution detail page — it'll show the screenshot the agent captured at the failure point. Then jump to [`recording-troubleshooting.md`](recording-troubleshooting.md).

---

## Stage 3 — API tests + structured assertions

**Goal:** generate an API test from English, then add a post-step that asserts something specific about the response.

| Prompt | Card | Notes |
|---|---|---|
| *Generate an API test in security/smoke that lists users and asserts the response has a "users" array* | `confirmRun` (mode: **Normal**) | Normal mode = generate fresh API steps. Only valid for API. |
| *Add an API post-step after step 1 that captures the first user's id into `{{firstUserId}}`* | `confirmCreatePostStep` | The captured token becomes available to later steps via `{{firstUserId}}`. |
| *Add an API post-step after step 2 that asserts response.status equals "active"* | `confirmCreatePostStep` | The assistant builds the assertion JSON. Try-call validates it via dry-run before saving. |

**Concept introduced:** **tokens** — `{{firstUserId}}` is substituted at run time from previous captures. The same `{{Token}}` substitution drives aseXML and DB / Service Bus assertions in later stages.

**Self-check:** open the test set in the dashboard and inspect the API test case. You should see the captures list and assertions list rendered visually under the parent step.

---

## Stage 4 — Cross-system orchestration

**Goal:** chain a UI action to a database row, then to a published message. This is where AITestCrew earns its keep — one objective verifies the whole stack.

### 4a — Database assertions

| Prompt | Card |
|---|---|
| *Add a DB assertion after step 2 of security/smoke that the audit log table has a row with userId = `{{firstUserId}}`* | `confirmCreatePostStep` (kind: dbCheck) |
| *Try the query first to make sure it parses* | The editor's **Try query** button hits `POST /api/db-check/dry-run` and shows the row count without saving. |

The assistant will pick a `connectionKey` from your env's `DbConnections` (`SdrReportingDb`, etc.). If you have multiple, name it: *"...using SdrReportingDb"*.

### 4b — Service Bus event assertions

| Prompt | Card |
|---|---|
| *Peek the meter-events topic on sumo-retail* | `peekServiceBusMessages` |
| (click **Use this field as criterion** on a message) | Chains into `confirmCreatePostStep` with the criterion pre-filled. |
| *Add an event assertion after step 3 that a UserCreated event fires with userId = `{{firstUserId}}`* | `confirmCreatePostStep` (kind: eventAssert) |

**Concept introduced:** **inline vs deferred post-steps.** Short waits (`waitBeforeSeconds < 30`) execute inline and block the parent step's slot. Longer waits get **deferred** to the queue, retry on a schedule, and free the agent to claim other work. If you forget to mention waiting, the assistant defaults to inline.

---

## Stage 5 — aseXML (B2B AEMO transactions)

**Goal:** drive a full real-world B2B flow — generate an aseXML transaction, deliver it to a Bravo endpoint, then verify the downstream UI processed it.

This is the most complex workflow in AITestCrew. **Don't start Stage 5 until Stages 2–4 feel routine.**

| Prompt | Card |
|---|---|
| *Create an aseXML CustomerDetailsNotification delivery test in mfn/deliveries for endpoint NEMMCO on sumo-retail* | `confirmCreate` + `confirmCreatePostStep` for the delivery step. |
| *Add a post-delivery verification that the Brave Cloud customer page shows the new name, waiting 5 minutes after delivery* | `confirmCreatePostStep` (kind: aseXmlVerify). Wait of 5 min triggers the **deferred** path. |
| *Run mfn/deliveries* | `confirmRun`. The delivery agent renders → uploads → enqueues the verification → frees its slot. |

**What to watch in the run banner:**
- *"Delivered — verification queued, next attempt in ~4 min"* — the deferred path is working.
- *"Awaiting Verification"* lasting longer than expected — the assistant can diagnose it. Ask: *"Why is mfn/deliveries stuck on Awaiting Verification?"* The assistant will pull the relevant run queue + agent capability state and explain.

**Tuning knob discovery:** the prompts *"Tune deferred verification timing"* or *"How do I change the retry interval?"* lead you into `/tune-deferred-verification` territory. See [`docs/architecture.md → Deferred Post-Delivery Verification`](architecture.md) for the deep model.

---

## Bonus — editing what you already created

Anything you created with `confirmCreate*` can be modified with `confirmEdit*`. The assistant carries every existing field through and only changes what you asked for.

| Prompt | Card |
|---|---|
| *Update the DB assertion in step 2 of security/smoke to also check the timestamp is within the last minute* | `confirmEditPostStep` |
| *Change the wait on the verification in mfn/deliveries to 10 minutes* | `confirmEditPostStep` |
| *Rename security/smoke to security/auth-smoke* | (manual on the dashboard — assistant doesn't rename test sets) |

---

## Progression checklist

Tick these off as you go. Most QAs reach Stage 4 in their first week and Stage 5 by week 2–3.

- [ ] **Stage 1** — Listed modules, test sets, environments, endpoints, agents through the assistant
- [ ] **Stage 2** — Created a test set, recorded a UI test case, ran it (Reuse)
- [ ] **Stage 3** — Generated an API test (Normal), added a capture post-step, added an assertion post-step
- [ ] **Stage 4a** — Added a DB assertion, ran Try query, ran the test
- [ ] **Stage 4b** — Peeked Service Bus, promoted a field into an event-assert post-step
- [ ] **Stage 5** — Authored an aseXML delivery objective with a deferred verification, ran it, diagnosed an Awaiting state through the assistant

---

## When the assistant gets it wrong

The assistant is steered by the prompt template in `src/AiTestCrew.WebApi/Services/ChatIntentService.cs:479`. It can:

- Misread *"add a test for X"* as `Normal` (API generation) when you meant `confirmRecord`. **Fix:** include the target — *"record a test… for Blazor UI"*.
- Default to **inline** post-step waits when you wanted deferred. **Fix:** state the wait explicitly — *"…waiting 5 minutes after delivery"*.
- Pick the wrong env / endpoint when you have several. **Fix:** include them — *"…on sumo-retail for endpoint NEMMCO"*.

If you find a prompt the assistant **consistently** mishandles, share it in the team channel — the prompt template can be tuned without code changes.

---

## Reference docs at each stage

| Stage | If you want the deep dive |
|---|---|
| 2 | [`recording-troubleshooting.md`](recording-troubleshooting.md) — recorder sharp edges |
| 3 | [`functional.md`](functional.md) §"Command reference" — every CLI flag the assistant ultimately calls |
| 4a | [`architecture.md`](architecture.md) → DB Assert Step section |
| 4b | [`architecture.md`](architecture.md) → Event Assertion Step (Azure Service Bus) section |
| 5 | [`architecture.md`](architecture.md) → Deferred Post-Delivery Verification + Seamless Authentication Recovery |
