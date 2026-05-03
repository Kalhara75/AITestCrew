namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Provides a token for API authentication.
/// Implementations may return a static token or acquire one dynamically.
/// </summary>
public interface ITokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops any cached token so the next <see cref="GetTokenAsync"/> re-acquires.
    /// Called after a 401/403 response to force a refresh from credentials.
    /// No-op for providers that don't cache (e.g. <c>StaticTokenProvider</c>).
    /// </summary>
    Task InvalidateAsync(CancellationToken ct = default);
}
