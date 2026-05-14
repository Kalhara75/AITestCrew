namespace AiTestCrew.WebApi.Endpoints;

using AiTestCrew.WebApi.Services;

public static class AdminBackupEndpoints
{
    public static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/admin/backup -- trigger an out-of-cycle backup.
        // Returns { path, sizeBytes, durationMs }. 409 if a backup is already running.
        group.MapPost("/", async (DatabaseBackupService svc) =>
        {
            try
            {
                var result = await svc.RunBackupAsync();
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress"))
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // GET /api/admin/backup/status -- last success/error times, size, next scheduled, disk count.
        group.MapGet("/status", (DatabaseBackupService svc) =>
            Results.Ok(svc.GetStatus()));

        // GET /api/admin/backup/list -- file paths of all backups on disk.
        group.MapGet("/list", (DatabaseBackupService svc) =>
            Results.Ok(new { paths = svc.ListBackupPaths() }));

        return group;
    }
}
