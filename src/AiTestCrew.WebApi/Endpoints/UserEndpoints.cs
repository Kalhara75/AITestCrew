using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/users — list all users (API keys are masked in the response)
        group.MapGet("/", async (IUserRepository repo) =>
        {
            var users = await repo.ListAllAsync();
            return Results.Ok(users.Select(u => new
            {
                u.Id, u.Name, u.CreatedAt, u.IsActive,
                ApiKeyPreview = MaskKey(u.ApiKey)
            }));
        });

        // POST /api/users — create a new user, returns the full API key (only time it's shown)
        group.MapPost("/", async (CreateUserRequest request, IUserRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required" });

            var user = await repo.CreateAsync(request.Name);
            return Results.Created($"/api/users/{user.Id}", new
            {
                user.Id, user.Name, user.ApiKey, user.CreatedAt, user.IsActive
            });
        });

        // GET /api/users/me — return the current authenticated user
        group.MapGet("/me", (HttpContext ctx) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Ok(new { authenticated = false });
            return Results.Ok(new
            {
                authenticated = true,
                user = new { user.Id, user.Name, user.CreatedAt, user.IsActive }
            });
        });

        // POST /api/users/validate — validate an API key (used by the login page, exempt from auth)
        group.MapPost("/validate", async (ValidateKeyRequest request, IUserRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
                return Results.BadRequest(new { error = "apiKey is required" });

            var user = await repo.GetByApiKeyAsync(request.ApiKey);
            if (user is null || !user.IsActive)
                return Results.Ok(new { valid = false });

            return Results.Ok(new
            {
                valid = true,
                user = new { user.Id, user.Name, user.CreatedAt }
            });
        });

        // DELETE /api/users/{id}
        group.MapDelete("/{id}", async (string id, IUserRepository repo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"User '{id}' not found" });

            await repo.DeleteAsync(id);
            return Results.NoContent();
        });

        // PUT /api/users/{id}/active — enable or disable a user
        group.MapPut("/{id}/active", async (string id, SetActiveRequest request, IUserRepository repo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"User '{id}' not found" });

            await repo.SetActiveAsync(id, request.IsActive);
            return Results.Ok(new { id, isActive = request.IsActive });
        });

        return group;
    }

    private static string MaskKey(string key) =>
        key.Length > 8 ? string.Concat(key.AsSpan(0, 8), "...") : "***";
}

public record CreateUserRequest(string Name);
public record ValidateKeyRequest(string ApiKey);
public record SetActiveRequest(bool IsActive);
