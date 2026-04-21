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

        // When a test set is in the current page context, surface its objectives so
        // the LLM can scope run triggers to a single objective by id / display name.
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
                        targetType = o.TargetType
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
            currentTestSet
        };
    }

    private static string BuildSystemPrompt(object catalog, ChatRequestContext? context)
    {
        var catalogJson = JsonSerializer.Serialize(catalog, LlmJsonHelper.JsonOpts);
        var contextJson = context is null ? "none" : JsonSerializer.Serialize(context, LlmJsonHelper.JsonOpts);
        return $$"""
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

              Run trigger — the user asked to run / rerun / replay / verify a test set or objective:
              { "kind": "confirmRun", "summary": "Reuse mfn-delivery on sumo-retail", "data": {
                  "mode": "Reuse" | "Rebaseline" | "VerifyOnly",
                  "moduleId": "<from catalog>",
                  "testSetId": "<from catalog>",
                  "objectiveId": "<optional, only if user scoped to one objective>",
                  "objectiveName": "<optional alternative to objectiveId>",
                  "environmentKey": "<optional, omit to use the test set's stored env or default>",
                  "apiStackKey": "<optional>",
                  "apiModule": "<optional>",
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
                  "deliveryStepIndex": <optional integer>,            // RecordVerification, default 0
                  "environmentKey": "<optional>"
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
            - Never use mode "Normal" — generating new test cases from scratch is not supported yet.
            - "Reuse" replays every test case in the test set; "Rebaseline" regenerates AI-generated test cases and reruns them; "VerifyOnly" skips delivery and re-runs post-delivery UI verifications (requires objectiveId, only valid for aseXML delivery objectives).
            - If the user scopes to an objective ("run the Deliver MFN objective"), only set objectiveId / objectiveName when the objective is visible in catalog.currentTestSet.objectives. Do NOT invent objective ids from free-form user phrases.
            - When the user says "run this" on a test-set page, read moduleId + testSetId from the current page context.
            - When the user says "run the <name> test set" without a module, match against catalog.modules[*].testSets[*] — prefer exact name/id match.

            Recording rules:
            - AuthSetup is only valid for UI_Web_MVC or UI_Web_Blazor (not desktop).
            - "target" must match the UI surface the user mentioned ("Blazor", "Legacy MVC", "Desktop" / "WinForms").
            - For RecordVerification, the objective must be present in catalog.currentTestSet.objectives AND have AseXml delivery steps (the card server will validate). Only offer it when the user is on a test-set page that contains a delivery objective.
            - Only propose recording when catalog.agents contains at least one Online agent with the matching capability; otherwise reply that no agent is available and emit no actions.

            Create rules:
            - For "testSet" target, moduleId is required and must be present in the catalog.
            - For "module" target, only name is required. Don't invent a description unless the user provided one.

            Universal rules:
            - Only use ids/keys/codes that appear literally in the catalog. Never invent values.
            - If a request can't be satisfied from the catalog, reply explaining why and emit no actions.
            - Keep replies short: 1–3 sentences. The card shows the structured details; the reply is a brief human confirmation.
            - Never wrap the JSON in markdown fences and never add text before or after the JSON.

            Current page context (from the URL): {{contextJson}}

            Catalog:
            {{catalogJson}}
            """;
    }
}
