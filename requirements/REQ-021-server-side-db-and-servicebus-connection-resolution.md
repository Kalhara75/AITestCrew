---
id: REQ-021
title: Server-side DB + Service Bus connection resolution so distributed agents don't carry connection strings
status: Proposed
created: 2026-05-18
author: Kalhara Samarasinghe
author-note: discovered while running a tesla-retail test from a remote QA PC — teardown failed with "missing BravoDb connection string" because the agent's local appsettings.json has empty DbConnections (sanitised by publish.ps1). Same root cause as REQ-011 but for SQL + Service Bus credentials instead of LLM keys.
area: webapi + agents + core
---

# REQ-021 — Server-side DB + Service Bus connection resolution for distributed agents

## Goal

QA workstations running `--agent` should never hold SQL connection strings or Service Bus credentials. The server (WebApi) is the single place those secrets live; the agent asks the server to resolve them at job time.

When this lands, a sanitised pack on a QA PC can run a test that includes Data Pack scripts, DB Assert post-steps, Bravo teardown, or Service Bus event-asserts — without anything sensitive ever appearing in that machine's `appsettings.json`.

## Why now

A test against `tesla-retail` triggered from a remote QA PC just failed with:

> *Overall Result: FAIL. The test produced zero executable steps due to a critical configuration error … The failure occurred during teardown, indicating the test environment "tesla-retail" is missing a required Bravo DB connection string, which must be set in either `TestEnvironment.Environments.<env>.BravoDbConnectionString` or `TestEnvironment.AseXml.BravoDb.ConnectionString`.*

The agent's `appsettings.json` is correctly sanitised — `publish.ps1` (lines 98–146) blanks every top-level secret and empties both `DbConnections` and `ServiceBusConnections` dictionaries on the agent pack, top-level and per-env. The server is configured correctly. The bug is that connection-string resolution runs **agent-side** (`EnvironmentResolver.ResolveDbConnectionString` reads from the local `_config` snapshot), so the resolver returns null and the run errors before any step executes.

The naive fix — "tell QAs to paste the connection string into their local config" — has the same problems as the equivalent fix did for REQ-011 (LLM keys):

1. Reintroduces secret sprawl across every QA workstation.
2. Means every QA needs prod DB credentials on their laptop, with no central rotation story.
3. Makes credential rotation an N-machine update instead of a one-server update.

REQ-011 already established the precedent: when a credential is missing locally and a server is reachable, route through the server. This requirement extends that pattern to DB + Service Bus.

## Current behaviour

### DB connection strings

```
Job dispatched (RunEndpoints.cs:111):
  RunQueueEntry.RequestJson contains EnvironmentKey + stack/module/etc.
  Connection strings are NOT in the payload.

Agent picks job up (JobExecutor.cs:102-124):
  Deserialises QueuedRunRequest, calls orchestrator.RunAsync(..., environmentKey: ...)

DB Assert step (DbCheckAgent.cs:117):
  var connectionString = _envResolver.ResolveDbConnectionString(check.ConnectionKey, envKey);
  -> EnvironmentResolver.cs:103-124 reads from the agent's bound TestEnvironmentConfig
  -> returns null (sanitised pack has empty dictionaries)
  -> SqlConnection ctor throws / step errors

Data Pack runner (DataPackRunner.cs:95):
  var connectionString = _envResolver.ResolveBravoDbConnectionString(envKey);
  -> same code path, same null result, same failure mode.

Bravo teardown (BravoTeardownExecutor):
  same.

aseXML delivery endpoint lookup (BravoEndpointResolver.cs:131):
  var conn = _envResolver.ResolveBravoDbConnectionString(envKey);
  -> queries mil.V2_MIL_EndPoint for the delivery endpoint's FTP host /
     username / password before each aseXML upload
  -> same null result, throws InvalidOperationException with
     "Bravo DB connection string is not configured…"
```

### Service Bus connections

```
Event Assert step (AzureServiceBusEventAgent.cs:112):
  var connection = _envResolver.ResolveServiceBusConnection(connectionKey, envKey);
  -> EnvironmentResolver reads local ServiceBusConnections dict
  -> returns null on a sanitised pack
  -> receiver factory throws
```

Both resolution paths share the exact same shape: a method on `IEnvironmentResolver` that reads from the locally-bound `TestEnvironmentConfig`. The fix shape is therefore identical for both.

## Scope — what's in

### 1. New server endpoint: `GET /api/environments/{envKey}/connections/db/{connectionKey}`

```
Request:
  GET /api/environments/tesla-retail/connections/db/BravoDb
  X-Api-Key: atc_...

Response (200):
{
  "envKey":         "tesla-retail",
  "connectionKey":  "BravoDb",
  "connectionString": "Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True;",
  "source":         "Environment" | "TopLevel" | "Legacy"   // diagnostic hint
}

Response (404):
{ "error": "No connection string configured for envKey='tesla-retail', connectionKey='BravoDb'" }

Response (401): missing/invalid X-Api-Key
Response (403): caller lacks the role needed to resolve credentials (see §4 below)
```

- Lives in a new `src/AiTestCrew.WebApi/Endpoints/AgentConfigEndpoints.cs`, mapped under `app.MapGroup("/api/environments")` in `Program.cs`.
- Authenticated by the existing `ApiKeyAuthMiddleware`. Every agent already carries an `atc_` key.
- Internally calls the server's own `IEnvironmentResolver.ResolveDbConnectionString(connectionKey, envKey)` — **zero new resolution logic**. The server's local resolver remains the ground truth; the endpoint just exposes it over HTTP.
- Logs `{userId, agentId, envKey, connectionKey, source, latencyMs}` — connection string value is **never** logged. The audit trail tells admins "agent X on machine Y resolved BravoDb for env Z at time T".

### 2. Parallel endpoint: `GET /api/environments/{envKey}/connections/servicebus/{connectionKey}`

Same shape and auth. Body returns the full `ServiceBusConnectionConfig`:

```json
{
  "envKey": "tesla-retail",
  "connectionKey": "MeterEvents",
  "config": {
    "authMode": "ConnectionString" | "AzureAd",
    "connectionString": "Endpoint=sb://...;SharedAccessKey=...",
    "fullyQualifiedNamespace": "myns.servicebus.windows.net",
    "managedIdentityClientId": null
  },
  "source": "Environment" | "TopLevel"
}
```

For `AuthMode = AzureAd`, only the namespace + optional MI client id come back; no credential value (the agent uses `DefaultAzureCredential` against its own machine identity). That case is already working today but stays consistent with this endpoint shape for uniformity.

### 3. New agent-side resolver: `RemoteEnvironmentResolver`

`src/AiTestCrew.Agents/Environment/RemoteEnvironmentResolver.cs` — implements `IEnvironmentResolver` by wrapping the existing local `EnvironmentResolver` and delegating only the secret-bearing methods to the server.

Wrapper pattern (not full reimplementation) because most `IEnvironmentResolver` methods resolve non-secret values (URLs, usernames, paths, flags) that should keep coming from local config — those still ride along in the sanitised pack. Only the connection-string methods need server resolution.

```csharp
public class RemoteEnvironmentResolver : IEnvironmentResolver
{
    private readonly IEnvironmentResolver _local;     // bound to the sanitised local config
    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly string _apiKey;
    private readonly ConcurrentDictionary<(string env, string key), string?> _dbCache = new();
    private readonly ConcurrentDictionary<(string env, string key), ServiceBusConnectionConfig?> _sbCache = new();

    // Pass-through for non-secret resolution
    public string ResolveLegacyWebUiUrl(string? key) => _local.ResolveLegacyWebUiUrl(key);
    public string ResolveBraveCloudUiUrl(string? key) => _local.ResolveBraveCloudUiUrl(key);
    // ...all URL/username/path/flag methods delegate to _local...

    // Server resolution for secrets
    public string? ResolveDbConnectionString(string connectionKey, string? envKey)
    {
        // 1. Try local first — if a connection string IS configured locally (dev machine,
        //    server itself running in agent mode), use it. This preserves the dev box workflow.
        var local = _local.ResolveDbConnectionString(connectionKey, envKey);
        if (!string.IsNullOrWhiteSpace(local)) return local;

        // 2. Fall through to server.
        var resolved = _local.ResolveKey(envKey);
        return _dbCache.GetOrAdd((resolved, connectionKey), _ => FetchDbFromServer(resolved, connectionKey));
    }

    public ServiceBusConnectionConfig? ResolveServiceBusConnection(string connectionKey, string? envKey)
    {
        var local = _local.ResolveServiceBusConnection(connectionKey, envKey);
        if (local is not null) return local;
        var resolved = _local.ResolveKey(envKey);
        return _sbCache.GetOrAdd((resolved, connectionKey), _ => FetchSbFromServer(resolved, connectionKey));
    }

    public string ResolveBravoDbConnectionString(string? key)
    {
        // Legacy shim — route through ResolveDbConnectionString("BravoDb", key).
        // Returns "" (not null) for compatibility with existing string-typed callers.
        return ResolveDbConnectionString("BravoDb", key) ?? "";
    }
    // ListDbConnectionKeys / ListServiceBusConnectionKeys — see §6.
}
```

- Cache is **per-process, in-memory only**, never persisted. An agent restart re-fetches. Rotation story: rotate on server, bounce the agents.
- Cache TTL: none initially. The connection string is stable across a run; if rotation lands during a run, the new value picks up on the next agent restart. If we later add a TTL or a server-pushed invalidation signal, do it as a follow-up — out of scope here.
- Network errors during fetch surface as the same `null` the local path returned: the caller already treats `null` as `TestStatus.Error` with a specific message. No new exception type.

### 4. Authorisation: who can pull a connection string

The endpoint is authenticated by `X-Api-Key`. Authorisation is **role-based**, mirroring REQ-010:

- Default policy: any user role with capability to *run tests* against an env can resolve that env's connection strings. The connection string is no more sensitive than the ability to dispatch a job that hits the DB — so adding a separate role gate would be theatre.
- Audit log records every resolve so admins have visibility.
- Optional per-env opt-out: a new `EnvironmentConfig.AllowAgentConnectionResolution` flag (default `true`) that, when set to `false`, returns 403 for that env. Useful for "I want this env to be server-only — agents must not run any DB-touching step against it." Listed under **Open questions** below as the only piece that might warrant tightening.

### 5. Runner DI: pick local vs remote at startup

`src/AiTestCrew.Runner/Program.cs` (the `--agent` path) replaces the direct `EnvironmentResolver` registration with a similar three-way decision to REQ-011:

```csharp
var hasLocalDb     = AnyDbConnectionConfigured(envConfig);
var hasServer      = !string.IsNullOrWhiteSpace(envConfig.ServerUrl)
                  && !string.IsNullOrWhiteSpace(envConfig.ApiKey);
var envResolveMode = envConfig.EnvironmentResolutionMode;  // new — Auto | Local | RemoteProxy

bool useRemote = envResolveMode switch {
    "RemoteProxy" => true,
    "Local"       => false,
    _ /* Auto */  => !hasLocalDb && hasServer,   // proxy only when there's nothing locally and a server exists
};

if (useRemote) {
    services.AddSingleton<IEnvironmentResolver>(sp =>
        new RemoteEnvironmentResolver(
            inner: new EnvironmentResolver(sp.GetRequiredService<IOptions<TestEnvironmentConfig>>()),
            http: sp.GetRequiredService<IHttpClientFactory>().CreateClient("env-resolver"),
            serverUrl: envConfig.ServerUrl,
            apiKey: envConfig.ApiKey,
            logger: sp.GetRequiredService<ILogger<RemoteEnvironmentResolver>>()));
} else {
    services.AddSingleton<IEnvironmentResolver, EnvironmentResolver>();
}
```

`AnyDbConnectionConfigured` returns true if the agent has ANY connection string at the top level, in any per-env block, or in `BravoDbConnectionString` / `AseXml.BravoDb.ConnectionString`. The intent: a dev box with full local config keeps using the local resolver and never makes a server hop. Only fully-sanitised packs flip to remote.

The WebApi never wires `RemoteEnvironmentResolver` — it always uses the local one (server *is* the source of truth, no infinite recursion).

### 6. `ListDbConnectionKeys` / `ListServiceBusConnectionKeys`

These are used by the chat assistant and the editor dropdowns to populate "which connection?" pickers, and they currently read the local dictionaries.

- On an agent (sanitised pack) the local dictionaries are empty → these would return empty lists.
- But: these calls also happen on the **server** (where the WebApi serves the chat assistant and the editor UI) — and on the server the local resolver is the ground truth. So in practice these methods are called server-side, not agent-side.
- For completeness: when an agent does call these (e.g., a future LLM-driven generation path on the agent), `RemoteEnvironmentResolver` adds a `GET /api/environments/{envKey}/connections/db` (no key — list endpoint) and `GET /api/environments/{envKey}/connections/servicebus`. Returns just the keys, not the values. Cached the same way.

### 7. Acceptance tests

- **WebApi.Tests** — `AgentConfigEndpointsTests.cs`:
  - Happy path returns 200 with the resolved value for a configured (env, key) pair.
  - Returns 404 for an env/key that isn't configured.
  - Returns 401 for missing/invalid `X-Api-Key`.
  - Returns 403 when the env has `AllowAgentConnectionResolution = false`.
  - Service Bus endpoint: returns full `ServiceBusConnectionConfig` with both AuthMode shapes.
  - Audit log captures `{userId, agentId, envKey, connectionKey, source}` — and crucially, **does NOT capture the connection string value** itself.

- **Agents.Tests** — `RemoteEnvironmentResolverTests.cs`:
  - Local-first: when local has a value, server is never called (no HTTP traffic).
  - Local-empty: hits the server, caches, second call doesn't re-hit.
  - Server-404: returns `null` (the existing contract).
  - Server-unreachable: returns `null` and logs the network error (does not throw).
  - `ResolveBravoDbConnectionString("tesla-retail")` routes through `ResolveDbConnectionString("BravoDb", "tesla-retail")` correctly.

- **End-to-end smoke** (manual, captured in PR description): on the sanitised QA pack — empty `DbConnections`, empty `ServiceBusConnections`, empty `BravoDbConnectionString` — trigger the failing `tesla-retail` test from the screenshot above. Run completes; teardown runs; no banner.

## Scope — what's out

- **Bulk endpoint** (`GET /api/environments/{envKey}/connections` returning everything). Per-key calls + per-key caching are simpler. If the number of round trips ever shows up in profiling, add bulk later. Out of scope now.
- **Per-call server-side rate limiting.** Tied to REQ-006. When the rate limiter lands, attach this endpoint to it. Don't build a parallel limiter.
- **Encryption-at-rest for the cache.** The cache is in-memory only, lives for the duration of the agent process, and only holds values the agent is about to use anyway. The OS process boundary is the trust boundary.
- **Connection-string mutation from the agent side.** The agent only *reads*. Admins continue to edit `appsettings.json` on the server (or whatever config story we have there).
- **Auto-rotation push** (server notifies agents that a connection string changed). Restart the agents. If this becomes a pain point, add a server-sent invalidation later.
- **Connection-string-free providers** (e.g., Azure AD-only DB connections). Out of scope; the existing connection-string shape covers every DB AITestCrew touches today.
- **Secrets stored inside the customer's Bravo DB itself.** Once the agent can resolve the BravoDb connection string via the server, `BravoEndpointResolver` will query `mil.V2_MIL_EndPoint` from the agent and receive FTP usernames / passwords for the delivery target. Those values then live in agent process memory for the duration of the aseXML upload. That is intentional — the agent needs them to actually upload the file — but it is worth naming: REQ-021 moves *the connection string used to reach the customer's DB* off the QA machine, not *the credentials stored inside the customer's DB*. If we ever want to hide FTP creds from agents too, that becomes a separate requirement (likely a server-side "deliver this XML to endpoint X" RPC where the agent never sees the FTP creds at all).

## Acceptance criteria

1. Running `--agent` on a machine with empty `DbConnections`, empty `ServiceBusConnections`, empty `BravoDbConnectionString`, and a valid `ServerUrl` + `ApiKey` succeeds for: a DB Assert step against any env, a Bravo teardown against any env, a Data Pack startup script against any env, and an Azure Service Bus event-assert step against any env.
2. The specific test from the screenshot above (`tesla-retail` invoice command creation with teardown) runs green on a sanitised remote pack without any local connection-string configuration.
2a. An aseXML delivery objective with a non-default `EndpointCode` (i.e., one that requires `BravoEndpointResolver` to look up FTP host / username / password from `mil.V2_MIL_EndPoint`) runs end-to-end on the same sanitised pack — the endpoint lookup succeeds via the proxied BravoDb connection string, the FTP upload connects, and the deferred verification completes. No "Bravo DB connection string is not configured" banner.
3. The server's `/api/environments/{envKey}/connections/db/{connectionKey}` rejects calls without `X-Api-Key`, accepts calls with a valid key, and returns 404 for unknown env/key combinations.
4. The admin's local dev box (full `DbConnections` populated, `ServerUrl` blank) shows **no behaviour change** — startup wires the plain `EnvironmentResolver`, no HTTP hop.
5. The WebApi process itself **always** uses the local `EnvironmentResolver` — it never instantiates `RemoteEnvironmentResolver`. There is no path that lets the server call itself for credential resolution.
6. Connection-string values do not appear in any agent log line, server log line, or HTTP request body beyond the response body of the resolve endpoint itself.
7. Setting `AllowAgentConnectionResolution = false` on an env (per-env config, server-side) causes every agent resolve request for that env to return 403, and surfaces in the run as a clear error message ("env X has agent connection resolution disabled — run this from the server itself or enable it") — not a silent null.
8. The QA quickstart says, in one line, "your agent fetches connection strings from the server — you don't configure DB or Service Bus connections locally."

## Open questions (none blocking)

- **`AllowAgentConnectionResolution` default.** Defaulting to `true` keeps the bug-fix story trivial. Defaulting to `false` makes the feature explicitly opt-in per env, which is more conservative but means every existing customer env needs a config edit. Lean `true` for now, document the flag, revisit if there's a concrete env where someone wants server-only.
- **Cache invalidation on connection-string rotation.** No story today beyond "bounce the agents." Server-sent invalidation (long-poll or webhook) is doable but out of scope; track separately.
- **Should `IEnvironmentResolver.ResolveDbConnectionString` go async?** Today it's synchronous, and the wrapping HTTP call needs to be sync-over-async or block on `GetAwaiter().GetResult()`. The hot-path call sites (`DbCheckAgent`, `DataPackRunner`, `BravoTeardownExecutor`) all live inside async methods, so a properly async signature would be cleaner — but it ripples through every implementation including the local one. Recommend: keep it sync for v1, accept the `.Result` pattern in `RemoteEnvironmentResolver` since each call happens once per step and is small. Async migration is a separate refactor.
- **Should `ServerUrl` + `ApiKey` checks be unified across REQ-011 + REQ-021?** They're now the gating fields for both LLM proxy and connection resolution. A startup validation in REQ-005 could check "if agent mode and any of the proxies are auto-enabled, ServerUrl + ApiKey must be set." Likely yes, but track in REQ-005's follow-up.

## File-level impact preview

| File | Change |
|---|---|
| `src/AiTestCrew.WebApi/Endpoints/AgentConfigEndpoints.cs` | **NEW** — `GET /api/environments/{envKey}/connections/db/{connectionKey}` + `/servicebus/{connectionKey}` + list variants |
| `src/AiTestCrew.WebApi/Program.cs` | Map `/api/environments` group |
| `src/AiTestCrew.Agents/Environment/RemoteEnvironmentResolver.cs` | **NEW** — `IEnvironmentResolver` wrapper that delegates secret resolution over HTTP |
| `src/AiTestCrew.Runner/Program.cs` | Three-way DI registration for `IEnvironmentResolver` (Auto / Local / RemoteProxy) in `--agent` path |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Add `EnvironmentResolutionMode` field (Auto / Local / RemoteProxy, default Auto) |
| `src/AiTestCrew.Core/Configuration/EnvironmentConfig.cs` | Add `AllowAgentConnectionResolution` flag (default `true`) |
| `src/AiTestCrew.Runner/appsettings.example.json` | Show `"EnvironmentResolutionMode": "Auto"` with a comment block |
| `src/AiTestCrew.WebApi/appsettings.example.json` | Same (note: server ignores this field) |
| `publish.ps1` | Inject `"EnvironmentResolutionMode": "Auto"` into sanitised output. Continue blanking `DbConnections` + `ServiceBusConnections` (no change to strip list). |
| `docs/qa-quickstart.md` | One-line clarification: connection strings come from the server |
| `docs/architecture.md` | New section "Remote Connection Resolution" under Distributed Execution; mirrors the existing "LLM Proxy" section |
| `CLAUDE.md` "Where to extend" table | Update rows for "A new DB connection" + "A new Azure Service Bus namespace" — note that server is the source of truth; agents auto-inherit via the proxy |
| `tests/AiTestCrew.WebApi.Tests/AgentConfigEndpointsTests.cs` | **NEW** |
| `tests/AiTestCrew.Agents.Tests/RemoteEnvironmentResolverTests.cs` | **NEW** |

## Done means

- A QA runs the failing `tesla-retail` invoice-command-creation test from their sanitised agent pack. It passes (or fails on actual test logic, not config). The teardown banner is gone forever.
- One copy of every connection string lives on the server. Adding the next QA stays a one-line `POST /api/users` call. Rotating a DB password stays a one-line `appsettings.json` edit on the server + an agent bounce.
- `grep -r "ConnectionString" /sanitised-agent-pack/appsettings.json` returns empty values everywhere it appears.
