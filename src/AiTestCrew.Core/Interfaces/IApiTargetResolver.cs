namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Resolves API base URLs and token providers from the ApiStacks configuration.
/// Each stack defines a base URL, security module (auth endpoint), and API modules (path prefixes).
/// </summary>
public interface IApiTargetResolver
{
    /// <summary>
    /// Resolves the full API base URL for the specified stack and module.
    /// Returns "{stack.BaseUrl}/{module.PathPrefix}".
    /// When null, uses DefaultApiStack/DefaultApiModule from config.
    /// </summary>
    string ResolveApiBaseUrl(string? stackKey, string? moduleKey);

    /// <summary>
    /// Returns a token provider for the specified stack.
    /// Each stack has its own cached LoginTokenProvider pointing at
    /// that stack's Security module login endpoint.
    /// </summary>
    ITokenProvider GetTokenProvider(string? stackKey);

    /// <summary>Auth scheme for requests (e.g. "Bearer"). Shared across all stacks.</summary>
    string GetAuthScheme(string? stackKey);

    /// <summary>Auth header name for requests (e.g. "Authorization"). Shared across all stacks.</summary>
    string GetAuthHeaderName(string? stackKey);
}
