using AiTestCrew.Core.Configuration;

namespace AiTestCrew.Core.Interfaces;

/// <summary>
/// Resolves per-environment (customer) settings. Agents call through this
/// instead of reading URLs / credentials / DB connection strings directly
/// from <see cref="TestEnvironmentConfig"/>.
///
/// Every resolver method falls back to the equivalent top-level field on
/// <see cref="TestEnvironmentConfig"/> when the chosen environment block
/// omits the setting, so legacy single-environment configs keep working.
/// </summary>
public interface IEnvironmentResolver
{
    /// <summary>
    /// Returns the effective environment key given an optional caller-supplied value.
    /// Precedence: supplied key → <see cref="TestEnvironmentConfig.DefaultEnvironment"/>
    /// → first key in <see cref="TestEnvironmentConfig.Environments"/> → "default".
    /// </summary>
    string ResolveKey(string? requested);

    /// <summary>Enumerates configured environment keys in insertion order.</summary>
    IReadOnlyCollection<string> ListKeys();

    /// <summary>Returns the <see cref="EnvironmentConfig"/> for the key, or an empty block when the key is unknown.</summary>
    EnvironmentConfig Resolve(string? key);

    /// <summary>Human-readable display name for the environment (falls back to the key).</summary>
    string ResolveDisplayName(string? key);

    string ResolveLegacyWebUiUrl(string? key);
    string ResolveLegacyWebUiUsername(string? key);
    string ResolveLegacyWebUiPassword(string? key);
    string? ResolveLegacyWebUiStorageStatePath(string? key);

    string ResolveBraveCloudUiUrl(string? key);
    string ResolveBraveCloudUiUsername(string? key);
    string ResolveBraveCloudUiPassword(string? key);
    string? ResolveBraveCloudUiStorageStatePath(string? key);
    string? ResolveBraveCloudUiTotpSecret(string? key);

    string ResolveWinFormsAppPath(string? key);
    string? ResolveWinFormsAppArgs(string? key);

    string ResolveBravoDbConnectionString(string? key);

    /// <summary>
    /// Returns whether SQL data teardown is opted-in for the given environment.
    /// Falls back to <see cref="TestEnvironmentConfig.DataTeardownEnabled"/>
    /// (default false) when the env block leaves it unset.
    /// </summary>
    bool ResolveDataTeardownEnabled(string? key);

    /// <summary>
    /// Returns the API BaseUrl for the given stack, applying the environment's
    /// <see cref="EnvironmentConfig.ApiStackBaseUrls"/> override when present.
    /// Falls back to <c>ApiStacks[stackKey].BaseUrl</c>.
    /// </summary>
    string ResolveApiStackBaseUrl(string? key, string stackKey);
}
