using AiTestCrew.Core.Models;
using AiTestCrew.Agents.Persistence;
using AiTestCrew.Core.Capabilities;
using AiTestCrew.WebApi.Integrations.JiraXray;
using AiTestCrew.WebApi.Services;

namespace AiTestCrew.WebApi.Endpoints;

public static class XrayEndpoints
{
    public static RouteGroupBuilder MapXrayEndpoints(this RouteGroupBuilder group)
    {

        // POST /api/xray/import
        // Fetches the Xray ticket and returns a preview (decompose + map, no persistence)
        group.MapPost("/import", async (
            XrayImportRequest? body, HttpContext ctx, IXrayImportService importSvc, CancellationToken ct) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();
            if (body is null) return Results.BadRequest(new { error = "request body is required" });
            if (string.IsNullOrWhiteSpace(body.TicketKey))
                return Results.BadRequest(new { error = "ticketKey is required" });
            if (string.IsNullOrWhiteSpace(body.ModuleId))
                return Results.BadRequest(new { error = "moduleId is required" });
            if (string.IsNullOrWhiteSpace(body.TestSetId))
                return Results.BadRequest(new { error = "testSetId is required" });
            try
            {
                var preview = await importSvc.PreviewAsync(body, ct);
                return Results.Ok(preview);
            }
            catch (XrayTicketNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (XrayAuthException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 401);
            }
            catch (XrayUpstreamException ex)
            {
                return Results.Problem(detail: ex.Message, title: "Xray upstream error", statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Import failed", statusCode: 500);
            }
        });

        // POST /api/xray/import/confirm
        // Persists the accepted objectives and writes gap REQ files
        group.MapPost("/import/confirm", async (
            XrayImportConfirmRequest? body, HttpContext ctx, IXrayImportService importSvc, CancellationToken ct) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();
            if (body is null) return Results.BadRequest(new { error = "request body is required" });
            try
            {
                var result = await importSvc.ConfirmAsync(body, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Confirm failed", statusCode: 500);
            }
        });

        // GET /api/xray/capabilities
        // Returns current AITestCrew capabilities as structured DTO (for UI to display)
        group.MapGet("/capabilities", (HttpContext ctx) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Unauthorized();
            return Results.Ok(CapabilityRegistry.GetDto());
        });

        return group;
    }
}
