using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Microsoft.SemanticKernel;
using AiTestCrew.Agents.WebUiBase;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.BraveCloudUiAgent;

/// <summary>
/// Tests the Brave Cloud UI (Blazor) using Playwright.
///
/// Authentication: Azure OpenID SSO — the agent navigates to the application,
/// which redirects to the Azure AD login page. Credentials are filled
/// automatically from config.
///
/// Session persistence: after a successful SSO login the browser auth state
/// (cookies + localStorage) is saved to BraveCloudUiStorageStatePath.
/// Subsequent runs within BraveCloudUiStorageStateMaxAgeHours reuse this
/// saved state and skip the full SSO flow.
///
/// Configuration required (TestEnvironment section):
///   BraveCloudUiUrl                   — root URL of the application
///   BraveCloudUiUsername              — Azure AD account email (MFA must be disabled)
///   BraveCloudUiPassword              — Azure AD account password
///   BraveCloudUiStorageStatePath      — (optional) path for saved auth state JSON
///   BraveCloudUiStorageStateMaxAgeHours — how long saved state is considered valid (default 8)
///
/// IMPORTANT: The test Azure AD account must have MFA disabled or be excluded
/// from MFA via a conditional access policy. Automated flows cannot handle
/// interactive MFA prompts.
/// </summary>
public class BraveCloudUiTestAgent : BaseWebUiTestAgent
{
    public override string Name => "Brave Cloud UI Agent";
    public override string Role =>
        "Senior Web UI Test Engineer specialising in Blazor applications with Azure SSO. " +
        "You write thorough Playwright-based UI tests covering happy path, error handling, " +
        "and boundary conditions.";

    protected override string TargetBaseUrl => _config.BraveCloudUiUrl;
    protected override string TargetBaseUrlConfigKey => "BraveCloudUiUrl";

    public BraveCloudUiTestAgent(
        Kernel kernel,
        ILogger<BraveCloudUiTestAgent> logger,
        TestEnvironmentConfig config)
        : base(kernel, logger, config)
    {
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.UI_Web_Blazor);

    protected override (string Username, string Password)? GetConfiguredCredentials() =>
        string.IsNullOrEmpty(_config.BraveCloudUiUsername)
            ? null
            : (_config.BraveCloudUiUsername, _config.BraveCloudUiPassword);

    /// <summary>
    /// Performs a full Azure SSO login once and saves the resulting storage state so that
    /// subsequent per-test-case contexts (via <see cref="BuildContextOptions"/>) start authenticated.
    /// Skipped when a fresh storage state file already exists.
    /// </summary>
    protected override async Task PerformOneTimeAuthSetupAsync(IBrowser browser, CancellationToken ct)
    {
        if (HasFreshStorageState())
        {
            Logger.LogInformation("[{Agent}] Using saved storage state (skipping SSO login)", Name);
            return;
        }

        // Create a temporary context just for the SSO login flow
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions());
        var page = await context.NewPageAsync();
        await PerformSsoLoginAsync(page, context);
    }

    /// <summary>Provide storage state to the browser context if a fresh file exists.</summary>
    protected override BrowserNewContextOptions BuildContextOptions()
    {
        if (HasFreshStorageState())
            return new BrowserNewContextOptions { StorageStatePath = _config.BraveCloudUiStorageStatePath };
        return new BrowserNewContextOptions();
    }

    private async Task PerformSsoLoginAsync(IPage page, IBrowserContext context)
    {
        Logger.LogInformation("[{Agent}] Performing full SSO login for {Url}", Name, _config.BraveCloudUiUrl);

        // Navigate to the app — it will redirect to Azure AD
        await page.GotoAsync(_config.BraveCloudUiUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for the Microsoft login page
        await page.WaitForURLAsync(
            url => url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 15_000 });

        Logger.LogInformation("[{Agent}] On Azure AD login page — filling credentials", Name);

        // Enter email
        await page.WaitForSelectorAsync("input[type='email']", new PageWaitForSelectorOptions { Timeout = 15_000 });
        await page.FillAsync("input[type='email']", _config.BraveCloudUiUsername);
        await page.ClickAsync("input[type='submit']");

        // Enter password
        await page.WaitForSelectorAsync("input[type='password']", new PageWaitForSelectorOptions { Timeout = 15_000 });
        await page.FillAsync("input[type='password']", _config.BraveCloudUiPassword);
        await page.ClickAsync("input[type='submit']");

        // Handle "Stay signed in?" prompt (kmsi — keep me signed in)
        try
        {
            await page.WaitForSelectorAsync("#KmsiCheckboxField, [id*='stay'], [id*='kmsi']",
                new PageWaitForSelectorOptions { Timeout = 5_000 });
            // Click "Yes" / "Submit" to accept
            await page.ClickAsync("#idSIButton9, [type='submit']");
        }
        catch
        {
            // Prompt not present — continue
        }

        // Wait until redirected back to our application
        await page.WaitForURLAsync(
            url => url.StartsWith(_config.BraveCloudUiUrl, StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 30_000 });

        Logger.LogInformation("[{Agent}] SSO login successful — now at: {Url}", Name, page.Url);

        // Save auth state for future runs
        if (!string.IsNullOrEmpty(_config.BraveCloudUiStorageStatePath))
        {
            var dir = Path.GetDirectoryName(_config.BraveCloudUiStorageStatePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = _config.BraveCloudUiStorageStatePath
            });
            Logger.LogInformation("[{Agent}] Saved auth storage state to: {Path}",
                Name, _config.BraveCloudUiStorageStatePath);
        }
    }

    private bool HasFreshStorageState()
    {
        if (string.IsNullOrEmpty(_config.BraveCloudUiStorageStatePath)) return false;
        if (!File.Exists(_config.BraveCloudUiStorageStatePath)) return false;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_config.BraveCloudUiStorageStatePath);
        return age.TotalHours < _config.BraveCloudUiStorageStateMaxAgeHours;
    }
}
