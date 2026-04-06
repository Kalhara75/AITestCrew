using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.WebUiBase;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.LegacyWebUiAgent;

/// <summary>
/// Tests the legacy ASP.NET MVC web UI using Playwright.
///
/// Authentication: forms-based login — the agent navigates to the login page,
/// fills username and password from config, and submits the form before any
/// test cases are executed.
///
/// Configuration required (TestEnvironment section):
///   LegacyWebUiUrl      — root URL of the application
///   LegacyWebUiLoginPath — path to the login page (default: /Account/Login)
///   LegacyWebUiUsername  — login username
///   LegacyWebUiPassword  — login password
/// </summary>
public class LegacyWebUiTestAgent : BaseWebUiTestAgent
{
    public override string Name => "Legacy Web UI Agent";
    public override string Role =>
        "Senior Web UI Test Engineer specialising in ASP.NET MVC applications. " +
        "You write thorough Playwright-based UI tests covering happy path, error handling, " +
        "and boundary conditions.";

    protected override string TargetBaseUrl => _config.LegacyWebUiUrl;
    protected override string TargetBaseUrlConfigKey => "LegacyWebUiUrl";

    public LegacyWebUiTestAgent(
        Kernel kernel,
        ILogger<LegacyWebUiTestAgent> logger,
        TestEnvironmentConfig config)
        : base(kernel, logger, config)
    {
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.UI_Web_MVC);

    protected override (string Username, string Password)? GetConfiguredCredentials() =>
        string.IsNullOrEmpty(_config.LegacyWebUiUsername)
            ? null
            : (_config.LegacyWebUiUsername, _config.LegacyWebUiPassword);
}
