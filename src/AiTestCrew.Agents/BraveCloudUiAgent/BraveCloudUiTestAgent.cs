using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Microsoft.SemanticKernel;
using OtpNet;
using AiTestCrew.Agents.WebUiBase;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.BraveCloudUiAgent;

/// <summary>
/// Tests the Brave Cloud UI (Blazor) using Playwright.
///
/// Authentication: Azure OpenID SSO — the agent navigates to the application,
/// which redirects to the Azure AD login page. Credentials are filled
/// automatically from config.
///
/// MFA / TOTP support (three modes):
///   1. Fully automated — set BraveCloudUiTotpSecret to the base32 shared secret.
///      The agent computes the 6-digit TOTP code and enters it automatically.
///   2. Semi-automated — leave TotpSecret empty, set PlaywrightHeadless=false.
///      The agent pauses on the MFA screen for manual code entry (up to 120 s).
///   3. Fail-fast — leave TotpSecret empty with PlaywrightHeadless=true.
///      Throws immediately with remediation instructions.
///
/// Session persistence: after a successful SSO login the browser auth state
/// (cookies + localStorage) is saved to BraveCloudUiStorageStatePath.
/// Subsequent runs within BraveCloudUiStorageStateMaxAgeHours reuse this
/// saved state and skip the full SSO flow.
///
/// Configuration required (TestEnvironment section):
///   BraveCloudUiUrl                      — root URL of the application
///   BraveCloudUiUsername                 — Azure AD account email
///   BraveCloudUiPassword                 — Azure AD account password
///   BraveCloudUiTotpSecret               — (optional) base32 TOTP secret for automated MFA
///   BraveCloudUiStorageStatePath         — (optional) path for saved auth state JSON
///   BraveCloudUiStorageStateMaxAgeHours  — how long saved state is considered valid (default 8)
/// </summary>
public class BraveCloudUiTestAgent : BaseWebUiTestAgent
{
    public override string Name => "Brave Cloud UI Agent";
    public override string Role =>
        "Senior Web UI Test Engineer specialising in Blazor applications with Azure SSO. " +
        "You write thorough Playwright-based UI tests covering happy path, error handling, " +
        "and boundary conditions.";

    protected override string TargetBaseUrl => _envResolver.ResolveBraveCloudUiUrl(CurrentEnvironmentKey);
    protected override string TargetBaseUrlConfigKey => "BraveCloudUiUrl";

    public BraveCloudUiTestAgent(
        Kernel kernel,
        ILogger<BraveCloudUiTestAgent> logger,
        TestEnvironmentConfig config,
        IEnvironmentResolver envResolver,
        AiTestCrew.Agents.PostSteps.PostStepOrchestrator postStepOrchestrator)
        : base(kernel, logger, config, envResolver, postStepOrchestrator)
    {
    }

    public override Task<bool> CanHandleAsync(TestTask task) =>
        Task.FromResult(task.Target == TestTargetType.UI_Web_Blazor);

    protected override (string Username, string Password)? GetConfiguredCredentials()
    {
        var user = _envResolver.ResolveBraveCloudUiUsername(CurrentEnvironmentKey);
        if (string.IsNullOrEmpty(user)) return null;
        var pwd = _envResolver.ResolveBraveCloudUiPassword(CurrentEnvironmentKey);
        return (user, pwd);
    }

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

    protected override bool ShouldSkipSetupSteps() => HasFreshStorageState();

    /// <summary>Provide storage state to the browser context if a fresh file exists.</summary>
    protected override BrowserNewContextOptions BuildContextOptions()
    {
        // Use 1920×1080 viewport to match a typical maximized recording session.
        // The default 1280×720 is too small for Blazor apps with side menus — items
        // near the bottom get clipped and selectors time out.
        var opts = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        };
        if (HasFreshStorageState())
            opts.StorageStatePath = ResolveStorageStatePath();
        return opts;
    }

    private async Task PerformSsoLoginAsync(IPage page, IBrowserContext context)
    {
        var appUrl = _envResolver.ResolveBraveCloudUiUrl(CurrentEnvironmentKey);
        Logger.LogInformation("[{Agent}] Performing full SSO login for {Url}", Name, appUrl);

        // Navigate to the app — it will redirect to Azure AD
        await page.GotoAsync(appUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for the Microsoft login page
        await page.WaitForURLAsync(
            url => url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 15_000 });

        Logger.LogInformation("[{Agent}] On Azure AD login page — filling credentials", Name);

        // Enter email
        await page.WaitForSelectorAsync("input[type='email']", new PageWaitForSelectorOptions { Timeout = 15_000 });
        await page.FillAsync("input[type='email']", _envResolver.ResolveBraveCloudUiUsername(CurrentEnvironmentKey));
        await page.ClickAsync("input[type='submit']");

        // Enter password
        await page.WaitForSelectorAsync("input[type='password']", new PageWaitForSelectorOptions { Timeout = 15_000 });
        await page.FillAsync("input[type='password']", _envResolver.ResolveBraveCloudUiPassword(CurrentEnvironmentKey));
        await page.ClickAsync("input[type='submit']");

        // Handle TOTP / MFA challenge if presented (appears between password and KMSI prompt)
        await HandleTotpIfPresentAsync(page);

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
            url => url.StartsWith(appUrl, StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 30_000 });

        Logger.LogInformation("[{Agent}] SSO login successful — now at: {Url}", Name, page.Url);

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
            Logger.LogInformation("[{Agent}] Saved auth storage state to: {Path}",
                Name, resolvedPath);
        }
    }

    /// <summary>
    /// Detects whether Azure AD is showing a TOTP code entry page after password submission.
    /// If detected, either computes the code automatically or waits for manual entry.
    /// If not detected (remembered device, conditional access satisfied), returns immediately.
    /// </summary>
    private async Task HandleTotpIfPresentAsync(IPage page)
    {
        const string totpInputSelector = "input#idTxtBx_SAOTCC_OTC, input[name='otc']";

        bool totpPageDetected;
        try
        {
            await page.WaitForSelectorAsync(totpInputSelector,
                new PageWaitForSelectorOptions { Timeout = 5_000 });
            totpPageDetected = true;
        }
        catch (TimeoutException)
        {
            Logger.LogInformation("[{Agent}] No TOTP prompt detected — MFA was skipped", Name);
            return;
        }

        if (!totpPageDetected) return;

        var totpSecret = _envResolver.ResolveBraveCloudUiTotpSecret(CurrentEnvironmentKey);
        if (!string.IsNullOrEmpty(totpSecret))
        {
            // Automated TOTP entry
            Logger.LogInformation("[{Agent}] TOTP prompt detected — computing code from secret", Name);

            var secretBytes = Base32Encoding.ToBytes(totpSecret);
            var totp = new Totp(secretBytes);
            var code = totp.ComputeTotp();

            await page.FillAsync(totpInputSelector, code);
            await page.ClickAsync("#idSubmit_SAOTCC_Continue, input[type='submit']");

            Logger.LogInformation("[{Agent}] TOTP code submitted", Name);

            // Check for error (wrong code / clock skew)
            try
            {
                var errorEl = await page.WaitForSelectorAsync(
                    "#idSpan_SAOTCC_Error, .alert-error",
                    new PageWaitForSelectorOptions { Timeout = 3_000 });
                if (errorEl != null)
                {
                    var errorText = await errorEl.TextContentAsync() ?? "Unknown error";
                    throw new InvalidOperationException(
                        $"TOTP verification failed: {errorText}. " +
                        "Check that BraveCloudUiTotpSecret is correct and the system clock is synchronized.");
                }
            }
            catch (TimeoutException)
            {
                // No error appeared — TOTP was accepted
            }
        }
        else if (_config.PlaywrightHeadless)
        {
            throw new InvalidOperationException(
                "Azure AD MFA (TOTP) prompt detected but BraveCloudUiTotpSecret is not configured " +
                "and the browser is running in headless mode. Either: " +
                "(1) Set BraveCloudUiTotpSecret to the base32 TOTP secret for automated entry, or " +
                "(2) Set PlaywrightHeadless=false so you can enter the code manually in the browser.");
        }
        else
        {
            // Semi-automated: browser is visible, wait for manual entry
            Logger.LogWarning(
                "[{Agent}] TOTP prompt detected but no secret configured. " +
                "Please enter the MFA code in the browser window. Waiting up to 120 seconds...", Name);

            await page.WaitForSelectorAsync(totpInputSelector,
                new PageWaitForSelectorOptions { State = WaitForSelectorState.Hidden, Timeout = 120_000 });

            Logger.LogInformation("[{Agent}] Manual MFA entry completed", Name);
        }
    }

    /// <summary>Resolve the storage state path relative to the app base directory so it's
    /// consistent regardless of the current working directory.</summary>
    private string? ResolveStorageStatePath()
    {
        var path = _envResolver.ResolveBraveCloudUiStorageStatePath(CurrentEnvironmentKey);
        if (string.IsNullOrEmpty(path)) return null;
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    private bool HasFreshStorageState()
    {
        var path = ResolveStorageStatePath();
        if (path is null || !File.Exists(path)) return false;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        return age.TotalHours < _config.BraveCloudUiStorageStateMaxAgeHours;
    }
}
