using System.Text.Json;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Orchestrator;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTestCrew.WebApi.Endpoints;

public static class ModuleEndpoints
{
    public static RouteGroupBuilder MapModuleEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/modules — list all modules with test set counts
        group.MapGet("/", async (ModuleRepository moduleRepo, TestSetRepository tsRepo,
            ExecutionHistoryRepository historyRepo) =>
        {
            var modules = await moduleRepo.ListAllAsync();
            var result = modules.Select(m =>
            {
                var testSets = tsRepo.ListByModule(m.Id);
                var totalCases = testSets.Sum(ts => ts.Tasks.Sum(t => t.TestCases.Count));
                return new
                {
                    m.Id,
                    m.Name,
                    m.Description,
                    m.CreatedAt,
                    m.UpdatedAt,
                    TestSetCount = testSets.Count,
                    TotalTestCases = totalCases
                };
            });
            return Results.Ok(result);
        });

        // POST /api/modules — create a module
        group.MapPost("/", async (CreateModuleRequest request, ModuleRepository moduleRepo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required" });

            var id = SlugHelper.ToSlug(request.Name);
            if (moduleRepo.Exists(id))
                return Results.Conflict(new { error = $"Module '{id}' already exists" });

            var module = await moduleRepo.CreateAsync(request.Name, request.Description);
            return Results.Created($"/api/modules/{module.Id}", module);
        });

        // GET /api/modules/{moduleId} — module detail
        group.MapGet("/{moduleId}", async (string moduleId, ModuleRepository moduleRepo,
            TestSetRepository tsRepo) =>
        {
            var module = await moduleRepo.GetAsync(moduleId);
            if (module is null) return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            var testSets = tsRepo.ListByModule(moduleId);
            return Results.Ok(new
            {
                module.Id,
                module.Name,
                module.Description,
                module.CreatedAt,
                module.UpdatedAt,
                TestSetCount = testSets.Count,
                TotalTestCases = testSets.Sum(ts => ts.Tasks.Sum(t => t.TestCases.Count))
            });
        });

        // PUT /api/modules/{moduleId} — update module
        group.MapPut("/{moduleId}", async (string moduleId, UpdateModuleRequest request,
            ModuleRepository moduleRepo) =>
        {
            var module = await moduleRepo.GetAsync(moduleId);
            if (module is null) return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            if (!string.IsNullOrWhiteSpace(request.Name)) module.Name = request.Name;
            if (request.Description is not null) module.Description = request.Description;

            await moduleRepo.UpdateAsync(module);
            return Results.Ok(module);
        });

        // DELETE /api/modules/{moduleId} — delete empty module
        group.MapDelete("/{moduleId}", async (string moduleId, ModuleRepository moduleRepo) =>
        {
            if (!moduleRepo.Exists(moduleId))
                return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            try
            {
                await moduleRepo.DeleteAsync(moduleId);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // GET /api/modules/{moduleId}/testsets — list test sets in module
        group.MapGet("/{moduleId}/testsets", (string moduleId, ModuleRepository moduleRepo,
            TestSetRepository tsRepo, ExecutionHistoryRepository historyRepo) =>
        {
            if (!moduleRepo.Exists(moduleId))
                return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            var testSets = tsRepo.ListByModule(moduleId);
            var result = testSets.Select(ts =>
            {
                var latestRun = historyRepo.GetLatestRun(ts.Id);
                return new
                {
                    ts.Id,
                    ts.Name,
                    ts.ModuleId,
                    ts.Objectives,
                    ts.ObjectiveNames,
                    Objective = ts.Objective,
                    TaskCount = ts.Tasks.Count,
                    TestCaseCount = ts.Tasks.Sum(t => t.TestCases.Count),
                    ts.CreatedAt,
                    ts.LastRunAt,
                    ts.RunCount,
                    LastRunStatus = latestRun?.Status
                };
            });
            return Results.Ok(result);
        });

        // POST /api/modules/{moduleId}/testsets — create empty test set
        group.MapPost("/{moduleId}/testsets", async (string moduleId, CreateTestSetRequest request,
            ModuleRepository moduleRepo, TestSetRepository tsRepo) =>
        {
            if (!moduleRepo.Exists(moduleId))
                return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required" });

            var id = SlugHelper.ToSlug(request.Name);
            var existing = await tsRepo.LoadAsync(moduleId, id);
            if (existing is not null)
                return Results.Conflict(new { error = $"Test set '{id}' already exists in module '{moduleId}'" });

            var testSet = await tsRepo.CreateEmptyAsync(moduleId, request.Name);
            return Results.Created($"/api/modules/{moduleId}/testsets/{testSet.Id}", testSet);
        });

        // GET /api/modules/{moduleId}/testsets/{tsId} — test set detail
        group.MapGet("/{moduleId}/testsets/{tsId}", async (string moduleId, string tsId,
            TestSetRepository tsRepo, ExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var latestRun = historyRepo.GetLatestRun(tsId);
            return Results.Ok(new
            {
                testSet.Id,
                testSet.Name,
                testSet.ModuleId,
                testSet.Objectives,
                testSet.ObjectiveNames,
                Objective = testSet.Objective,
                testSet.CreatedAt,
                testSet.LastRunAt,
                testSet.RunCount,
                LastRunStatus = latestRun?.Status,
                testSet.Tasks
            });
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId} — delete test set and all runs
        group.MapDelete("/{moduleId}/testsets/{tsId}", async (string moduleId, string tsId,
            TestSetRepository tsRepo, ExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            await historyRepo.DeleteRunsForTestSetAsync(tsId);
            await tsRepo.DeleteAsync(moduleId, tsId);
            return Results.NoContent();
        });

        // GET /api/modules/{moduleId}/testsets/{tsId}/runs — run history
        group.MapGet("/{moduleId}/testsets/{tsId}/runs", (string moduleId, string tsId,
            ExecutionHistoryRepository historyRepo) =>
        {
            var runs = historyRepo.ListRuns(tsId);
            var result = runs.Select(r => new
            {
                r.RunId,
                r.Mode,
                r.Status,
                r.StartedAt,
                r.CompletedAt,
                r.TotalDuration,
                r.TotalTasks,
                r.PassedTasks,
                r.FailedTasks,
                r.ErrorTasks
            });
            return Results.Ok(result);
        });

        // GET /api/modules/{moduleId}/testsets/{tsId}/runs/{runId} — run detail
        group.MapGet("/{moduleId}/testsets/{tsId}/runs/{runId}", async (
            string moduleId, string tsId, string runId,
            ExecutionHistoryRepository historyRepo) =>
        {
            var run = await historyRepo.GetRunAsync(tsId, runId);
            if (run is null) return Results.NotFound(new { error = $"Run '{runId}' not found" });
            return Results.Ok(run);
        });

        // POST /api/modules/{moduleId}/testsets/{tsId}/move-objective — move an objective to another test set
        group.MapPost("/{moduleId}/testsets/{tsId}/move-objective", async (
            string moduleId, string tsId, MoveObjectiveRequest request,
            TestSetRepository tsRepo, ModuleRepository moduleRepo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Objective))
                return Results.BadRequest(new { error = "objective is required" });
            if (string.IsNullOrWhiteSpace(request.DestinationModuleId))
                return Results.BadRequest(new { error = "destinationModuleId is required" });
            if (string.IsNullOrWhiteSpace(request.DestinationTestSetId))
                return Results.BadRequest(new { error = "destinationTestSetId is required" });

            if (moduleId == request.DestinationModuleId && tsId == request.DestinationTestSetId)
                return Results.BadRequest(new { error = "Source and destination must differ" });

            // Validate source exists
            var source = await tsRepo.LoadAsync(moduleId, tsId);
            if (source is null)
                return Results.NotFound(new { error = $"Source test set '{tsId}' not found in module '{moduleId}'" });

            if (!source.Objectives.Contains(request.Objective, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Objective not found in source test set" });

            // Validate destination exists
            if (!moduleRepo.Exists(request.DestinationModuleId))
                return Results.NotFound(new { error = $"Destination module '{request.DestinationModuleId}' not found" });

            var dest = await tsRepo.LoadAsync(request.DestinationModuleId, request.DestinationTestSetId);
            if (dest is null)
                return Results.NotFound(new { error = $"Destination test set '{request.DestinationTestSetId}' not found" });

            try
            {
                await tsRepo.MoveObjectiveAsync(
                    moduleId, tsId,
                    request.DestinationModuleId, request.DestinationTestSetId,
                    request.Objective);
                return Results.Ok(new { moved = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // PUT /api/modules/{moduleId}/testsets/{tsId}/tasks/{taskId}/testcases/{index} — update a single test case
        group.MapPut("/{moduleId}/testsets/{tsId}/tasks/{taskId}/testcases/{index:int}",
            async (string moduleId, string tsId, string taskId, int index,
                ApiTestCase updated, TestSetRepository tsRepo, ExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var task = testSet.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task is null)
                return Results.NotFound(new { error = $"Task '{taskId}' not found in test set '{tsId}'" });

            if (index < 0 || index >= task.TestCases.Count)
                return Results.BadRequest(new { error = $"Test case index {index} out of range (0-{task.TestCases.Count - 1})" });

            task.TestCases[index] = updated;
            await tsRepo.SaveAsync(testSet, moduleId);

            var latestRun = historyRepo.GetLatestRun(tsId);
            return Results.Ok(new
            {
                testSet.Id, testSet.Name, testSet.ModuleId,
                testSet.Objectives, testSet.ObjectiveNames,
                Objective = testSet.Objective,
                testSet.CreatedAt, testSet.LastRunAt, testSet.RunCount,
                LastRunStatus = latestRun?.Status,
                testSet.Tasks
            });
        });

        // PUT /api/modules/{moduleId}/testsets/{tsId}/tasks/{taskId}/webuicases/{index} — update a single Web UI test case
        group.MapPut("/{moduleId}/testsets/{tsId}/tasks/{taskId}/webuicases/{index:int}",
            async (string moduleId, string tsId, string taskId, int index,
                AiTestCrew.Agents.Shared.WebUiTestCase updated,
                TestSetRepository tsRepo, ExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var task = testSet.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task is null)
                return Results.NotFound(new { error = $"Task '{taskId}' not found in test set '{tsId}'" });

            if (index < 0 || index >= task.WebUiTestCases.Count)
                return Results.BadRequest(new { error = $"Web UI test case index {index} out of range (0-{task.WebUiTestCases.Count - 1})" });

            task.WebUiTestCases[index] = updated;
            await tsRepo.SaveAsync(testSet, moduleId);

            var latestRun = historyRepo.GetLatestRun(tsId);
            return Results.Ok(new
            {
                testSet.Id, testSet.Name, testSet.ModuleId,
                testSet.Objectives, testSet.ObjectiveNames,
                Objective = testSet.Objective,
                testSet.CreatedAt, testSet.LastRunAt, testSet.RunCount,
                LastRunStatus = latestRun?.Status,
                testSet.Tasks
            });
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId}/tasks/{taskId}/webuicases/{index} — delete a Web UI test case
        group.MapDelete("/{moduleId}/testsets/{tsId}/tasks/{taskId}/webuicases/{index:int}",
            async (string moduleId, string tsId, string taskId, int index,
                TestSetRepository tsRepo, ExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var task = testSet.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task is null)
                return Results.NotFound(new { error = $"Task '{taskId}' not found in test set '{tsId}'" });

            if (index < 0 || index >= task.WebUiTestCases.Count)
                return Results.BadRequest(new { error = $"Web UI test case index {index} out of range (0-{task.WebUiTestCases.Count - 1})" });

            task.WebUiTestCases.RemoveAt(index);
            await tsRepo.SaveAsync(testSet, moduleId);

            var latestRun = historyRepo.GetLatestRun(tsId);
            return Results.Ok(new
            {
                testSet.Id, testSet.Name, testSet.ModuleId,
                testSet.Objectives, testSet.ObjectiveNames,
                Objective = testSet.Objective,
                testSet.CreatedAt, testSet.LastRunAt, testSet.RunCount,
                LastRunStatus = latestRun?.Status,
                testSet.Tasks
            });
        });

        // POST /api/modules/{moduleId}/testsets/{tsId}/ai-patch — preview LLM-applied changes
        group.MapPost("/{moduleId}/testsets/{tsId}/ai-patch",
            async (string moduleId, string tsId, AiPatchRequest request,
                TestSetRepository tsRepo, Kernel kernel, ILogger<TestOrchestrator> logger) =>
        {
            if (string.IsNullOrWhiteSpace(request.Instruction))
                return Results.BadRequest(new { error = "instruction is required" });

            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            // Build the scoped list of test cases to send to the LLM
            var entries = new List<TestCasePatchEntry>();
            foreach (var task in testSet.Tasks)
            {
                if (request.Scope?.TaskId is not null && task.TaskId != request.Scope.TaskId)
                    continue;

                for (int i = 0; i < task.TestCases.Count; i++)
                {
                    if (request.Scope?.CaseIndex is not null && i != request.Scope.CaseIndex)
                        continue;
                    entries.Add(new TestCasePatchEntry(task.TaskId, i, task.TestCases[i]));
                }
            }

            if (entries.Count == 0)
                return Results.BadRequest(new { error = "No test cases match the specified scope" });

            // Serialize only the test cases for the LLM prompt
            var casesForLlm = entries.Select(e => e.TestCase).ToList();
            var casesJson = JsonSerializer.Serialize(casesForLlm, LlmJsonHelper.JsonOpts);

            var prompt = $"""
                Here are the current API test cases as a JSON array:
                {casesJson}

                Instruction: {request.Instruction}

                Apply the instruction to the test cases and return the modified JSON array.
                Return ONLY a valid JSON array with the same number of elements.
                Preserve all fields exactly as-is unless the instruction specifically asks to change them.
                """;

            try
            {
                var chatService = kernel.GetRequiredService<IChatCompletionService>();
                var history = new ChatHistory();
                history.AddSystemMessage(
                    "You are an API test case editor. You receive a JSON array of test cases and a natural language instruction. " +
                    "Apply the instruction and return the modified array. Return ONLY valid JSON. " +
                    "Do not include markdown fences, explanation, or any text outside the JSON array. " +
                    "Preserve the exact number of elements unless explicitly told to add or remove.");
                history.AddUserMessage(prompt);

                var response = await chatService.GetChatMessageContentAsync(history);
                var patched = LlmJsonHelper.DeserializeLlmResponse<List<ApiTestCase>>(response.Content ?? "");

                if (patched is null || patched.Count != entries.Count)
                    return Results.UnprocessableEntity(new
                    {
                        error = $"LLM returned an invalid response (expected {entries.Count} test cases, got {patched?.Count ?? 0})"
                    });

                var patchedEntries = entries.Select((e, idx) =>
                    new TestCasePatchEntry(e.TaskId, e.CaseIndex, patched[idx])).ToList();

                return Results.Ok(new AiPatchPreview(entries, patchedEntries));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI patch failed for {Module}/{TestSet}", moduleId, tsId);
                return Results.UnprocessableEntity(new { error = $"LLM call failed: {ex.Message}" });
            }
        });

        // POST /api/modules/{moduleId}/testsets/{tsId}/ai-patch/apply — apply previewed patches
        group.MapPost("/{moduleId}/testsets/{tsId}/ai-patch/apply",
            async (string moduleId, string tsId, AiPatchApplyRequest request,
                TestSetRepository tsRepo, ExecutionHistoryRepository historyRepo) =>
        {
            if (request.Patches is null || request.Patches.Count == 0)
                return Results.BadRequest(new { error = "patches array is required" });

            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            foreach (var patch in request.Patches)
            {
                var task = testSet.Tasks.FirstOrDefault(t => t.TaskId == patch.TaskId);
                if (task is null) continue;
                if (patch.CaseIndex < 0 || patch.CaseIndex >= task.TestCases.Count) continue;
                task.TestCases[patch.CaseIndex] = patch.TestCase;
            }

            await tsRepo.SaveAsync(testSet, moduleId);

            var latestRun = historyRepo.GetLatestRun(tsId);
            return Results.Ok(new
            {
                testSet.Id, testSet.Name, testSet.ModuleId,
                testSet.Objectives, testSet.ObjectiveNames,
                Objective = testSet.Objective,
                testSet.CreatedAt, testSet.LastRunAt, testSet.RunCount,
                LastRunStatus = latestRun?.Status,
                testSet.Tasks
            });
        });

        return group;
    }
}

public record CreateModuleRequest(string Name, string? Description);
public record UpdateModuleRequest(string? Name, string? Description);
public record CreateTestSetRequest(string Name);
public record MoveObjectiveRequest(string Objective, string DestinationModuleId, string DestinationTestSetId);

// ── Test case editing records ──
public record AiPatchRequest(string Instruction, AiPatchScope? Scope);
public record AiPatchScope(string? TaskId, int? CaseIndex);
public record TestCasePatchEntry(string TaskId, int CaseIndex, ApiTestCase TestCase);
public record AiPatchPreview(List<TestCasePatchEntry> Original, List<TestCasePatchEntry> Patched);
public record AiPatchApplyRequest(List<TestCasePatchEntry> Patches);
