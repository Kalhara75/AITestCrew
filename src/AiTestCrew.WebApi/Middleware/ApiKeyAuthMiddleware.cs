using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.WebApi.Middleware;

/// <summary>
/// Validates the <c>X-Api-Key</c> header on every request (except health check).
/// On success, stores the <see cref="AiTestCrew.Core.Models.User"/> in
/// <c>HttpContext.Items["User"]</c> for downstream use.
/// When no <see cref="IUserRepository"/> is registered (file-based storage mode),
/// the middleware is a no-op — all requests pass through unauthenticated.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Always allow health check, auth status, and the SPA fallback (non-API routes)
        if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // If no user repository is registered, auth is disabled (file-based storage mode)
        var userRepo = context.RequestServices.GetService<IUserRepository>();
        if (userRepo is null)
        {
            await _next(context);
            return;
        }

        // Allow the login validation endpoint
        if (path.Equals("/api/users/validate", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Bootstrap: allow POST /api/users when no users exist yet
        if (path.Equals("/api/users", StringComparison.OrdinalIgnoreCase)
            && context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var users = await userRepo.ListAllAsync();
            if (users.Count == 0)
            {
                await _next(context);
                return;
            }
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader)
            || string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Missing X-Api-Key header" });
            return;
        }

        var user = await userRepo.GetByApiKeyAsync(apiKeyHeader.ToString());
        if (user is null || !user.IsActive)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or inactive API key" });
            return;
        }

        context.Items["User"] = user;
        await _next(context);
    }
}
