---
id: REQ-011
title: Server-side LLM proxy so distributed agents don't need their own Anthropic key
status: Proposed
created: 2026-05-15
author: Kalhara Samarasinghe
author-note: discovered while testing the REQ-010 distribution model end-to-end — first remote agent failed because it had no local LLM key
area: webapi + runner + agents + core
---

# REQ-011 — Server-side LLM proxy for distributed agents

## Goal

Let a distributed agent run jobs without a local LLM API key. The agent should transparently route every `IChatCompletionService` call to the server, which uses its single configured key to forward the call to Anthropic (or whatever provider is wired in) and returns the response. The admin keeps **one** API key — on the server. QA machines stay free of LLM credentials.

When the requirement lands, a QA can run an agent off the sanitised pack (no `LlmApiKey` in their `appsettings.json`) and every test type that uses the LLM today — generated API tests, run summaries, the chat assistant's plumbing — keeps working exactly as it does on the server.

## Why now

The distribution pack (`publish.ps1 -Runner`) strips `LlmApiKey` along with every other secret because **agents shouldn't carry secrets**. That decision is correct, but it surfaces a latent dependency: the Runner constructs an LLM client at startup regardless of whether a particular job will use it, and `BaseTestAgent.SummariseResultsAsync` fires an LLM call at the **end of every run** to write the narrative summary — including for purely recorded UI tests.

Today, the first remote QA agent (`BRLAP110`) reproducibly fails any run with:

> *Anthropic rejected your authorization, most likely due to an invalid API Key. Full API response follows: {"type":"error","error":{"type":"authentication_error","message":"x-api-key header is required"}}*

The naive fix is "tell QAs to fill in `LlmApiKey` too," but that:

1. Reintroduces the exact secret-sprawl problem the sanitisation pass was designed to prevent.
2. Forks billing across N keys — no per-user spend visibility, no central rate-limit, no kill switch.
3. Requires every QA to be issued an Anthropic key, which is a procurement step that scales badly.

A proxy lives at the right layer: keys stay where the server's existing API-key middleware (`Middleware/ApiKeyAuthMiddleware.cs`) already enforces auth, and the cost-attribution + audit story improves rather than degrades.

## Current behaviour

```
Agent startup (Runner/Program.cs:427-437):
  if (LlmProvider == "Anthropic") {
      services.AddSingleton<IChatCompletionService>(
          new AnthropicChatCompletionService(envConfig.LlmApiKey, envConfig.LlmModel));
  }

End of every run (Agents/Base/BaseTestAgent.cs:97-116):
  SummariseResultsAsync(...) -> AskLlmAsync(...) -> _chatService.GetChatMessageContentsAsync(...)
  -> AnthropicChatCompletionService -> https://api.anthropic.com  with x-api-key: <empty>
  -> 401 authentication_error
```

`AnthropicChatCompletionService.cs` is identical between Runner and WebApi today. Every agent code path that calls `AskLlmAsync` / `AskLlmForJsonAsync` ultimately resolves to whichever `IChatCompletionService` was registered at startup — there's no per-call routing decision.

## Scope — what's in

### 1. New server endpoint: `POST /api/llm/chat`

```
Request:
{
  "model":     "claude-sonnet-4-6",          // optional — server falls back to its configured LlmModel
  "messages":  [{"role":"system","content":"..."},{"role":"user","content":"..."}],
  "maxTokens": 4096,                          // optional
  "temperature": 0.0                          // optional
}

Response (200):
{
  "content":      "<assistant reply text>",
  "model":        "claude-sonnet-4-6",
  "stopReason":   "end_turn",
  "usage": { "inputTokens": 312, "outputTokens": 88 }
}

Response (4xx/5xx):
{ "error": "<short message>", "providerError": "<raw provider response if available>" }
```

- Lives in a new `src/AiTestCrew.WebApi/Endpoints/LlmEndpoints.cs`, mapped under `app.MapGroup("/api/llm")` in `Program.cs` alongside the other groups.
- Authenticated by the existing `ApiKeyAuthMiddleware` — every agent already carries an `atc_` key for `/api/queue/next`, etc. **No new auth surface.**
- Internally calls the same `IChatCompletionService` the server already has registered. No new dependency on `Anthropic.SDK` at the endpoint layer.
- Logs `{userId, agentId (header), model, inputTokens, outputTokens, latencyMs}` to the existing logger pipeline so admins get cost / usage visibility without writing a dashboard.

### 2. New agent-side service: `RemoteChatCompletionService`

`src/AiTestCrew.Agents/Llm/RemoteChatCompletionService.cs` — implements `IChatCompletionService` (Microsoft.SemanticKernel.ChatCompletion).

- Constructor takes `(HttpClient client, string serverUrl, string apiKey, string defaultModel, ILogger logger)`.
- `GetChatMessageContentsAsync` serialises the SK `ChatHistory` into the endpoint's request shape, POSTs to `{serverUrl}/api/llm/chat` with `X-Api-Key: {apiKey}`, deserialises the response into a `ChatMessageContent`.
- `GetStreamingChatMessageContentsAsync` — **out of scope for v1**. Throw `NotSupportedException` for now; agents don't call streaming today (`grep` for `GetStreamingChat` returns zero hits in `AiTestCrew.Agents`).
- Surfaces server errors as exceptions with the `providerError` body in the message so existing `catch` blocks in agents log usefully.

### 3. Runner DI: pick local vs remote at startup

`src/AiTestCrew.Runner/Program.cs` replaces the unconditional Anthropic registration with a three-way decision:

```csharp
var hasLocalKey  = !string.IsNullOrWhiteSpace(envConfig.LlmApiKey);
var hasServer    = !string.IsNullOrWhiteSpace(envConfig.ServerUrl)
                && !string.IsNullOrWhiteSpace(envConfig.ApiKey);
var llmMode      = envConfig.LlmMode;   // new field — Auto | Local | RemoteProxy

bool useRemote = llmMode switch {
    "RemoteProxy" => true,
    "Local"       => false,
    _ /* Auto */  => !hasLocalKey && hasServer,  // fall back to proxy only when no local key
};

if (useRemote) {
    services.AddSingleton<IChatCompletionService>(sp =>
        new RemoteChatCompletionService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm"),
            envConfig.ServerUrl, envConfig.ApiKey, envConfig.LlmModel,
            sp.GetRequiredService<ILogger<RemoteChatCompletionService>>()));
} else if (envConfig.LlmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)) {
    services.AddSingleton<IChatCompletionService>(
        new AnthropicChatCompletionService(envConfig.LlmApiKey, envConfig.LlmModel));
} else {
    kernelBuilder.AddOpenAIChatCompletion(envConfig.LlmModel, envConfig.LlmApiKey);
}
```

The `Auto` default means:
- **Server / dev box** (has `LlmApiKey`, may or may not have `ServerUrl`) → Local. **No behaviour change.**
- **Sanitised agent pack** (no `LlmApiKey`, has `ServerUrl` + `ApiKey`) → RemoteProxy. **Bug fixed.**
- **Misconfigured agent** (no `LlmApiKey`, no `ServerUrl`) → falls through to current code path, gets the same error it gets today. Startup-time `appsettings` validation (REQ-005) can refuse to start in this case if we extend it.

`LlmMode` lives in `TestEnvironmentConfig` and the example file. Default `"Auto"`.

### 4. publish.ps1 sanitisation update

The sanitiser already blanks `LlmApiKey`. **No change** to the strip list. But:

- Set `LlmMode = "Auto"` explicitly in the sanitised output (currently absent → defaults to `Auto` anyway, but explicit is friendlier).
- README + `qa-quickstart.md` get a one-line update: *"Your agent uses the server's LLM key — you don't configure one."*

### 5. Acceptance tests

- **WebApi.Tests** — `LlmEndpointsTests.cs`: happy-path POST returns `200` + content; missing API key returns `401`; bad payload returns `400`.
- **Agents.Tests** — `RemoteChatCompletionServiceTests.cs`: fake `HttpMessageHandler` returns a canned response; service surfaces it as `ChatMessageContent`. Failure path surfaces server's `providerError`.
- **End-to-end smoke** (manual, captured in PR description): publish a sanitised pack, run an agent on a second machine, trigger the failing test from screenshot #2, observe the run completes green and the summary narrative appears in execution detail.

## Scope — what's out

- **Streaming** (`GetStreamingChatMessageContentsAsync`). Not used by any agent today. Add in a follow-up if/when the assistant or generation path adopts streaming.
- **Per-user rate limiting** on the LLM endpoint. The server already has REQ-006 rate-limiter consolidation — when that's in place, attach the LLM endpoint to it. Don't build a parallel limiter here.
- **Provider abstraction beyond what the server already supports.** The proxy forwards to whatever `IChatCompletionService` the WebApi has registered. If you wire OpenAI server-side, agents use OpenAI through the proxy automatically.
- **Cost dashboards.** Log usage; build the dashboard later. Out of scope.
- **Removing LLM calls from recorded-test paths.** `SummariseResultsAsync` for recorded tests *could* be skipped (it adds little value vs. raw step results), but that's a separate trim worth its own conversation. Don't conflate.

## Acceptance criteria

1. Running `--agent` on a machine with `LlmApiKey = ""` and a valid `ServerUrl` + `ApiKey` succeeds for: a recorded Web UI test, a recorded Desktop UI test, an AI-generated API test, and an aseXML delivery objective with a deferred verification.
2. Server's `/api/llm/chat` rejects requests without `X-Api-Key`, accepts requests with a valid key, logs usage per user.
3. The admin's local dev box (full `LlmApiKey` populated, `ServerUrl` blank) shows **no behaviour change** — startup still wires the local `AnthropicChatCompletionService`, no proxy HTTP hop.
4. Setting `LlmMode = "Local"` on an agent that has both a local key AND a server forces local. Setting `LlmMode = "RemoteProxy"` forces proxy even when a local key is present (useful for testing the proxy from the dev machine).
5. The QA quickstart's "Step 2 — fill one field" instruction holds — no new field for QAs to set.
6. No secret values appear in agent logs or HTTP request bodies on the agent side beyond what's already there (`X-Api-Key` is the only sensitive header, same as every other agent call).

## Open questions (none blocking)

- Do we want a single `IChatCompletionService` interface backed by the proxy, or do we also need to proxy the lower-level Anthropic SDK calls some agents make directly? Spot check says no — all agent LLM access goes through `BaseTestAgent.AskLlmAsync` and `AskLlmForJsonAsync`, both of which use the injected `IChatCompletionService`. To be confirmed during implementation.
- Should the proxy support a "warm spare" provider failover (e.g., fall through to OpenAI when Anthropic is down)? Probably yes, but **not in this requirement** — track separately.
- Should agent → server LLM calls bypass the WebApi's per-IP rate limiter to avoid an agent hammering it during a generation-heavy run? Tied to REQ-006; flag on the issue.

## File-level impact preview

| File | Change |
|---|---|
| `src/AiTestCrew.WebApi/Endpoints/LlmEndpoints.cs` | **NEW** — `POST /api/llm/chat` |
| `src/AiTestCrew.WebApi/Program.cs` | Map `/api/llm` group |
| `src/AiTestCrew.Agents/Llm/RemoteChatCompletionService.cs` | **NEW** — `IChatCompletionService` over HTTP |
| `src/AiTestCrew.Runner/Program.cs` | Replace unconditional Anthropic registration with the three-way switch above |
| `src/AiTestCrew.Core/Configuration/TestEnvironmentConfig.cs` | Add `LlmMode` field (Auto / Local / RemoteProxy, default Auto) |
| `src/AiTestCrew.Runner/appsettings.example.json` | Show `"LlmMode": "Auto"` with a comment block |
| `src/AiTestCrew.WebApi/appsettings.example.json` | Same |
| `publish.ps1` | Inject `"LlmMode": "Auto"` into sanitised output; README wording |
| `docs/qa-quickstart.md` | One-line clarification in Step 2 |
| `docs/architecture.md` | New short section "LLM Proxy" under the Distributed Execution chapter |
| `CLAUDE.md` "Where to extend" table | New row: *Adding a new LLM provider* → server-side only; agents auto-inherit via the proxy |
| `tests/AiTestCrew.WebApi.Tests/LlmEndpointsTests.cs` | **NEW** |
| `tests/AiTestCrew.Agents.Tests/RemoteChatCompletionServiceTests.cs` | **NEW** |

## Done means

- A QA who unzips today's sanitised pack and pastes their `atc_` key into `ApiKey` can run any test type without seeing the *"Anthropic rejected your authorization"* banner again.
- One Anthropic key, on the server, for the whole team. Adding the next QA stays a one-line `POST /api/users` call.
