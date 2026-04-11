namespace AiTestCrew.Core.Configuration;

/// <summary>
/// Defines a single API stack — a deployment with a base URL,
/// an auth endpoint (via its Security module), and a set of API modules (path prefixes).
/// Configured in appsettings.json under TestEnvironment.ApiStacks.
/// </summary>
public class ApiStackConfig
{
    /// <summary>Base URL for this stack, e.g. "https://sumo-dev.braveenergy.com.au".</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Key of the module within this stack that provides the auth/login endpoint.
    /// E.g. "security". The login URL is resolved as:
    /// {BaseUrl}/{Modules[SecurityModule].PathPrefix}{LoginPath}
    /// </summary>
    public string SecurityModule { get; set; } = "security";

    /// <summary>
    /// Path appended to the security module's base URL to form the login endpoint.
    /// Defaults to "/AccessManagement/Login".
    /// </summary>
    public string LoginPath { get; set; } = "/AccessManagement/Login";

    /// <summary>
    /// Dictionary of API modules within this stack.
    /// Key is a short identifier (e.g. "sdr", "security", "mds", "eb2b").
    /// </summary>
    public Dictionary<string, ApiModuleConfig> Modules { get; set; } = new();
}

/// <summary>
/// A single API module within a stack — defines the URL path prefix.
/// </summary>
public class ApiModuleConfig
{
    /// <summary>Human-readable display name, e.g. "Standing Data Replication (BraveCloud)".</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Path prefix appended to the stack's BaseUrl.
    /// E.g. "sdrbc/api/v1" — the full URL becomes "{StackBaseUrl}/{PathPrefix}/{endpoint}".
    /// </summary>
    public string PathPrefix { get; set; } = "";
}
