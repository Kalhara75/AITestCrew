namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Provides a token for API authentication.
/// Implementations may return a static token or acquire one dynamically.
/// </summary>
public interface ITokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken ct = default);
}
