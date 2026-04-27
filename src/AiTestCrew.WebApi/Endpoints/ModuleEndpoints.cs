using System.Text.Json;
using AiTestCrew.Agents.ApiAgent;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;
using AiTestCrew.Orchestrator;
using AiTestCrew.WebApi.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTestCrew.WebApi.Endpoints;

public static class ModuleEndpoints
{
    public static RouteGroupBuilder MapModuleEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/modules — list all modules with test set counts
        group.MapGet("/", async (IModuleRepository moduleRepo, ITestSetRepository tsRepo,
            IExecutionHistoryRepository historyRepo) =>
        {
            var modules = await moduleRepo.ListAllAsync();
            var result = modules.Select(m =>
            {
                var testSets = tsRepo.ListByModule(m.Id);
                return new
                {
                    m.Id,
                    m.Name,
                    m.Description,
                    m.CreatedAt,
                    m.UpdatedAt,
                    TestSetCount = testSets.Count,
                    TotalObjectives = testSets.Sum(ts => ts.TestObjectives.Count)
                };
            });
            return Results.Ok(result);
        });

        // POST /api/modules — create a module
        group.MapPost("/", async (CreateModuleRequest request, IModuleRepository moduleRepo) =>
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
        group.MapGet("/{moduleId}", async (string moduleId, IModuleRepository moduleRepo,
            ITestSetRepository tsRepo) =>
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
                TotalObjectives = testSets.Sum(ts => ts.TestObjectives.Count)
            });
        });

        // PUT /api/modules/{moduleId} — update module
        group.MapPut("/{moduleId}", async (string moduleId, UpdateModuleRequest request,
            IModuleRepository moduleRepo) =>
        {
            var module = await moduleRepo.GetAsync(moduleId);
            if (module is null) return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            if (!string.IsNullOrWhiteSpace(request.Name)) module.Name = request.Name;
            if (request.Description is not null) module.Description = request.Description;

            await moduleRepo.UpdateAsync(module);
            return Results.Ok(module);
        });

        // DELETE /api/modules/{moduleId} — cascade delete module, its test sets, and their execution history
        group.MapDelete("/{moduleId}", async (string moduleId,
            IModuleRepository moduleRepo, ITestSetRepository tsRepo,
            IExecutionHistoryRepository historyRepo, IModuleRunTracker moduleRunTracker) =>
        {
            if (!moduleRepo.Exists(moduleId))
                return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            if (moduleRunTracker.HasActiveModuleRunForModule(moduleId))
                return Results.Conflict(new { error = $"Module '{moduleId}' has an active run; wait for it to finish before deleting." });

            try
            {
                foreach (var ts in tsRepo.ListByModule(moduleId))
                {
                    await historyRepo.DeleteRunsForTestSetAsync(ts.Id);
                    await tsRepo.DeleteAsync(moduleId, ts.Id);
                }

                await moduleRepo.DeleteAsync(moduleId);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // GET /api/modules/{moduleId}/testsets — list test sets in module
        group.MapGet("/{moduleId}/testsets", (string moduleId, IModuleRepository moduleRepo,
            ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            if (!moduleRepo.Exists(moduleId))
                return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            var testSets = tsRepo.ListByModule(moduleId);
            var result = testSets.Select(ts =>
            {
                var objStatuses = historyRepo.GetLatestObjectiveStatuses(ts.Id);
                var currentIds = ts.TestObjectives.Select(o => o.Id).ToHashSet();
                return new
                {
                    ts.Id,
                    ts.Name,
                    ts.ModuleId,
                    ts.ApiStackKey,
                    ts.ApiModule,
                    ts.EndpointCode,
                    ts.EnvironmentKey,
                    ts.Objectives,
                    ts.ObjectiveNames,
                    Objective = ts.Objective,
                    ObjectiveCount = ts.TestObjectives.Count,
                    ts.CreatedAt,
                    ts.LastRunAt,
                    RunCount = historyRepo.CountRuns(ts.Id),
                    LastRunStatus = AggregateStatus(objStatuses, currentIds)
                };
            });
            return Results.Ok(result);
        });

        // POST /api/modules/{moduleId}/testsets — create empty test set
        group.MapPost("/{moduleId}/testsets", async (string moduleId, CreateTestSetRequest request,
            IModuleRepository moduleRepo, ITestSetRepository tsRepo) =>
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
            ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId} — delete test set and all runs
        group.MapDelete("/{moduleId}/testsets/{tsId}", async (string moduleId, string tsId,
            ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
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
            IExecutionHistoryRepository historyRepo) =>
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
                r.TotalObjectives,
                r.PassedObjectives,
                r.FailedObjectives,
                r.ErrorObjectives
            });
            return Results.Ok(result);
        });

        // GET /api/modules/{moduleId}/testsets/{tsId}/runs/{runId} — run detail
        group.MapGet("/{moduleId}/testsets/{tsId}/runs/{runId}", async (
            string moduleId, string tsId, string runId,
            IExecutionHistoryRepository historyRepo) =>
        {
            var run = await historyRepo.GetRunAsync(tsId, runId);
            if (run is null) return Results.NotFound(new { error = $"Run '{runId}' not found" });
            return Results.Ok(run);
        });

        // POST /api/modules/{moduleId}/testsets/{tsId}/move-objective — move an objective to another test set
        group.MapPost("/{moduleId}/testsets/{tsId}/move-objective", async (
            string moduleId, string tsId, MoveObjectiveRequest request,
            ITestSetRepository tsRepo, IModuleRepository moduleRepo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Objective))
                return Results.BadRequest(new { error = "objective is required" });
            if (string.IsNullOrWhiteSpace(request.DestinationModuleId))
                return Results.BadRequest(new { error = "destinationModuleId is required" });
            if (string.IsNullOrWhiteSpace(request.DestinationTestSetId))
                return Results.BadRequest(new { error = "destinationTestSetId is required" });

            if (moduleId == request.DestinationModuleId && tsId == request.DestinationTestSetId)
                return Results.BadRequest(new { error = "Source and destination must differ" });

            var source = await tsRepo.LoadAsync(moduleId, tsId);
            if (source is null)
                return Results.NotFound(new { error = $"Source test set '{tsId}' not found in module '{moduleId}'" });

            if (!source.Objectives.Contains(request.Objective, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Objective not found in source test set" });

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

        // PUT /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId} — update a test objective's definition
        group.MapPut("/{moduleId}/testsets/{tsId}/objectives/{objectiveId}",
            async (string moduleId, string tsId, string objectiveId,
                TestObjective updated, ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var idx = testSet.TestObjectives.FindIndex(o => o.Id == objectiveId);
            if (idx < 0)
                return Results.NotFound(new { error = $"Objective '{objectiveId}' not found in test set '{tsId}'" });

            // Preserve identity fields, update definition
            updated.Id = objectiveId;
            updated.ParentObjective = testSet.TestObjectives[idx].ParentObjective;
            updated.AgentName = testSet.TestObjectives[idx].AgentName;
            updated.TargetType = testSet.TestObjectives[idx].TargetType;
            testSet.TestObjectives[idx] = updated;

            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId} — delete a test objective
        group.MapDelete("/{moduleId}/testsets/{tsId}/objectives/{objectiveId}",
            async (string moduleId, string tsId, string objectiveId,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var removed = testSet.TestObjectives.RemoveAll(o => o.Id == objectiveId);
            if (removed == 0)
                return Results.NotFound(new { error = $"Objective '{objectiveId}' not found in test set '{tsId}'" });

            await tsRepo.SaveAsync(testSet, moduleId);
            await historyRepo.RemoveObjectiveFromHistoryAsync(tsId, objectiveId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/deliveries/{deliveryIndex}/verifications/{verificationIndex}
        // Removes a single post-delivery UI verification from an aseXML delivery case.
        // Indices are 0-based positions in the test-set JSON's aseXmlDeliverySteps[].postDeliveryVerifications[] list.
        group.MapDelete("/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/deliveries/{deliveryIndex:int}/verifications/{verificationIndex:int}",
            async (string moduleId, string tsId, string objectiveId,
                int deliveryIndex, int verificationIndex,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var objective = testSet.TestObjectives.FirstOrDefault(o =>
                string.Equals(o.Id, objectiveId, StringComparison.OrdinalIgnoreCase));
            if (objective is null)
                return Results.NotFound(new { error = $"Objective '{objectiveId}' not found in test set '{tsId}'" });

            if (deliveryIndex < 0 || deliveryIndex >= objective.AseXmlDeliverySteps.Count)
                return Results.BadRequest(new { error = $"deliveryIndex {deliveryIndex} out of range (0..{objective.AseXmlDeliverySteps.Count - 1})" });

            var delivery = objective.AseXmlDeliverySteps[deliveryIndex];
            if (verificationIndex < 0 || verificationIndex >= delivery.PostSteps.Count)
                return Results.BadRequest(new { error = $"verificationIndex {verificationIndex} out of range (0..{delivery.PostSteps.Count - 1})" });

            delivery.PostSteps.RemoveAt(verificationIndex);
            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // PUT /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/deliveries/{deliveryIndex}/verifications/{verificationIndex}
        // Replaces a single post-delivery UI verification in place. Body is the full updated VerificationStep.
        group.MapPut("/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/deliveries/{deliveryIndex:int}/verifications/{verificationIndex:int}",
            async (string moduleId, string tsId, string objectiveId,
                int deliveryIndex, int verificationIndex,
                AiTestCrew.Agents.AseXmlAgent.VerificationStep updated,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            var objective = testSet.TestObjectives.FirstOrDefault(o =>
                string.Equals(o.Id, objectiveId, StringComparison.OrdinalIgnoreCase));
            if (objective is null)
                return Results.NotFound(new { error = $"Objective '{objectiveId}' not found in test set '{tsId}'" });

            if (deliveryIndex < 0 || deliveryIndex >= objective.AseXmlDeliverySteps.Count)
                return Results.BadRequest(new { error = $"deliveryIndex {deliveryIndex} out of range (0..{objective.AseXmlDeliverySteps.Count - 1})" });

            var delivery = objective.AseXmlDeliverySteps[deliveryIndex];
            if (verificationIndex < 0 || verificationIndex >= delivery.PostSteps.Count)
                return Results.BadRequest(new { error = $"verificationIndex {verificationIndex} out of range (0..{delivery.PostSteps.Count - 1})" });

            delivery.PostSteps[verificationIndex] = updated;
            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // ─────────────────────────────────────────────────────────────────────
        // Generalized post-step endpoints (Slice 2)
        //
        // parentKind is one of: "Api", "WebUi", "DesktopUi", "AseXml", "AseXmlDeliver".
        // parentIndex is the 0-based position in the corresponding step-list
        // field on the TestObjective.
        //
        // The legacy deliveries/verifications routes above remain for back-compat
        // but these new routes are the canonical surface for any parent type.
        // ─────────────────────────────────────────────────────────────────────

        // POST /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/post-steps/{parentKind}/{parentIndex}
        // Appends a new post-step to any parent step list.
        group.MapPost("/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/post-steps/{parentKind}/{parentIndex:int}",
            async (string moduleId, string tsId, string objectiveId,
                string parentKind, int parentIndex,
                AiTestCrew.Agents.AseXmlAgent.VerificationStep newStep,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var (testSet, objective, list, err) = await ResolvePostStepListAsync(
                tsRepo, moduleId, tsId, objectiveId, parentKind, parentIndex);
            if (err is not null) return err;

            list!.Add(newStep);
            await tsRepo.SaveAsync(testSet!, moduleId);
            return Results.Ok(TestSetResponse(testSet!, historyRepo));
        });

        // PUT /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/post-steps/{parentKind}/{parentIndex}/{postIndex}
        // Replaces a post-step in place.
        group.MapPut("/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/post-steps/{parentKind}/{parentIndex:int}/{postIndex:int}",
            async (string moduleId, string tsId, string objectiveId,
                string parentKind, int parentIndex, int postIndex,
                AiTestCrew.Agents.AseXmlAgent.VerificationStep updated,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var (testSet, objective, list, err) = await ResolvePostStepListAsync(
                tsRepo, moduleId, tsId, objectiveId, parentKind, parentIndex);
            if (err is not null) return err;

            if (postIndex < 0 || postIndex >= list!.Count)
                return Results.BadRequest(new { error = $"postIndex {postIndex} out of range (0..{list.Count - 1})" });

            list[postIndex] = updated;
            await tsRepo.SaveAsync(testSet!, moduleId);
            return Results.Ok(TestSetResponse(testSet!, historyRepo));
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/post-steps/{parentKind}/{parentIndex}/{postIndex}
        group.MapDelete("/{moduleId}/testsets/{tsId}/objectives/{objectiveId}/post-steps/{parentKind}/{parentIndex:int}/{postIndex:int}",
            async (string moduleId, string tsId, string objectiveId,
                string parentKind, int parentIndex, int postIndex,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var (testSet, objective, list, err) = await ResolvePostStepListAsync(
                tsRepo, moduleId, tsId, objectiveId, parentKind, parentIndex);
            if (err is not null) return err;

            if (postIndex < 0 || postIndex >= list!.Count)
                return Results.BadRequest(new { error = $"postIndex {postIndex} out of range (0..{list.Count - 1})" });

            list.RemoveAt(postIndex);
            await tsRepo.SaveAsync(testSet!, moduleId);
            return Results.Ok(TestSetResponse(testSet!, historyRepo));
        });

        // PUT /api/modules/{moduleId}/testsets/{tsId}/setup-steps — save/update setup steps
        group.MapPut("/{moduleId}/testsets/{tsId}/setup-steps",
            async (string moduleId, string tsId, SetupStepsRequest request,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            testSet.SetupStartUrl = request.SetupStartUrl ?? "";
            testSet.SetupSteps = request.SetupSteps ?? [];
            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId}/setup-steps — clear setup steps
        group.MapDelete("/{moduleId}/testsets/{tsId}/setup-steps",
            async (string moduleId, string tsId,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            testSet.SetupStartUrl = "";
            testSet.SetupSteps = [];
            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // PUT /api/modules/{moduleId}/testsets/{tsId}/teardown-steps — save/update teardown steps
        group.MapPut("/{moduleId}/testsets/{tsId}/teardown-steps",
            async (string moduleId, string tsId, TeardownStepsRequest request,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            // Guardrail-validate each step at save-time so bad SQL never reaches the DB.
            var steps = request.TeardownSteps ?? [];
            foreach (var step in steps)
            {
                var (ok, reason) = AiTestCrew.Agents.Teardown.SqlGuardrails.Validate(step.Sql);
                if (!ok)
                    return Results.BadRequest(new { error = $"Step '{step.Name}' rejected: {reason}" });
            }

            testSet.TeardownSteps = steps;
            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // DELETE /api/modules/{moduleId}/testsets/{tsId}/teardown-steps — clear teardown steps
        group.MapDelete("/{moduleId}/testsets/{tsId}/teardown-steps",
            async (string moduleId, string tsId,
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            testSet.TeardownSteps = [];
            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // POST /api/modules/{moduleId}/run — run all test sets in a module (parallel)
        group.MapPost("/{moduleId}/run", async (string moduleId,
            IModuleRepository moduleRepo, ITestSetRepository tsRepo,
            IRunTracker runTracker, IModuleRunTracker moduleRunTracker,
            TestOrchestrator orchestrator, TestEnvironmentConfig config,
            AiTestCrew.Core.Interfaces.IRunQueueRepository? queueRepo,
            HttpContext ctx,
            ILogger<TestOrchestrator> logger) =>
        {
            var module = await moduleRepo.GetAsync(moduleId);
            if (module is null)
                return Results.NotFound(new { error = $"Module '{moduleId}' not found" });

            var testSets = tsRepo.ListByModule(moduleId);
            if (testSets.Count == 0)
                return Results.BadRequest(new { error = "Module has no test sets to run" });

            // Filter to test sets that have objectives
            var runnableTestSets = testSets.Where(ts => ts.TestObjectives.Count > 0).ToList();
            if (runnableTestSets.Count == 0)
                return Results.BadRequest(new { error = "Module has no test sets with objectives to run" });

            if (moduleRunTracker.HasActiveModuleRunForModule(moduleId))
                return Results.Conflict(new { error = $"Module '{moduleId}' already has an active run" });

            var moduleRunId = Guid.NewGuid().ToString("N")[..12];
            var tsProgress = runnableTestSets.Select(ts => new TestSetRunProgress
            {
                TestSetId = ts.Id,
                TestSetName = ts.Name ?? ts.Objective ?? ts.Id,
                Status = "Pending"
            }).ToList();

            moduleRunTracker.Create(moduleRunId, moduleId, module.Name, tsProgress);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Run all test sets in parallel — the orchestrator's
                    // AgentConcurrencyLimiter gates how many agents execute at once.
                    var requestedBy = (ctx.Items["User"] as AiTestCrew.Core.Models.User)?.Id;
                    var testSetTasks = runnableTestSets.Select(ts => Task.Run(async () =>
                    {
                        var childRunId = Guid.NewGuid().ToString("N")[..12];
                        moduleRunTracker.AdvanceToTestSet(moduleRunId, ts.Id, childRunId);
                        runTracker.Create(childRunId, "", "Reuse", ts.Id);

                        try
                        {
                            var agentTarget = queueRepo is not null
                                ? RunDispatchHelper.GetAgentRequiredTarget(ts, null) : null;

                            if (agentTarget is not null && queueRepo is not null)
                            {
                                // Enqueue for a local agent and poll until terminal
                                var entry = new AiTestCrew.Core.Models.RunQueueEntry
                                {
                                    Id = childRunId,
                                    ModuleId = moduleId,
                                    TestSetId = ts.Id,
                                    TargetType = agentTarget,
                                    Mode = RunMode.Reuse.ToString(),
                                    RequestedBy = requestedBy,
                                    RequestJson = System.Text.Json.JsonSerializer.Serialize(new RunRequest(
                                        Objective: null, ObjectiveName: null, Mode: "Reuse",
                                        TestSetId: ts.Id, ModuleId: moduleId, ObjectiveId: null,
                                        ApiStackKey: ts.ApiStackKey, ApiModule: ts.ApiModule,
                                        VerificationWaitOverride: null)),
                                    CreatedAt = DateTime.UtcNow
                                };
                                await queueRepo.EnqueueAsync(entry);
                                var status = runTracker.Get(childRunId);
                                if (status is not null) status.Status = "Queued";

                                // Wait for the agent to finish
                                while (true)
                                {
                                    await Task.Delay(2000);
                                    var job = await queueRepo.GetByIdAsync(childRunId);
                                    if (job is null) break;
                                    if (job.Status is "Completed" or "Failed" or "Cancelled")
                                    {
                                        if (job.Status == "Completed")
                                        {
                                            runTracker.Complete(childRunId, ts.Id);
                                            moduleRunTracker.CompleteTestSet(moduleRunId, ts.Id, true, null);
                                        }
                                        else
                                        {
                                            var err = job.Error ?? job.Status;
                                            runTracker.Fail(childRunId, err);
                                            moduleRunTracker.CompleteTestSet(moduleRunId, ts.Id, false, err);
                                        }
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                await orchestrator.RunAsync(
                                    "", RunMode.Reuse, ts.Id,
                                    externalRunId: childRunId,
                                    moduleId: moduleId,
                                    targetTestSetId: ts.Id,
                                    apiStackKey: ts.ApiStackKey,
                                    apiModule: ts.ApiModule,
                                    environmentKey: ts.EnvironmentKey);
                                runTracker.Complete(childRunId, ts.Id);
                                moduleRunTracker.CompleteTestSet(moduleRunId, ts.Id, true, null);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Module run {ModuleRunId}: test set {TestSetId} failed", moduleRunId, ts.Id);
                            runTracker.Fail(childRunId, ex.Message);
                            moduleRunTracker.CompleteTestSet(moduleRunId, ts.Id, false, ex.Message);
                        }
                    })).ToArray();

                    await Task.WhenAll(testSetTasks);
                    moduleRunTracker.Complete(moduleRunId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Module run {ModuleRunId} failed", moduleRunId);
                    moduleRunTracker.Fail(moduleRunId, ex.Message);
                }
            });

            return Results.Accepted($"/api/modules/{moduleId}/run/status", new
            {
                moduleRunId,
                moduleId,
                status = "Running",
                startedAt = DateTime.UtcNow,
                totalTestSets = runnableTestSets.Count
            });
        });

        // GET /api/modules/{moduleId}/run/status — poll module run progress
        group.MapGet("/{moduleId}/run/status", (string moduleId, IModuleRunTracker moduleRunTracker) =>
        {
            var status = moduleRunTracker.GetByModuleId(moduleId);
            if (status is null)
                return Results.NotFound(new { error = $"No active or recent run for module '{moduleId}'" });
            return Results.Ok(status);
        });

        // POST /api/modules/{moduleId}/testsets/{tsId}/ai-patch — preview LLM-applied changes
        group.MapPost("/{moduleId}/testsets/{tsId}/ai-patch",
            async (string moduleId, string tsId, AiPatchRequest request,
                ITestSetRepository tsRepo, Kernel kernel, ILogger<TestOrchestrator> logger) =>
        {
            if (string.IsNullOrWhiteSpace(request.Instruction))
                return Results.BadRequest(new { error = "instruction is required" });

            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            // Build the scoped list of objectives to send to the LLM
            var targets = testSet.TestObjectives.AsEnumerable();
            if (request.Scope?.ObjectiveId is not null)
                targets = targets.Where(o => o.Id == request.Scope.ObjectiveId);

            var targetList = targets.ToList();
            if (targetList.Count == 0)
                return Results.BadRequest(new { error = "No objectives match the specified scope" });

            // For API objectives, collect all API steps across targeted objectives
            var apiCases = targetList
                .SelectMany(o => o.ApiSteps.Select(s => s.ToTestCase("")))
                .ToList();

            if (apiCases.Count == 0)
                return Results.BadRequest(new { error = "AI patch currently only supports API test objectives" });

            var casesJson = JsonSerializer.Serialize(apiCases, LlmJsonHelper.JsonOpts);

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

                if (patched is null || patched.Count != apiCases.Count)
                    return Results.UnprocessableEntity(new
                    {
                        error = $"LLM returned an invalid response (expected {apiCases.Count} test cases, got {patched?.Count ?? 0})"
                    });

                // Map patched cases back — for now scoped to single objective
                var patchEntries = patched
                    .Select((tc, idx) => new ObjectivePatchEntry(targetList[0].Id, tc))
                    .ToList();

                var originalEntries = apiCases
                    .Select((tc, idx) => new ObjectivePatchEntry(targetList[0].Id, tc))
                    .ToList();

                return Results.Ok(new AiPatchPreview(originalEntries, patchEntries));
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
                ITestSetRepository tsRepo, IExecutionHistoryRepository historyRepo) =>
        {
            if (request.Patches is null || request.Patches.Count == 0)
                return Results.BadRequest(new { error = "patches array is required" });

            var testSet = await tsRepo.LoadAsync(moduleId, tsId);
            if (testSet is null)
                return Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" });

            // Group patches by objective and replace the API steps
            foreach (var group in request.Patches.GroupBy(p => p.ObjectiveId))
            {
                var objective = testSet.TestObjectives.FirstOrDefault(o => o.Id == group.Key);
                if (objective is null) continue;

                objective.ApiSteps = group
                    .Select(p => ApiTestDefinition.FromTestCase(p.TestCase))
                    .ToList();
            }

            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(TestSetResponse(testSet, historyRepo));
        });

        // ── Runner API client endpoints (for distributed recording / remote execution) ──

        // PUT /api/modules/{moduleId}/testsets/{tsId}/data — save full test set (used by Runner API client)
        group.MapPut("/{moduleId}/testsets/{tsId}/data",
            async (string moduleId, string tsId, PersistedTestSet testSet,
                ITestSetRepository tsRepo) =>
        {
            testSet.Id = tsId;
            testSet.ModuleId = moduleId;
            await tsRepo.SaveAsync(testSet, moduleId);
            return Results.Ok(new { saved = true });
        });

        // POST /api/modules/{moduleId}/testsets/{tsId}/merge — merge objectives (used by orchestrator via Runner)
        group.MapPost("/{moduleId}/testsets/{tsId}/merge",
            async (string moduleId, string tsId, MergeObjectivesRequest request,
                ITestSetRepository tsRepo) =>
        {
            await tsRepo.MergeObjectivesAsync(moduleId, tsId,
                request.Objectives, request.Objective,
                request.ObjectiveName, request.ApiStackKey, request.ApiModule,
                request.EndpointCode, request.EnvironmentKey);
            return Results.Ok(new { merged = true });
        });

        // POST /api/modules/{moduleId}/testsets/{tsId}/run-stats — increment run stats
        group.MapPost("/{moduleId}/testsets/{tsId}/run-stats",
            async (string moduleId, string tsId, ITestSetRepository tsRepo) =>
        {
            await tsRepo.UpdateRunStatsAsync(moduleId, tsId);
            return Results.Ok(new { updated = true });
        });

        // GET /api/modules/{moduleId}/testsets/{tsId}/delivery-context/{objectiveId}
        group.MapGet("/{moduleId}/testsets/{tsId}/delivery-context/{objectiveId}",
            async (string moduleId, string tsId, string objectiveId,
                IExecutionHistoryRepository historyRepo) =>
        {
            var ctx = await historyRepo.GetLatestDeliveryContextAsync(tsId, moduleId, objectiveId);
            return ctx is not null ? Results.Ok(ctx) : Results.NotFound();
        });

        // GET /api/modules/{moduleId}/testsets/{tsId}/objective-statuses — latest status per objective
        group.MapGet("/{moduleId}/testsets/{tsId}/objective-statuses",
            (string moduleId, string tsId, IExecutionHistoryRepository historyRepo) =>
        {
            var statuses = historyRepo.GetLatestObjectiveStatuses(tsId);
            return Results.Ok(statuses.ToDictionary(
                kvp => kvp.Key,
                kvp => new { kvp.Value.Result.Status, kvp.Value.Result.CompletedAt, kvp.Value.RunId }));
        });

        return group;
    }

    /// <summary>
    /// Resolves the <c>PostSteps</c> list on a parent step identified by
    /// (moduleId, testSetId, objectiveId, parentKind, parentIndex). Returns
    /// <paramref name="err"/> set to a Results.* response when anything can't
    /// be found — callers short-circuit with <c>if (err is not null) return err;</c>.
    /// </summary>
    private static async Task<(
        AiTestCrew.Agents.Persistence.PersistedTestSet? TestSet,
        AiTestCrew.Agents.Persistence.TestObjective? Objective,
        List<AiTestCrew.Agents.AseXmlAgent.VerificationStep>? List,
        Microsoft.AspNetCore.Http.IResult? Err)>
    ResolvePostStepListAsync(
        AiTestCrew.Agents.Persistence.ITestSetRepository tsRepo,
        string moduleId, string tsId, string objectiveId,
        string parentKind, int parentIndex)
    {
        var testSet = await tsRepo.LoadAsync(moduleId, tsId);
        if (testSet is null)
            return (null, null, null,
                Microsoft.AspNetCore.Http.Results.NotFound(new { error = $"Test set '{tsId}' not found in module '{moduleId}'" }));

        var objective = testSet.TestObjectives.FirstOrDefault(o =>
            string.Equals(o.Id, objectiveId, StringComparison.OrdinalIgnoreCase));
        if (objective is null)
            return (testSet, null, null,
                Microsoft.AspNetCore.Http.Results.NotFound(new { error = $"Objective '{objectiveId}' not found in test set '{tsId}'" }));

        List<AiTestCrew.Agents.AseXmlAgent.VerificationStep>? list;
        int count;
        switch (parentKind)
        {
            case "Api":
                count = objective.ApiSteps.Count;
                if (parentIndex < 0 || parentIndex >= count) goto BadIndex;
                list = objective.ApiSteps[parentIndex].PostSteps;
                break;
            case "WebUi":
                count = objective.WebUiSteps.Count;
                if (parentIndex < 0 || parentIndex >= count) goto BadIndex;
                list = objective.WebUiSteps[parentIndex].PostSteps;
                break;
            case "DesktopUi":
                count = objective.DesktopUiSteps.Count;
                if (parentIndex < 0 || parentIndex >= count) goto BadIndex;
                list = objective.DesktopUiSteps[parentIndex].PostSteps;
                break;
            case "AseXml":
                count = objective.AseXmlSteps.Count;
                if (parentIndex < 0 || parentIndex >= count) goto BadIndex;
                list = objective.AseXmlSteps[parentIndex].PostSteps;
                break;
            case "AseXmlDeliver":
                count = objective.AseXmlDeliverySteps.Count;
                if (parentIndex < 0 || parentIndex >= count) goto BadIndex;
                list = objective.AseXmlDeliverySteps[parentIndex].PostSteps;
                break;
            default:
                return (testSet, objective, null,
                    Microsoft.AspNetCore.Http.Results.BadRequest(new { error = $"Unknown parentKind '{parentKind}'. Expected one of: Api, WebUi, DesktopUi, AseXml, AseXmlDeliver." }));
        }

        return (testSet, objective, list, null);

        BadIndex:
        return (testSet, objective, null,
            Microsoft.AspNetCore.Http.Results.BadRequest(new { error = $"parentIndex {parentIndex} out of range for '{parentKind}' (0..{count - 1})" }));
    }

    private static object TestSetResponse(PersistedTestSet testSet, IExecutionHistoryRepository historyRepo)
    {
        var objStatuses = historyRepo.GetLatestObjectiveStatuses(testSet.Id);
        var currentIds = testSet.TestObjectives.Select(o => o.Id).ToHashSet();
        return new
        {
            testSet.Id, testSet.Name, testSet.ModuleId,
            testSet.ApiStackKey, testSet.ApiModule, testSet.EndpointCode, testSet.EnvironmentKey,
            testSet.Objectives, testSet.ObjectiveNames,
            Objective = testSet.Objective,
            testSet.CreatedAt, testSet.LastRunAt, RunCount = historyRepo.CountRuns(testSet.Id),
            testSet.SetupStartUrl, testSet.SetupSteps,
            testSet.TeardownSteps,
            LastRunStatus = AggregateStatus(objStatuses, currentIds),
            ObjectiveStatuses = objStatuses
                .Where(kvp => currentIds.Contains(kvp.Key))
                .ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    kvp.Value.Result.Status,
                    kvp.Value.Result.CompletedAt,
                    kvp.Value.RunId
                }),
            testSet.TestObjectives
        };
    }

    private static string? AggregateStatus(
        Dictionary<string, (PersistedObjectiveResult Result, string RunId)> objStatuses,
        IEnumerable<string>? currentObjectiveIds = null)
    {
        var values = currentObjectiveIds is not null
            ? objStatuses.Where(kvp => currentObjectiveIds.Contains(kvp.Key)).Select(kvp => kvp.Value)
            : objStatuses.Values;

        var list = values.ToList();
        if (list.Count == 0) return null;
        if (list.Any(o => o.Result.Status == "Error")) return "Error";
        if (list.Any(o => o.Result.Status == "Failed")) return "Failed";
        if (list.Any(o => o.Result.Status == "Skipped")) return "Skipped";
        return "Passed";
    }
}

public record CreateModuleRequest(string Name, string? Description);
public record UpdateModuleRequest(string? Name, string? Description);
public record CreateTestSetRequest(string Name);
public record MoveObjectiveRequest(string Objective, string DestinationModuleId, string DestinationTestSetId);
public record MergeObjectivesRequest(
    List<TestObjective> Objectives, string Objective,
    string? ObjectiveName = null, string? ApiStackKey = null, string? ApiModule = null,
    string? EndpointCode = null, string? EnvironmentKey = null);

// ── Test objective editing records ──
public record AiPatchRequest(string Instruction, AiPatchScope? Scope);
public record AiPatchScope(string? ObjectiveId);
public record ObjectivePatchEntry(string ObjectiveId, ApiTestCase TestCase);
public record AiPatchPreview(List<ObjectivePatchEntry> Original, List<ObjectivePatchEntry> Patched);
public record AiPatchApplyRequest(List<ObjectivePatchEntry> Patches);
public record SetupStepsRequest(string? SetupStartUrl, List<WebUiStep>? SetupSteps);
public record TeardownStepsRequest(List<SqlTeardownStep>? TeardownSteps);
