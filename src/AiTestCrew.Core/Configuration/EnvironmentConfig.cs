namespace AiTestCrew.Core.Configuration;

/// <summary>
/// Per-environment (customer) settings block. Identified by a slug key
/// ("sumo-retail", "ams-metering", "tasn-networks") under
/// TestEnvironment.Environments in appsettings.json.
///
/// Every field is optional — when a field is null/empty, the
/// <see cref="AiTestCrew.Core.Interfaces.IEnvironmentResolver"/> falls
/// back to the equivalent top-level field on
/// <see cref="TestEnvironmentConfig"/>. This lets legacy configs (with
/// no Environments section at all) keep working unchanged.
/// </summary>
public class EnvironmentConfig
{
    /// <summary>Human-readable name shown in lists (e.g. "Sumo Retail").</summary>
    public string DisplayName { get; set; } = "";

    // --- Legacy ASP.NET MVC web UI ---
    public string? LegacyWebUiUrl { get; set; }
    public string? LegacyWebUiUsername { get; set; }
    public string? LegacyWebUiPassword { get; set; }
    public string? LegacyWebUiStorageStatePath { get; set; }

    // --- Brave Cloud UI (Blazor / Azure OpenID SSO) ---
    public string? BraveCloudUiUrl { get; set; }
    public string? BraveCloudUiUsername { get; set; }
    public string? BraveCloudUiPassword { get; set; }
    public string? BraveCloudUiStorageStatePath { get; set; }
    public string? BraveCloudUiTotpSecret { get; set; }

    // --- WinForms Desktop UI ---
    public string? WinFormsAppPath { get; set; }
    public string? WinFormsAppArgs { get; set; }

    // --- Bravo application DB (aseXML delivery endpoint resolution) ---
    public string? BravoDbConnectionString { get; set; }

    /// <summary>
    /// Per-stack BaseUrl overrides. Key is the ApiStacks key
    /// (e.g. "bravecloud", "legacy"); value is the BaseUrl that replaces
    /// the ApiStacks[key].BaseUrl when this environment is active.
    /// </summary>
    public Dictionary<string, string> ApiStackBaseUrls { get; set; } = new();
}
