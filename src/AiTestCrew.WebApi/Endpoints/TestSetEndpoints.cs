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
                var latestRun = historyRepo.GetLatestRun(ts.Id);
                return new
                {
                    ts.Id,
                    ts.Objective,
                    ts.ObjectiveNames,
                    ObjectiveCount = ts.TestObjectives.Count,
                    ts.CreatedAt,
                    ts.LastRunAt,
                    ts.RunCount,
                    LastRunStatus = latestRun?.Status
                };
            });
            return Results.Ok(result);
        });

        group.MapGet("/{id}", async (string id, TestSetRepository repo, ExecutionHistoryRepository historyRepo) =>
        {
            var testSet = await repo.LoadAsync(id);
            if (testSet is null) return Results.NotFound(new { error = $"Test set '{id}' not found" });

            var latestRun = historyRepo.GetLatestRun(id);
            return Results.Ok(new
            {
                testSet.Id,
                testSet.Objective,
                testSet.ObjectiveNames,
                testSet.CreatedAt,
                testSet.LastRunAt,
                testSet.RunCount,
                LastRunStatus = latestRun?.Status,
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
}
