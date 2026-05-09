using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Environment;
using AiTestCrew.Agents.PostSteps;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
using AiTestCrew.Core.Interfaces;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.DesktopUiBase;

/// <summary>
/// Base class for all FlaUI-powered desktop UI test agents.
///
/// Execution model:
///   1. Launch the target application via <see cref="LaunchApplication"/>
///   2. Wait for the main window to appear
///   3. LLM explores the app via <see cref="DesktopAutomationTools"/> SK plugin
///   4. LLM generates test cases as JSON
///   5. Each test case executes sequentially using <see cref="DesktopStepExecutor"/>
///   6. Application is closed and relaunched between every test case so each one
///      starts from a clean process (child windows, dialogs, and any sibling
///      processes from the prior case are reaped via Process.Kill(entireProcessTree:true))
/// </summary>
public abstract class BaseDesktopUiTestAgent : BaseTestAgent
{
    protected readonly TestEnvironmentConfig _config;
    protected readonly IEnvironmentResolver _envResolver;

    /// <summary>
    /// Active environment key for the current task (set at the top of
    /// <see cref="ExecuteAsync"/>). Subclasses read <see cref="TargetAppPath"/>
    /// via <see cref="_envResolver"/> so per-customer WinForms builds are launched.
    /// </summary>
    protected string? CurrentEnvironmentKey { get; private set; }

    /// <summary>Path to the executable under test.</summary>
    protected abstract string TargetAppPath { get; }

    /// <summary>Optional command-line arguments for the app.</summary>
    protected virtual string TargetAppArgs => "";

    /// <summary>Config key name shown in error messages when TargetAppPath is empty.</summary>
    protected abstract string TargetAppPathConfigKey { get; }

    protected BaseDesktopUiTestAgent(
        Kernel kernel, ILogger logger,
        TestEnvironmentConfig config, IEnvironmentResolver envResolver,
        PostStepOrchestrator postStepOrchestrator)
        : base(kernel, logger, postStepOrchestrator)
    {
        _config = config;
        _envResolver = envResolver;
    }

    /// <summary>Extension point: override to customize app launch.</summary>
    protected virtual Application LaunchApplication()
    {
        // Reap any pre-existing instance so we never attach to a stale window
        // (crashed prior run, manually-opened instance, or a process leaked by
        // a silent CloseApp failure on the previous test case).
        SweepStaleProcesses();

        // Set WorkingDirectory to the app's own folder so it can find sibling DLLs.
        // Many WinForms apps load assemblies relative to their exe directory.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = TargetAppPath,
            Arguments = TargetAppArgs ?? "",
            WorkingDirectory = Path.GetDirectoryName(TargetAppPath) ?? ""
        };
        return Application.Launch(psi);
    }

    /// <summary>Extension point: called after the main window appears. Override for splash screens.</summary>
    protected virtual void WaitForAppReady(Application app, Window mainWindow) { }

    // ─────────────────────────────────────────────────────
    // Main execution entry point
    // ─────────────────────────────────────────────────────

    public override async Task<TestResult> ExecuteAsync(TestTask task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<TestStep>();

        task.Parameters.TryGetValue("EnvironmentKey", out var rawEnvKey);
        CurrentEnvironmentKey = rawEnvKey as string;
        var envParams = StepParameterSubstituter.ReadEnvironmentParameters(task.Parameters);

        Logger.LogInformation("[{Agent}] Starting desktop UI task: {Desc} (env: {Env})",
            Name, task.Description, CurrentEnvironmentKey ?? "default");

        Application? app = null;
        UIA3Automation? automation = null;

        try
        {
            // ── Check for pre-loaded test cases (reuse mode) ──
            List<DesktopUiTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
                && preloaded is List<DesktopUiTestCase> saved)
            {
                // Per-environment {{Token}} substitution so recorded selectors / values
                // adapt to the active customer's app variant.
                testCases = envParams.Count > 0
                    ? saved.Select(tc => StepParameterSubstituter.Apply(tc, envParams)).ToList()
                    : saved;
                steps.Add(TestStep.Pass("load-cases",
                    $"Loaded {testCases.Count} saved desktop UI test cases (reuse mode)"));
                Logger.LogInformation("[{Agent}] Reuse mode: {Count} saved test cases", Name, testCases.Count);
            }

            // ── VerifyOnly: skip the parent desktop flow, run only post-steps ──
            var verifyOnly = task.Parameters.TryGetValue("VerifyOnly", out var voFlag) && voFlag is true;
            if (verifyOnly)
            {
                if (testCases is null)
                {
                    steps.Add(TestStep.Err("verify-only",
                        "VerifyOnly requires preloaded test cases — run via --reuse first."));
                    return BuildResult(task, steps,
                        "Missing preloaded test cases for VerifyOnly.", sw.Elapsed, []);
                }
                return await RunVerifyOnlyAsync(task, testCases, tc => tc.PostSteps, sw, ct);
            }

            // ── Validate config ──
            if (string.IsNullOrWhiteSpace(TargetAppPath))
            {
                var cfgError = $"Application path is not configured. " +
                    $"Set '{TargetAppPathConfigKey}' in appsettings.json under TestEnvironment.";
                steps.Add(TestStep.Err("config-check", cfgError));
                Logger.LogError("[{Agent}] {Error}", Name, cfgError);
                return BuildResult(task, steps, cfgError, sw.Elapsed, []);
            }

            if (!File.Exists(TargetAppPath))
            {
                var cfgError = $"Application not found at path: {TargetAppPath}";
                steps.Add(TestStep.Err("config-check", cfgError));
                Logger.LogError("[{Agent}] {Error}", Name, cfgError);
                return BuildResult(task, steps, cfgError, sw.Elapsed, []);
            }

            // ── Launch application ──
            automation = new UIA3Automation();
            app = LaunchApplication();
            var mainWindow = WaitForMainWindow(app, automation);
            WaitForAppReady(app, mainWindow);
            NormalizeAppWindow(app);

            steps.Add(TestStep.Pass("app-launch",
                $"Launched {Path.GetFileName(TargetAppPath)} — main window: \"{mainWindow.Title}\""));

            // ── Generate test cases if not in reuse mode ──
            if (testCases is null)
            {
                try
                {
                    testCases = await ExploreAndGenerateTestCasesAsync(app, automation, task.Description, ct);
                }
                catch (Exception ex)
                {
                    steps.Add(TestStep.Err("explore-generate", $"Exploration/generation failed: {ex.Message}"));
                    Logger.LogError(ex, "[{Agent}] Exploration failed", Name);
                    var errSummary = await SummariseResultsAsync(steps, ct);
                    return BuildResult(task, steps, errSummary, sw.Elapsed, []);
                }

                if (testCases is null || testCases.Count == 0)
                {
                    steps.Add(TestStep.Err("generate-cases", "LLM returned no test cases"));
                    var errSummary = await SummariseResultsAsync(steps, ct);
                    return BuildResult(task, steps, errSummary, sw.Elapsed, []);
                }

                steps.Add(TestStep.Pass("generate-cases",
                    $"LLM generated {testCases.Count} desktop UI test cases via live app exploration"));
                Logger.LogInformation("[{Agent}] Generated {Count} test cases", Name, testCases.Count);
            }

            // ── Execute each test case ──
            for (var tcIdx = 0; tcIdx < testCases.Count; tcIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var tc = testCases[tcIdx];

                // Each test case starts from a freshly launched application.
                // The LLM generation prompt promises this contract (see Phase 2
                // prompt below: "Each test case is self-contained and assumes a
                // freshly launched application.") and recorded cases assume the
                // same — child windows, dialogs, and any sibling processes from
                // the previous case must be torn down before the next begins.
                if (tcIdx > 0)
                {
                    CloseApp(app);
                    app = LaunchApplication();
                    mainWindow = WaitForMainWindow(app, automation);
                    WaitForAppReady(app, mainWindow);
                    NormalizeAppWindow(app);
                }

                // REQ-004: pre-parent drain for any EventAssert post-step that
                // requested it. No-op when nothing on this test case requested it.
                if (!await TryPreParentDrainsAsync(
                        tc.PostSteps, tcIdx + 1, steps, CurrentEnvironmentKey, envParams, ct))
                    continue;

                var tcSteps = ExecuteDesktopTestCase(app, mainWindow, automation, tc);
                steps.AddRange(tcSteps);

                // Post-steps (sub-actions / sub-verifications) attached to this
                // desktop case. Long-wait post-steps defer to the queue when
                // enabled; short-wait ones run inline.
                if (tc.PostSteps.Count > 0)
                {
                    await RunPostStepsAsync(
                        tc.PostSteps, tc, tcSteps, tcIdx + 1,
                        steps, CurrentEnvironmentKey, envParams, ct, task);
                }
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
            CloseApp(app);
            automation?.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────
    // Agentic exploration + test case generation
    // ─────────────────────────────────────────────────────

    private async Task<List<DesktopUiTestCase>?> ExploreAndGenerateTestCasesAsync(
        Application app, UIA3Automation automation, string objective, CancellationToken ct)
    {
        var tools = new DesktopAutomationTools(app, automation, Logger, _config.WinFormsScreenshotDir);
        var plugin = KernelPluginFactory.CreateFromObject(tools, "desktop");

        var explorationKernel = (Kernel)Kernel.Clone();
        explorationKernel.Plugins.Add(plugin);

        var history = new ChatHistory();
        history.AddSystemMessage(
            $"You are a {Role}. You are part of an automated AI testing crew.");

        history.AddUserMessage($"""
            Explore a Windows desktop application using the desktop automation tools provided.
            Your goal is to understand its UI so we can write reliable automated test cases.

            OBJECTIVE: {objective}
            APPLICATION: {Path.GetFileName(TargetAppPath)}

            Instructions:
            1. Call snapshot() — see the current window state and all interactive elements.
            2. Interact with the application (click buttons, fill text boxes, explore menus).
            3. Call snapshot() after each interaction to see what changed.
            4. Collect: exact AutomationId and Name of all relevant interactive elements,
               window titles, and any error/success messages you observe.

            End with a plain-English summary of what you found: what controls exist, what
            they do, and how the UI responds to interactions. Do NOT output JSON yet.
            """);

        var settings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        // ── Phase 1: Exploration (tool calls) ──
        Logger.LogInformation("[{Agent}] Phase 1 — live desktop exploration for: {Desc}", Name, objective);

        var explorationChatService = explorationKernel.GetRequiredService<IChatCompletionService>();
        var explorationResponse = await explorationChatService.GetChatMessageContentAsync(
            history,
            executionSettings: settings,
            kernel: explorationKernel,
            cancellationToken: ct);

        var summary = explorationResponse.Content ?? "";
        Logger.LogInformation("[{Agent}] Phase 1 complete. Summary: {S}",
            Name, summary[..Math.Min(300, summary.Length)]);

        // ── Phase 2: JSON generation (no tools) ──
        var observationBlock = tools.Observations.Count > 0
            ? string.Join("\n", tools.Observations.Select((o, i) =>
                $"  Window {i + 1}: Title=\"{o.WindowTitle}\"\n{o.InteractiveElements}"))
            : "  (no snapshots recorded)";

        history.Add(explorationResponse);
        history.AddUserMessage($"""
            Good. Now output the test cases as a JSON array.

            AUTHORITATIVE OBSERVED ELEMENTS — use ONLY these identifiers, never invent values:
            {observationBlock}

            Rules:
            - Use AutomationId as the primary selector when available.
            - Use Name as a fallback when AutomationId is empty.
            - Each test case is self-contained and assumes a freshly launched application.
            - Cover: happy path, invalid input, boundary/edge cases. Generate 3-6 test cases.
            - For wait: when no element selector, set value to milliseconds (e.g. "2000").

            ALLOWED STEP ACTIONS (exact strings only):
            click, double-click, right-click, fill, select, check, uncheck, press, hover,
            assert-text, assert-visible, assert-hidden, assert-enabled, assert-disabled,
            wait-for-window, switch-window, close-window, menu-navigate, wait

            Output ONLY a valid JSON array. No markdown. No explanation.
            Each element: name, description, steps (array), takeScreenshotOnFailure (true).
            Each step: action, automationId (string or null), name (string or null),
            className (string or null), controlType (string or null), treePath (string or null),
            value (string or null), menuPath (string or null), windowTitle (string or null),
            timeoutMs (default 5000).
            """);

        Logger.LogInformation("[{Agent}] Phase 2 — generating JSON test cases", Name);

        var generationChatService = Kernel.GetRequiredService<IChatCompletionService>();
        var generationResponse = await generationChatService.GetChatMessageContentAsync(
            history, cancellationToken: ct);

        var raw = generationResponse.Content ?? "";
        Logger.LogInformation("[{Agent}] Phase 2 complete. Raw ({Len} chars): {Preview}",
            Name, raw.Length, raw[..Math.Min(300, raw.Length)]);

        return LlmJsonHelper.DeserializeLlmResponse<List<DesktopUiTestCase>>(raw);
    }

    // ─────────────────────────────────────────────────────
    // Test case execution
    // ─────────────────────────────────────────────────────

    private List<TestStep> ExecuteDesktopTestCase(
        Application app, Window window, UIA3Automation automation, DesktopUiTestCase tc)
    {
        Logger.LogInformation("[{Agent}] Executing desktop test case: {Name}", Name, tc.Name);
        var stepResults = new List<TestStep>();
        var failed = false;

        for (var i = 0; i < tc.Steps.Count; i++)
        {
            if (failed)
            {
                stepResults.Add(new TestStep
                {
                    Action = $"{tc.Name} [{i + 1}/{tc.Steps.Count}] {tc.Steps[i].Action}",
                    Summary = "Skipped — previous step failed",
                    Status = TestStatus.Skipped
                });
                continue;
            }

            // Refresh window reference (window may have been replaced by dialog, etc.)
            try
            {
                var windows = app.GetAllTopLevelWindows(automation);
                if (windows.Length > 0)
                    window = windows[0];
            }
            catch { /* keep existing reference */ }

            // Re-normalize on every step so that window transitions
            // (e.g. login dialog → main form) get caught and resized to
            // the configured target dimensions before the next click fires.
            NormalizeAppWindow(app);

            // Ensure the target app is in the foreground BEFORE every step.
            // Clicks naturally focus their target via the OS, but coord-based
            // reads (FromPoint, OCR for assert-text / assert-text-ocr /
            // assert-count) capture whatever window is topmost at that pixel.
            // Without this, any other window that drifts on top between steps
            // (browser, IDE, notification toast) corrupts the assertion result —
            // e.g. asserting on a Bravo grid cell silently OCRs the dashboard's
            // user-pill text instead.
            try
            {
                DesktopWindowNormalizer.EnsureForeground((uint)app.ProcessId, Logger);
            }
            catch (Exception ex)
            {
                Logger.LogDebug("[{Agent}] EnsureForeground failed (non-fatal): {Msg}", Name, ex.Message);
            }

            var result = DesktopStepExecutor.ExecuteStep(
                window, app, tc.Steps[i], automation,
                tc.Name, i, tc.Steps.Count, Logger);

            stepResults.Add(result);

            if (result.Status == TestStatus.Failed || result.Status == TestStatus.Error)
            {
                failed = true;

                // Screenshot on failure
                if (tc.TakeScreenshotOnFailure)
                {
                    try
                    {
                        var screenshotPath = CaptureScreenshot(window, tc.Name);
                        if (screenshotPath is not null)
                        {
                            // Detail is init-only, so replace the step with an updated one
                            stepResults[^1] = TestStep.Fail(result.Action, result.Summary,
                                $"{result.Detail} | Screenshot: {screenshotPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Failed to capture screenshot: {Msg}", ex.Message);
                    }
                }
            }
        }

        return stepResults;
    }

    // ─────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Forces the app's main window to the configured fixed size so recorded
    /// window-relative click coordinates are portable across monitors. Cheap
    /// and idempotent — safe to call on every step. No-op when normalization
    /// is disabled in config.
    /// </summary>
    private void NormalizeAppWindow(Application app)
    {
        if (!_config.WinFormsNormalizeWindow) return;
        try
        {
            DesktopWindowNormalizer.TryNormalize(
                (uint)app.ProcessId,
                _config.WinFormsWindowWidth,
                _config.WinFormsWindowHeight,
                Logger);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Agent}] Window normalization failed (non-fatal)", Name);
        }
    }

    private Window WaitForMainWindow(Application app, UIA3Automation automation)
    {
        var timeout = TimeSpan.FromSeconds(_config.WinFormsAppLaunchTimeoutSeconds);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            var windows = app.GetAllTopLevelWindows(automation);
            if (windows.Length > 0)
            {
                Logger.LogDebug("[{Agent}] Main window found: \"{Title}\"", Name, windows[0].Title);
                return windows[0];
            }
            Thread.Sleep(250);
        }

        throw new TimeoutException(
            $"Application did not show a window within {_config.WinFormsAppLaunchTimeoutSeconds}s");
    }

    private string? CaptureScreenshot(Window window, string testCaseName)
    {
        var dir = _config.WinFormsScreenshotDir ?? _config.PlaywrightScreenshotDir;
        if (string.IsNullOrEmpty(dir)) return null;

        Directory.CreateDirectory(dir);
        var safeName = string.Concat(testCaseName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var fileName = $"desktop_{safeName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
        var path = Path.Combine(dir, fileName);

        var image = Capture.Element(window);
        image.ToFile(path);

        Logger.LogDebug("[{Agent}] Screenshot saved: {Path}", Name, path);
        // When running in agent mode (ServerUrl set), push a copy to the server so the dashboard can render it
        _ = AiTestCrew.Agents.Shared.RemoteScreenshotUploader.TryUploadAsync(_config, path, Logger);
        // Return just the filename — the WebApi serves /screenshots/{fileName}
        return fileName;
    }

    private void CloseApp(Application? app)
    {
        if (app is null) return;

        // Capture pid before any close attempt — once the FlaUI Application is
        // disposed the property may throw, and we still want to reap orphans.
        int pid;
        try { pid = app.ProcessId; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Agent}] CloseApp: could not read ProcessId; falling back to app.Kill()", Name);
            try { app.Kill(); } catch (Exception killEx) { Logger.LogError(killEx, "[{Agent}] CloseApp: app.Kill() threw", Name); }
            return;
        }

        // Phase 1: polite close — posts WM_CLOSE to the main window.
        try
        {
            app.Close();
            app.WaitWhileBusy(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Agent}] CloseApp: app.Close() threw (pid {Pid})", Name, pid);
        }

        // Phase 2: did it actually exit? If not, force-kill the process tree
        // (reaps child windows and any sibling processes the app spawned).
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return;

            Logger.LogWarning(
                "[{Agent}] CloseApp: pid {Pid} did not exit after Close() — force-killing process tree (likely modal dialog or unsaved-changes prompt blocked WM_CLOSE)",
                Name, pid);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);

            if (!proc.HasExited)
                Logger.LogError("[{Agent}] CloseApp: pid {Pid} still alive 5s after Kill(entireProcessTree:true)", Name, pid);
        }
        catch (ArgumentException)
        {
            // Process already gone — Close() succeeded, nothing to do.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] CloseApp: failed to force-kill pid {Pid}", Name, pid);
        }
    }

    /// <summary>
    /// Reaps any pre-existing processes whose main module path equals
    /// <see cref="TargetAppPath"/>. Called before each <see cref="LaunchApplication"/>
    /// so a crashed prior run, a manually-launched instance, or a leaked process
    /// from the previous test case cannot leave us attaching to the wrong window.
    /// </summary>
    private void SweepStaleProcesses()
    {
        var targetPath = TargetAppPath;
        if (string.IsNullOrWhiteSpace(targetPath)) return;

        string targetExeName;
        try { targetExeName = Path.GetFileNameWithoutExtension(targetPath); }
        catch { return; }

        if (string.IsNullOrEmpty(targetExeName)) return;

        Process[] candidates;
        try { candidates = Process.GetProcessesByName(targetExeName); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Agent}] SweepStaleProcesses: could not enumerate processes for '{Exe}'", Name, targetExeName);
            return;
        }

        foreach (var proc in candidates)
        {
            try
            {
                // Match by full exe path so a Sumo Bravo doesn't get killed
                // while a Tesla Bravo test is running (both are Bravo.exe).
                string? procPath = null;
                try { procPath = proc.MainModule?.FileName; } catch { /* access denied — skip */ }

                if (procPath is null || !PathsEqual(procPath, targetPath))
                    continue;

                Logger.LogWarning(
                    "[{Agent}] SweepStaleProcesses: killing pre-existing pid {Pid} matching {Path}",
                    Name, proc.Id, targetPath);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{Agent}] SweepStaleProcesses: failed to kill pid {Pid}", Name, proc.Id);
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    protected override string PostStepParentKind => "DesktopUi";

    /// <summary>
    /// Publishes the {{Token}} values a desktop UI parent case contributes to
    /// its post-steps. Parent test case is always a <see cref="DesktopUiTestCase"/>.
    /// </summary>
    protected override IDictionary<string, string> BuildPostStepContext(
        object parentTestCase, IReadOnlyList<TestStep> parentSteps)
    {
        var ctx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parentTestCase is DesktopUiTestCase tc)
        {
            if (!string.IsNullOrEmpty(tc.Name))        ctx["ParentCaseName"] = tc.Name;
            if (!string.IsNullOrEmpty(tc.Description)) ctx["ParentCaseDescription"] = tc.Description;
        }
        if (!string.IsNullOrEmpty(TargetAppPath))      ctx["AppPath"] = TargetAppPath;
        if (!string.IsNullOrEmpty(CurrentEnvironmentKey)) ctx["EnvironmentKey"] = CurrentEnvironmentKey;
        return ctx;
    }

    private TestResult BuildResult(
        TestTask task, List<TestStep> steps, string summary,
        TimeSpan duration, List<DesktopUiTestCase> testCases)
    {
        var failCount  = steps.Count(s => s.Status == TestStatus.Failed);
        var errorCount = steps.Count(s => s.Status == TestStatus.Error);
        var awaitingCount = steps.Count(s => s.Status == TestStatus.AwaitingVerification);

        // AwaitingVerification when post-steps were deferred — the parent run
        // must NOT be reported Passed, otherwise FromSuiteResult finalises history
        // with status=Passed before the deferred verifications complete.
        var status = errorCount > 0 ? TestStatus.Error
                   : failCount  > 0 ? TestStatus.Failed
                   : awaitingCount > 0 ? TestStatus.AwaitingVerification
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
                ["appPath"]            = TargetAppPath,
                ["generatedTestCases"] = testCases
            }
        };
    }
}
