using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
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
/// When <c>LegacyWebUiStorageStatePath</c> is configured, the agent performs
/// the login once and saves the resulting cookies as Playwright StorageState.
/// Subsequent agent executions (including concurrent ones under parallel mode)
/// reuse the saved state, eliminating login contention.
///
/// Configuration required (TestEnvironment section):
///   LegacyWebUiUrl                     — root URL of the application
///   LegacyWebUiLoginPath               — path to the login page (default: /Account/Login)
///   LegacyWebUiUsername                 — login username
///   LegacyWebUiPassword                — login password
///   LegacyWebUiStorageStatePath         — (optional) path for cached auth state
///   LegacyWebUiStorageStateMaxAgeHours  — TTL in hours (default: 8)
/// </summary>
public class LegacyWebUiTestAgent : BaseWebUiTestAgent
{
    /// <summary>
    /// Serializes the initial login across all concurrent ExecuteAsync calls.
    /// Only one thread performs the Playwright login; others wait then reuse the saved state.
    /// Same double-checked locking pattern as LoginTokenProvider.
    /// </summary>
    private static readonly SemaphoreSlim s_loginGate = new(1, 1);

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

    // ─────────────────────────────────────────────────────
    // Storage state caching (mirrors BraveCloudUiTestAgent)
    // ─────────────────────────────────────────────────────

    protected override async Task PerformOneTimeAuthSetupAsync(IBrowser browser, CancellationToken ct)
    {
        // Feature disabled — fall back to per-test-case login (existing behavior)
        if (string.IsNullOrEmpty(_config.LegacyWebUiStorageStatePath))
            return;

        // Fast path: cached state still fresh
        if (HasFreshStorageState())
        {
            Logger.LogInformation("[{Agent}] Using saved storage state (skipping login)", Name);
            return;
        }

        // Serialize — only one agent instance performs the login at a time
        await s_loginGate.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock (another thread may have completed login)
            if (HasFreshStorageState())
            {
                Logger.LogInformation("[{Agent}] Another agent completed login — reusing saved state", Name);
                return;
            }

            await PerformFormsLoginAsync(browser);
        }
        finally
        {
            s_loginGate.Release();
        }
    }

    protected override BrowserNewContextOptions BuildContextOptions()
    {
        var opts = new BrowserNewContextOptions();
        if (HasFreshStorageState())
            opts.StorageStatePath = ResolveStorageStatePath();
        return opts;
    }

    protected override bool ShouldSkipSetupSteps() => HasFreshStorageState();

    private async Task PerformFormsLoginAsync(IBrowser browser)
    {
        var loginUrl = $"{_config.LegacyWebUiUrl.TrimEnd('/')}{_config.LegacyWebUiLoginPath}";
        Logger.LogInformation("[{Agent}] Performing forms login at {Url}", Name, loginUrl);

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions());
        var page = await context.NewPageAsync();

        // Navigate to login page
        await page.GotoAsync(loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Fill username — try common ASP.NET MVC selectors
        var usernameField = page.Locator("input#UserName, input[name='UserName'], input[name='Email'], input[type='text']").First;
        await usernameField.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await usernameField.FillAsync(_config.LegacyWebUiUsername);

        // Fill password
        var passwordField = page.Locator("input#Password, input[name='Password'], input[type='password']").First;
        await passwordField.FillAsync(_config.LegacyWebUiPassword);

        // Click submit
        var urlBeforeSubmit = page.Url;
        var submitButton = page.Locator("button[type='submit'], input[type='submit']").First;
        await submitButton.ClickAsync();

        // Wait for redirect away from login page
        var loginPath = _config.LegacyWebUiLoginPath.TrimStart('/');
        if (!string.IsNullOrEmpty(loginPath))
        {
            await page.WaitForURLAsync(
                url => !url.Contains(loginPath, StringComparison.OrdinalIgnoreCase),
                new PageWaitForURLOptions { Timeout = 30_000 });
        }
        else
        {
            // LoginPath not configured — wait for URL to change from the login page
            await page.WaitForURLAsync(
                url => !string.Equals(url, urlBeforeSubmit, StringComparison.OrdinalIgnoreCase),
                new PageWaitForURLOptions { Timeout = 30_000 });
        }

        Logger.LogInformation("[{Agent}] Login successful — now at: {Url}", Name, page.Url);

        // Save auth state for future runs
        var resolvedPath = ResolveStorageStatePath();
        if (!string.IsNullOrEmpty(resolvedPath))
        {
            var dir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = resolvedPath
            });
            Logger.LogInformation("[{Agent}] Saved auth storage state to: {Path}", Name, resolvedPath);
        }
    }

    private string? ResolveStorageStatePath()
    {
        if (string.IsNullOrEmpty(_config.LegacyWebUiStorageStatePath)) return null;
        return Path.IsPathRooted(_config.LegacyWebUiStorageStatePath)
            ? _config.LegacyWebUiStorageStatePath
            : Path.Combine(AppContext.BaseDirectory, _config.LegacyWebUiStorageStatePath);
    }

    private bool HasFreshStorageState()
    {
        var path = ResolveStorageStatePath();
        if (path is null || !File.Exists(path)) return false;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        return age.TotalHours < _config.LegacyWebUiStorageStateMaxAgeHours;
    }
}
