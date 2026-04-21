using System.Text.Json;
using AiTestCrew.Agents.Recording;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

public static class RecordingEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapRecordingEndpoints(this RouteGroupBuilder group)
    {
        // POST /api/recordings — enqueue a recording/auth-setup job for a local agent
        group.MapPost("/", async (StartRecordingRequest request, IRunQueueRepository queueRepo,
            IAgentRepository agentRepo, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Kind))
                return Results.BadRequest(new { error = "kind is required" });
            if (string.IsNullOrWhiteSpace(request.Target))
                return Results.BadRequest(new { error = "target is required" });

            // Validate target is a UI capability the queue routes on
            if (request.Target is not ("UI_Web_MVC" or "UI_Web_Blazor" or "UI_Desktop_WinForms"))
                return Results.BadRequest(new { error = $"target must be UI_Web_MVC, UI_Web_Blazor, or UI_Desktop_WinForms (got '{request.Target}')" });

            // Kind-specific validation + DTO construction
            string requestJson;
            string moduleIdForRow;
            string testSetIdForRow;
            string? objectiveIdForRow;
            switch (request.Kind)
            {
                case "Record":
                    if (string.IsNullOrWhiteSpace(request.ModuleId) || string.IsNullOrWhiteSpace(request.TestSetId) || string.IsNullOrWhiteSpace(request.CaseName))
                        return Results.BadRequest(new { error = "moduleId, testSetId, caseName required for Record" });
                    requestJson = JsonSerializer.Serialize(new RecordCaseRequest(
                        request.ModuleId!, request.TestSetId!, request.CaseName!, request.Target, request.EnvironmentKey), JsonOpts);
                    moduleIdForRow = request.ModuleId!;
                    testSetIdForRow = request.TestSetId!;
                    objectiveIdForRow = null;
                    break;

                case "RecordSetup":
                    if (string.IsNullOrWhiteSpace(request.ModuleId) || string.IsNullOrWhiteSpace(request.TestSetId))
                        return Results.BadRequest(new { error = "moduleId, testSetId required for RecordSetup" });
                    requestJson = JsonSerializer.Serialize(new RecordSetupRequest(
                        request.ModuleId!, request.TestSetId!, request.Target, request.EnvironmentKey), JsonOpts);
                    moduleIdForRow = request.ModuleId!;
                    testSetIdForRow = request.TestSetId!;
                    objectiveIdForRow = null;
                    break;

                case "RecordVerification":
                    if (string.IsNullOrWhiteSpace(request.ModuleId) || string.IsNullOrWhiteSpace(request.TestSetId)
                        || string.IsNullOrWhiteSpace(request.ObjectiveId) || string.IsNullOrWhiteSpace(request.VerificationName))
                        return Results.BadRequest(new { error = "moduleId, testSetId, objectiveId, verificationName required for RecordVerification" });
                    requestJson = JsonSerializer.Serialize(new RecordVerificationRequest(
                        request.ModuleId!, request.TestSetId!, request.ObjectiveId!, request.VerificationName!,
                        request.Target, request.WaitBeforeSeconds ?? 0, request.DeliveryStepIndex ?? 0,
                        request.EnvironmentKey), JsonOpts);
                    moduleIdForRow = request.ModuleId!;
                    testSetIdForRow = request.TestSetId!;
                    objectiveIdForRow = request.ObjectiveId;
                    break;

                case "AuthSetup":
                    if (request.Target is not ("UI_Web_MVC" or "UI_Web_Blazor"))
                        return Results.BadRequest(new { error = "AuthSetup target must be UI_Web_MVC or UI_Web_Blazor" });
                    requestJson = JsonSerializer.Serialize(new AuthSetupRequest(request.Target, request.EnvironmentKey), JsonOpts);
                    moduleIdForRow = "";
                    testSetIdForRow = "";
                    objectiveIdForRow = null;
                    break;

                default:
                    return Results.BadRequest(new { error = $"Unknown kind '{request.Kind}'. Expected Record, RecordSetup, RecordVerification, or AuthSetup." });
            }

            // If agentId is specified, validate it's online and has the right capability
            if (!string.IsNullOrWhiteSpace(request.AgentId))
            {
                var agent = await agentRepo.GetByIdAsync(request.AgentId);
                if (agent is null)
                    return Results.BadRequest(new { error = $"Agent '{request.AgentId}' is not registered" });
                if (!agent.Capabilities.Contains(request.Target))
                    return Results.BadRequest(new { error = $"Agent '{agent.Name}' does not advertise capability '{request.Target}'" });
            }

            var user = ctx.Items["User"] as User;
            var entry = new RunQueueEntry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                ModuleId = moduleIdForRow,
                TestSetId = testSetIdForRow,
                ObjectiveId = objectiveIdForRow,
                TargetType = request.Target,
                Mode = "",
                JobKind = request.Kind,
                RequestedBy = user?.Id,
                RequestJson = requestJson,
                CreatedAt = DateTime.UtcNow
            };
            await queueRepo.EnqueueAsync(entry);

            return Results.Accepted($"/api/queue/{entry.Id}", new
            {
                jobId = entry.Id,
                status = "Queued",
                jobKind = request.Kind,
                targetType = request.Target,
            });
        });

        return group;
    }
}

public record StartRecordingRequest(
    string Kind,                   // Record | RecordSetup | RecordVerification | AuthSetup
    string Target,                 // UI_Web_MVC | UI_Web_Blazor | UI_Desktop_WinForms
    string? AgentId = null,        // optional — pre-validated when provided
    string? ModuleId = null,
    string? TestSetId = null,
    string? CaseName = null,
    string? ObjectiveId = null,
    string? VerificationName = null,
    int? WaitBeforeSeconds = null,
    int? DeliveryStepIndex = null,
    string? EnvironmentKey = null);
