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
    // WinFormsCloseAppBetweenTests: when true, the app is relaunched for each test case (clean state).
    public string WinFormsAppPath { get; set; } = "";
    public string? WinFormsAppArgs { get; set; }
    public int WinFormsAppLaunchTimeoutSeconds { get; set; } = 30;
    public string? WinFormsScreenshotDir { get; set; }
    public bool WinFormsCloseAppBetweenTests { get; set; } = true;

    // --- Data teardown (per-test-set SQL DELETE statements) ---
    // Global fallback opt-in: applied when an EnvironmentConfig block leaves
    // DataTeardownEnabled unset (null). Defaults to false so legacy configs
    // without any teardown awareness remain safe.
    public bool DataTeardownEnabled { get; set; } = false;

    // --- aseXML (AEMO B2B transactions) ---
    // TemplatesPath: directory containing transaction templates + manifests, grouped by
    //   transaction type. Each template is a .xml with {{tokens}} plus a sibling
    //   .manifest.json describing which fields are auto-generated, user-supplied, or constant.
    // OutputPath: directory where rendered XML payloads are written, one sub-folder per run.
    public AseXmlConfig AseXml { get; set; } = new();
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
