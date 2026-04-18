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
    /// Environment-aware overload. When <paramref name="environmentKey"/> provides
    /// an override for the stack's BaseUrl via <c>Environments[env].ApiStackBaseUrls</c>,
    /// that URL is used instead of the stack's default.
    /// </summary>
    string ResolveApiBaseUrl(string? stackKey, string? moduleKey, string? environmentKey);

    /// <summary>
    /// Returns a token provider for the specified stack.
    /// Each stack has its own cached LoginTokenProvider pointing at
    /// that stack's Security module login endpoint.
    /// </summary>
    ITokenProvider GetTokenProvider(string? stackKey);

    /// <summary>
    /// Environment-aware overload. The token provider is cached per (env, stack)
    /// pair so that two environments sharing the same stack key still authenticate
    /// against their own base URLs and hold independent tokens.
    /// </summary>
    ITokenProvider GetTokenProvider(string? stackKey, string? environmentKey);

    /// <summary>Auth scheme for requests (e.g. "Bearer"). Shared across all stacks.</summary>
    string GetAuthScheme(string? stackKey);

    /// <summary>Auth header name for requests (e.g. "Authorization"). Shared across all stacks.</summary>
    string GetAuthHeaderName(string? stackKey);
}
