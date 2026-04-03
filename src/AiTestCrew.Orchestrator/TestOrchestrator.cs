using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Orchestrator;

/// <summary>
/// Phase 1 orchestrator: takes a natural language objective,
/// decomposes it into tasks, routes each to the right agent,
/// and aggregates results.
///
/// In later phases, this will handle parallel execution,
/// dependency ordering, retry logic, and adaptive re-planning.
/// </summary>
public class TestOrchestrator
{
    private readonly List<ITestAgent> _agents;
    private readonly Kernel _kernel;
    private readonly TestEnvironmentConfig _config;
    private readonly ILogger<TestOrchestrator> _logger;

    public TestOrchestrator(
        IEnumerable<ITestAgent> agents,
        Kernel kernel,
        TestEnvironmentConfig config,
        ILogger<TestOrchestrator> logger)
    {
        _agents = agents.ToList();
        _kernel = kernel;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Run the full test flow: decompose → route → execute → report.
    /// </summary>
    public async Task<TestSuiteResult> RunAsync(
        string objective, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("╔══════════════════════════════════════════╗");
        _logger.LogInformation("║  AI TEST CREW - Starting Test Run        ║");
        _logger.LogInformation("╚══════════════════════════════════════════╝");
        _logger.LogInformation("Objective: {Obj}", objective);
        _logger.LogInformation("Available agents: {Agents}",
            string.Join(", ", _agents.Select(a => a.Name)));

        // ── 1. Decompose the objective into tasks ──
        var tasks = await DecomposeObjectiveAsync(objective, ct);
        _logger.LogInformation("Decomposed into {Count} tasks:", tasks.Count);
        foreach (var t in tasks)
        {
            _logger.LogInformation("  [{Id}] {Target}: {Desc}",
                t.Id, t.Target, t.Description);
        }

        // ── 2. Execute each task (sequential in Phase 1) ──
        var results = new List<TestResult>();
        bool endpointUnreachable = false; // fail-fast flag

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();

            var agent = await FindAgentAsync(task);
            if (agent is null)
            {
                _logger.LogWarning("No agent can handle task [{Id}] ({Target})",
                    task.Id, task.Target);
                results.Add(new TestResult
                {
                    TaskId = task.Id,
                    AgentName = "None",
                    Status = TestStatus.Skipped,
                    Summary = $"No agent available for target type: {task.Target}"
                });
                continue;
            }

            // ── Fail-fast: if a previous API task found all endpoints 404, skip ──
            if (endpointUnreachable && task.Target is TestTargetType.API_REST
                                                    or TestTargetType.API_GraphQL)
            {
                _logger.LogWarning(
                    "Skipping [{Id}] — base URL appears unreachable (all prior API calls returned 404)",
                    task.Id);
                results.Add(new TestResult
                {
                    TaskId    = task.Id,
                    AgentName = agent.Name,
                    Status    = TestStatus.Skipped,
                    Summary   = "Skipped: base URL appears unreachable — check ApiBaseUrl in appsettings.json"
                });
                continue;
            }

            _logger.LogInformation("Routing [{Id}] -> {Agent}", task.Id, agent.Name);

            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            taskCts.CancelAfter(task.Timeout);

            try
            {
                var result = await agent.ExecuteAsync(task, taskCts.Token);
                results.Add(result);

                _logger.LogInformation("[{Id}] Result: {Status} ({Passed}/{Total} steps passed)",
                    task.Id, result.Status, result.PassedSteps, result.Steps.Count);

                // Detect consistent 404: if >75% of executed steps mention "got 404"
                // and 0 steps passed, the base URL is almost certainly wrong.
                if (result.Status is TestStatus.Failed or TestStatus.Error
                    && result.PassedSteps == 0
                    && result.Steps.Count > 0)
                {
                    var notFoundCount = result.Steps.Count(s =>
                        s.Summary.Contains("got 404", StringComparison.OrdinalIgnoreCase) ||
                        s.Detail?.Contains("404", StringComparison.OrdinalIgnoreCase) == true);

                    if (notFoundCount >= (result.Steps.Count * 0.75))
                    {
                        endpointUnreachable = true;
                        _logger.LogWarning(
                            "All test steps returned 404 — subsequent API tasks will be skipped. " +
                            "Verify ApiBaseUrl ({Url}) is correct.", _config.ApiBaseUrl);
                    }
                }
            }
            catch (OperationCanceledException) when (taskCts.IsCancellationRequested)
            {
                results.Add(new TestResult
                {
                    TaskId    = task.Id,
                    AgentName = agent.Name,
                    Status    = TestStatus.Error,
                    Summary   = $"Task timed out after {task.Timeout.TotalSeconds}s"
                });
            }
        }

        // ── 3. Generate summary ──
        var summary = await GenerateSummaryAsync(objective, results, ct);

        _logger.LogInformation("╔══════════════════════════════════════════╗");
        _logger.LogInformation("║  TEST RUN COMPLETE                       ║");
        _logger.LogInformation("╚══════════════════════════════════════════╝");
        _logger.LogInformation("Duration: {Dur}", sw.Elapsed);

        return new TestSuiteResult
        {
            Objective = objective,
            Results = results,
            Summary = summary,
            TotalDuration = sw.Elapsed
        };
    }

    /// <summary>
    /// Use the LLM to decompose a natural language objective
    /// into concrete, typed test tasks.
    /// </summary>
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
            // Fallback: treat the entire objective as one API task
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

    // DTO for deserialising the LLM's decomposed tasks
    private record TaskDto(
        string Description,
        string Target,
        string Priority,
        Dictionary<string, object>? Parameters);
}