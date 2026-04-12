using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AiTestCrew.Agents.Base;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;
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
///   6. Application is optionally relaunched between test cases for clean state
/// </summary>
public abstract class BaseDesktopUiTestAgent : BaseTestAgent
{
    protected readonly TestEnvironmentConfig _config;

    /// <summary>Path to the executable under test.</summary>
    protected abstract string TargetAppPath { get; }

    /// <summary>Optional command-line arguments for the app.</summary>
    protected virtual string TargetAppArgs => "";

    /// <summary>Config key name shown in error messages when TargetAppPath is empty.</summary>
    protected abstract string TargetAppPathConfigKey { get; }

    protected BaseDesktopUiTestAgent(Kernel kernel, ILogger logger, TestEnvironmentConfig config)
        : base(kernel, logger)
    {
        _config = config;
    }

    /// <summary>Extension point: override to customize app launch.</summary>
    protected virtual Application LaunchApplication()
    {
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

        Logger.LogInformation("[{Agent}] Starting desktop UI task: {Desc}", Name, task.Description);

        Application? app = null;
        UIA3Automation? automation = null;

        try
        {
            // ── Check for pre-loaded test cases (reuse mode) ──
            List<DesktopUiTestCase>? testCases = null;
            if (task.Parameters.TryGetValue("PreloadedTestCases", out var preloaded)
                && preloaded is List<DesktopUiTestCase> saved)
            {
                testCases = saved;
                steps.Add(TestStep.Pass("load-cases",
                    $"Loaded {testCases.Count} saved desktop UI test cases (reuse mode)"));
                Logger.LogInformation("[{Agent}] Reuse mode: {Count} saved test cases", Name, testCases.Count);
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

                // Optionally relaunch the app between test cases for clean state
                if (tcIdx > 0 && _config.WinFormsCloseAppBetweenTests)
                {
                    CloseApp(app);
                    app = LaunchApplication();
                    mainWindow = WaitForMainWindow(app, automation);
                    WaitForAppReady(app, mainWindow);
                }
                else if (tcIdx > 0)
                {
                    // Refresh the window reference
                    mainWindow = WaitForMainWindow(app, automation);
                }

                var tcSteps = ExecuteDesktopTestCase(app, mainWindow, automation, tc);
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
        // Return just the filename — the WebApi serves /screenshots/{fileName}
        return fileName;
    }

    private static void CloseApp(Application? app)
    {
        if (app is null) return;
        try
        {
            app.Close();
            app.WaitWhileBusy(TimeSpan.FromSeconds(5));
        }
        catch { /* best effort */ }

        try
        {
            if (!app.HasExited)
                app.Kill();
        }
        catch { /* already exited */ }
    }

    private TestResult BuildResult(
        TestTask task, List<TestStep> steps, string summary,
        TimeSpan duration, List<DesktopUiTestCase> testCases)
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
                ["appPath"]            = TargetAppPath,
                ["generatedTestCases"] = testCases
            }
        };
    }
}
