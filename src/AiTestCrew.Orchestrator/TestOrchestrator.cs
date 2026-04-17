using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.AseXmlAgent;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;
using AiTestCrew.Core.Services;

namespace AiTestCrew.Orchestrator;

/// <summary>
/// Orchestrator: takes a natural language objective, decomposes it into tasks,
/// routes each to the right agent, and aggregates results.
///
/// Each user objective produces ONE TestObjective in persistence.
/// The agent-generated test cases (API calls, UI flows) become steps within that objective.
/// </summary>
public class TestOrchestrator
{
    private readonly List<ITestAgent> _agents;
    private readonly Kernel _kernel;
    private readonly TestEnvironmentConfig _config;
    private readonly ITestSetRepository _testSetRepo;
    private readonly IExecutionHistoryRepository _historyRepo;
    private readonly IModuleRepository _moduleRepo;
    private readonly AgentConcurrencyLimiter _concurrencyLimiter;
    private readonly ILogger<TestOrchestrator> _logger;

    public TestOrchestrator(
        IEnumerable<ITestAgent> agents,
        Kernel kernel,
        TestEnvironmentConfig config,
        ITestSetRepository testSetRepo,
        IExecutionHistoryRepository historyRepo,
        IModuleRepository moduleRepo,
        AgentConcurrencyLimiter concurrencyLimiter,
        ILogger<TestOrchestrator> logger)
    {
        _agents = agents.ToList();
        _kernel = kernel;
        _config = config;
        _testSetRepo = testSetRepo;
        _historyRepo = historyRepo;
        _moduleRepo = moduleRepo;
        _concurrencyLimiter = concurrencyLimiter;
        _logger = logger;
    }

    /// <summary>
    /// Run the full test flow: decompose → route → execute → report.
    /// </summary>
    public async Task<TestSuiteResult> RunAsync(
        string objective,
        RunMode mode = RunMode.Normal,
        string? reuseId = null,
        CancellationToken ct = default,
        string? externalRunId = null,
        string? moduleId = null,
        string? targetTestSetId = null,
        string? objectiveName = null,
        string? objectiveId = null,
        string? apiStackKey = null,
        string? apiModule = null,
        string? endpointCode = null,
        int? verificationWaitOverride = null)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        var isModuleScoped = !string.IsNullOrEmpty(moduleId) && !string.IsNullOrEmpty(targetTestSetId);

        _logger.LogInformation("╔══════════════════════════════════════════╗");
        _logger.LogInformation("║  AI TEST CREW - Starting Test Run        ║");
        _logger.LogInformation("╚══════════════════════════════════════════╝");
        _logger.LogInformation("Objective: {Obj}", objective);
        _logger.LogInformation("Mode: {Mode}", mode);
        if (isModuleScoped)
            _logger.LogInformation("Module: {Module}, TestSet: {TestSet}", moduleId, targetTestSetId);
        _logger.LogInformation("Available agents: {Agents}",
            string.Join(", ", _agents.Select(a => a.Name)));

        List<TestTask> tasks;

        // ── Reuse / VerifyOnly mode: load saved test set, skip LLM decomposition ──
        if (mode is RunMode.Reuse or RunMode.VerifyOnly)
        {
            PersistedTestSet? saved;
            if (isModuleScoped)
            {
                saved = await _testSetRepo.LoadAsync(moduleId!, targetTestSetId!);
                reuseId = targetTestSetId;
            }
            else
            {
                saved = await _testSetRepo.LoadAsync(reuseId!);
            }

            if (saved is null)
            {
                var available = _testSetRepo.ListAll();
                var availableIds = available.Count > 0
                    ? string.Join(", ", available.Select(s => s.Id))
                    : "(none)";
                var errorMsg = $"Test set '{reuseId ?? targetTestSetId}' not found. Available IDs: {availableIds}";
                _logger.LogError(errorMsg);
                return new TestSuiteResult
                {
                    Objective = objective,
                    Results =
                    [
                        new TestResult
                        {
                            ObjectiveId = "n/a",
                            ObjectiveName = "Error",
                            AgentName = "Orchestrator",
                            Status = TestStatus.Error,
                            Summary = errorMsg
                        }
                    ],
                    Summary = errorMsg,
                    TotalDuration = sw.Elapsed
                };
            }

            // Use the original objective stored in the test set
            objective = saved.Objective;
            _logger.LogInformation("Reuse mode: loaded test set '{Id}' (run #{Run}, {Count} objectives)",
                saved.Id, saved.RunCount + 1, saved.TestObjectives.Count);

            // Build tasks from saved objectives — one task per objective, injecting its steps
            tasks = saved.TestObjectives.Select(obj =>
            {
                var targetType = Enum.TryParse<TestTargetType>(obj.TargetType, out var t)
                    ? t : TestTargetType.API_REST;

                var parameters = new Dictionary<string, object>();
                if (obj.AseXmlDeliverySteps.Count > 0)
                    parameters["PreloadedTestCases"] = obj.AseXmlDeliverySteps
                        .Select(s => s.ToTestCase(s.Description)).ToList();
                else if (obj.AseXmlSteps.Count > 0)
                    parameters["PreloadedTestCases"] = obj.AseXmlSteps
                        .Select(s => s.ToTestCase(s.Description)).ToList();
                else if (obj.DesktopUiSteps.Count > 0)
                    parameters["PreloadedTestCases"] = obj.DesktopUiSteps
                        .Select(s => s.ToTestCase(s.Description)).ToList();
                else if (obj.WebUiSteps.Count > 0)
                    parameters["PreloadedTestCases"] = obj.WebUiSteps
                        .Select(s => s.ToTestCase(s.Description)).ToList();
                else
                    parameters["PreloadedTestCases"] = obj.ApiSteps
                        .Select(s => s.ToTestCase("")).ToList();

                // Inject test-set-level setup steps (e.g. login) so the agent
                // can run them before each test case.
                if (saved.SetupSteps.Count > 0)
                {
                    parameters["SetupSteps"] = saved.SetupSteps;
                    parameters["SetupStartUrl"] = saved.SetupStartUrl;
                }

                // Inject API stack+module: CLI flags override persisted values
                var effectiveStack = apiStackKey ?? saved.ApiStackKey;
                var effectiveModule = apiModule ?? saved.ApiModule;
                if (effectiveStack is not null) parameters["ApiStackKey"] = effectiveStack;
                if (effectiveModule is not null) parameters["ApiModule"] = effectiveModule;

                // Inject aseXML delivery endpoint: CLI override > saved default
                var effectiveEndpoint = !string.IsNullOrWhiteSpace(endpointCode)
                    ? endpointCode
                    : saved.EndpointCode;
                if (!string.IsNullOrWhiteSpace(effectiveEndpoint))
                    parameters["EndpointCode"] = effectiveEndpoint!;

                return new TestTask
                {
                    Id = obj.Id,
                    Description = obj.Name,
                    Target = targetType,
                    Parameters = parameters
                };
            }).ToList();

            // ── VerifyOnly: inject extra parameters so the delivery agent skips phases 1-4 ──
            if (mode == RunMode.VerifyOnly)
            {
                foreach (var t in tasks)
                {
                    t.Parameters["VerifyOnly"] = true;
                    t.Parameters["TestSetId"] = targetTestSetId ?? reuseId ?? "";
                    if (!string.IsNullOrEmpty(moduleId))
                        t.Parameters["ModuleId"] = moduleId;
                    if (verificationWaitOverride.HasValue)
                        t.Parameters["VerificationWaitOverride"] = verificationWaitOverride.Value;
                }
            }

            // ── Single-objective filter: run only one objective from the set ──
            // Matches on Id (slug) first; falls back to Description (display name)
            // case-insensitively so users can pass either form on the CLI.
            if (!string.IsNullOrEmpty(objectiveId))
            {
                var filtered = tasks
                    .Where(t => string.Equals(t.Id, objectiveId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (filtered.Count == 0)
                {
                    filtered = tasks
                        .Where(t => string.Equals(t.Description, objectiveId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                tasks = filtered;

                if (tasks.Count == 0)
                {
                    var available = saved.TestObjectives.Count > 0
                        ? string.Join("; ", saved.TestObjectives.Select(o =>
                            string.IsNullOrWhiteSpace(o.Name) || o.Name == o.Id
                                ? o.Id
                                : $"{o.Id} (\"{o.Name}\")"))
                        : "(none)";
                    var errorMsg = $"Objective '{objectiveId}' not found in test set '{reuseId ?? targetTestSetId}'. Available: {available}";
                    _logger.LogError(errorMsg);
                    return new TestSuiteResult
                    {
                        Objective = objective,
                        Results =
                        [
                            new TestResult
                            {
                                ObjectiveId = objectiveId,
                                ObjectiveName = "Error",
                                AgentName = "Orchestrator",
                                Status = TestStatus.Error,
                                Summary = errorMsg
                            }
                        ],
                        Summary = errorMsg,
                        TotalDuration = sw.Elapsed
                    };
                }
                _logger.LogInformation("Single-objective mode: running only '{ObjId}'", objectiveId);
            }
        }
        else
        {
            // ── Normal / Rebaseline: decompose via LLM ──
            tasks = await DecomposeObjectiveAsync(objective, ct);

            // Inject API stack+module into each task so the agent targets the right stack
            foreach (var t in tasks)
            {
                if (apiStackKey is not null) t.Parameters["ApiStackKey"] = apiStackKey;
                if (apiModule is not null) t.Parameters["ApiModule"] = apiModule;
                if (!string.IsNullOrWhiteSpace(endpointCode)) t.Parameters["EndpointCode"] = endpointCode;
            }

            _logger.LogInformation("Decomposed into {Count} tasks:", tasks.Count);
            foreach (var t in tasks)
            {
                _logger.LogInformation("  [{Id}] {Target}: {Desc}",
                    t.Id, t.Target, t.Description);
            }
        }

        // ── Execute tasks (parallel, throttled by MaxParallelAgents) ──
        // Each agent returns ONE TestResult per task with N steps inside.
        // The global AgentConcurrencyLimiter caps how many agents run at once.
        var indexedResults = new ConcurrentBag<(int Index, TestResult Result)>();
        int endpointUnreachable = 0; // 0 = false, 1 = true (for Interlocked)

        var executionTasks = tasks.Select((task, index) => Task.Run(async () =>
        {
            await _concurrencyLimiter.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var agent = await FindAgentAsync(task);
                if (agent is null)
                {
                    _logger.LogWarning("No agent can handle task [{Id}] ({Target})",
                        task.Id, task.Target);
                    indexedResults.Add((index, new TestResult
                    {
                        ObjectiveId = task.Id,
                        ObjectiveName = task.Description,
                        AgentName = "None",
                        Status = TestStatus.Skipped,
                        Summary = $"No agent available for target type: {task.Target}"
                    }));
                    return;
                }

                // ── Fail-fast: if another task found all endpoints 404, skip (best-effort) ──
                if (Volatile.Read(ref endpointUnreachable) == 1
                    && task.Target is TestTargetType.API_REST
                                    or TestTargetType.API_GraphQL)
                {
                    _logger.LogWarning(
                        "Skipping [{Id}] — base URL appears unreachable (all prior API calls returned 404)",
                        task.Id);
                    indexedResults.Add((index, new TestResult
                    {
                        ObjectiveId   = task.Id,
                        ObjectiveName = task.Description,
                        AgentName     = agent.Name,
                        Status        = TestStatus.Skipped,
                        Summary       = "Skipped: base URL appears unreachable — check ApiBaseUrl in appsettings.json"
                    }));
                    return;
                }

                _logger.LogInformation("Routing [{Id}] -> {Agent}", task.Id, agent.Name);

                using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                taskCts.CancelAfter(task.Timeout);

                try
                {
                    var result = await agent.ExecuteAsync(task, taskCts.Token);
                    indexedResults.Add((index, result));

                    _logger.LogInformation("[{Id}] Result: {Status} ({Passed}/{Total} steps passed)",
                        task.Id, result.Status, result.PassedSteps, result.Steps.Count);

                    // Detect consistent 404
                    if (result.Status is TestStatus.Failed or TestStatus.Error
                        && result.PassedSteps == 0
                        && result.Steps.Count > 0)
                    {
                        var notFoundCount = result.Steps.Count(s =>
                            s.Summary.Contains("got 404", StringComparison.OrdinalIgnoreCase) ||
                            s.Detail?.Contains("404", StringComparison.OrdinalIgnoreCase) == true);

                        if (notFoundCount >= (result.Steps.Count * 0.75))
                        {
                            Interlocked.Exchange(ref endpointUnreachable, 1);
                            _logger.LogWarning(
                                "All test steps returned 404 — subsequent API tasks will be skipped. " +
                                "Verify ApiStacks configuration is correct.");
                        }
                    }
                }
                catch (OperationCanceledException) when (taskCts.IsCancellationRequested)
                {
                    indexedResults.Add((index, new TestResult
                    {
                        ObjectiveId   = task.Id,
                        ObjectiveName = task.Description,
                        AgentName     = agent.Name,
                        Status        = TestStatus.Error,
                        Summary       = $"Task timed out after {task.Timeout.TotalSeconds}s"
                    }));
                }
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        })).ToArray();

        await Task.WhenAll(executionTasks);

        // Reassemble results in original order for deterministic output
        var results = indexedResults.OrderBy(r => r.Index).Select(r => r.Result).ToList();

        // ── Persist test set ──
        if (mode is RunMode.Normal or RunMode.Rebaseline)
        {
            if (isModuleScoped)
                await SaveTestSetToModuleAsync(objective, results, moduleId!, targetTestSetId!, mode, objectiveName, apiStackKey, apiModule, endpointCode);
            else
                await SaveTestSetAsync(objective, results, objectiveName, apiStackKey, apiModule, endpointCode);
        }

        // ── Update run statistics (Reuse / VerifyOnly mode) ──
        if (mode is RunMode.Reuse or RunMode.VerifyOnly)
        {
            if (isModuleScoped)
                await _testSetRepo.UpdateRunStatsAsync(moduleId!, targetTestSetId!);
            else if (reuseId is not null)
                await _testSetRepo.UpdateRunStatsAsync(reuseId);
        }

        // ── Generate summary ──
        var summary = await GenerateSummaryAsync(objective, results, ct);

        _logger.LogInformation("╔══════════════════════════════════════════╗");
        _logger.LogInformation("║  TEST RUN COMPLETE                       ║");
        _logger.LogInformation("╚══════════════════════════════════════════╝");
        _logger.LogInformation("Duration: {Dur}", sw.Elapsed);

        var suiteResult = new TestSuiteResult
        {
            Objective = objective,
            Results = results,
            Summary = summary,
            TotalDuration = sw.Elapsed
        };

        // ── Persist execution history (all modes) ──
        try
        {
            var testSetId = isModuleScoped
                ? targetTestSetId!
                : (mode is RunMode.Reuse or RunMode.VerifyOnly) && reuseId is not null
                    ? reuseId
                    : SlugHelper.ToSlug(objective);
            var executionRun = PersistedExecutionRun.FromSuiteResult(
                suiteResult, testSetId, mode, startedAt, isModuleScoped ? moduleId : null);
            if (externalRunId is not null)
                executionRun.RunId = externalRunId;
            await _historyRepo.SaveAsync(executionRun);
            _logger.LogInformation("Execution history saved: {RunId}", executionRun.RunId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save execution history (non-fatal)");
        }

        return suiteResult;
    }

    // ─────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────

    /// <summary>Save test set to legacy flat directory.</summary>
    private async Task SaveTestSetAsync(
        string objective, List<TestResult> results,
        string? objectiveName = null,
        string? apiStackKey = null, string? apiModule = null,
        string? endpointCode = null)
    {
        var testObjective = BuildObjectiveFromResults(objective, results, objectiveName);
        if (testObjective is null)
        {
            _logger.LogWarning("No test cases were generated — test set will not be saved.");
            return;
        }

        var slug = SlugHelper.ToSlug(objective);
        var testSet = new PersistedTestSet
        {
            Id = slug,
            Name = objective,
            ModuleId = "",
            SchemaVersion = 2,
            Objectives = [objective],
            CreatedAt = DateTime.UtcNow,
            LastRunAt = DateTime.UtcNow,
            RunCount = 1,
            TestObjectives = [testObjective],
            ApiStackKey = apiStackKey,
            ApiModule = apiModule,
            EndpointCode = string.IsNullOrWhiteSpace(endpointCode) ? null : endpointCode
        };

        if (!string.IsNullOrWhiteSpace(objectiveName))
            testSet.ObjectiveNames[objective] = objectiveName;

        await _testSetRepo.SaveAsync(testSet);
        _logger.LogInformation("Test set saved: {Id}", slug);
    }

    /// <summary>Save/merge test set into a module directory.</summary>
    private async Task SaveTestSetToModuleAsync(
        string objective, List<TestResult> results,
        string moduleId, string testSetId, RunMode mode,
        string? objectiveName = null,
        string? apiStackKey = null, string? apiModule = null,
        string? endpointCode = null)
    {
        var testObjective = BuildObjectiveFromResults(objective, results, objectiveName);
        if (testObjective is null)
        {
            _logger.LogWarning("No test cases were generated — test set will not be updated.");
            return;
        }

        if (mode == RunMode.Rebaseline)
        {
            var existing = await _testSetRepo.LoadAsync(moduleId, testSetId);

            // Keep objectives from other user objectives
            var objectivesFromOthers = existing?.TestObjectives
                .Where(o => !string.Equals(o.ParentObjective, objective, StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];

            var userObjectives = existing?.Objectives.ToList() ?? [];
            if (!userObjectives.Contains(objective, StringComparer.OrdinalIgnoreCase))
                userObjectives.Add(objective);

            var objectiveNames = existing?.ObjectiveNames ?? new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(objectiveName))
                objectiveNames[objective] = objectiveName;

            var testSet = new PersistedTestSet
            {
                Id = testSetId,
                Name = existing?.Name ?? testSetId,
                ModuleId = moduleId,
                SchemaVersion = 2,
                Objectives = userObjectives,
                ObjectiveNames = objectiveNames,
                CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
                LastRunAt = DateTime.UtcNow,
                RunCount = (existing?.RunCount ?? 0) + 1,
                TestObjectives = [..objectivesFromOthers, testObjective],
                ApiStackKey = apiStackKey ?? existing?.ApiStackKey,
                ApiModule = apiModule ?? existing?.ApiModule,
                EndpointCode = !string.IsNullOrWhiteSpace(endpointCode) ? endpointCode : existing?.EndpointCode
            };
            await _testSetRepo.SaveAsync(testSet, moduleId);
            _logger.LogInformation("Test set rebaselined (objective: {Obj}): {Module}/{Id}",
                objective, moduleId, testSetId);
        }
        else
        {
            // Normal: merge into existing test set
            await _testSetRepo.MergeObjectivesAsync(moduleId, testSetId, [testObjective], objective, objectiveName, apiStackKey, apiModule, endpointCode);
            _logger.LogInformation("Test objective merged into: {Module}/{Id}", moduleId, testSetId);
        }
    }

    /// <summary>
    /// Builds ONE TestObjective from all agent results for a given user objective.
    /// All generated test cases across all tasks become steps within this objective.
    /// </summary>
    private static TestObjective? BuildObjectiveFromResults(
        string objective, List<TestResult> results, string? objectiveName)
    {
        var apiSteps = new List<ApiTestDefinition>();
        var webUiSteps = new List<WebUiTestDefinition>();
        var desktopUiSteps = new List<DesktopUiTestDefinition>();
        var aseXmlSteps = new List<AseXmlTestDefinition>();
        var aseXmlDeliverySteps = new List<AseXmlDeliveryTestDefinition>();
        var agentName = "";
        var targetType = "API_REST";

        foreach (var r in results)
        {
            agentName = r.AgentName;

            if (r.Metadata.TryGetValue("generatedTestCases", out var v))
            {
                if (v is List<ApiTestCase> apiCases)
                {
                    targetType = "API_REST";
                    foreach (var tc in apiCases)
                        apiSteps.Add(ApiTestDefinition.FromTestCase(tc));
                }
                else if (v is List<WebUiTestCase> uiCases)
                {
                    targetType = "UI_Web_MVC";
                    foreach (var tc in uiCases)
                        webUiSteps.Add(WebUiTestDefinition.FromTestCase(tc));
                }
                else if (v is List<DesktopUiTestCase> desktopCases)
                {
                    targetType = "UI_Desktop_WinForms";
                    foreach (var tc in desktopCases)
                        desktopUiSteps.Add(DesktopUiTestDefinition.FromTestCase(tc));
                }
                else if (v is List<AseXmlDeliveryTestCase> deliveryCases)
                {
                    targetType = "AseXml_Deliver";
                    foreach (var tc in deliveryCases)
                        aseXmlDeliverySteps.Add(AseXmlDeliveryTestDefinition.FromTestCase(tc));
                }
                else if (v is List<AseXmlDeliveryTestDefinition> deliveryDefs)
                {
                    targetType = "AseXml_Deliver";
                    aseXmlDeliverySteps.AddRange(deliveryDefs);
                }
                else if (v is List<AseXmlTestCase> xmlCases)
                {
                    targetType = "AseXml_Generate";
                    foreach (var tc in xmlCases)
                        aseXmlSteps.Add(AseXmlTestDefinition.FromTestCase(tc));
                }
                else if (v is List<AseXmlTestDefinition> xmlDefs)
                {
                    // Agent already returned definitions (preferred) — use as-is.
                    targetType = "AseXml_Generate";
                    aseXmlSteps.AddRange(xmlDefs);
                }
            }
        }

        if (apiSteps.Count == 0 && webUiSteps.Count == 0 && desktopUiSteps.Count == 0
            && aseXmlSteps.Count == 0 && aseXmlDeliverySteps.Count == 0)
            return null;

        var displayName = !string.IsNullOrWhiteSpace(objectiveName)
            ? objectiveName
            : objective.Length <= 80
                ? objective
                : string.Concat(objective.AsSpan(0, 77), "...");

        return new TestObjective
        {
            Id = SlugHelper.ToSlug(objective),
            Name = displayName,
            ParentObjective = objective,
            AgentName = agentName,
            TargetType = targetType,
            ApiSteps = apiSteps,
            WebUiSteps = webUiSteps,
            DesktopUiSteps = desktopUiSteps,
            AseXmlSteps = aseXmlSteps,
            AseXmlDeliverySteps = aseXmlDeliverySteps
        };
    }

    // ─────────────────────────────────────────────────────
    // LLM helpers
    // ─────────────────────────────────────────────────────

    private async Task<List<TestTask>> DecomposeObjectiveAsync(
        string objective, CancellationToken ct)
    {
        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();

        history.AddSystemMessage("""
            You are a senior QA architect. You decompose test objectives
            into specific, actionable test tasks.
            Always respond with a valid JSON array and nothing else.
            """);

        history.AddUserMessage($$"""
            Decompose this test objective into tasks:
            "{{objective}}"

            Rules:
            - Generate a MAXIMUM of 5 tasks. If the objective targets a SINGLE endpoint,
              generate exactly 1 task (do not split it into sub-tasks per concern).
            - Only generate multiple tasks if the objective explicitly mentions multiple
              DIFFERENT endpoints or fundamentally different concerns.
            - Do NOT generate separate tasks for auth checks, schema validation,
              performance, or header checks — those are sub-concerns handled within
              a single task.

            Available target types (use these EXACT values):
            - API_REST: REST API endpoint testing
            - UI_Web_MVC: ASP.NET MVC page testing
            - UI_Web_Blazor: Blazor component testing
            - UI_Desktop_WinForms: WinForms desktop app testing
            - BackgroundJob_Hangfire: Hangfire job testing
            - MessageBus: Message bus pub/sub testing
            - Database: Direct database validation
            - AseXml_Generate: Generate an AEMO aseXML transaction payload (e.g.
              MeterFaultAndIssueNotification, CustomerDetailsNotification) from a
              named template + user-supplied field values. Choose this when the
              objective only asks to produce/render the XML (no transmission).
            - AseXml_Deliver: Generate AND transmit an aseXML payload to a Bravo
              endpoint named in the objective (e.g. "deliver/send/transmit to
              GatewaySPARQ"). Picks the endpoint from mil.V2_MIL_EndPoint by code.
              Choose this over AseXml_Generate whenever the objective names an
              endpoint or uses verbs like "deliver", "send", "drop", "transmit",
              "submit", "push" for an aseXML transaction.

            For Phase 1, focus on API_REST tasks.
            If the objective mentions database, add a Database task but mark it
            as a separate item — it will be skipped if no DB agent is available.

            Respond with JSON (array of 1–5 items only):
            [
              {
                "description": "clear description of what to test",
                "target": "API_REST",
                "priority": "Normal",
                "parameters": {
                  "endpoints": ["/api/..."],
                  "authRequired": false
                }
              }
            ]
            """);

        var response = await chatService.GetChatMessageContentAsync(
            history, cancellationToken: ct);

        var raw = response.Content ?? "[]";
        var cleaned = CleanJson(raw);

        try
        {
            var parsed = JsonSerializer.Deserialize<List<TaskDto>>(
                cleaned, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return parsed?.Select(dto => new TestTask
            {
                Description = dto.Description,
                Target = Enum.TryParse<TestTargetType>(dto.Target, true, out var t)
                    ? t : TestTargetType.API_REST,
                Priority = Enum.TryParse<TestPriority>(dto.Priority, true, out var p)
                    ? p : TestPriority.Normal,
                Parameters = dto.Parameters ?? []
            }).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError("Failed to parse decomposed tasks: {Err}\nRaw: {Raw}",
                ex.Message, cleaned);
            return
            [
                new TestTask
                {
                    Description = objective,
                    Target = TestTargetType.API_REST,
                }
            ];
        }
    }

    private async Task<ITestAgent?> FindAgentAsync(TestTask task)
    {
        foreach (var agent in _agents)
        {
            if (await agent.CanHandleAsync(task))
                return agent;
        }
        return null;
    }

    private async Task<string> GenerateSummaryAsync(
        string objective, List<TestResult> results, CancellationToken ct)
    {
        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();

        history.AddSystemMessage("You are a test results analyst. Be concise.");

        var resultsSummary = string.Join("\n", results.Select(r =>
            $"- [{r.Status}] {r.AgentName}: {r.Summary} ({r.PassedSteps}/{r.Steps.Count} steps)"));

        history.AddUserMessage($"""
            Summarise this test run in 3-5 sentences:
            Objective: "{objective}"
            Results:
            {resultsSummary}

            Include: overall pass/fail, key findings, recommended actions.
            Respond with plain text.
            """);

        var response = await chatService.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        return response.Content ?? "Summary generation failed.";
    }

    private static string CleanJson(string raw)
    {
        var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();
        var first = cleaned.IndexOfAny(['{', '[']);
        var last = cleaned.LastIndexOfAny(['}', ']']);
        return (first >= 0 && last > first)
            ? cleaned[first..(last + 1)]
            : cleaned;
    }

    private record TaskDto(
        string Description,
        string Target,
        string Priority,
        Dictionary<string, object>? Parameters);
}
