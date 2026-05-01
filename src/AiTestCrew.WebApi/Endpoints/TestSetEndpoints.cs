using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

public static class TestSetEndpoints
{
    public static RouteGroupBuilder MapTestSetEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", (ITestSetRepository repo, IExecutionHistoryRepository historyRepo) =>
        {
            var testSets = repo.ListAll();
            var result = testSets.Select(ts =>
            {
                var objStatuses = historyRepo.GetLatestObjectiveStatuses(ts.Id);
                var currentIds = ts.TestObjectives.Select(o => o.Id).ToHashSet();
                return new
                {
                    ts.Id,
                    ts.Objective,
                    ts.ObjectiveNames,
                    ObjectiveCount = ts.TestObjectives.Count,
                    ts.CreatedAt,
                    ts.LastRunAt,
                    RunCount = historyRepo.CountRuns(ts.Id),
                    LastRunStatus = AggregateStatus(objStatuses, currentIds)
                };
            });
            return Results.Ok(result);
        });

        group.MapGet("/{id}", async (string id, ITestSetRepository repo, IExecutionHistoryRepository historyRepo,
            IRunQueueRepository? queueRepo, IPendingVerificationRepository? pendingRepo) =>
        {
            var testSet = await repo.LoadAsync(id);
            if (testSet is null) return Results.NotFound(new { error = $"Test set '{id}' not found" });

            var objStatuses = historyRepo.GetLatestObjectiveStatuses(id);
            var currentIds = testSet.TestObjectives.Select(o => o.Id).ToHashSet();

            // Live overlay: queue + pending state takes precedence over history
            // so the row pill reflects the current run instead of the previous
            // finalised one (history writes lag deferred-verification finalise).
            var liveOverrides = await BuildLiveStatusOverridesAsync(id, queueRepo, pendingRepo);

            var objectiveStatuses = currentIds.ToDictionary(
                objId => objId,
                objId =>
                {
                    if (liveOverrides.TryGetValue(objId, out var live))
                        return (object?)new
                        {
                            Status = live.Status,
                            CompletedAt = (DateTime?)null,
                            RunId = live.RunId,
                        };
                    if (objStatuses.TryGetValue(objId, out var hist))
                        return (object?)new
                        {
                            Status = hist.Result.Status,
                            CompletedAt = (DateTime?)hist.Result.CompletedAt,
                            RunId = (string?)hist.RunId,
                        };
                    return null;
                })
                .Where(kvp => kvp.Value is not null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

            return Results.Ok(new
            {
                testSet.Id,
                testSet.Objective,
                testSet.ObjectiveNames,
                testSet.CreatedAt,
                testSet.LastRunAt,
                RunCount = historyRepo.CountRuns(id),
                LastRunStatus = AggregateStatus(objStatuses, currentIds),
                ObjectiveStatuses = objectiveStatuses,
                testSet.TestObjectives
            });
        });

        group.MapGet("/{id}/runs", (string id, IExecutionHistoryRepository historyRepo) =>
        {
            var runs = historyRepo.ListRuns(id);
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

        group.MapGet("/{id}/runs/{runId}", async (string id, string runId, IExecutionHistoryRepository historyRepo) =>
        {
            var run = await historyRepo.GetRunAsync(id, runId);
            if (run is null) return Results.NotFound(new { error = $"Run '{runId}' not found" });
            return Results.Ok(run);
        });

        return group;
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

    /// <summary>
    /// Walks the run queue and pending-verification tables for a test set and
    /// returns the most-relevant LIVE status per objective. AwaitingVerification
    /// (a pending row exists) wins over Running / Claimed / Queued (queue entry).
    /// Used by the test-set GET handler so the dashboard's row pill reflects
    /// the current run instead of the last finalised one.
    /// </summary>
    internal static async Task<Dictionary<string, (string Status, string? RunId)>>
        BuildLiveStatusOverridesAsync(
            string testSetId,
            IRunQueueRepository? queueRepo,
            IPendingVerificationRepository? pendingRepo)
    {
        var overrides = new Dictionary<string, (string Status, string? RunId)>(StringComparer.OrdinalIgnoreCase);
        if (queueRepo is null) return overrides;

        var recent = await queueRepo.ListRecentAsync(100);
        var runIdToObjectiveId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in recent)
        {
            if (!string.Equals(entry.TestSetId, testSetId, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.ObjectiveId)) continue;
            if (!runIdToObjectiveId.ContainsKey(entry.Id))
                runIdToObjectiveId[entry.Id] = entry.ObjectiveId;
            if (entry.Status is not ("Queued" or "Claimed" or "Running")) continue;
            if (!overrides.ContainsKey(entry.ObjectiveId))
                overrides[entry.ObjectiveId] = (entry.Status, entry.Id);
        }

        if (pendingRepo is null) return overrides;

        var pending = await pendingRepo.ListPendingAsync();
        foreach (var p in pending)
        {
            if (!string.Equals(p.TestSetId, testSetId, StringComparison.OrdinalIgnoreCase)) continue;
            var objId = !string.IsNullOrEmpty(p.DeliveryObjectiveId)
                ? p.DeliveryObjectiveId
                : runIdToObjectiveId.GetValueOrDefault(p.ParentRunId);
            if (string.IsNullOrEmpty(objId)) continue;
            overrides[objId] = ("AwaitingVerification", p.ParentRunId);
        }

        return overrides;
    }
}
