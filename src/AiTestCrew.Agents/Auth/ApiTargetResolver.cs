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
    private readonly ConcurrentDictionary<string, ITokenProvider> _stackProviders = new();

    public ApiTargetResolver(
        TestEnvironmentConfig config,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _http = httpClient;
        _loggerFactory = loggerFactory;

        if (_config.ApiStacks.Count == 0)
            throw new InvalidOperationException(
                "ApiStacks must be configured in appsettings.json → TestEnvironment.ApiStacks. " +
                "Define at least one stack with a BaseUrl and Modules.");
    }

    public string ResolveApiBaseUrl(string? stackKey, string? moduleKey)
    {
        var stack = ResolveStack(stackKey);
        var module = ResolveModule(stack, moduleKey);

        return $"{stack.BaseUrl.TrimEnd('/')}/{module.PathPrefix.TrimStart('/')}";
    }

    public ITokenProvider GetTokenProvider(string? stackKey)
    {
        var resolvedKey = ResolveStackKey(stackKey);
        return _stackProviders.GetOrAdd(resolvedKey, key =>
        {
            var stack = _config.ApiStacks[key];
            var loginUrl = BuildLoginUrl(stack);

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

    private static string BuildLoginUrl(ApiStackConfig stack)
    {
        var baseUrl = stack.BaseUrl.TrimEnd('/');

        if (stack.Modules.TryGetValue(stack.SecurityModule, out var secModule))
        {
            var prefix = secModule.PathPrefix.Trim('/');
            return $"{baseUrl}/{prefix}{stack.LoginPath}";
        }

        // Fallback: append login path directly to base URL
        return $"{baseUrl}{stack.LoginPath}";
    }
}
