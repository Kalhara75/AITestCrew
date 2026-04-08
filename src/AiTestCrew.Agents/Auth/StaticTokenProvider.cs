using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.Auth;

/// <summary>
/// Returns a pre-configured static token. Used when AuthToken is set directly in config.
/// </summary>
public class StaticTokenProvider : ITokenProvider
{
    private readonly string? _token;

    public StaticTokenProvider(string? token) => _token = token;

    public Task<string?> GetTokenAsync(CancellationToken ct = default)
        => Task.FromResult(_token);
}
