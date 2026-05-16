using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/users -- list all users (API keys are masked in the response)
        group.MapGet("/", async (IUserRepository repo) =>
        {
            var users = await repo.ListAllAsync();
            return Results.Ok(users.Select(u => new
            {
                u.Id, u.Name, u.CreatedAt, u.IsActive, u.Role,
                ApiKeyPreview = MaskKey(u.ApiKey)
            }));
        });

        // POST /api/users -- create a new user, returns the full API key (only time it is shown)
        group.MapPost("/", async (CreateUserRequest request, IUserRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required" });

            var user = await repo.CreateAsync(request.Name);
            return Results.Created($"/api/users/{user.Id}", new
            {
                user.Id, user.Name, user.ApiKey, user.CreatedAt, user.IsActive, user.Role
            });
        });

        // GET /api/users/me -- return the current authenticated user with role
        group.MapGet("/me", (HttpContext ctx) =>
        {
            var user = ctx.Items["User"] as User;
            if (user is null) return Results.Ok(new { authenticated = false });
            return Results.Ok(new
            {
                authenticated = true,
                user = new { user.Id, user.Name, user.CreatedAt, user.IsActive, user.Role }
            });
        });

        // POST /api/users/validate -- validate an API key (used by the login page, exempt from auth)
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
                user = new { user.Id, user.Name, user.CreatedAt, user.Role }
            });
        });

        // DELETE /api/users/{id}
        group.MapDelete("/{id}", async (string id, IUserRepository repo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"User '{id}' not found" });

            await repo.DeleteAsync(id);
            return Results.NoContent();
        });

        // PUT /api/users/{id}/active -- enable or disable a user
        group.MapPut("/{id}/active", async (string id, SetActiveRequest request, IUserRepository repo) =>
        {
            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"User '{id}' not found" });

            await repo.SetActiveAsync(id, request.IsActive);
            return Results.Ok(new { id, isActive = request.IsActive });
        });

        // PUT /api/users/{id}/role -- Admin-only. Promote or demote a user role.
        // Self-demotion from Admin is rejected when the caller is the only remaining admin.
        group.MapPut("/{id}/role", async (string id, SetRoleRequest request,
            IUserRepository repo, HttpContext ctx) =>
        {
            var validRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "User", "AuthSteward", "Admin" };
            if (string.IsNullOrWhiteSpace(request.Role) || !validRoles.Contains(request.Role))
                return Results.BadRequest(new { error = "role must be User, AuthSteward, or Admin" });

            var me = ctx.Items["User"] as User;
            if (me is not null && me.Role != "Admin")
                return Results.Problem(title: "Forbidden", detail: "Only admins can change user roles", statusCode: 403);

            var existing = await repo.GetByIdAsync(id);
            if (existing is null) return Results.NotFound(new { error = $"User '{id}' not found" });

            // Last-admin guard: prevent the only remaining admin from demoting themselves.
            if (me is not null && me.Id == id && existing.Role == "Admin" && request.Role != "Admin")
            {
                var adminCount = await repo.CountAdminsAsync();
                if (adminCount <= 1)
                    return Results.Problem(
                        title: "Forbidden",
                        detail: "Cannot demote the only remaining admin. Promote another user to Admin first.",
                        statusCode: 403);
            }

            // Normalise casing to the canonical values.
            var normalised = validRoles.Contains(request.Role) ? request.Role : request.Role;
            await repo.SetRoleAsync(id, normalised);
            return Results.Ok(new { id, role = normalised });
        });

        return group;
    }

    private static string MaskKey(string key) =>
        key.Length > 8 ? string.Concat(key.AsSpan(0, 8), "...") : "***";
}

public record CreateUserRequest(string Name);
public record ValidateKeyRequest(string ApiKey);
public record SetActiveRequest(bool IsActive);
public record SetRoleRequest(string Role);
