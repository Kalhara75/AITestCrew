using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;

namespace AiTestCrew.Agents.Environment;

/// <summary>
/// Default <see cref="IEnvironmentResolver"/>. Reads per-environment overrides from
/// <see cref="TestEnvironmentConfig.Environments"/>; every field falls back to the
/// legacy top-level field on the config so configs without an Environments section
/// continue to work unchanged.
/// </summary>
public class EnvironmentResolver : IEnvironmentResolver
{
    public const string DefaultFallbackKey = "default";

    private readonly TestEnvironmentConfig _config;

    public EnvironmentResolver(TestEnvironmentConfig config)
    {
        _config = config;
    }

    public string ResolveKey(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)
            && _config.Environments.ContainsKey(requested))
            return requested;

        if (!string.IsNullOrWhiteSpace(_config.DefaultEnvironment)
            && _config.Environments.ContainsKey(_config.DefaultEnvironment))
            return _config.DefaultEnvironment;

        if (_config.Environments.Count > 0)
            return _config.Environments.Keys.First();

        // Legacy single-env mode: synthesise a default key so callers can
        // still thread an EnvironmentKey through the system.
        return !string.IsNullOrWhiteSpace(_config.DefaultEnvironment)
            ? _config.DefaultEnvironment!
            : DefaultFallbackKey;
    }

    public IReadOnlyCollection<string> ListKeys()
    {
        if (_config.Environments.Count > 0)
            return _config.Environments.Keys.ToArray();

        return new[] { ResolveKey(null) };
    }

    public EnvironmentConfig Resolve(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)
            && _config.Environments.TryGetValue(key, out var env))
            return env;

        return new EnvironmentConfig();
    }

    public string ResolveDisplayName(string? key)
    {
        var resolvedKey = ResolveKey(key);
        var env = Resolve(resolvedKey);
        return !string.IsNullOrWhiteSpace(env.DisplayName) ? env.DisplayName : resolvedKey;
    }

    public string ResolveLegacyWebUiUrl(string? key) =>
        Pick(Resolve(key).LegacyWebUiUrl, _config.LegacyWebUiUrl);

    public string ResolveLegacyWebUiUsername(string? key) =>
        Pick(Resolve(key).LegacyWebUiUsername, _config.LegacyWebUiUsername);

    public string ResolveLegacyWebUiPassword(string? key) =>
        Pick(Resolve(key).LegacyWebUiPassword, _config.LegacyWebUiPassword);

    public string? ResolveLegacyWebUiStorageStatePath(string? key) =>
        PickNullable(Resolve(key).LegacyWebUiStorageStatePath, _config.LegacyWebUiStorageStatePath);

    public string ResolveBraveCloudUiUrl(string? key) =>
        Pick(Resolve(key).BraveCloudUiUrl, _config.BraveCloudUiUrl);

    public string ResolveBraveCloudUiUsername(string? key) =>
        Pick(Resolve(key).BraveCloudUiUsername, _config.BraveCloudUiUsername);

    public string ResolveBraveCloudUiPassword(string? key) =>
        Pick(Resolve(key).BraveCloudUiPassword, _config.BraveCloudUiPassword);

    public string? ResolveBraveCloudUiStorageStatePath(string? key) =>
        PickNullable(Resolve(key).BraveCloudUiStorageStatePath, _config.BraveCloudUiStorageStatePath);

    public string? ResolveBraveCloudUiTotpSecret(string? key) =>
        PickNullable(Resolve(key).BraveCloudUiTotpSecret, _config.BraveCloudUiTotpSecret);

    public string ResolveWinFormsAppPath(string? key) =>
        Pick(Resolve(key).WinFormsAppPath, _config.WinFormsAppPath);

    public string? ResolveWinFormsAppArgs(string? key) =>
        PickNullable(Resolve(key).WinFormsAppArgs, _config.WinFormsAppArgs);

    public string ResolveBravoDbConnectionString(string? key) =>
        Pick(Resolve(key).BravoDbConnectionString, _config.AseXml.BravoDb.ConnectionString);

    public string? ResolveDbConnectionString(string connectionKey, string? envKey)
    {
        if (string.IsNullOrWhiteSpace(connectionKey)) return null;

        var env = Resolve(envKey);
        if (env.DbConnections.TryGetValue(connectionKey, out var perEnv)
            && !string.IsNullOrWhiteSpace(perEnv))
            return perEnv;

        if (_config.DbConnections.TryGetValue(connectionKey, out var topLevel)
            && !string.IsNullOrWhiteSpace(topLevel))
            return topLevel;

        // Back-compat: "BravoDb" falls back to the legacy connection string.
        if (string.Equals(connectionKey, "BravoDb", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = ResolveBravoDbConnectionString(envKey);
            if (!string.IsNullOrWhiteSpace(legacy)) return legacy;
        }

        return null;
    }

    public bool ResolveAllowDbDryRun(string? envKey)
    {
        // Unknown env keys are conservative-deny — typos shouldn't accidentally permit.
        if (!string.IsNullOrWhiteSpace(envKey)
            && !_config.Environments.ContainsKey(envKey))
            return false;

        var env = Resolve(envKey);
        return env.AllowDbDryRun;
    }

    public IReadOnlyList<string> ListDbConnectionKeys(string? envKey)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BravoDb" };
        var env = Resolve(envKey);
        foreach (var k in env.DbConnections.Keys) keys.Add(k);
        foreach (var k in _config.DbConnections.Keys) keys.Add(k);
        return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ServiceBusConnectionConfig? ResolveServiceBusConnection(string connectionKey, string? envKey)
    {
        if (string.IsNullOrWhiteSpace(connectionKey)) return null;

        var env = Resolve(envKey);
        if (env.ServiceBusConnections.TryGetValue(connectionKey, out var perEnv)
            && IsUsableConnection(perEnv))
            return perEnv;

        if (_config.ServiceBusConnections.TryGetValue(connectionKey, out var topLevel)
            && IsUsableConnection(topLevel))
            return topLevel;

        return null;
    }

    public bool ResolveAllowEventAssertPeek(string? envKey)
    {
        // Mirrors ResolveAllowDbDryRun: unknown env keys are conservative-deny.
        if (!string.IsNullOrWhiteSpace(envKey)
            && !_config.Environments.ContainsKey(envKey))
            return false;

        var env = Resolve(envKey);
        return env.AllowEventAssertPeek;
    }

    public bool ResolveAllowApiDryRun(string? envKey)
    {
        // Mirrors ResolveAllowDbDryRun: unknown env keys are conservative-deny.
        if (!string.IsNullOrWhiteSpace(envKey)
            && !_config.Environments.ContainsKey(envKey))
            return false;

        var env = Resolve(envKey);
        return env.AllowApiDryRun;
    }

    public IReadOnlyList<string> ListServiceBusConnectionKeys(string? envKey)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var env = Resolve(envKey);
        foreach (var k in env.ServiceBusConnections.Keys) keys.Add(k);
        foreach (var k in _config.ServiceBusConnections.Keys) keys.Add(k);
        return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsUsableConnection(ServiceBusConnectionConfig? c)
    {
        if (c is null) return false;
        return c.AuthMode switch
        {
            ServiceBusAuthMode.ConnectionString => !string.IsNullOrWhiteSpace(c.ConnectionString),
            ServiceBusAuthMode.AzureAd => !string.IsNullOrWhiteSpace(c.FullyQualifiedNamespace),
            _ => false,
        };
    }

    public bool ResolveDataTeardownEnabled(string? key)
    {
        var env = Resolve(key);
        return env.DataTeardownEnabled ?? _config.DataTeardownEnabled;
    }

    public bool ResolveRunDataPacksOnStartup(string? key) =>
        Resolve(key).RunDataPacksOnStartup;

    public string ResolveApiStackBaseUrl(string? key, string stackKey)
    {
        var env = Resolve(key);
        if (env.ApiStackBaseUrls.TryGetValue(stackKey, out var overrideUrl)
            && !string.IsNullOrWhiteSpace(overrideUrl))
            return overrideUrl;

        if (_config.ApiStacks.TryGetValue(stackKey, out var stack))
            return stack.BaseUrl;

        return "";
    }

    public bool ResolveAuthHealthEnabled(string? key) =>
        Resolve(key).AuthHealthEnabled;

    private static string Pick(string? envValue, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue!;
        return fallback ?? "";
    }

    private static string? PickNullable(string? envValue, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue;
        return fallback;
    }
}
