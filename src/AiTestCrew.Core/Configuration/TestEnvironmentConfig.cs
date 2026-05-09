using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AiTestCrew.Core.Configuration;

/// <summary>
/// All environment-specific settings for the test crew.
/// Bound from appsettings.json -> "TestEnvironment" section.
/// </summary>
public class TestEnvironmentConfig
{
    // --- LLM settings ---
    public string LlmProvider { get; set; } = "OpenAI";  // "OpenAI" or "Azure"
    public string LlmEndpoint { get; set; } = "https://api.openai.com/v1";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "gpt-4o";
    // Note: For Claude, use a proxy like LiteLLM that exposes
    // an OpenAI-compatible endpoint pointing to Anthropic's API.
    // Or use the Anthropic SK connector (community package).

    // --- API stacks ---
    // Each stack defines a base URL, security module (auth endpoint), and a set of
    // API modules (path prefixes). Auth credentials below are shared across all stacks.
    public Dictionary<string, ApiStackConfig> ApiStacks { get; set; } = new();
    public string? DefaultApiStack { get; set; }
    public string? DefaultApiModule { get; set; }
    public string? OpenApiSpecUrl { get; set; }

    // --- Environments (customers) ---
    // Each environment provides per-customer overrides for UI URLs, credentials,
    // WinForms app path, Bravo DB connection string, and per-stack BaseUrls.
    // Empty dictionary = single-environment mode; top-level flat fields are used as-is.
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = new();

    // The environment key used when no explicit --environment is passed and the
    // test set has no persisted EnvironmentKey. Falls back to the first key in
    // Environments, or to the implicit single-env "default" when Environments is empty.
    public string? DefaultEnvironment { get; set; }

    // --- Authentication ---
    // Set AuthToken to inject credentials into every request automatically.
    // AuthScheme: "Bearer" (default), "Basic", or "None"
    // AuthHeaderName: header to use — defaults to "Authorization".
    //   For API-key-style auth (e.g. X-Api-Key) change AuthScheme to "None"
    //   and set AuthHeaderName + AuthToken together.
    public string? AuthToken { get; set; }
    public string AuthScheme { get; set; } = "Bearer";
    public string AuthHeaderName { get; set; } = "Authorization";
    public string? AuthUsername { get; set; }
    public string? AuthPassword { get; set; }

    // --- Server (WebApi hosting) ---
    // ListenUrl: the URL(s) the WebApi binds to. Multiple URLs separated by semicolons.
    // When empty, the WebApi defaults to "http://localhost:5050".
    public string ListenUrl { get; set; } = "";
    // CorsOrigins: allowed CORS origins. Empty = allow Vite dev defaults (localhost:5173, localhost:3000).
    // Set to "*" to allow any origin, or provide a semicolon-separated list.
    public string[] CorsOrigins { get; set; } = [];

    // --- Remote server (Runner API client mode) ---
    // When ServerUrl is set, the Runner CLI uses HTTP calls to the WebApi instead of
    // accessing storage directly. Used for distributed recording.
    public string ServerUrl { get; set; } = "";
    // API key for authenticating with the remote server
    public string ApiKey { get; set; } = "";

    // --- Storage ---
    // StorageProvider: "File" (default, backward compatible) or "Sqlite"
    public string StorageProvider { get; set; } = "File";
    // SqliteConnectionString: required when StorageProvider = "Sqlite"
    // e.g. "Data Source=C:/data/aitestcrew.db"
    public string SqliteConnectionString { get; set; } = "";

    // --- Execution settings ---
    public int MaxParallelAgents { get; set; } = 4;
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public bool VerboseLogging { get; set; } = true;
    public int MaxExecutionRunsPerTestSet { get; set; } = 10;

    // --- Distributed execution (Phase 4) ---
    // Agents send heartbeats; if one is silent longer than this, the server marks it Offline.
    public int AgentHeartbeatTimeoutSeconds { get; set; } = 120;

    // --- Runner agent mode (Phase 4) ---
    // Agent-mode identifier — persists across restarts so the same machine keeps the same id.
    public string AgentName { get; set; } = "";
    // Target types this agent can execute (e.g. "UI_Web_Blazor,UI_Web_MVC,UI_Desktop_WinForms").
    // Empty = default to all three UI target types.
    public string AgentCapabilities { get; set; } = "";
    // Poll cadence when idle.
    public int AgentPollIntervalSeconds { get; set; } = 10;
    // Heartbeat cadence.
    public int AgentHeartbeatIntervalSeconds { get; set; } = 30;

    // --- Playwright browser settings ---
    public string PlaywrightBrowser { get; set; } = "chromium";  // chromium | firefox | webkit
    public bool PlaywrightHeadless { get; set; } = true;
    public string? PlaywrightScreenshotDir { get; set; }

    // --- Legacy ASP.NET MVC web UI ---
    public string LegacyWebUiUrl { get; set; } = "";
    public string LegacyWebUiLoginPath { get; set; } = "/Account/Login";
    public string LegacyWebUiUsername { get; set; } = "";
    public string LegacyWebUiPassword { get; set; } = "";
    public string? LegacyWebUiStorageStatePath { get; set; }
    public int LegacyWebUiStorageStateMaxAgeHours { get; set; } = 8;

    // --- Brave Cloud UI (Blazor / Azure OpenID SSO) ---
    // BraveCloudUiStorageStatePath: path to save/load browser auth state (JSON).
    // On first run the agent performs a full SSO login and saves the state.
    // Subsequent runs within BraveCloudUiStorageStateMaxAgeHours reuse the saved state.
    // When BraveCloudUiTotpSecret is set, the agent handles Azure AD MFA automatically.
    // When empty and MFA is encountered: headless=false → manual entry; headless=true → fail-fast.
    public string BraveCloudUiUrl { get; set; } = "";
    public string? BraveCloudUiStorageStatePath { get; set; }
    public string BraveCloudUiUsername { get; set; } = "";  // AAD email
    public string BraveCloudUiPassword { get; set; } = "";  // AAD password
    public int BraveCloudUiStorageStateMaxAgeHours { get; set; } = 8;
    public string? BraveCloudUiTotpSecret { get; set; }     // Base32 TOTP secret for Azure AD MFA

    // --- WinForms Desktop UI ---
    // WinFormsAppPath: full path to the .exe to launch for desktop UI testing.
    // WinFormsAppArgs: optional command-line arguments to pass to the app.
    public string WinFormsAppPath { get; set; } = "";
    public string? WinFormsAppArgs { get; set; }
    public int WinFormsAppLaunchTimeoutSeconds { get; set; } = 30;
    public string? WinFormsScreenshotDir { get; set; }

    // Window-size normalization: forces the app's main window to a known
    // (width, height) at launch and on every detected window transition (e.g.
    // login → main form). Both the recorder and replay engine apply identical
    // normalization, so window-relative click coordinates are portable across
    // monitors / resolutions / DPI settings. Set NormalizeWindow=false to
    // honour whatever size the app picks for itself (legacy behaviour).
    public bool WinFormsNormalizeWindow { get; set; } = true;
    public int WinFormsWindowWidth { get; set; } = 1600;
    public int WinFormsWindowHeight { get; set; } = 900;

    // --- Data teardown (per-test-set SQL DELETE statements) ---
    // Global fallback opt-in: applied when an EnvironmentConfig block leaves
    // DataTeardownEnabled unset (null). Defaults to false so legacy configs
    // without any teardown awareness remain safe.
    public bool DataTeardownEnabled { get; set; } = false;

    /// <summary>
    /// Top-level DB connection registry — fallback when a per-env block
    /// (<see cref="EnvironmentConfig.DbConnections"/>) doesn't define the key.
    /// Used by <see cref="AiTestCrew.Core.Interfaces.IEnvironmentResolver.ResolveDbConnectionString"/>.
    /// Entries are keyed by logical connection name (e.g. <c>"BravoDb"</c>,
    /// <c>"SdrReportingDb"</c>).
    /// </summary>
    public Dictionary<string, string> DbConnections { get; set; } = new();

    /// <summary>
    /// Top-level Azure Service Bus namespace registry — fallback when a
    /// per-env block (<see cref="EnvironmentConfig.ServiceBusConnections"/>)
    /// doesn't define the key. Used by
    /// <see cref="AiTestCrew.Core.Interfaces.IEnvironmentResolver.ResolveServiceBusConnection"/>.
    /// </summary>
    public Dictionary<string, ServiceBusConnectionConfig> ServiceBusConnections { get; set; } = new();

    /// <summary>
    /// Stored-procedure name prefixes that teardown SQL is allowed to invoke
    /// via <c>EXEC</c>. Procs whose name does not start with one of these
    /// prefixes (case-insensitive) are rejected by <c>SqlGuardrails</c>.
    /// Default: <c>["usp_"]</c> — pairs with the standard convention for
    /// dev-installed teardown procs shipped via the data-pack runner. Set to
    /// an empty array to disallow EXEC entirely.
    /// </summary>
    public string[] TeardownExecAllowedPrefixes { get; set; } = ["usp_"];

    // --- Data packs (version-controlled SQL scripts run at WebApi startup) ---
    /// <summary>
    /// Path to the datapacks root. Relative paths resolve against
    /// <c>AppContext.BaseDirectory</c> (the build-output bin/ folder).
    /// Layout: <c>{DataPacksPath}/{phase}/{envKey}/{NN.subfolder}/{NN.script}.sql</c>
    /// where {phase} is "datateardown" or "datapreparation".
    /// Per-env opt-in via <see cref="EnvironmentConfig.RunDataPacksOnStartup"/>.
    /// </summary>
    public string DataPacksPath { get; set; } = "datapacks";

    // --- aseXML (AEMO B2B transactions) ---
    // TemplatesPath: directory containing transaction templates + manifests, grouped by
    //   transaction type. Each template is a .xml with {{tokens}} plus a sibling
    //   .manifest.json describing which fields are auto-generated, user-supplied, or constant.
    // OutputPath: directory where rendered XML payloads are written, one sub-folder per run.
    public AseXmlConfig AseXml { get; set; } = new();

    // --- Chat (Assistant drawer persistence) ---
    public ChatConfig Chat { get; set; } = new();

    /// <summary>
    /// Seamless authentication recovery. Tunes silent auto-recovery on auth failure,
    /// the URL patterns that flag a login redirect mid-test, and timeouts for the
    /// pause-and-resume dispatcher.
    /// </summary>
    public AuthRecoveryConfig Auth { get; set; } = new();
}

/// <summary>
/// Tunables for seamless authentication recovery (see Auth Recovery model).
/// </summary>
public class AuthRecoveryConfig
{
    /// <summary>
    /// When true, a 401/403 response invalidates the cached JWT and retries the
    /// request once before failing. Almost always sufficient for service-account
    /// JWTs that have rotated mid-flight.
    /// </summary>
    public bool AutoRecoverApi { get; set; } = true;

    /// <summary>
    /// When true, a mid-test login redirect deletes the cached storage state and
    /// re-runs the existing TOTP-automated login. Requires
    /// <c>BraveCloudUiTotpSecret</c> / valid creds for fully silent recovery.
    /// </summary>
    public bool AutoRecoverUi { get; set; } = true;

    /// <summary>
    /// Substrings that, when present in the page URL after a UI step, indicate
    /// the session has been bumped to a login screen. Matched case-insensitively.
    /// Defaults cover Azure AD SSO and the Legacy MVC forms login.
    /// </summary>
    public string[] LoginRedirectUrlPatterns { get; set; } =
    [
        "login.microsoftonline.com",
        "/Account/Login"
    ];

    /// <summary>
    /// Maximum time an in-progress auth-refresh request may run before the
    /// janitor marks it Failed and propagates the failure to dependent runs.
    /// </summary>
    public int AuthRefreshMaxLatencySeconds { get; set; } = 300;

    /// <summary>
    /// When true (default), runs that hit <c>AuthRequiredException</c> are parked
    /// in <c>AwaitingAuth</c> pending an auth-refresh. Set false to skip the
    /// dispatcher and finalise as Failed (escape hatch for headless CI).
    /// </summary>
    public bool PauseOnAuthFailure { get; set; } = true;

    /// <summary>
    /// Pre-flight warning window. The dashboard's auth-health panel surfaces a
    /// cached storage-state file as <c>ExpiringSoon</c> when its age is within
    /// this many hours of the per-surface TTL (e.g. 7h on a default 8h TTL).
    /// Bump to 4 if you want more lead time before firing the warning.
    /// </summary>
    public int ExpiryWarningHours { get; set; } = 1;
}

/// <summary>
/// Tunables for the persisted Assistant conversation feature
/// (see <see cref="AiTestCrew.Core.Interfaces.IChatConversationRepository"/>).
/// </summary>
public class ChatConfig
{
    /// <summary>
    /// How many conversations to retain per user. Older threads beyond this
    /// cap are auto-pruned on the next conversation-create. Set to 0 to disable
    /// the cap (unbounded — not recommended).
    /// </summary>
    public int MaxConversationsPerUser { get; set; } = 20;

    /// <summary>
    /// How many of the most recent messages from a conversation to feed back
    /// to the LLM as history on each turn. The DB still keeps every message
    /// for replay in the UI; this only bounds prompt size.
    /// </summary>
    public int MaxMessagesPerConversation { get; set; } = 200;
}

/// <summary>
/// Configuration for the aseXML generation and delivery agents.
/// </summary>
public class AseXmlConfig
{
    /// <summary>Path to the templates root. Relative paths are resolved against AppContext.BaseDirectory.</summary>
    public string TemplatesPath { get; set; } = "templates/asexml";

    /// <summary>Path where rendered XML (and, for zipped endpoints, the archive) is written. Relative paths are resolved against AppContext.BaseDirectory.</summary>
    public string OutputPath { get; set; } = "output/asexml";

    /// <summary>Bravo application database used by the delivery agent to resolve endpoint connection details.</summary>
    public BravoDbConfig BravoDb { get; set; } = new();

    /// <summary>Upload timeout (per file) in seconds for the delivery agent.</summary>
    public int DeliveryTimeoutSeconds { get; set; } = 60;

    /// <summary>Retry count for a single upload attempt. Zero means fail-fast.</summary>
    public int DeliveryRetryCount { get; set; }

    /// <summary>
    /// Default wait (seconds) before a post-delivery UI verification runs, when
    /// the recording command doesn't pass --wait. Allows Bravo time to consume
    /// the dropped file before UI assertions query it.
    /// </summary>
    public int DefaultVerificationWaitSeconds { get; set; } = 30;

    /// <summary>
    /// When true (default), post-delivery UI verifications with a wait greater than
    /// <see cref="VerificationDeferThresholdSeconds"/> are queued with a future
    /// <c>not_before_at</c> so the delivery agent slot is freed immediately. When
    /// false, the delivery agent blocks on <c>Task.Delay</c> and runs the verification
    /// inline (legacy behaviour, useful for local debugging).
    /// </summary>
    public bool DeferVerifications { get; set; } = true;

    /// <summary>
    /// Waits ≤ this value run synchronously on the delivery agent even when
    /// <see cref="DeferVerifications"/> is true — the queueing/agent-hop overhead
    /// isn't worth it for short delays.
    /// </summary>
    public int VerificationDeferThresholdSeconds { get; set; } = 30;

    /// <summary>
    /// Fraction of <c>WaitBeforeSeconds</c> at which the FIRST deferred verification
    /// attempt runs. A value of 0.5 means the first attempt fires at half the configured
    /// wait; if it fails, retries re-enqueue every <see cref="VerificationRetryIntervalSeconds"/>
    /// until <c>WaitBeforeSeconds + VerificationGraceSeconds</c> elapses.
    /// </summary>
    public double VerificationEarlyStartFraction { get; set; } = 0.5;

    /// <summary>Gap between failed deferred-verification attempts. Keeps the re-enqueue cadence predictable.</summary>
    public int VerificationRetryIntervalSeconds { get; set; } = 30;

    /// <summary>Added to <c>WaitBeforeSeconds</c> to form the absolute deadline past which a final failure is recorded.</summary>
    public int VerificationGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum time a pending verification may stay in the queue without being claimed
    /// by an agent. Past this, the janitor marks it Failed and finalises the parent run.
    /// </summary>
    public int VerificationMaxLatencySeconds { get; set; } = 3600;

    /// <summary>Poll cadence (seconds) for the CLI live-view while waiting on deferred verifications.</summary>
    public int DeferredPollCliIntervalSeconds { get; set; } = 10;
}

/// <summary>
/// Bravo application DB connection used to resolve SFTP/FTP endpoint details for the delivery agent.
/// </summary>
public class BravoDbConfig
{
    /// <summary>
    /// SQL Server connection string for the Bravo application DB.
    /// Stored in appsettings.json only — never in appsettings.example.json.
    /// </summary>
    public string ConnectionString { get; set; } = "";
}
