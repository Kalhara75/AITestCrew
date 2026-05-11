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
    /// Per-customer-environment DB connection registry, keyed by logical
    /// connection name (e.g. <c>"BravoDb"</c>, <c>"SdrReportingDb"</c>). Looked
    /// up by <see cref="AiTestCrew.Core.Interfaces.IEnvironmentResolver.ResolveDbConnectionString"/>;
    /// falls back to <see cref="TestEnvironmentConfig.DbConnections"/> when an
    /// env doesn't override the key, and (for <c>"BravoDb"</c> only) to the
    /// legacy <see cref="BravoDbConnectionString"/>.
    /// </summary>
    public Dictionary<string, string> DbConnections { get; set; } = new();

    /// <summary>
    /// Per-env opt-out for the <c>POST /api/db-check/dry-run</c> endpoint.
    /// Default <c>true</c> — set to <c>false</c> on production-style envs to
    /// disable exploratory queries for the dry-run "Try query" UI button while
    /// still allowing scheduled DB-check post-steps to run as normal.
    /// </summary>
    public bool AllowDbDryRun { get; set; } = true;

    /// <summary>
    /// Per-customer-environment Azure Service Bus namespace registry, keyed by
    /// logical connection name (e.g. <c>"DefaultBus"</c>, <c>"MeterEvents"</c>).
    /// Looked up by
    /// <see cref="AiTestCrew.Core.Interfaces.IEnvironmentResolver.ResolveServiceBusConnection"/>;
    /// falls back to <see cref="TestEnvironmentConfig.ServiceBusConnections"/>
    /// when an env doesn't override the key. Unknown key surfaces as a config
    /// error at runtime (<c>TestStatus.Error</c>), not a data failure.
    /// </summary>
    public Dictionary<string, ServiceBusConnectionConfig> ServiceBusConnections { get; set; } = new();

    /// <summary>
    /// Per-env opt-out for the <c>POST /api/event-assert/peek</c> endpoint.
    /// Default <c>true</c> — set to <c>false</c> on production-style envs to
    /// disable the editor's "Peek messages" UI button while still allowing
    /// scheduled event-assert post-steps to run as normal. Mirrors the
    /// <see cref="AllowDbDryRun"/> precedent.
    /// </summary>
    public bool AllowEventAssertPeek { get; set; } = true;

    /// <summary>
    /// Per-env opt-out for the <c>POST /api/api-step/dry-run</c> endpoint.
    /// Default <c>true</c> -- set to <c>false</c> on production-style envs to
    /// disable exploratory API calls from the editor while still allowing
    /// scheduled API post-steps to run as normal.
    /// Mirrors <see cref="AllowDbDryRun"/> and <see cref="AllowEventAssertPeek"/>.
    /// </summary>
    public bool AllowApiDryRun { get; set; } = true;

    /// <summary>
    /// Opt-in flag for SQL data teardown in this environment. When false
    /// (the default), test-set teardown steps are rejected before any SQL
    /// runs. Set to true only on environments where DELETE statements are safe
    /// (dev/test customer DBs), never on production.
    /// Null = inherit top-level <c>TestEnvironmentConfig.DataTeardownEnabled</c>.
    /// </summary>
    public bool? DataTeardownEnabled { get; set; }

    /// <summary>
    /// Per-env opt-in for executing version-controlled data-pack SQL scripts at
    /// WebApi startup. Default false — set to true only on environments where
    /// dev-authored DDL/DML is safe (dev/test customer DBs), never on production.
    /// </summary>
    public bool RunDataPacksOnStartup { get; set; } = false;

    /// <summary>
    /// Per-stack BaseUrl overrides. Key is the ApiStacks key
    /// (e.g. "bravecloud", "legacy"); value is the BaseUrl that replaces
    /// the ApiStacks[key].BaseUrl when this environment is active.
    /// </summary>
    public Dictionary<string, string> ApiStackBaseUrls { get; set; } = new();

    /// <summary>
    /// Whether this env should appear in the dashboard's pre-flight
    /// auth-health panel. Defaults to true; set to false to hide an env from
    /// the panel — useful for environments you don't actively run UI tests
    /// against (e.g. a customer you only hit via API), so its cached UI auth
    /// state never prompts for attention.
    ///
    /// Effect is twofold: agents skip the env when scanning storage-state
    /// files, AND the WebApi endpoint will not surface tiles for it even if
    /// historical rows exist in <c>agent_auth_state</c>.
    /// </summary>
    public bool AuthHealthEnabled { get; set; } = true;
}
