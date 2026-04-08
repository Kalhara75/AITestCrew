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

    // --- Target application ---
    public string BaseUrl { get; set; } = "https://localhost:5001";
    public string ApiBaseUrl { get; set; } = "https://localhost:5001/api";
    public string? OpenApiSpecUrl { get; set; }

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

    // --- Execution settings ---
    public int MaxParallelAgents { get; set; } = 4;
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public bool VerboseLogging { get; set; } = true;

    // --- Playwright browser settings ---
    public string PlaywrightBrowser { get; set; } = "chromium";  // chromium | firefox | webkit
    public bool PlaywrightHeadless { get; set; } = true;
    public string? PlaywrightScreenshotDir { get; set; }

    // --- Legacy ASP.NET MVC web UI ---
    public string LegacyWebUiUrl { get; set; } = "";
    public string LegacyWebUiLoginPath { get; set; } = "/Account/Login";
    public string LegacyWebUiUsername { get; set; } = "";
    public string LegacyWebUiPassword { get; set; } = "";

    // --- Brave Cloud UI (Blazor / Azure OpenID SSO) ---
    // BraveCloudUiStorageStatePath: path to save/load browser auth state (JSON).
    // On first run the agent performs a full SSO login and saves the state.
    // Subsequent runs within BraveCloudUiStorageStateMaxAgeHours reuse the saved state.
    // NOTE: The test Azure AD account must have MFA disabled (or excluded via conditional access).
    public string BraveCloudUiUrl { get; set; } = "";
    public string? BraveCloudUiStorageStatePath { get; set; }
    public string BraveCloudUiUsername { get; set; } = "";  // AAD email
    public string BraveCloudUiPassword { get; set; } = "";  // AAD password
    public int BraveCloudUiStorageStateMaxAgeHours { get; set; } = 8;
}
