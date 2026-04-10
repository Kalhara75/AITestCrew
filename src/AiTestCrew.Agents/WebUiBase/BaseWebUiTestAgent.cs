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

            // ── Check for test-set-level setup steps (e.g. login) ──
            List<WebUiStep>? setupSteps = null;
            string? setupStartUrl = null;
            if (task.Parameters.TryGetValue("SetupSteps", out var ss) && ss is List<WebUiStep> ssTyped)
                setupSteps = ssTyped;
            if (task.Parameters.TryGetValue("SetupStartUrl", out var su) && su is string suStr)
                setupStartUrl = suStr;
            if (setupSteps is { Count: > 0 })
            {
                steps.Add(TestStep.Pass("setup-steps",
                    $"Will run {setupSteps.Count} setup step(s) before each test case"));
                Logger.LogInformation("[{Agent}] Setup steps: {Count} steps, startUrl={Url}",
                    Name, setupSteps.Count, setupStartUrl ?? "(none)");
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

            // ── Execute each test case — individual Playwright steps become separate TestSteps ──
            foreach (var tc in testCases)
            {
                ct.ThrowIfCancellationRequested();
                var tcSteps = await ExecuteUiTestCaseAsync(browser, tc, setupSteps, setupStartUrl, ct);
                steps.AddRange(tcSteps);
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
            navigate, click, fill, type, select, check, uncheck, hover, press,
            assert-text, assert-visible, assert-hidden, assert-url-contains, assert-title-contains, wait
            - "fill" sets the value at once (triggers input+change+keyup). Good for most fields.
            - "type" types character-by-character (triggers keydown/keypress/keyup per key). Use for search/filter inputs that react to each keystroke.

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

    private async Task<List<TestStep>> ExecuteUiTestCaseAsync(
        IBrowser browser, WebUiTestCase tc,
        List<WebUiStep>? setupSteps, string? setupStartUrl,
        CancellationToken ct)
    {
        Logger.LogInformation("[{Agent}] Executing test case: {Name}", Name, tc.Name);
        var stepResults = new List<TestStep>();

        // Each test case gets its own fresh context — no auth state from prior cases leaks in.
        // BuildContextOptions() may inject saved auth state (e.g. storage state for SSO).
        await using var context = await browser.NewContextAsync(BuildContextOptions());
        var page = await context.NewPageAsync();

        // Inject lightweight SPA DOM stability observer for wait-for-stable steps.
        // childList only — do NOT observe attributes (Blazor fires thousands of _bl_* changes).
        await page.AddInitScriptAsync(@"
            (function() {
                if (window.__aitcLastDomChangeTs) return;
                var __lastTs = Date.now();
                new MutationObserver(function() { __lastTs = Date.now(); })
                    .observe(document.documentElement, { childList: true, subtree: true });
                window.__aitcLastDomChangeTs = function() { return __lastTs; };
            })();
        ");

        try
        {
            // ── Run setup steps first (e.g. login) if configured ──
            if (setupSteps is { Count: > 0 })
            {
                if (!string.IsNullOrWhiteSpace(setupStartUrl))
                {
                    var sUrl = ResolveUrl(setupStartUrl);
                    await page.GotoAsync(sUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                }

                for (var s = 0; s < setupSteps.Count; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = setupSteps[s];
                    var stepLabel = $"{tc.Name} [setup {s + 1}/{setupSteps.Count}] {step.Action}";
                    var sw = Stopwatch.StartNew();

                    try
                    {
                        await ExecuteUiStepAsync(page, step);
                        sw.Stop();
                        stepResults.Add(new TestStep
                        {
                            Action = stepLabel,
                            Summary = $"{step.Action} {step.Selector ?? ""} {step.Value ?? ""}".Trim(),
                            Status = TestStatus.Passed,
                            Duration = sw.Elapsed
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        sw.Stop();
                        Logger.LogWarning("[{Agent}] Setup step {Idx}/{Total} failed in '{Case}': {Msg}",
                            Name, s + 1, setupSteps.Count, tc.Name, ex.Message);

                        var screenshotPath = tc.TakeScreenshotOnFailure
                            ? await CaptureScreenshotAsync(page, tc.Name)
                            : null;
                        var detail = screenshotPath is not null
                            ? $"{ex.Message} | Screenshot: {screenshotPath}"
                            : ex.Message;

                        stepResults.Add(new TestStep
                        {
                            Action = stepLabel,
                            Summary = $"Setup failed: {ex.Message}",
                            Status = TestStatus.Failed,
                            Detail = detail,
                            Duration = sw.Elapsed
                        });

                        // Setup failure → skip remaining setup + all test steps
                        for (var rs = s + 1; rs < setupSteps.Count; rs++)
                        {
                            var sk = setupSteps[rs];
                            stepResults.Add(TestStep.Fail(
                                $"{tc.Name} [setup {rs + 1}/{setupSteps.Count}] {sk.Action}",
                                "Skipped — setup step failed",
                                $"{sk.Action} {sk.Selector ?? ""} {sk.Value ?? ""}".Trim()));
                        }
                        for (var j = 0; j < tc.Steps.Count; j++)
                        {
                            var sk = tc.Steps[j];
                            stepResults.Add(TestStep.Fail(
                                $"{tc.Name} [{j + 1}/{tc.Steps.Count}] {sk.Action}",
                                "Skipped — setup failed",
                                $"{sk.Action} {sk.Selector ?? ""} {sk.Value ?? ""}".Trim()));
                        }
                        return stepResults;
                    }
                }
            }

            // ── Navigate to the test case's own start URL ──
            if (!string.IsNullOrWhiteSpace(tc.StartUrl))
            {
                var url = ResolveUrl(tc.StartUrl);
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            }

            for (var i = 0; i < tc.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = tc.Steps[i];
                var stepLabel = $"{tc.Name} [{i + 1}/{tc.Steps.Count}] {step.Action}";
                var sw = Stopwatch.StartNew();

                try
                {
                    await ExecuteUiStepAsync(page, step);
                    sw.Stop();
                    stepResults.Add(new TestStep
                    {
                        Action = stepLabel,
                        Summary = $"{step.Action} {step.Selector ?? ""} {step.Value ?? ""}".Trim(),
                        Status = TestStatus.Passed,
                        Duration = sw.Elapsed
                    });
                }
                catch (PlaywrightException ex)
                {
                    sw.Stop();
                    Logger.LogWarning("[{Agent}] Step {Idx}/{Total} failed in '{Case}': {Msg}",
                        Name, i + 1, tc.Steps.Count, tc.Name, ex.Message);

                    var screenshotPath = tc.TakeScreenshotOnFailure
                        ? await CaptureScreenshotAsync(page, tc.Name)
                        : null;
                    var detail = screenshotPath is not null
                        ? $"{ex.Message} | Screenshot: {screenshotPath}"
                        : ex.Message;

                    stepResults.Add(new TestStep
                    {
                        Action = stepLabel,
                        Summary = $"Failed: {ex.Message}",
                        Status = TestStatus.Failed,
                        Detail = detail,
                        Duration = sw.Elapsed
                    });

                    // Mark remaining steps as skipped
                    for (var j = i + 1; j < tc.Steps.Count; j++)
                    {
                        var skipped = tc.Steps[j];
                        var skippedLabel = $"{tc.Name} [{j + 1}/{tc.Steps.Count}] {skipped.Action}";
                        stepResults.Add(TestStep.Fail(skippedLabel,
                            "Skipped — previous step failed",
                            $"{skipped.Action} {skipped.Selector ?? ""} {skipped.Value ?? ""}".Trim()));
                    }
                    return stepResults;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw.Stop();
                    Logger.LogError(ex, "[{Agent}] Unexpected error at step {Idx} in '{Case}'",
                        Name, i + 1, tc.Name);

                    var errScreenshotPath = tc.TakeScreenshotOnFailure
                        ? await CaptureScreenshotAsync(page, tc.Name)
                        : null;
                    var errDetail = errScreenshotPath is not null
                        ? $"{ex.Message} | Screenshot: {errScreenshotPath}"
                        : ex.Message;

                    stepResults.Add(new TestStep
                    {
                        Action = stepLabel,
                        Summary = ex.Message,
                        Status = TestStatus.Error,
                        Detail = errDetail,
                        Duration = sw.Elapsed
                    });

                    // Mark remaining steps as skipped
                    for (var j = i + 1; j < tc.Steps.Count; j++)
                    {
                        var skipped = tc.Steps[j];
                        var skippedLabel = $"{tc.Name} [{j + 1}/{tc.Steps.Count}] {skipped.Action}";
                        stepResults.Add(TestStep.Fail(skippedLabel,
                            "Skipped — previous step errored",
                            $"{skipped.Action} {skipped.Selector ?? ""} {skipped.Value ?? ""}".Trim()));
                    }
                    return stepResults;
                }
            }
        }
        catch (PlaywrightException ex)
        {
            // StartUrl navigation failure — no individual steps ran yet
            Logger.LogWarning("[{Agent}] Navigation failed for '{Case}': {Msg}", Name, tc.Name, ex.Message);
            var navScreenshot = tc.TakeScreenshotOnFailure
                ? await CaptureScreenshotAsync(page, tc.Name)
                : null;
            var navDetail = navScreenshot is not null
                ? $"{ex.Message} | Screenshot: {navScreenshot}"
                : ex.Message;
            stepResults.Add(TestStep.Fail($"{tc.Name} [navigate]",
                $"Failed to navigate to {tc.StartUrl}: {ex.Message}", navDetail));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "[{Agent}] Unexpected error in test case '{Case}'", Name, tc.Name);
            var errScreenshot = tc.TakeScreenshotOnFailure
                ? await CaptureScreenshotAsync(page, tc.Name)
                : null;
            var errDetail = errScreenshot is not null
                ? $"{ex.Message} | Screenshot: {errScreenshot}"
                : ex.Message;
            stepResults.Add(TestStep.Err($"{tc.Name} [error]", ex.Message, errDetail));
        }

        return stepResults;
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
                var clickTimeout = Math.Max(timeout, 15_000f);
                try
                {
                    await page.ClickAsync(step.Selector!, new PageClickOptions { Timeout = clickTimeout });
                }
                catch (PlaywrightException)
                {
                    // Click failed — a modal/overlay (e.g. Welcome dialog) may be blocking
                    // the target or preventing the tree menu from being interactable.
                    // Dismiss any modal, then retry.
                    await TryDismissOverlaysAsync(page);

                    try
                    {
                        await page.ClickAsync(step.Selector!,
                            new PageClickOptions { Timeout = 5_000f });
                    }
                    catch (PlaywrightException)
                    {
                        // Force bypasses actionability; JS click bypasses the viewport entirely.
                        try
                        {
                            await page.ClickAsync(step.Selector!,
                                new PageClickOptions { Force = true, Timeout = 5_000f });
                        }
                        catch (PlaywrightException)
                        {
                            await page.EvalOnSelectorAsync(step.Selector!, "el => el.click()");
                        }
                    }
                }
                // Allow navigation / SPA rendering triggered by the click to settle
                await WaitForSpaSettleAsync(page);
                break;

            case "click-icon":
                // Click an icon-only button identified by its SVG path prefix (Value).
                // Value format: "svgPathPrefix" or "svgPathPrefix|N" where N is the
                // zero-based occurrence index when the same icon appears multiple times.
                // CSS/XPath can't reliably query SVG path[d] in HTML documents,
                // so we use JS evaluation to find and click the button.
                // Polls with retries (like Playwright's auto-wait) because the button
                // may not exist yet after SPA navigation.
                var iconValue = step.Value ?? "";
                var iconParts = iconValue.Split('|', 2);
                var svgPrefix = iconParts[0];
                var iconNth = iconParts.Length > 1 && int.TryParse(iconParts[1], out var n) ? n : 0;
                var iconTimeout = Math.Max(step.TimeoutMs, 15_000);
                var iconSw = System.Diagnostics.Stopwatch.StartNew();
                var iconClicked = false;
                while (iconSw.ElapsedMilliseconds < iconTimeout)
                {
                    iconClicked = await page.EvaluateAsync<bool>(@"([prefix, nth]) => {
                        let idx = 0;
                        for (const p of document.querySelectorAll('svg path')) {
                            const d = p.getAttribute('d');
                            if (d && d.startsWith(prefix)) {
                                if (idx === nth) {
                                    const btn = p.closest('button');
                                    if (btn) { btn.click(); return true; }
                                }
                                idx++;
                            }
                        }
                        return false;
                    }", new object[] { svgPrefix, iconNth });
                    if (iconClicked) break;
                    await page.WaitForTimeoutAsync(500);
                }
                if (!iconClicked)
                    throw new PlaywrightException(
                        $"Timeout {iconTimeout}ms: no button found with SVG icon path starting with: {svgPrefix[..Math.Min(30, svgPrefix.Length)]} (nth={iconNth})");
                // JS btn.click() returns before Blazor processes the event.
                await WaitForSpaSettleAsync(page);
                break;

            case "fill":
                await page.FillAsync(step.Selector!, step.Value ?? "",
                    new PageFillOptions { Timeout = timeout });
                // FillAsync dispatches 'input' and 'change' but NOT keyboard events.
                // Many JS-based filters (e.g. jQuery keyup handlers) need keyup to trigger,
                // so dispatch it explicitly after fill.
                await page.EvalOnSelectorAsync(step.Selector!,
                    @"el => {
                        el.dispatchEvent(new Event('input', { bubbles: true }));
                        el.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true }));
                    }");
                // Brief pause so debounced JS handlers (menu filters, search-as-you-type)
                // have time to process and update the DOM before the next step runs.
                await page.WaitForTimeoutAsync(500);
                break;

            case "type":
                // Character-by-character typing — fires keydown/keypress/keyup per character.
                // Use this instead of 'fill' when the target relies on per-keystroke JS handlers.
                await page.Locator(step.Selector!).PressSequentiallyAsync(step.Value ?? "",
                    new LocatorPressSequentiallyOptions { Delay = 50, Timeout = timeout });
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

            case "wait-for-stable":
                var stableMs = int.TryParse(step.Value, out var sms) ? sms : 1000;
                var stableTimeout = Math.Max(step.TimeoutMs, 15_000);
                try
                {
                    await page.WaitForFunctionAsync(
                        $"() => typeof window.__aitcLastDomChangeTs === 'function' && (Date.now() - window.__aitcLastDomChangeTs()) > {stableMs}",
                        null,
                        new PageWaitForFunctionOptions { Timeout = stableTimeout });
                }
                catch (TimeoutException)
                {
                    Logger.LogWarning("[{Agent}] wait-for-stable timed out after {Ms}ms", Name, stableTimeout);
                }
                break;

            default:
                Logger.LogWarning("[{Agent}] Unknown step action '{Action}' — skipping", Name, step.Action);
                break;
        }
    }

    // ─────────────────────────────────────────────────────
    // Modal / overlay dismissal
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort attempt to dismiss modal dialogs or overlays that block interaction
    /// with the page (e.g. a Welcome popup after login). Tries Escape key first, then
    /// common close-button selectors. Failures are silently ignored.
    /// </summary>
    private async Task TryDismissOverlaysAsync(IPage page)
    {
        Logger.LogInformation("[{Agent}] Click failed — attempting to dismiss modal overlays", Name);

        // 1. Try pressing Escape — works for most modal implementations
        try
        {
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(500);
        }
        catch { /* non-fatal */ }

        // 2. Try clicking common close-button selectors (Bootstrap, Kendo, generic)
        var closeSelectors = new[]
        {
            ".modal .close",
            ".modal-dialog .close",
            ".modal .btn-close",
            "[data-dismiss='modal']",
            "[data-bs-dismiss='modal']",
            ".modal button[aria-label='Close']",
            ".modal-header button",
            ".k-window .k-window-action",       // Kendo UI window close
            ".k-dialog .k-window-action",       // Kendo UI dialog close
            "[role='dialog'] button.close",
            "[role='dialog'] button[aria-label='Close']",
            ".mud-dialog button[aria-label='Close']",  // MudBlazor dialog close
            ".mud-dialog .mud-dialog-actions button",  // MudBlazor dialog action
            ".mud-overlay"                              // MudBlazor backdrop
        };

        foreach (var sel in closeSelectors)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 200 }))
                {
                    await btn.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 1_000 });
                    await page.WaitForTimeoutAsync(500);
                    Logger.LogInformation("[{Agent}] Dismissed overlay via '{Sel}'", Name, sel);
                    return;
                }
            }
            catch { /* try next selector */ }
        }

        // 3. Nuclear option — use JS to remove any modal/dialog/overlay elements from the DOM
        try
        {
            var removed = await page.EvaluateAsync<int>(@"() => {
                let count = 0;
                // Remove modal backdrops
                document.querySelectorAll('.modal-backdrop, .k-overlay, .mud-overlay').forEach(el => { el.remove(); count++; });
                // Close/hide modal dialogs (Bootstrap, Kendo, MudBlazor)
                document.querySelectorAll('.modal.show, .modal.in, .k-window, .mud-dialog-container, [role=""dialog""]').forEach(el => {
                    el.style.display = 'none';
                    el.classList.remove('show', 'in');
                    count++;
                });
                document.querySelectorAll('.mud-drawer--open.mud-drawer--overlay').forEach(el => {
                    el.classList.remove('mud-drawer--open');
                    count++;
                });
                document.body.classList.remove('modal-open');
                document.body.style.overflow = '';
                return count;
            }");
            if (removed > 0)
            {
                Logger.LogInformation("[{Agent}] JS removed/hid {Count} overlay elements", Name, removed);
                await page.WaitForTimeoutAsync(300);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("[{Agent}] JS overlay removal failed: {Msg}", Name, ex.Message);
        }
    }

    /// <summary>
    /// Post-click wait for SPA applications. Checks for MudBlazor loading indicators
    /// first, then waits for NetworkIdle.
    /// </summary>
    private async Task WaitForSpaSettleAsync(IPage page)
    {
        await page.WaitForTimeoutAsync(300);
        try
        {
            var loading = page.Locator(".mud-progress-circular, .mud-skeleton, .mud-table-loading").First;
            if (await loading.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                await Assertions.Expect(loading).ToBeHiddenAsync(
                    new LocatorAssertionsToBeHiddenOptions { Timeout = 15_000 });
        }
        catch { /* no loading indicator — continue */ }
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new PageWaitForLoadStateOptions { Timeout = 10_000 }); }
        catch { /* non-fatal */ }
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
            var fileName = $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var path = Path.Combine(_config.PlaywrightScreenshotDir, fileName);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            Logger.LogInformation("[{Agent}] Screenshot saved: {Path}", Name, path);
            // Return just the filename — the WebApi serves /screenshots/{fileName}
            return fileName;
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
