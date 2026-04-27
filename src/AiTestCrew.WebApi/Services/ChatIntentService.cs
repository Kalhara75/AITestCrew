using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.WebApi.Models.Chat;

namespace AiTestCrew.WebApi.Services;

public interface IChatIntentService
{
    Task<ChatResponse> ProcessAsync(ChatRequest request, CancellationToken ct = default);
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

    public ChatIntentService(
        Kernel kernel,
        IModuleRepository moduleRepo,
        ITestSetRepository testSetRepo,
        IEnvironmentResolver envResolver,
        IEndpointResolver endpointResolver,
        TestEnvironmentConfig cfg,
        ILogger<ChatIntentService> logger,
        IAgentRepository? agentRepo = null)
    {
        _kernel = kernel;
        _moduleRepo = moduleRepo;
        _testSetRepo = testSetRepo;
        _envResolver = envResolver;
        _endpointResolver = endpointResolver;
        _cfg = cfg;
        _logger = logger;
        _agentRepo = agentRepo;
    }

    public async Task<ChatResponse> ProcessAsync(ChatRequest request, CancellationToken ct = default)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return new ChatResponse { Reply = "Send a message to get started." };

        var catalog = await BuildCatalogAsync(request.Context, ct);
        var systemPrompt = BuildSystemPrompt(catalog, request.Context);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        foreach (var msg in request.Messages)
        {
            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                history.AddUserMessage(msg.Content);
            else if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                history.AddAssistantMessage(msg.Content);
        }

        var raw = (await chatService.GetChatMessageContentAsync(history, cancellationToken: ct)).Content ?? "";
        _logger.LogDebug("Chat LLM raw response: {Raw}", raw[..Math.Min(500, raw.Length)]);

        var parsed = LlmJsonHelper.DeserializeLlmResponse<ChatResponse>(raw);
        if (parsed is null)
        {
            _logger.LogWarning("Failed to parse chat LLM response as JSON; falling back to raw text.");
            return new ChatResponse { Reply = raw };
        }
        return parsed;
    }

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

        var endpoints = new List<string>();
        try
        {
            endpoints.AddRange(await _endpointResolver.ListCodesAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Endpoint catalog unavailable (Bravo DB connection).");
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
            endpoints,
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
                postStepCount = s.PostSteps.Count
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
                postStepCount = s.PostSteps.Count
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
                postStepCount = s.PostSteps.Count
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
                postStepCount = s.PostSteps.Count
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
                postStepCount = s.PostSteps.Count
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
                      "connectionKey": "BravoDb",        // only supported value today
                      "sql": "<single read-only SELECT; {{Tokens}} from the parent context — NMI, MessageID, StartUrl etc. — are substituted at runtime>",
                      "expectedRowCount": <optional integer>,
                      "expectedColumnValues": { <col>: "<expected value or {{Token}}>", ... }, // use one of rowCount or columnValues
                      "timeoutSeconds": 15
                    },
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
            - Every post-step carries a waitBeforeSeconds. That value AND catalog.postStepConfig together decide whether the step runs inline (same process, blocking) or deferred (queued for a remote agent to claim after the wait):
                INLINE when  catalog.postStepConfig.deferEnabled = false  OR  waitBeforeSeconds <= catalog.postStepConfig.deferThresholdSeconds
                DEFERRED when catalog.postStepConfig.deferEnabled = true  AND  waitBeforeSeconds > catalog.postStepConfig.deferThresholdSeconds
              Use these exact values — don't hardcode 30. If deferEnabled is false, deferral is impossible regardless of wait (and you should tell the user so).
            - Map the user's phrasing to waitBeforeSeconds:
                "run inline" / "immediately" / "right after" / "in the same process" → 0
                "wait N seconds" / "after N seconds" → N (respect their number literally)
                "settle" / "small delay" / "let it finish" (no number) → pick a short value BELOW deferThresholdSeconds (e.g. 5)
                "defer" / "queue" / "long-running" / "run later" / "wait for Bravo to process" / "on another machine" → pick a value ABOVE deferThresholdSeconds (default 60 if they didn't give a number; 300 for "a few minutes"; 600 for "ten minutes")
                "wait N minutes" / "N min" → N × 60, which will naturally cross the threshold when N ≥ 1
            - Include a sentence in the `reply` that explicitly states which mode will be used and why. Examples:
                "Will run INLINE (wait 5s, under the {deferThresholdSeconds}s defer threshold)."
                "Will DEFER to the queue (wait 300s, over the {deferThresholdSeconds}s threshold) — a Blazor agent will claim it once the wait elapses."
                "Deferral is disabled on this server — this will run inline regardless of the 600s wait. Ask an operator to set TestEnvironment.AseXml.DeferVerifications=true to enable deferred execution."
            - When the user asks for deferred execution but no Online agent in catalog.agents advertises the post-step's target capability, warn them the deferred step will sit in the queue until such an agent connects. Still emit the card — the queue entry is valid — but make the reply honest about the gap.

            Post-step authoring rules (confirmCreatePostStep):
            - Use this when the user wants to ADD a DB check, API call, aseXML generate, or aseXML deliver as a post-step of an existing parent. Typical phrases: "add a DB check that …", "confirm Jobs has a row for …", "after the WinForms test drop an MFN aseXML file".
            - Resolve parentKind + parentStepIndex from currentTestSet.objectives[*].parentSteps — exact match where possible; ask if ambiguous.
            - For Db_SqlServer: write the SQL yourself from the user's description using {{Token}} placeholders (double curly braces in the actual JSON) for values they mentioned that look like parent context (NMI, MessageID, TransactionID, StartUrl). The SQL MUST be a single SELECT — no semicolons, no INSERT/UPDATE/DELETE/DDL. Prefer expectedRowCount for "confirm at least one row" checks; prefer expectedColumnValues when the user specified column values. Never invent values that weren't in the user's message or the parent context.
            - For API_REST/AseXml_Generate/AseXml_Deliver post-steps, only emit if the user gave enough info to fully populate the respective definition. If not, ask for the missing fields rather than guessing.
            - Pick waitBeforeSeconds per the "Deferred vs inline post-steps" rules above — don't duplicate that logic here.

            Create rules:
            - For "testSet" target, moduleId is required and must be present in the catalog.
            - For "module" target, only name is required. Don't invent a description unless the user provided one.

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
