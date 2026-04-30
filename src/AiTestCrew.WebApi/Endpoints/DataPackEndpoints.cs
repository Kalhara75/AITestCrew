using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.WebApi.Endpoints;

public static class DataPackEndpoints
{
    public static RouteGroupBuilder MapDataPackEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/data-packs/startup-report — most recent run captured at WebApi startup.
        // 200 + body when a run has completed; 200 + null when the runner hasn't executed yet.
        group.MapGet("/startup-report", (IDataPackRunner runner) =>
            Results.Ok(runner.LatestReport));

        return group;
    }
}
