Tune or debug deferred post-delivery verification. The feature queues UI verifications with a future claim time so the delivery agent slot is freed immediately; retries re-enqueue on failure up to a deadline.

Arguments: $ARGUMENTS
Common forms:
- `/tune-deferred-verification` — open-ended diagnosis / tuning walk-through
- `/tune-deferred-verification hard ceiling` — user wants `wait` to be a hard deadline
- `/tune-deferred-verification stuck awaiting` — a run is stuck in AwaitingVerification

## Reference first

Read `docs/architecture.md` → **Deferred Post-Delivery Verification** section end-to-end before changing anything. Key concepts:
- `WaitBeforeSeconds` is the **deadline** for a green verification, not the time to wait before a single attempt.
- First attempt fires at `wait × VerificationEarlyStartFraction` (default 0.5). Failures re-enqueue every `VerificationRetryIntervalSeconds`. Absolute deadline = `wait + VerificationGraceSeconds`.
- State lives in `run_queue` (v6 columns) + `run_pending_verifications`. The Runner in remote mode talks to these via REST (`/api/queue`, `/api/pending-verifications`); the agent never touches the server DB directly.
- Summary regeneration happens inside `AseXmlDeliveryAgent.TryFinaliseParentRunAsync` — the provisional "Inconclusive" LLM summary is replaced when the run finalises.

## The knobs (all in `appsettings.json → TestEnvironment.AseXml`)

| Field | Default | When to change it |
|---|---|---|
| `DeferVerifications` | `true` | `false` = legacy inline `Task.Delay`. Rarely useful in prod; handy for local debugging without an agent. |
| `VerificationDeferThresholdSeconds` | `30` | Raise if the queueing overhead is noticeable for short waits; otherwise leave. |
| `VerificationEarlyStartFraction` | `0.5` | Lower (e.g. 0.3) if Bravo is usually faster than expected and you want a quicker green. Raise (e.g. 0.8) if early attempts are almost always a waste. |
| `VerificationRetryIntervalSeconds` | `30` | Lower for faster retries in short-wait scenarios; raise if UI agents are expensive or flaky. |
| `VerificationGraceSeconds` | `30` | **Set to `0` for a hard ceiling at `WaitBeforeSeconds`**. Common ask. |
| `VerificationMaxLatencySeconds` | `3600` | Lower if you want the janitor to give up faster on orphaned pending rows. |
| `DeferredPollCliIntervalSeconds` | `10` | CLI live-view cadence only; doesn't affect execution. |

CLI single-invocation override: `--no-defer-verifications` forces the synchronous path for one run.

## Walk-through: hard wait ceiling

User wants exactly `WaitBeforeSeconds` as the max duration for the full verification window.

1. Set `VerificationGraceSeconds: 0` in `appsettings.json`.
2. Explain: with `wait=120`, attempts fire at 60 s / 90 s / 120 s. Deadline is at 120 s. Past 120 s, the verification is marked Failed.
3. No code change. No restart of the PC agent needed — WebApi reads config on start, so restart the Docker container only.

## Walk-through: "stuck in Awaiting forever"

1. Identify the run id (from the dashboard URL or `/api/runs/{id}/status`).
2. On the WebApi's DB, run:
   ```sql
   SELECT pending_id, status, attempt_count, first_due_at, deadline_at, current_queue_entry_id
   FROM run_pending_verifications WHERE parent_run_id = '<runId>';

   SELECT id, status, target_type, not_before_at, deadline_at, attempt_count, claimed_by
   FROM run_queue WHERE parent_run_id = '<runId>' ORDER BY created_at DESC;
   ```
3. Interpret:
   - **Pending row exists, queue entry `Queued` with `not_before_at` in the past, no agent claimed**: no agent has the required capability. Check the `agents` table for online agents with the right `capabilities` column. Most common cause: the PC agent isn't running or its `--capabilities` was restricted.
   - **Queue entry `Claimed` with an old `claimed_at`**: agent process died. The janitor (`AgentHeartbeatMonitor`) reclaims stale claims (> 2 × `AgentHeartbeatTimeoutSeconds`) on its 30 s tick. Wait or restart the WebApi.
   - **Pending row terminal but run status stale**: the status endpoint at `/api/runs/{id}/status` reconciles on next poll — if the UI is open, refresh. If persistent, check that `ExecutionHistoryRepository.GetRunAsync` can locate the run by its testSetId.
   - **Pending row past its deadline still `Pending`**: the agent crashed between attempt and mark-terminal. Janitor sweep (`ListExpiredAsync`) fails it past `VerificationMaxLatencySeconds`; check WebApi logs for "Expired deferred-verification".
4. Server-side log search terms: `Deferred`, `Reclaimed stale queue entry`, `Expired deferred-verification`, `Finalised run`.

## Walk-through: tuning retry behaviour for a specific test set

Currently a global config. If the user needs per-test-case tuning:
- Short-term: adjust `WaitBeforeSeconds` on the verification itself (authored during `--record-verification` or edited via the UI). Longer wait = longer deadline.
- Long-term (code change): add a per-verification override field to `VerificationStep` (e.g. `GraceSecondsOverride`), plumb it through `DeferredVerificationRequest.Verifications[]`, consume in `AseXmlDeliveryAgent.TryEnqueueDeferredVerifications` when computing `deadline_at`. The REST endpoints carry the payload opaquely — no server-side changes needed. See `docs/architecture.md → Tuning / extending deferred verification`.

## What NOT to change without a plan

- The discriminator string `"kind": "DeferredVerification"` in `DeferredVerificationRequest.Kind` — the `JobExecutor.TryParseDeferredRequest` branch matches on it. Breaking this orphans all in-flight deferred entries.
- `SqliteRunQueueRepository.ClaimNextAsync`'s `not_before_at` filter — without it, deferred entries claim immediately and the feature reverts to inline execution timing.
- The `pending_id` == first queue entry's id invariant — multiple places rely on it for stable identity across retries.
- The `run_pending_verifications` → execution history merge in `TryFinaliseParentRunAsync` — the JSON run file is merged last-writer-wins. Never write to it from two places.

## Build + verify after config changes

Config-only changes: just restart the WebApi (Docker). Re-run a test case with a verification wait > threshold and watch the dashboard flow through `Awaiting` → `Passed`/`Failed`.

Code-path changes: `dotnet build AITestCrew.slnx` must pass with 0 errors. Then rebuild the Docker image, redeploy, and restart the PC agent.
