using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.WebUiBase;

/// <summary>
/// Base class for all Playwright-powered web UI test agents.
///
/// Execution model:
///   1. (Optional) One-time auth setup — subclasses override <see cref="PerformOneTimeAuthSetupAsync"/>
///      to do things like Azure SSO + save storage state. Default is a no-op.
///   2. Page discovery — a fresh unauthenticated context navigates to the base URL so the LLM
///      sees the real landing/login page structure.
///   3. LLM test case generation — test cases are self-contained; each must include any login
///      steps it needs (or rely on auth state injected via <see cref="BuildContextOptions"/>).
///   4. Per-test-case execution — every test case gets its own fresh browser context via
///      <see cref="BuildContextOptions"/>. No state leaks between test cases.
/// </summary>
public abstract class BaseWebUiTestAgent : BaseTestAgent
{
    protected readonly TestEnvironmentConfig _config;

    /// <summary>The root URL of the application under test.</summary>
    protected abstract string TargetBaseUrl { get; }

    /// <summary>Config key name shown in error messages when TargetBaseUrl is empty.</summary>
    protected abstract string TargetBaseUrlConfigKey { get; }

    protected BaseWebUiTestAgent(Kernel kernel, ILogger logger, TestEnvironmentConfig config)
        : base(kernel, logger)
    {
        _config = config;
    }

    // ─────────────────────────────────────────────────────
    // Extension points for subclasses
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Optional one-time setup before test cases run — e.g. Azure SSO login + save storage state.
    /// Default is a no-op. Legacy MVC does nothing here; auth is handled inside test case steps.
    /// </summary>
    protected virtual Task PerformOneTimeAuthSetupAsync(IBrowser browser, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Context options applied to every fresh test-case context.
    /// Override to inject saved auth state (e.g. StorageStatePath for BraveCloud SSO).
    /// </summary>
    protected virtual BrowserNewContextOptions BuildContextOptions() => new();

    /// <summary>
    /// Returns credential hints to inject into the LLM prompt so the generated test cases
    /// use the configured username/password instead of inventing values like "admin".
    /// Returns null if credentials are not configured.
    /// </summary>
    protected virtual (string Username, string Password)? GetConfiguredCredentials() => null;

    // ─────────────────────────────────────────────────────
    // Main execution entry point
    // ─────────────────────────────────────────────────────

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();

        Logger.LogInformation("[{Agent}] Starting task: {Desc}", Name, task.Description);

        IPlaywright? playwright = null;
        IBrowser? browser = null;

        try
        {
            // ── Check for pre-loaded test cases (reuse mode) ──
            List<WebUiTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
                && preloaded is List<WebUiTestCase> saved)
            {
                testCases = saved;
                steps.Add(TestStep.Pass("load-cases",
                    $"Loaded {testCases.Count} saved UI test cases (reuse mode — skipping LLM generation)"));
                Logger.LogInformation("[{Agent}] Reuse mode: {Count} saved test cases", Name, testCases.Count);
            }

            // ── Validate base URL ──
            if (string.IsNullOrWhiteSpace(TargetBaseUrl))
            {
                var cfgError = $"Base URL is not configured. " +
                    $"Set '{TargetBaseUrlConfigKey}' in appsettings.json under TestEnvironment.";
                steps.Add(TestStep.Err("config-check", cfgError));
                Logger.LogError("[{Agent}] {Error}", Name, cfgError);
                return BuildResult(task, steps, cfgError, sw.Elapsed, []);
            }

            // ── Launch browser ──
            playwright = await Playwright.CreateAsync();
            browser = await LaunchBrowserAsync(playwright);
            steps.Add(TestStep.Pass("browser-launch",
                $"Launched {_config.PlaywrightBrowser} (headless={_config.PlaywrightHeadless})"));

            // ── Optional one-time auth setup ──
            try
            {
                await PerformOneTimeAuthSetupAsync(browser, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[{Agent}] One-time auth setup failed (non-fatal): {Msg}", Name, ex.Message);
                steps.Add(TestStep.Pass("auth-setup", $"Auth setup skipped: {ex.Message}"));
            }

            // ── Generate test cases if not in reuse mode ──
            if (testCases is null)
            {
                try
                {
                    testCases = await ExploreAndGenerateTestCasesAsync(browser, task.Description, ct);
                }
                catch (Exception ex)
                {
                    steps.Add(TestStep.Err("explore-generate", $"Exploration/generation failed: {ex.Message}"));
                    Logger.LogError(ex, "[{Agent}] Exploration failed for {Url}", Name, TargetBaseUrl);
                    var errSummary = await SummariseResultsAsync(steps, ct);
                    return BuildResult(task, steps, errSummary, sw.Elapsed, []);
                }

                if (testCases is null || testCases.Count == 0)
                {
                    steps.Add(TestStep.Err("generate-cases", "LLM returned no test cases"));
                    var errSummary = await SummariseResultsAsync(steps, ct);
                    return BuildResult(task, steps, errSummary, sw.Elapsed, []);
                }

                steps.Add(TestStep.Pass("generate-cases", $"LLM generated {testCases.Count} test cases via live browser exploration"));
                Logger.LogInformation("[{Agent}] Generated {Count} test cases", Name, testCases.Count);
            }

            // ── Execute each test case (each becomes a step) ──
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                var tcStep = await ExecuteUiTestCaseAsync(browser, tc, ct);
                steps.Add(tcStep);
            }

            var summary = await SummariseResultsAsync(steps, ct);
            return BuildResult(task, steps, summary, sw.Elapsed, testCases);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] Unexpected error in ExecuteAsync", Name);
            steps.Add(TestStep.Err("unexpected-error", ex.Message));
            var summary = await SummariseResultsAsync(steps, ct);
            return BuildResult(task, steps, summary, sw.Elapsed, []);
        }
        finally
        {
            if (browser is not null) await browser.CloseAsync();
            playwright?.Dispose();
        }
    }

    private TestResult BuildResult(
        TestTask task, List<TestStep> steps, string summary,
        TimeSpan duration, List<WebUiTestCase> testCases)
    {
        var failCount  = steps.Count(s => s.Status == TestStatus.Failed);
        var errorCount = steps.Count(s => s.Status == TestStatus.Error);

        var status = errorCount > 0 ? TestStatus.Error
                   : failCount  > 0 ? TestStatus.Failed
                   : TestStatus.Passed;

        return new TestResult
        {
            ObjectiveId   = task.Id,
            ObjectiveName = task.Description,
            AgentName     = Name,
            Status        = status,
            Summary       = summary,
            Steps         = steps,
            Duration      = duration,
            Metadata      = new Dictionary<string, object>
            {
                ["totalCases"]         = testCases.Count,
                ["baseUrl"]            = TargetBaseUrl,
                ["generatedTestCases"] = testCases
            }
        };
    }

    // ─────────────────────────────────────────────────────
    // Browser helpers
    // ─────────────────────────────────────────────────────

    private async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright)
    {
        var options = new BrowserTypeLaunchOptions { Headless = _config.PlaywrightHeadless };
        return _config.PlaywrightBrowser.ToLowerInvariant() switch
        {
            "firefox" => await playwright.Firefox.LaunchAsync(options),
            "webkit"  => await playwright.Webkit.LaunchAsync(options),
            _         => await playwright.Chromium.LaunchAsync(options)
        };
    }

    // ─────────────────────────────────────────────────────
    // Agentic exploration + test case generation
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Gives the LLM a live browser (via <see cref="PlaywrightBrowserTools"/>) so it can
    /// navigate, fill forms, and observe real selectors and URLs before generating test cases.
    /// This replaces the old single-snapshot approach that led to invented selectors.
    /// </summary>
    private async Task<List<WebUiTestCase>?> ExploreAndGenerateTestCasesAsync(
        IBrowser browser, string objective, CancellationToken ct)
    {
        // Fresh unauthenticated context for exploration — same isolation as test case execution
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions());
        var page = await context.NewPageAsync();

        // Navigate to the base URL so exploration starts at the real landing/login page
        await page.GotoAsync(TargetBaseUrl,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 20_000 });

        // Build the browser tools plugin for this exploration session
        var tools  = new PlaywrightBrowserTools(page, Logger, TargetBaseUrl);
        var plugin = KernelPluginFactory.CreateFromObject(tools, "browser");

        // Clone kernel so we don't pollute the shared singleton with the browser plugin
        var explorationKernel = (Kernel)Kernel.Clone();
        explorationKernel.Plugins.Add(plugin);

        var creds = GetConfiguredCredentials();
        var credBlock = creds is not null
            ? $"Username: {creds.Value.Username}\nPassword: {creds.Value.Password}"
            : "(no credentials configured — skip auth steps)";

        var history = new ChatHistory();
        history.AddSystemMessage(
            $"You are a {Role}. You are part of an automated AI testing crew.");

        history.AddUserMessage($"""
            Explore a web application using the browser tools provided.
            Your goal is to understand its UI so we can write reliable automated test cases.

            OBJECTIVE: {objective}
            BASE URL: {TargetBaseUrl}
            CREDENTIALS:
            {credBlock}

            Instructions:
            1. Call snapshot() — you are already at the base URL; see what the page shows.
            2. If there is a login form, fill it with the configured credentials, click submit,
               then call snapshot() again to see where you land after login.
            3. Explore pages relevant to the objective (navigate, click, snapshot as needed).
            4. Collect: exact CSS selectors of all interactive elements, actual page URLs,
               page titles, and any error or success messages.

            End with a plain-English summary of what you found: what pages exist, what
            selectors you saw, and what happens at each step. Do NOT output JSON yet.
            """);

        var settings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        // ── Phase 1: Exploration (tool calls) ────────────────────────────────────────────
        // The LLM navigates, snapshots, fills, clicks — ending with a plain-text summary.
        // We deliberately do NOT ask for JSON here; asking for both in one pass causes the
        // LLM to end with a human-readable conclusion instead of the JSON array.
        Logger.LogInformation("[{Agent}] Phase 1 — live browser exploration for: {Desc}", Name, objective);

        var explorationChatService = explorationKernel.GetRequiredService<IChatCompletionService>();
        var explorationResponse = await explorationChatService.GetChatMessageContentAsync(
            history,
            executionSettings: settings,
            kernel: explorationKernel,
            cancellationToken: ct);

        var summary = explorationResponse.Content ?? "";
        Logger.LogInformation("[{Agent}] Phase 1 complete. Summary: {S}",
            Name, summary[..Math.Min(300, summary.Length)]);

        // ── Phase 2: JSON generation (no tools) ───────────────────────────────────────────
        // Append the exploration summary, then ask specifically for JSON.
        // Using the base Kernel (no browser plugin) ensures the LLM can't try tool calls.

        // Build an authoritative reference of every page observed during exploration.
        // Injecting these as ground truth prevents the LLM from hallucinating titles/URLs.
        var observationBlock = tools.Observations.Count > 0
            ? string.Join("\n", tools.Observations.Select((o, i) =>
                $"  Page {i + 1}: URL=\"{o.Url}\" | Title=\"{o.Title}\""))
            : "  (no snapshots recorded)";

        history.Add(explorationResponse);
        history.AddUserMessage($"""
            Good. Now output the test cases as a JSON array.

            AUTHORITATIVE OBSERVED VALUES — use ONLY these for assertions, never invent values:
            {observationBlock}

            Rules:
            - "selector" must be copied EXACTLY from a snapshot's interactive elements list.
            - "value" in assert-title-contains must be the exact Title from the list above.
            - "value" in assert-url-contains must be a substring of a URL from the list above.
            - "startUrl" must be a URL path from the list above (use "/" for the root).
            - Each test case is self-contained: include login steps if the test needs auth.
            - Use configured credentials ({(creds is not null ? creds.Value.Username : "N/A")}) — never 'admin' or invented values.
            - Prefer assert-url-contains over assert-title-contains for post-action checks.
            - Cover: happy path, invalid credentials, boundary/edge cases. Generate 3-6 test cases.
            - For wait: selector = null, value = milliseconds as a string (e.g. "2000").

            ALLOWED STEP ACTIONS (exact strings only):
            navigate, click, fill, select, check, uncheck, hover, press,
            assert-text, assert-visible, assert-hidden, assert-url-contains, assert-title-contains, wait

            Output ONLY a valid JSON array. No markdown. No explanation.
            Each element: name, description, startUrl, takeScreenshotOnFailure (true),
            steps (array of: action, selector (string or null), value (string or null), timeoutMs (default 5000)).
            """);

        Logger.LogInformation("[{Agent}] Phase 2 — generating JSON test cases", Name);

        var generationChatService = Kernel.GetRequiredService<IChatCompletionService>();
        var generationResponse = await generationChatService.GetChatMessageContentAsync(
            history, cancellationToken: ct);

        var raw = generationResponse.Content ?? "";
        Logger.LogInformation("[{Agent}] Phase 2 complete. Raw ({Len} chars): {Preview}",
            Name, raw.Length, raw[..Math.Min(300, raw.Length)]);

        return LlmJsonHelper.DeserializeLlmResponse<List<WebUiTestCase>>(raw);
    }

    // ─────────────────────────────────────────────────────
    // Test case execution — fresh context per test case
    // ─────────────────────────────────────────────────────

    private async Task<TestStep> ExecuteUiTestCaseAsync(IBrowser browser, WebUiTestCase tc, CancellationToken ct)
    {
        Logger.LogInformation("[{Agent}] Executing test case: {Name}", Name, tc.Name);

        // Each test case gets its own fresh context — no auth state from prior cases leaks in.
        // BuildContextOptions() may inject saved auth state (e.g. storage state for SSO).
        await using var context = await browser.NewContextAsync(BuildContextOptions());
        var page = await context.NewPageAsync();

        try
        {
            if (!string.IsNullOrWhiteSpace(tc.StartUrl))
            {
                var url = ResolveUrl(tc.StartUrl);
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            }

            foreach (var step in tc.Steps)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteUiStepAsync(page, step);
            }

            return TestStep.Pass(tc.Name, $"[{tc.Name}] All {tc.Steps.Count} steps passed");
        }
        catch (PlaywrightException ex)
        {
            Logger.LogWarning("[{Agent}] Playwright error in '{Case}': {Msg}", Name, tc.Name, ex.Message);
            var screenshotPath = tc.TakeScreenshotOnFailure
                ? await CaptureScreenshotAsync(page, tc.Name)
                : null;
            var detail = screenshotPath is not null
                ? $"{ex.Message} | Screenshot: {screenshotPath}"
                : ex.Message;
            return TestStep.Fail(tc.Name, $"[{tc.Name}] Failed: {ex.Message}", detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "[{Agent}] Unexpected error in test case '{Case}'", Name, tc.Name);
            return TestStep.Err(tc.Name, ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────
    // Step dispatcher
    // ─────────────────────────────────────────────────────

    protected async Task ExecuteUiStepAsync(IPage page, WebUiStep step)
    {
        var timeout = (float)step.TimeoutMs;

        switch (step.Action.ToLowerInvariant())
        {
            case "navigate":
                await page.GotoAsync(ResolveUrl(step.Value ?? "/"),
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = timeout });
                break;

            case "click":
                // Clicks often trigger form submissions/navigation — use at least 15s
                var clickTimeout = Math.Max(timeout, 15_000f);
                try
                {
                    await page.ClickAsync(step.Selector!, new PageClickOptions { Timeout = clickTimeout });
                }
                catch (PlaywrightException)
                {
                    // Fall back to JS click for elements that resist Playwright's actionability checks
                    await page.EvalOnSelectorAsync(step.Selector!, "el => el.click()");
                }
                // Allow navigation triggered by the click to settle
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                    new PageWaitForLoadStateOptions { Timeout = 15_000 }); }
                catch { /* non-fatal — page may not navigate */ }
                break;

            case "fill":
                await page.FillAsync(step.Selector!, step.Value ?? "",
                    new PageFillOptions { Timeout = timeout });
                break;

            case "select":
                await page.SelectOptionAsync(step.Selector!, step.Value ?? "",
                    new PageSelectOptionOptions { Timeout = timeout });
                break;

            case "check":
                await page.CheckAsync(step.Selector!, new PageCheckOptions { Timeout = timeout });
                break;

            case "uncheck":
                await page.UncheckAsync(step.Selector!, new PageUncheckOptions { Timeout = timeout });
                break;

            case "hover":
                await page.HoverAsync(step.Selector!, new PageHoverOptions { Timeout = timeout });
                break;

            case "press":
                if (step.Selector is not null)
                    await page.PressAsync(step.Selector, step.Value ?? "Enter",
                        new PagePressOptions { Timeout = timeout });
                else
                    await page.Keyboard.PressAsync(step.Value ?? "Enter");
                break;

            case "assert-text":
                await Assertions.Expect(page.Locator(step.Selector!))
                    .ToContainTextAsync(step.Value ?? "",
                        new LocatorAssertionsToContainTextOptions { Timeout = timeout });
                break;

            case "assert-visible":
                await Assertions.Expect(page.Locator(step.Selector!))
                    .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = timeout });
                break;

            case "assert-hidden":
                await Assertions.Expect(page.Locator(step.Selector!))
                    .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = timeout });
                break;

            case "assert-url-contains":
                await Assertions.Expect(page)
                    .ToHaveURLAsync(new System.Text.RegularExpressions.Regex(
                        System.Text.RegularExpressions.Regex.Escape(step.Value ?? "")),
                        new PageAssertionsToHaveURLOptions { Timeout = timeout });
                break;

            case "assert-title-contains":
                await Assertions.Expect(page)
                    .ToHaveTitleAsync(new System.Text.RegularExpressions.Regex(
                        System.Text.RegularExpressions.Regex.Escape(step.Value ?? "")),
                        new PageAssertionsToHaveTitleOptions { Timeout = timeout });
                break;

            case "wait":
                if (step.Selector is not null)
                    await page.WaitForSelectorAsync(step.Selector,
                        new PageWaitForSelectorOptions { Timeout = timeout });
                else if (int.TryParse(step.Value, out var ms))
                    await Task.Delay(ms);
                else
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                        new PageWaitForLoadStateOptions { Timeout = timeout });
                break;

            default:
                Logger.LogWarning("[{Agent}] Unknown step action '{Action}' — skipping", Name, step.Action);
                break;
        }
    }

    // ─────────────────────────────────────────────────────
    // Screenshot helper
    // ─────────────────────────────────────────────────────

    protected async Task<string?> CaptureScreenshotAsync(IPage page, string testName)
    {
        if (string.IsNullOrEmpty(_config.PlaywrightScreenshotDir)) return null;
        try
        {
            Directory.CreateDirectory(_config.PlaywrightScreenshotDir);
            var safe = string.Concat(testName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var path = Path.Combine(_config.PlaywrightScreenshotDir,
                $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            Logger.LogInformation("[{Agent}] Screenshot saved: {Path}", Name, path);
            return path;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Agent}] Failed to capture screenshot", Name);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────
    // URL resolution
    // ─────────────────────────────────────────────────────

    protected string ResolveUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return TargetBaseUrl;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        return TargetBaseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
    }

}
