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

    // --- Execution settings ---
    public int MaxParallelAgents { get; set; } = 4;
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public bool VerboseLogging { get; set; } = true;
}
