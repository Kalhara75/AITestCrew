using AiTestCrew.Core.Models;

namespace AiTestCrew.WebApi.Extensions;

public static class HttpContextExtensions
{
    /// <summary>Returns the authenticated user, or null if auth is disabled.</summary>
    public static User? GetCurrentUser(this HttpContext context) =>
        context.Items["User"] as User;

    /// <summary>Returns the authenticated user's ID, or null.</summary>
    public static string? GetCurrentUserId(this HttpContext context) =>
        (context.Items["User"] as User)?.Id;
}
