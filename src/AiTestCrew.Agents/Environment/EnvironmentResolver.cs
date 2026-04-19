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

    public bool ResolveDataTeardownEnabled(string? key)
    {
        var env = Resolve(key);
        return env.DataTeardownEnabled ?? _config.DataTeardownEnabled;
    }

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
