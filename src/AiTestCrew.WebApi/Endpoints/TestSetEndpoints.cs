using AiTestCrew.Agents.Persistence;

namespace AiTestCrew.WebApi.Endpoints;

public static class TestSetEndpoints
{
    public static RouteGroupBuilder MapTestSetEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", (TestSetRepository repo, ExecutionHistoryRepository historyRepo) =>
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
                    ts.RunCount,
                    LastRunStatus = AggregateStatus(objStatuses, currentIds)
                };
            });
            return Results.Ok(result);
        });

        group.MapGet("/{id}", async (string id, TestSetRepository repo, ExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await repo.LoadAsync(id);
            if (testSet is null) return Results.NotFound(new { error = $"Test set '{id}' not found" });

            var objStatuses = historyRepo.GetLatestObjectiveStatuses(id);
            var currentIds = testSet.TestObjectives.Select(o => o.Id).ToHashSet();
            return Results.Ok(new
            {
                testSet.Id,
                testSet.Objective,
                testSet.ObjectiveNames,
                testSet.CreatedAt,
                testSet.LastRunAt,
                testSet.RunCount,
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
            });
        });

        group.MapGet("/{id}/runs", (string id, ExecutionHistoryRepository historyRepo) =>
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

        group.MapGet("/{id}/runs/{runId}", async (string id, string runId, ExecutionHistoryRepository historyRepo) =>
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
}
