using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models.Chat;
using AiTestCrew.WebApi.Models.Chat;

namespace AiTestCrew.WebApi.Services;

public interface IChatIntentService
{
    /// <summary>
    /// Process one chat turn. When <paramref name="userId"/> is non-null and a
    /// conversation repository is registered, the conversation is persisted
    /// (history loaded from DB, user + assistant turns appended). When null
    /// (legacy / file-storage mode), the call falls back to the stateless
    /// behaviour of resending the full transcript on each request.
    /// </summary>
    Task<ChatResponse> ProcessAsync(ChatRequest request, string? userId, CancellationToken ct = default);
}

public class ChatIntentService : IChatIntentService
{
    private readonly Kernel _kernel;
    private readonly IModuleRepository _moduleRepo;
    private readonly ITestSetRepository _testSetRepo;
    private readonly IEnvironmentResolver _envResolver;
    private readonly IEndpointResolver _endpointResolver;
    private readonly TestEnvironmentConfig _cfg;
    private readonly ILogger<ChatIntentService> _logger;
    private readonly IAgentRepository? _agentRepo;
    private readonly IChatConversationRepository? _convRepo;

    public ChatIntentService(
        Kernel kernel,
        IModuleRepository moduleRepo,
        ITestSetRepository testSetRepo,
        IEnvironmentResolver envResolver,
        IEndpointResolver endpointResolver,
        TestEnvironmentConfig cfg,
        ILogger<ChatIntentService> logger,
        IAgentRepository? agentRepo = null,
        IChatConversationRepository? convRepo = null)
    {
        _kernel = kernel;
        _moduleRepo = moduleRepo;
        _testSetRepo = testSetRepo;
        _envResolver = envResolver;
        _endpointResolver = endpointResolver;
        _cfg = cfg;
        _logger = logger;
        _agentRepo = agentRepo;
        _convRepo = convRepo;
    }

    public async Task<ChatResponse> ProcessAsync(ChatRequest request, string? userId, CancellationToken ct = default)
    {
        // Determine the new user message: prefer the explicit Message field;
        // fall back to the last entry in Messages for backwards compatibility.
        var userMessage = request.Message;
        if (string.IsNullOrWhiteSpace(userMessage) && request.Messages is { Count: > 0 })
        {
            var last = request.Messages[^1];
            if (string.Equals(last.Role, "user", StringComparison.OrdinalIgnoreCase))
                userMessage = last.Content;
        }
        if (string.IsNullOrWhiteSpace(userMessage))
            return new ChatResponse { Reply = "Send a message to get started." };

        var persistMode = _convRepo is not null && !string.IsNullOrWhiteSpace(userId);

        // ── Resolve / create the conversation, then load prior messages from the DB ──
        ChatConversation? conv = null;
        var priorTurns = new List<ChatMessage>();
        if (persistMode)
        {
            if (!string.IsNullOrWhiteSpace(request.ConversationId))
            {
                conv = await _convRepo!.GetAsync(request.ConversationId, userId!, ct);
                if (conv is null)
                {
                    _logger.LogInformation("Chat conversation {Id} not found for user; creating a new one.", request.ConversationId);
                }
            }
            if (conv is null)
            {
                conv = await _convRepo!.CreateAsync(userId!, AutoTitle(userMessage), _cfg.Chat.MaxConversationsPerUser, ct);
            }
            else if (conv.MessageCount == 0 && IsPlaceholderTitle(conv.Title))
            {
                // Conversation was created via "+ New chat" with the default
                // placeholder title — auto-title now that we have a real first
                // message to derive a label from.
                var derived = AutoTitle(userMessage);
                await _convRepo!.RenameAsync(conv.Id, userId!, derived, ct);
                conv.Title = derived;
            }

            var existing = await _convRepo!.GetMessagesAsync(conv.Id, userId!, ct);
            // Bound prompt size — drop oldest first.
            var keep = Math.Max(0, _cfg.Chat.MaxMessagesPerConversation);
            var startIdx = keep > 0 && existing.Count > keep ? existing.Count - keep : 0;
            for (var i = startIdx; i < existing.Count; i++)
                priorTurns.Add(new ChatMessage(existing[i].Role, existing[i].Content));
        }
        else if (request.Messages is not null)
        {
            // Stateless fallback: trust the client transcript as before, minus the last user msg
            // (which we add below as the current turn).
            for (var i = 0; i < request.Messages.Count - 1; i++)
                priorTurns.Add(request.Messages[i]);
        }

        // ── Persist the user turn first so it's visible even if the LLM call fails ──
        if (persistMode && conv is not null)
        {
            await _convRepo!.AppendMessageAsync(conv.Id, userId!, new ChatMessageRecord
            {
                Role = "user",
                Content = userMessage,
                CreatedAt = DateTime.UtcNow
            }, ct);
        }

        var catalog = await BuildCatalogAsync(request.Context, ct);
        var systemPrompt = BuildSystemPrompt(catalog, request.Context);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        foreach (var msg in priorTurns)
        {
            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                history.AddUserMessage(msg.Content);
            else if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                history.AddAssistantMessage(msg.Content);
        }
        history.AddUserMessage(userMessage);

        var raw = (await chatService.GetChatMessageContentAsync(history, cancellationToken: ct)).Content ?? "";
        _logger.LogDebug("Chat LLM raw response: {Raw}", raw[..Math.Min(500, raw.Length)]);

        var parsed = LlmJsonHelper.DeserializeLlmResponse<ChatResponse>(raw)
                     ?? new ChatResponse { Reply = raw };

        if (persistMode && conv is not null)
        {
            parsed.ConversationId = conv.Id;
            string? actionsJson = null;
            if (parsed.Actions is { Count: > 0 })
            {
                try { actionsJson = JsonSerializer.Serialize(parsed.Actions, LlmJsonHelper.JsonOpts); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to serialise chat actions for persistence."); }
            }
            await _convRepo!.AppendMessageAsync(conv.Id, userId!, new ChatMessageRecord
            {
                Role = "assistant",
                Content = parsed.Reply ?? "",
                ActionsJson = actionsJson,
                CreatedAt = DateTime.UtcNow
            }, ct);
        }

        return parsed;
    }

    private static string AutoTitle(string firstUserMessage)
    {
        var cleaned = firstUserMessage.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleaned.Length <= 60) return string.IsNullOrEmpty(cleaned) ? "New chat" : cleaned;
        return cleaned[..60].TrimEnd() + "…";
    }

    /// <summary>True for the default title set by "+ New chat" — anything the
    /// user explicitly renamed to is left alone.</summary>
    private static bool IsPlaceholderTitle(string? title) =>
        string.IsNullOrWhiteSpace(title)
        || string.Equals(title, "New chat", StringComparison.OrdinalIgnoreCase)
        || string.Equals(title, "Untitled", StringComparison.OrdinalIgnoreCase);

    private async Task<object> BuildCatalogAsync(ChatRequestContext? context, CancellationToken ct)
    {
        var modules = await _moduleRepo.ListAllAsync();
        var moduleList = modules.Select(m =>
        {
            var testSets = _testSetRepo.ListByModule(m.Id);
            return new
            {
                id = m.Id,
                name = m.Name,
                description = m.Description,
                testSets = testSets.Select(ts => new
                {
                    id = ts.Id,
                    name = ts.Name,
                    apiStackKey = ts.ApiStackKey,
                    apiModule = ts.ApiModule,
                    endpointCode = ts.EndpointCode,
                    environmentKey = ts.EnvironmentKey
                }).ToArray()
            };
        }).ToArray();

        // When a test set is in the current page context, surface its objectives
        // AND each objective's parent-step breakdown so the LLM can resolve
        // references like "after the Search web UI step" or "the second API call"
        // to a concrete (parentKind, parentStepIndex) tuple.
        object? currentTestSet = null;
        if (!string.IsNullOrWhiteSpace(context?.ModuleId) && !string.IsNullOrWhiteSpace(context?.TestSetId))
        {
            var ts = await _testSetRepo.LoadAsync(context.ModuleId!, context.TestSetId!);
            if (ts is not null)
            {
                currentTestSet = new
                {
                    moduleId = context.ModuleId,
                    testSetId = ts.Id,
                    name = ts.Name,
                    apiStackKey = ts.ApiStackKey,
                    apiModule = ts.ApiModule,
                    endpointCode = ts.EndpointCode,
                    environmentKey = ts.EnvironmentKey,
                    objectives = ts.TestObjectives.Select(o => new
                    {
                        id = o.Id,
                        name = string.IsNullOrWhiteSpace(o.Name) ? o.Id : o.Name,
                        source = o.Source,
                        targetType = o.TargetType,
                        // Flat list of (parentKind, parentStepIndex, short description, postStepCount)
                        // tuples across all five step-list fields. Lets the LLM pick
                        // a parent for post-step authoring without deep-reading the
                        // test set JSON.
                        parentSteps = BuildParentStepBreakdown(o)
                    }).ToArray()
                };
            }
        }

        var defaultEnv = _envResolver.ResolveKey(null);
        var envs = _envResolver.ListKeys().Select(k => new
        {
            key = k,
            displayName = _envResolver.ResolveDisplayName(k),
            isDefault = string.Equals(k, defaultEnv, StringComparison.OrdinalIgnoreCase)
        }).ToArray();

        var stacks = _cfg.ApiStacks.ToDictionary(
            kvp => kvp.Key,
            kvp => new { modules = kvp.Value.Modules.Keys.ToArray() });

        // Endpoints are sourced from each environment's Bravo DB
        // (Environments[env].BravoDbConnectionString), so the catalog
        // surfaces one list per env. A single env's broken/absent DB
        // is logged and reported as null — the rest still load.
        var endpointsByEnv = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _envResolver.ListKeys())
        {
            try
            {
                var codes = await _endpointResolver.ListCodesAsync(key, ct);
                endpointsByEnv[key] = codes.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Endpoint catalog unavailable for env '{Env}'.", key);
                endpointsByEnv[key] = null;
            }
        }

        object[] agents = Array.Empty<object>();
        if (_agentRepo is not null)
        {
            try
            {
                var list = await _agentRepo.ListAllAsync();
                agents = list.Select(a => (object)new
                {
                    id = a.Id,
                    name = a.Name,
                    status = a.Status,
                    capabilities = a.Capabilities
                }).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Agent catalog unavailable.");
            }
        }

        // Post-step deferral knobs — surfaced to the LLM so it picks a
        // waitBeforeSeconds that matches the user's intent (inline vs deferred)
        // against the CURRENT config, not a hardcoded guess. See "Deferred vs
        // inline post-steps" in the system prompt for how these get used.
        var postStepConfig = new
        {
            deferEnabled = _cfg.AseXml.DeferVerifications,
            deferThresholdSeconds = _cfg.AseXml.VerificationDeferThresholdSeconds,
            retryIntervalSeconds = _cfg.AseXml.VerificationRetryIntervalSeconds,
            graceSeconds = _cfg.AseXml.VerificationGraceSeconds,
        };

        return new
        {
            modules = moduleList,
            environments = envs,
            defaultEnvironment = defaultEnv,
            apiStacks = stacks,
            defaultStack = _cfg.DefaultApiStack,
            defaultModule = _cfg.DefaultApiModule,
            endpointsByEnv,
            agents,
            currentTestSet,
            postStepConfig
        };
    }

    /// <summary>
    /// Flattens every parent step across the five step-list fields of a
    /// <see cref="TestObjective"/> into a single catalog-friendly array. Each
    /// entry carries just enough info for the LLM to match a user reference
    /// like "the Search step" to a concrete (parentKind, parentStepIndex)
    /// — no deep payload, no recorded UI steps.
    /// </summary>
    private static object[] BuildParentStepBreakdown(TestObjective o)
    {
        var rows = new List<object>();
        for (var i = 0; i < o.ApiSteps.Count; i++)
        {
            var s = o.ApiSteps[i];
            rows.Add(new
            {
                parentKind = "Api",
                parentStepIndex = i,
                description = $"{s.Method} {s.Endpoint}".Trim(),
                postStepCount = s.PostSteps.Count,
                postStepWaits = s.PostSteps.Select(ps => ps.WaitBeforeSeconds).ToArray()
            });
        }
        for (var i = 0; i < o.WebUiSteps.Count; i++)
        {
            var s = o.WebUiSteps[i];
            rows.Add(new
            {
                parentKind = "WebUi",
                parentStepIndex = i,
                description = string.IsNullOrWhiteSpace(s.Description)
                    ? (string.IsNullOrWhiteSpace(s.StartUrl) ? $"web ui case {i}" : s.StartUrl)
                    : s.Description,
                postStepCount = s.PostSteps.Count,
                postStepWaits = s.PostSteps.Select(ps => ps.WaitBeforeSeconds).ToArray()
            });
        }
        for (var i = 0; i < o.DesktopUiSteps.Count; i++)
        {
            var s = o.DesktopUiSteps[i];
            rows.Add(new
            {
                parentKind = "DesktopUi",
                parentStepIndex = i,
                description = string.IsNullOrWhiteSpace(s.Description) ? $"desktop ui case {i}" : s.Description,
                postStepCount = s.PostSteps.Count,
                postStepWaits = s.PostSteps.Select(ps => ps.WaitBeforeSeconds).ToArray()
            });
        }
        for (var i = 0; i < o.AseXmlSteps.Count; i++)
        {
            var s = o.AseXmlSteps[i];
            rows.Add(new
            {
                parentKind = "AseXml",
                parentStepIndex = i,
                description = string.IsNullOrWhiteSpace(s.Description)
                    ? $"aseXml generate {s.TemplateId}".Trim()
                    : s.Description,
                postStepCount = s.PostSteps.Count,
                postStepWaits = s.PostSteps.Select(ps => ps.WaitBeforeSeconds).ToArray()
            });
        }
        for (var i = 0; i < o.AseXmlDeliverySteps.Count; i++)
        {
            var s = o.AseXmlDeliverySteps[i];
            rows.Add(new
            {
                parentKind = "AseXmlDeliver",
                parentStepIndex = i,
                description = string.IsNullOrWhiteSpace(s.Description)
                    ? $"aseXml deliver {s.TemplateId} → {s.EndpointCode}".Trim()
                    : s.Description,
                postStepCount = s.PostSteps.Count,
                postStepWaits = s.PostSteps.Select(ps => ps.WaitBeforeSeconds).ToArray()
            });
        }
        return rows.ToArray();
    }

    private static string BuildSystemPrompt(object catalog, ChatRequestContext? context)
    {
        var catalogJson = JsonSerializer.Serialize(catalog, LlmJsonHelper.JsonOpts);
        var contextJson = context is null ? "none" : JsonSerializer.Serialize(context, LlmJsonHelper.JsonOpts);
        return $$$"""
            You are the AITestCrew chat assistant, embedded in a web UI used by test engineers.

            You help users explore their test suite, navigate pages, trigger runs, create modules/test sets, and dispatch recording or auth-setup sessions to online agents. You do NOT execute mutations directly — you always produce a confirmation card (confirmRun, confirmCreate, or confirmRecord) and the user clicks Execute.

            RESPOND WITH A SINGLE JSON OBJECT and nothing else. Shape:
            {
              "reply": "natural-language answer shown to the user (1–3 sentences)",
              "actions": [ ... zero or more action objects ... ]
            }

            Each action must be one of:

              Navigation — the user asked to open / go to / show a page:
              { "kind": "navigate", "path": "/" }
              { "kind": "navigate", "path": "/modules/{moduleId}" }
              { "kind": "navigate", "path": "/modules/{moduleId}/testsets/{testSetId}" }

              Data display — the user asked to list / show structured data:
              { "kind": "showData", "title": "Environments", "data": <any JSON value> }

              Run trigger — the user asked to run / rerun / replay / verify a test set or objective, OR to generate a NEW API test case from a natural-language objective:
              { "kind": "confirmRun", "summary": "Reuse mfn-delivery on sumo-retail", "data": {
                  "mode": "Normal" | "Reuse" | "Rebaseline" | "VerifyOnly",
                  "objective": "<required for Normal/Rebaseline — the natural-language objective the LLM test-generator will expand into API test cases>",
                  "objectiveName": "<optional short display name for the new objective in Normal mode>",
                  "moduleId": "<from catalog>",
                  "testSetId": "<from catalog>",
                  "objectiveId": "<optional, only if user scoped an existing objective>",
                  "environmentKey": "<optional, omit to use the test set's stored env or default>",
                  "apiStackKey": "<for Normal API generation: required, must be a key of catalog.apiStacks>",
                  "apiModule": "<for Normal API generation: required, must be a key of catalog.apiStacks[apiStackKey].modules>",
                  "verificationWaitOverride": <optional integer seconds, only for VerifyOnly>
                }
              }

              Recording dispatch — the user asked to record a test case / setup / verification, or to do an auth-setup. The card lets the user pick which registered agent runs the session:
              { "kind": "confirmRecord", "summary": "Record login for retail on Blazor", "data": {
                  "recordingKind": "Record" | "RecordSetup" | "RecordVerification" | "AuthSetup",
                  "target": "UI_Web_MVC" | "UI_Web_Blazor" | "UI_Desktop_WinForms",
                  "moduleId": "<from catalog>",       // Record, RecordSetup, RecordVerification
                  "testSetId": "<from catalog>",       // Record, RecordSetup, RecordVerification
                  "caseName": "<display name>",        // Record only
                  "objectiveId": "<from currentTestSet.objectives>",  // RecordVerification only
                  "verificationName": "<display name>",               // RecordVerification only
                  "waitBeforeSeconds": <optional integer>,            // RecordVerification
                  "parentKind": "Api" | "WebUi" | "DesktopUi" | "AseXml" | "AseXmlDeliver",  // RecordVerification only — which parent step list
                  "parentStepIndex": <integer>,                       // RecordVerification only — 0-based index into the chosen parent list
                  "deliveryStepIndex": <optional integer>,            // DEPRECATED — use parentKind+parentStepIndex. Kept for old aseXML delivery flow: equivalent to parentKind="AseXmlDeliver" + parentStepIndex=<this value>. Default 0.
                  "environmentKey": "<optional>"
                }
              }

              Post-step authoring without a recorder — the user asked to add a DB check, API call, aseXML generation, or aseXML delivery AS a post-step of an existing parent. These carry a JSON payload directly rather than firing up a UI recorder:
              { "kind": "confirmCreatePostStep", "summary": "Add DB check 'Job row exists' after WebUi[1] 'Search'", "data": {
                  "moduleId": "<from catalog>",
                  "testSetId": "<from catalog>",
                  "objectiveId": "<from currentTestSet.objectives>",
                  "parentKind": "Api" | "WebUi" | "DesktopUi" | "AseXml" | "AseXmlDeliver",
                  "parentStepIndex": <0-based index from the objective's parentSteps list>,
                  "postStep": {
                    "description": "<short human label>",
                    "target": "Db_SqlServer" | "API_REST" | "AseXml_Generate" | "AseXml_Deliver",
                    "waitBeforeSeconds": <integer, default 0 for DbCheck when user didn't specify>,
                    "role": "Verification" | "Action",   // Verification unless the user described an action (drop aseXML, call API to mutate, etc.)
                    "dbCheck": {                         // present only when target is Db_SqlServer
                      "name": "<human label>",
                      "connectionKey": "BravoDb",        // logical key — usually BravoDb; SdrReportingDb / customer DBs allowed when configured
                      "sql": "<single read-only SELECT; {{Tokens}} from the parent context — NMI, MessageID, StartUrl etc. — are substituted at runtime>",
                      "expectedRowCount": <optional integer>,         // EITHER expectedRowCount …
                      "columnAssertions": [                            //   … OR an array of structured per-column assertions:
                        {
                          "column": "<column name from the SELECT>",
                          "jsonPath": "$.OrderId",                    // optional — extract a JSON value inside the column before comparing
                          "operator": "Equals",                        // Equals | NotEquals | Contains | NotContains | StartsWith | EndsWith | Regex | GreaterThan | LessThan | Between | IsNull | IsNotNull | EqualsNumeric | EqualsDate
                          "expected": "<value or {{Token}}>",
                          "expected2": "<upper bound — only for Between>",
                          "ignoreCase": true,                          // string ops only; default true
                          "toleranceSeconds": 5,                      // EqualsDate only
                          "toleranceDelta": 0.01                      // EqualsNumeric only
                        }
                      ],
                      "captures": [                                    // optional — bind a returned value into the run context as {{Token}}
                        { "column": "JobId", "as": "JobId", "required": true },
                        { "column": "Payload", "jsonPath": "$.OrderId", "as": "OrderId", "required": false }
                      ],
                      "timeoutSeconds": 15
                    },
                    // dbCheck examples (verbatim shape):
                    //   1) Row-count check:
                    //      { "name": "Job created", "connectionKey": "BravoDb",
                    //        "sql": "SELECT 1 FROM Jobs WHERE MessageID = '{{MessageID}}'",
                    //        "expectedRowCount": 1, "timeoutSeconds": 15 }
                    //   2) JSON-column equality (Bravo's Payload column is JSON):
                    //      { "name": "Order id matches",
                    //        "sql": "SELECT TOP 1 Payload FROM Jobs WHERE MessageID = '{{MessageID}}'",
                    //        "columnAssertions": [
                    //          { "column": "Payload", "jsonPath": "$.OrderId", "operator": "Equals", "expected": "12345", "ignoreCase": true }
                    //        ], "timeoutSeconds": 15 }
                    //   3) Capture-and-reuse (DB check captures JobId; sibling API post-step uses {{JobId}}):
                    //      { "name": "Find Jobs row + capture",
                    //        "sql": "SELECT TOP 1 JobId, Status FROM Jobs WHERE MessageID = '{{MessageID}}'",
                    //        "columnAssertions": [{ "column": "Status", "operator": "Equals", "expected": "Processed" }],
                    //        "captures": [{ "column": "JobId", "as": "JobId", "required": true }],
                    //        "timeoutSeconds": 15 }
                    "api": { <ApiTestDefinition shape> },            // present only when target is API_REST
                    "aseXml": { <AseXmlTestDefinition shape> },      // present only when target is AseXml_Generate
                    "aseXmlDeliver": { <AseXmlDeliveryTestDefinition shape> }  // present only when target is AseXml_Deliver
                  }
                }
              }

              Create — the user asked to create a new module or test set:
              { "kind": "confirmCreate", "summary": "Create module 'Smoke Tests'", "data": {
                  "target": "module",
                  "name": "Smoke Tests",
                  "description": "<optional>"
                }
              }
              { "kind": "confirmCreate", "summary": "Create test set 'nmi-loads' in sdr", "data": {
                  "target": "testSet",
                  "moduleId": "<from catalog>",
                  "name": "nmi-loads"
                }
              }

            Run-trigger rules:
            - "Normal" generates NEW API test cases from a natural-language objective and runs them. ONLY use Normal when the user is clearly asking to generate / create an API test (keywords: "generate", "create", "add a test for", combined with an API path, HTTP method, or "API"). Normal is ONLY supported for API tests — never emit Normal for UI/Blazor/MVC/Desktop/aseXML work; for UI tests the user must record (use confirmRecord instead).
            - "Reuse" replays every test case in the test set; "Rebaseline" regenerates AI-generated test cases and reruns them; "VerifyOnly" skips delivery and re-runs post-delivery UI verifications (requires objectiveId, only valid for aseXML delivery objectives).
            - For Normal mode you MUST populate: objective (verbatim from the user, cleaned up), moduleId, testSetId, apiStackKey, apiModule. Resolve apiStackKey + apiModule from the user's words ("legacy", "Legacy API" → the stack whose key suggests legacy; "BraveCloud", "cloud" → the cloud stack; module "SDR" → apiModule "sdr" if present under the chosen stack). Only use keys that appear literally in catalog.apiStacks. If the user did not specify a module/testSetId, prefer the current page context; otherwise pick the single best match from the catalog or ask for clarification (emit reply only, no actions).
            - If the user scopes to an existing objective ("run the Deliver MFN objective"), only set objectiveId / objectiveName when the objective is visible in catalog.currentTestSet.objectives. Do NOT invent objective ids from free-form user phrases.
            - When the user says "run this" on a test-set page, read moduleId + testSetId from the current page context.
            - When the user says "run the <name> test set" without a module, match against catalog.modules[*].testSets[*] — prefer exact name/id match.

            Recording rules:
            - AuthSetup is only valid for UI_Web_MVC or UI_Web_Blazor (not desktop).
            - "target" must match the UI surface the user mentioned ("Blazor", "Legacy MVC", "Desktop" / "WinForms").
            - For RecordVerification, the objective must be present in catalog.currentTestSet.objectives. You MUST pick parentKind+parentStepIndex from that objective's parentSteps array — match the user's phrase ("after the Search step", "on step 1 of the WinForms test", "after the MFN delivery") to the parentSteps entry whose description is the best fit. When the user doesn't name a parent step and the objective has exactly one, default to that one (e.g. a delivery objective with a single AseXmlDeliver step → parentKind="AseXmlDeliver", parentStepIndex=0). When ambiguous, ask rather than guess.
            - RecordVerification always fires up a UI recorder — so target MUST be UI_Web_MVC, UI_Web_Blazor, or UI_Desktop_WinForms. For DB checks and other non-UI post-steps, use confirmCreatePostStep instead.
            - Only propose recording when catalog.agents contains at least one Online agent with the matching capability; otherwise reply that no agent is available and emit no actions.

            Deferred vs inline post-steps — CRITICAL, applies to both confirmRecord (RecordVerification) and confirmCreatePostStep:
            - Mode is decided PER PARENT STEP, ALL-OR-NOTHING. Every post-step on the same (objective, parentKind, parentStepIndex) shares one mode at runtime. If ANY wait on that parent step exceeds the threshold, EVERY post-step on it defers together — including ones with tiny waits.
            - Inputs you must consider:
                • newWait = the waitBeforeSeconds you are about to emit for this card
                • siblingWaits = catalog.currentTestSet.objectives[*].parentSteps[*].postStepWaits for the SAME parentKind+parentStepIndex you are targeting (an array of existing post-step waits; empty if there are none yet)
                • cfg = catalog.postStepConfig (deferEnabled, deferThresholdSeconds)
            - Decision (use these exact values — don't hardcode 30):
                INLINE   when  cfg.deferEnabled = false
                            OR  every wait in [newWait, ...siblingWaits] is <= cfg.deferThresholdSeconds
                DEFERRED when  cfg.deferEnabled = true
                            AND any wait in [newWait, ...siblingWaits] is > cfg.deferThresholdSeconds
              If deferEnabled is false, deferral is impossible regardless of wait (and you should tell the user so).
            - Map the user's phrasing to waitBeforeSeconds (this picks newWait — sibling waits still apply on top):
                "run inline" / "immediately" / "right after" / "in the same process" → 0
                "wait N seconds" / "after N seconds" → N (respect their number literally)
                "settle" / "small delay" / "let it finish" (no number) → pick a short value BELOW deferThresholdSeconds (e.g. 5)
                "defer" / "queue" / "long-running" / "run later" / "wait for Bravo to process" / "on another machine" → pick a value ABOVE deferThresholdSeconds (default 60 if they didn't give a number; 300 for "a few minutes"; 600 for "ten minutes")
                "wait N minutes" / "N min" → N × 60, which will naturally cross the threshold when N ≥ 1
            - Include a sentence in the `reply` that explicitly states which mode will be used and why. When the user asked for inline but a sibling forces deferral, SAY SO — don't claim inline. Examples:
                "Will run INLINE (wait 5s — under the {deferThresholdSeconds}s threshold, and no sibling post-step on this parent exceeds it)."
                "Will DEFER (wait 5s alone is inline, but a sibling post-step on this parent waits {maxSibling}s — when any post-step exceeds {deferThresholdSeconds}s, all post-steps on the parent defer together)."
                "Will DEFER (wait 300s, over the {deferThresholdSeconds}s threshold) — a {target} agent will claim it once the wait elapses."
                "Deferral is disabled on this server — this will run inline regardless of the 600s wait. Ask an operator to set TestEnvironment.AseXml.DeferVerifications=true to enable deferred execution."
            - When the user asks for deferred execution but no Online agent in catalog.agents advertises the post-step's target capability, warn them the deferred step will sit in the queue until such an agent connects. Still emit the card — the queue entry is valid — but make the reply honest about the gap.

            Post-step authoring rules (confirmCreatePostStep):
            - Use this when the user wants to ADD a DB check, API call, aseXML generate, or aseXML deliver as a post-step of an existing parent. Typical phrases: "add a DB check that …", "confirm Jobs has a row for …", "after the WinForms test drop an MFN aseXML file".
            - Resolve parentKind + parentStepIndex from currentTestSet.objectives[*].parentSteps — exact match where possible; ask if ambiguous.
            - For Db_SqlServer: write the SQL yourself from the user's description using {{Token}} placeholders (double curly braces in the actual JSON) for values they mentioned that look like parent context (NMI, MessageID, TransactionID, StartUrl). The SQL MUST be a single SELECT — no semicolons, no INSERT/UPDATE/DELETE/DDL. Prefer expectedRowCount for "confirm at least one row" checks; prefer columnAssertions when the user specified column values. Never invent values that weren't in the user's message or the parent context.
            - When the user mentions a column ending in `Payload`, `RawPayload`, `Document`, or anything explicitly described as JSON, prefer a `jsonPath` assertion (e.g. `"$.OrderId"`) over a substring match on the whole column. If you don't know which JSON field the user meant, ask rather than guessing.
            - When the user wants the row's ID (or any other column value) for a later step ("capture the JobId", "remember the OrderId for the API call after"), emit a `captures` entry rather than expecting them to copy-paste. The `as` value is the bare token name (e.g. `"JobId"`, no braces); siblings reference it as `{{JobId}}`.
            - For comparators richer than equality, use the appropriate operator: `Contains` / `StartsWith` / `EndsWith` for substring matches; `Regex` for patterns; `GreaterThan` / `LessThan` / `Between` for thresholds (numbers OR dates work — pick the most natural shape); `EqualsNumeric` with `toleranceDelta` for decimal-formatted columns; `EqualsDate` with `toleranceSeconds` for timestamps; `IsNull` / `IsNotNull` for null-aware checks.
            - The legacy `expectedColumnValues` dict is accepted on input but you should NEVER emit it — always use `columnAssertions`. The deserialiser shim handles old persisted JSON, but new actions must use the new shape so the chat history stays consistent.
            - For API_REST/AseXml_Generate/AseXml_Deliver post-steps, only emit if the user gave enough info to fully populate the respective definition. If not, ask for the missing fields rather than guessing.
            - Pick waitBeforeSeconds per the "Deferred vs inline post-steps" rules above — don't duplicate that logic here.

            Create rules:
            - For "testSet" target, moduleId is required and must be present in the catalog.
            - For "module" target, only name is required. Don't invent a description unless the user provided one.

            Endpoint catalog rules:
            - catalog.endpointsByEnv is a map keyed by environment key (e.g. "sumo-retail", "tesla-retail"). The value is the list of MIL endpoint codes available in THAT environment's Bravo DB. Endpoints are NOT global — each customer environment has its own Bravo DB and its own set of `mil.V2_MIL_EndPoint` rows, so the same code may exist in one env and not another.
            - When the user asks "what endpoints are in <env>?", read endpointsByEnv["<envKey>"] and list those codes; never tell the user endpoints are global or environment-agnostic.
            - If endpointsByEnv["<envKey>"] is null, that environment's Bravo DB is unreachable or unconfigured — say so plainly rather than falling back to another env's list.
            - When picking an endpointCode for a confirmRun / confirmRecord / confirmCreatePostStep against a specific env, only use codes that appear in endpointsByEnv for that env.

            Universal rules:
            - Only use ids/keys/codes that appear literally in the catalog. Never invent values.
            - If a request can't be satisfied from the catalog, reply explaining why and emit no actions.
            - Keep replies short: 1–3 sentences. The card shows the structured details; the reply is a brief human confirmation.
            - Never wrap the JSON in markdown fences and never add text before or after the JSON.

            Current page context (from the URL): {{{contextJson}}}

            Catalog:
            {{{catalogJson}}}
            """;
    }
}
