using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.Auth;

/// <summary>
/// Resolves API base URLs and creates per-stack token providers
/// from <see cref="TestEnvironmentConfig.ApiStacks"/> configuration.
/// </summary>
public class ApiTargetResolver : IApiTargetResolver
{
    private readonly TestEnvironmentConfig _config;
    private readonly HttpClient _http;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEnvironmentResolver _envResolver;
    private readonly ConcurrentDictionary<string, ITokenProvider> _stackProviders = new();

    public ApiTargetResolver(
        TestEnvironmentConfig config,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        IEnvironmentResolver envResolver)
    {
        _config = config;
        _http = httpClient;
        _loggerFactory = loggerFactory;
        _envResolver = envResolver;

        if (_config.ApiStacks.Count == 0)
            throw new InvalidOperationException(
                "ApiStacks must be configured in appsettings.json → TestEnvironment.ApiStacks. " +
                "Define at least one stack with a BaseUrl and Modules.");
    }

    public string ResolveApiBaseUrl(string? stackKey, string? moduleKey)
        => ResolveApiBaseUrl(stackKey, moduleKey, null);

    public string ResolveApiBaseUrl(string? stackKey, string? moduleKey, string? environmentKey)
    {
        var stack = ResolveStack(stackKey);
        var module = ResolveModule(stack, moduleKey);

        var resolvedKey = ResolveStackKey(stackKey);
        var baseUrl = _envResolver.ResolveApiStackBaseUrl(environmentKey, resolvedKey);
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = stack.BaseUrl;

        return $"{baseUrl.TrimEnd('/')}/{module.PathPrefix.TrimStart('/')}";
    }

    public ITokenProvider GetTokenProvider(string? stackKey)
        => GetTokenProvider(stackKey, null);

    public ITokenProvider GetTokenProvider(string? stackKey, string? environmentKey)
    {
        var resolvedStackKey = ResolveStackKey(stackKey);
        var resolvedEnvKey = _envResolver.ResolveKey(environmentKey);
        var cacheKey = $"{resolvedEnvKey}|{resolvedStackKey}";

        return _stackProviders.GetOrAdd(cacheKey, _ =>
        {
            var stack = _config.ApiStacks[resolvedStackKey];
            var envBaseUrl = _envResolver.ResolveApiStackBaseUrl(resolvedEnvKey, resolvedStackKey);
            var effectiveBaseUrl = string.IsNullOrWhiteSpace(envBaseUrl) ? stack.BaseUrl : envBaseUrl;
            var loginUrl = BuildLoginUrl(effectiveBaseUrl, stack);

            if (!string.IsNullOrWhiteSpace(_config.AuthUsername)
                && !string.IsNullOrWhiteSpace(_config.AuthPassword)
                && string.IsNullOrWhiteSpace(_config.AuthToken))
            {
                return new LoginTokenProvider(
                    _http, loginUrl,
                    _config.AuthUsername!, _config.AuthPassword!,
                    _loggerFactory.CreateLogger<LoginTokenProvider>());
            }
            return new StaticTokenProvider(_config.AuthToken);
        });
    }

    public string GetAuthScheme(string? stackKey) => _config.AuthScheme;

    public string GetAuthHeaderName(string? stackKey) => _config.AuthHeaderName;

    private string ResolveStackKey(string? stackKey)
    {
        if (!string.IsNullOrEmpty(stackKey) && _config.ApiStacks.ContainsKey(stackKey))
            return stackKey;

        if (!string.IsNullOrEmpty(_config.DefaultApiStack)
            && _config.ApiStacks.ContainsKey(_config.DefaultApiStack))
            return _config.DefaultApiStack;

        return _config.ApiStacks.Keys.First();
    }

    private ApiStackConfig ResolveStack(string? stackKey)
    {
        var key = ResolveStackKey(stackKey);
        return _config.ApiStacks[key];
    }

    private ApiModuleConfig ResolveModule(ApiStackConfig stack, string? moduleKey)
    {
        if (!string.IsNullOrEmpty(moduleKey) && stack.Modules.TryGetValue(moduleKey, out var mod))
            return mod;

        if (!string.IsNullOrEmpty(_config.DefaultApiModule)
            && stack.Modules.TryGetValue(_config.DefaultApiModule, out var defMod))
            return defMod;

        return stack.Modules.Values.First();
    }

    private static string BuildLoginUrl(string rawBaseUrl, ApiStackConfig stack)
    {
        var baseUrl = rawBaseUrl.TrimEnd('/');

        if (stack.Modules.TryGetValue(stack.SecurityModule, out var secModule))
        {
            var prefix = secModule.PathPrefix.Trim('/');
            return $"{baseUrl}/{prefix}{stack.LoginPath}";
        }

        // Fallback: append login path directly to base URL
        return $"{baseUrl}{stack.LoginPath}";
    }
}
