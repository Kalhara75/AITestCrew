using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Models;

namespace AiTestCrew.Agents.DesktopUiBase;

/// <summary>
/// Executes desktop UI steps against a WinForms application using FlaUI.
/// Each step is dispatched by action type and returns a <see cref="TestStep"/> result.
/// </summary>
public static class DesktopStepExecutor
{
    /// <summary>
    /// Execute a single desktop UI step and return the result.
    /// </summary>
    public static TestStep ExecuteStep(
        Window window,
        Application app,
        DesktopUiStep step,
        UIA3Automation automation,
        string testCaseName,
        int stepIndex,
        int totalSteps,
        ILogger logger)
    {
        var label = $"{testCaseName} [{stepIndex + 1}/{totalSteps}] {step.Action}";
        var sw = Stopwatch.StartNew();

        try
        {
            switch (step.Action.ToLowerInvariant())
            {
                case "click":
                    ExecuteClick(window, step, automation, logger);
                    break;

                case "double-click":
                    ExecuteDoubleClick(window, step, automation, logger);
                    break;

                case "right-click":
                    ExecuteRightClick(window, step, automation, logger);
                    break;

                case "fill":
                    ExecuteFill(window, step, automation, logger);
                    break;

                case "select":
                    ExecuteSelect(window, step, automation, logger);
                    break;

                case "check":
                    ExecuteCheck(window, step, automation, logger, isChecked: true);
                    break;

                case "uncheck":
                    ExecuteCheck(window, step, automation, logger, isChecked: false);
                    break;

                case "press":
                    ExecutePress(step, logger);
                    break;

                case "hover":
                    ExecuteHover(window, step, automation, logger);
                    break;

                case "assert-text":
                    return ExecuteAssertText(window, step, automation, logger, label, sw);

                case "assert-visible":
                    return ExecuteAssertVisible(window, step, automation, logger, label, sw, expectVisible: true);

                case "assert-hidden":
                    return ExecuteAssertVisible(window, step, automation, logger, label, sw, expectVisible: false);

                case "assert-enabled":
                    return ExecuteAssertEnabled(window, step, automation, logger, label, sw, expectEnabled: true);

                case "assert-disabled":
                    return ExecuteAssertEnabled(window, step, automation, logger, label, sw, expectEnabled: false);

                case "wait-for-window":
                    ExecuteWaitForWindow(app, step, logger);
                    break;

                case "switch-window":
                    ExecuteSwitchWindow(app, step, logger);
                    break;

                case "close-window":
                    ExecuteCloseWindow(app, step, logger);
                    break;

                case "menu-navigate":
                    ExecuteMenuNavigate(window, step, logger);
                    break;

                case "wait":
                    ExecuteWait(window, step, automation, logger);
                    break;

                default:
                    logger.LogWarning("Unknown desktop step action '{Action}' — skipping", step.Action);
                    return TestStep.Pass(label,
                        $"Skipped unknown action '{step.Action}' ({sw.ElapsedMilliseconds}ms)");
            }

            return TestStep.Pass(label, $"{step.Action} completed ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Step '{Action}' failed", step.Action);
            return TestStep.Fail(label, ex.Message, $"{step.Action} failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────
    // Click actions
    // ─────────────────────────────────────────────────────

    private static void ExecuteClick(Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var element = ResolveElement(window, step, automation, logger);

        // Remember current window count before clicking — buttons like OK/Cancel/Submit
        // can close dialogs or open new windows.
        int windowCountBefore;
        Application? ownerApp = null;
        try
        {
            ownerApp = Application.Attach(window.Properties.ProcessId.Value);
            windowCountBefore = ownerApp.GetAllTopLevelWindows(automation).Length;
        }
        catch { windowCountBefore = -1; }

        ClickElement(element, logger);
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));

        // If the click changed the window count (dialog closed / new window opened),
        // give the app extra time to settle before the next step.
        if (windowCountBefore >= 0 && ownerApp is not null)
        {
            try
            {
                var windowCountAfter = ownerApp.GetAllTopLevelWindows(automation).Length;
                if (windowCountAfter != windowCountBefore)
                {
                    logger.LogDebug("Window count changed ({Before} -> {After}) — waiting for app to settle",
                        windowCountBefore, windowCountAfter);
                    Thread.Sleep(1500);
                    Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(500));
                }
            }
            catch { /* best effort */ }
        }
    }

    private static void ExecuteDoubleClick(Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var element = ResolveElement(window, step, automation, logger);
        element.DoubleClick();
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
    }

    private static void ExecuteRightClick(Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var element = ResolveElement(window, step, automation, logger);
        element.RightClick();
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Click an element using multiple strategies — handles ToolStrip buttons, ribbon items,
    /// and other WinForms controls that don't respond to a simple coordinate Click.
    /// </summary>
    private static void ClickElement(AutomationElement element, ILogger logger)
    {
        // 1. InvokePattern — most reliable for buttons, toolbar items, menu items.
        //    Triggers the control's default action without needing coordinates.
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                logger.LogDebug("Clicked via InvokePattern");
                return;
            }
        }
        catch { /* fall through */ }

        // 2. Standard Click — works for most interactive elements
        try
        {
            element.Click();
            return;
        }
        catch { /* fall through */ }

        // 3. Mouse.Click at the element's clickable point — last resort
        try
        {
            var point = element.GetClickablePoint();
            Mouse.Click(point);
            logger.LogDebug("Clicked via Mouse.Click at ({X},{Y})", point.X, point.Y);
        }
        catch (Exception ex)
        {
            logger.LogWarning("All click strategies failed: {Msg}", ex.Message);
            throw;
        }
    }

    // ─────────────────────────────────────────────────────
    // Input actions
    // ─────────────────────────────────────────────────────

    private static void ExecuteFill(Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var element = ResolveElement(window, step, automation, logger);

        // Try ValuePattern first (most reliable for text boxes)
        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(step.Value ?? "");
        }
        else
        {
            // Fallback: focus + keyboard
            element.Focus();
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(100));
            // Select all existing text then type new value
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(50));
            Keyboard.Type(step.Value ?? "");
        }

        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
    }

    private static void ExecuteSelect(Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var element = ResolveElement(window, step, automation, logger);
        var comboBox = element.AsComboBox();

        if (comboBox is null)
            throw new InvalidOperationException(
                $"Element is not a ComboBox (ControlType: {element.Properties.ControlType.ValueOrDefault})");

        var item = comboBox.Select(step.Value ?? "");

        if (item is null)
            throw new InvalidOperationException($"ComboBox item '{step.Value}' not found");

        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
    }

    private static void ExecuteCheck(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger, bool isChecked)
    {
        var element = ResolveElement(window, step, automation, logger);

        if (element.Patterns.Toggle.IsSupported)
        {
            var current = element.Patterns.Toggle.Pattern.ToggleState.Value;
            var desired = isChecked ? FlaUI.Core.Definitions.ToggleState.On : FlaUI.Core.Definitions.ToggleState.Off;

            if (current != desired)
                element.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            // Fallback: click
            element.Click();
        }

        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
    }

    private static void ExecutePress(DesktopUiStep step, ILogger logger)
    {
        var keyName = step.Value ?? "Enter";

        if (TryParseVirtualKey(keyName, out var vk))
        {
            Keyboard.Press(vk);
        }
        else
        {
            // Type as text (single character)
            Keyboard.Type(keyName);
        }

        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
    }

    private static void ExecuteHover(Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var element = ResolveElement(window, step, automation, logger);
        var point = element.GetClickablePoint();
        Mouse.MoveTo(point);
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
    }

    // ─────────────────────────────────────────────────────
    // Assertions
    // ─────────────────────────────────────────────────────

    private static TestStep ExecuteAssertText(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger,
        string label, Stopwatch sw)
    {
        var expected = step.Value ?? "";
        var timeout = Math.Max(step.TimeoutMs, 15_000); // at least 15s for assertions
        var actual = "";

        // Poll until the expected text appears or timeout expires.
        // Uses quick single-attempt searches (not the full-retry FindElementAcrossWindows)
        // so we can re-read the text every 500ms as it changes (e.g. "in progress" → "completed").
        while (sw.ElapsedMilliseconds < timeout)
        {
            // Quick search — try all scopes once without retrying
            var element = QuickFindElement(window, step, automation);
            if (element is not null)
            {
                actual = GetElementText(element);
                if (actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
                    return TestStep.Pass(label,
                        $"assert-text passed: '{actual}' contains '{expected}' ({sw.ElapsedMilliseconds}ms)");
            }

            Thread.Sleep(500);
        }

        if (string.IsNullOrEmpty(actual))
            return TestStep.Fail(label, "Element not found for assert-text",
                $"assert-text failed: element not found ({sw.ElapsedMilliseconds}ms)");

        return TestStep.Fail(label,
            $"Expected text containing '{expected}' but got '{actual}'",
            $"assert-text failed after {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Single-attempt element search across all scopes — no retry/polling.
    /// Used inside assertion polling loops where the caller handles retries.
    /// </summary>
    private static AutomationElement? QuickFindElement(
        Window window, DesktopUiStep step, UIA3Automation automation)
    {
        // 1. Primary window
        var element = TryFindInScope(window, step, automation);
        if (element is not null) return element;

        // 2. All other app windows
        try
        {
            var allWindows = Application.Attach(window.Properties.ProcessId.Value)
                .GetAllTopLevelWindows(automation);
            foreach (var w in allWindows)
            {
                try
                {
                    if (w.Properties.NativeWindowHandle.ValueOrDefault ==
                        window.Properties.NativeWindowHandle.ValueOrDefault)
                        continue;
                }
                catch { continue; }
                element = TryFindInScope(w, step, automation);
                if (element is not null) return element;
            }
        }
        catch { }

        // 3. Desktop root
        try
        {
            element = TryFindInScope(automation.GetDesktop(), step, automation);
            if (element is not null) return element;
        }
        catch { }

        return null;
    }

    private static TestStep ExecuteAssertVisible(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger,
        string label, Stopwatch sw, bool expectVisible)
    {
        var element = FindElementAcrossWindows(window, step, automation, logger);

        if (expectVisible)
        {
            if (element is null)
                return TestStep.Fail(label, "Element not found (expected visible)",
                    $"assert-visible failed ({sw.ElapsedMilliseconds}ms)");

            var isOffscreen = element.Properties.IsOffscreen.ValueOrDefault;
            return !isOffscreen
                ? TestStep.Pass(label, $"assert-visible passed ({sw.ElapsedMilliseconds}ms)")
                : TestStep.Fail(label, "Element is off-screen",
                    $"assert-visible failed: element is off-screen ({sw.ElapsedMilliseconds}ms)");
        }
        else
        {
            if (element is null)
                return TestStep.Pass(label,
                    $"assert-hidden passed: element not found ({sw.ElapsedMilliseconds}ms)");

            var isOffscreen = element.Properties.IsOffscreen.ValueOrDefault;
            return isOffscreen
                ? TestStep.Pass(label, $"assert-hidden passed ({sw.ElapsedMilliseconds}ms)")
                : TestStep.Fail(label, "Element is visible (expected hidden)",
                    $"assert-hidden failed ({sw.ElapsedMilliseconds}ms)");
        }
    }

    private static TestStep ExecuteAssertEnabled(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger,
        string label, Stopwatch sw, bool expectEnabled)
    {
        var element = FindElementAcrossWindows(window, step, automation, logger);
        if (element is null)
            return TestStep.Fail(label, "Element not found",
                $"assert-enabled/disabled failed: element not found ({sw.ElapsedMilliseconds}ms)");

        var isEnabled = element.Properties.IsEnabled.ValueOrDefault;

        if (isEnabled == expectEnabled)
            return TestStep.Pass(label,
                $"assert-{(expectEnabled ? "enabled" : "disabled")} passed ({sw.ElapsedMilliseconds}ms)");

        return TestStep.Fail(label,
            $"Element is {(isEnabled ? "enabled" : "disabled")} (expected {(expectEnabled ? "enabled" : "disabled")})",
            $"assertion failed ({sw.ElapsedMilliseconds}ms)");
    }

    // ─────────────────────────────────────────────────────
    // Window actions
    // ─────────────────────────────────────────────────────

    private static void ExecuteWaitForWindow(Application app, DesktopUiStep step, ILogger logger)
    {
        var title = step.WindowTitle ?? step.Value ?? "";
        var timeout = TimeSpan.FromMilliseconds(step.TimeoutMs);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            var windows = app.GetAllTopLevelWindows(new UIA3Automation());
            if (windows.Any(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogDebug("Window '{Title}' found after {Ms}ms", title, sw.ElapsedMilliseconds);
                return;
            }
            Thread.Sleep(250);
        }

        throw new TimeoutException($"Window '{title}' did not appear within {step.TimeoutMs}ms");
    }

    private static void ExecuteSwitchWindow(Application app, DesktopUiStep step, ILogger logger)
    {
        var title = step.WindowTitle ?? step.Value ?? "";
        var windows = app.GetAllTopLevelWindows(new UIA3Automation());
        var target = windows.FirstOrDefault(w =>
            w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            throw new InvalidOperationException($"Window '{title}' not found");

        target.Focus();
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(200));
        logger.LogDebug("Switched to window '{Title}'", target.Title);
    }

    private static void ExecuteCloseWindow(Application app, DesktopUiStep step, ILogger logger)
    {
        var title = step.WindowTitle ?? step.Value ?? "";
        var windows = app.GetAllTopLevelWindows(new UIA3Automation());
        var target = windows.FirstOrDefault(w =>
            w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            logger.LogDebug("Window '{Title}' not found — already closed?", title);
            return;
        }

        target.Close();
        Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(500));
        logger.LogDebug("Closed window '{Title}'", title);
    }

    // ─────────────────────────────────────────────────────
    // Menu navigation
    // ─────────────────────────────────────────────────────

    private static void ExecuteMenuNavigate(Window window, DesktopUiStep step, ILogger logger)
    {
        var menuPath = step.MenuPath ?? step.Value ?? "";
        var items = menuPath.Split(" > ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (items.Length == 0)
            throw new ArgumentException("MenuPath is empty");

        // Find the menu bar
        var menuBar = window.FindFirstDescendant(window.ConditionFactory.ByControlType(
            FlaUI.Core.Definitions.ControlType.MenuBar));

        if (menuBar is null)
            throw new InvalidOperationException("No MenuBar found in the window");

        // Navigate through each menu item in the chain
        AutomationElement? current = menuBar;
        foreach (var itemName in items)
        {
            var menuItem = current.FindFirstDescendant(
                current.ConditionFactory.ByName(itemName));

            if (menuItem is null)
                throw new InvalidOperationException($"Menu item '{itemName}' not found in path '{menuPath}'");

            menuItem.Click();
            Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(300));
            current = menuItem;
        }

        logger.LogDebug("Navigated menu path: {Path}", menuPath);
    }

    // ─────────────────────────────────────────────────────
    // Wait
    // ─────────────────────────────────────────────────────

    private static void ExecuteWait(Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        // If element selector is provided, wait for it to appear
        if (!string.IsNullOrEmpty(step.AutomationId) || !string.IsNullOrEmpty(step.Name) ||
            !string.IsNullOrEmpty(step.TreePath))
        {
            var element = DesktopElementResolver.FindElement(window, step, automation, logger);
            if (element is null)
                throw new TimeoutException($"Wait: element not found within {step.TimeoutMs}ms");
            return;
        }

        // Otherwise, fixed delay
        if (int.TryParse(step.Value, out var ms))
        {
            Thread.Sleep(ms);
        }
        else
        {
            Thread.Sleep(step.TimeoutMs);
        }
    }

    // ─────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Search for an element across the primary window, all other app windows, and
    /// the desktop root. Polls with retry up to the step's timeout.
    /// Returns null if not found (used by assertions which handle null gracefully).
    /// </summary>
    private static AutomationElement? FindElementAcrossWindows(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        return FindElementWithRetry(window, step, automation, logger);
    }

    private static AutomationElement ResolveElement(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var element = FindElementWithRetry(window, step, automation, logger);
        if (element is null)
        {
            var selectorDesc = step.AutomationId ?? step.Name ?? step.TreePath ?? "(none)";
            throw new InvalidOperationException(
                $"Element not found: {selectorDesc} (timeout {step.TimeoutMs}ms)");
        }
        return element;
    }

    /// <summary>
    /// Polls for an element across all possible scopes until found or timeout expires.
    /// Each iteration does a quick (non-blocking) search across:
    ///   1. The primary window
    ///   2. All other top-level app windows
    ///   3. The desktop root (catches MDI child forms, floating toolbars, etc.)
    /// </summary>
    private static AutomationElement? FindElementWithRetry(
        Window window, DesktopUiStep step, UIA3Automation automation, ILogger logger)
    {
        var sw = Stopwatch.StartNew();
        var timeout = step.TimeoutMs;
        Window[]? allWindows = null;

        while (sw.ElapsedMilliseconds < timeout)
        {
            // 1. Primary window
            var element = TryFindInScope(window, step, automation);
            if (element is not null) return element;

            // 2. All other top-level windows (cached per retry cycle)
            try
            {
                allWindows ??= Application.Attach(window.Properties.ProcessId.Value)
                    .GetAllTopLevelWindows(automation);

                foreach (var w in allWindows)
                {
                    try
                    {
                        if (w.Properties.NativeWindowHandle.ValueOrDefault ==
                            window.Properties.NativeWindowHandle.ValueOrDefault)
                            continue;
                    }
                    catch { continue; }

                    element = TryFindInScope(w, step, automation);
                    if (element is not null)
                    {
                        logger.LogDebug("Element found in window '{Title}' (fallback)", w.Title);
                        return element;
                    }
                }
            }
            catch { /* app might be transitioning */ }

            // 3. Desktop root — last resort, catches everything the process owns
            try
            {
                var desktop = automation.GetDesktop();
                element = TryFindInScope(desktop, step, automation);
                if (element is not null)
                {
                    logger.LogDebug("Element found via desktop root search");
                    return element;
                }
            }
            catch { /* best effort */ }

            // Refresh window list on next iteration
            allWindows = null;
            Thread.Sleep(300);
        }

        return null;
    }

    /// <summary>Single non-blocking attempt to find an element within one scope.</summary>
    private static AutomationElement? TryFindInScope(
        AutomationElement scope, DesktopUiStep step, UIA3Automation automation)
    {
        var cf = scope.ConditionFactory;

        if (!string.IsNullOrEmpty(step.AutomationId))
        {
            var el = scope.FindFirstDescendant(cf.ByAutomationId(step.AutomationId));
            if (el is not null) return el;
        }

        if (!string.IsNullOrEmpty(step.Name))
        {
            var el = scope.FindFirstDescendant(cf.ByName(step.Name));
            if (el is not null) return el;
        }

        if (!string.IsNullOrEmpty(step.ClassName) && !string.IsNullOrEmpty(step.ControlType)
            && Enum.TryParse<FlaUI.Core.Definitions.ControlType>(step.ControlType, true, out var ct))
        {
            var condition = new FlaUI.Core.Conditions.AndCondition(
                cf.ByClassName(step.ClassName), cf.ByControlType(ct));
            var allMatches = scope.FindAllDescendants(condition);
            // Only use ClassName+ControlType if it uniquely identifies the element.
            // Multiple matches (e.g. several Edit fields) → fall through to TreePath.
            if (allMatches.Length == 1)
                return allMatches[0];
            if (allMatches.Length > 1 && string.IsNullOrEmpty(step.TreePath))
                return allMatches[0]; // No TreePath — best effort
        }

        // TreePath — positional path, distinguishes sibling controls of same type
        if (!string.IsNullOrEmpty(step.TreePath))
        {
            var el = DesktopElementResolver.ResolveTreePath(scope, step.TreePath);
            if (el is not null) return el;
        }

        return null;
    }

    private static string GetElementText(AutomationElement element)
    {
        // 1. ValuePattern (text boxes, editable controls)
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var val = element.Patterns.Value.Pattern.Value.Value;
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
        }
        catch { }

        // 2. Name property (labels, buttons, tree items)
        var name = element.Properties.Name.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();

        // 3. Search immediate children (handles containers like Pane/Group)
        try
        {
            var children = element.FindAllChildren();
            foreach (var child in children)
            {
                try
                {
                    if (child.Patterns.Value.IsSupported)
                    {
                        var val = child.Patterns.Value.Pattern.Value.Value;
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                    }
                }
                catch { }

                var childName = child.Properties.Name.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(childName)) return childName.Trim();
            }
        }
        catch { }

        // 4. Search all descendant Text controls (deeper nested layouts)
        try
        {
            var textElements = element.FindAllDescendants(
                element.ConditionFactory.ByControlType(FlaUI.Core.Definitions.ControlType.Text));
            foreach (var te in textElements)
            {
                var teName = te.Properties.Name.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(teName)) return teName.Trim();
            }
        }
        catch { }

        return "";
    }

    private static bool TryParseVirtualKey(string keyName, out VirtualKeyShort vk)
    {
        vk = keyName.ToLowerInvariant() switch
        {
            "enter" or "return" => VirtualKeyShort.ENTER,
            "tab" => VirtualKeyShort.TAB,
            "escape" or "esc" => VirtualKeyShort.ESCAPE,
            "space" => VirtualKeyShort.SPACE,
            "backspace" or "back" => VirtualKeyShort.BACK,
            "delete" or "del" => VirtualKeyShort.DELETE,
            "up" or "arrowup" => VirtualKeyShort.UP,
            "down" or "arrowdown" => VirtualKeyShort.DOWN,
            "left" or "arrowleft" => VirtualKeyShort.LEFT,
            "right" or "arrowright" => VirtualKeyShort.RIGHT,
            "home" => VirtualKeyShort.HOME,
            "end" => VirtualKeyShort.END,
            "pageup" => VirtualKeyShort.PRIOR,
            "pagedown" => VirtualKeyShort.NEXT,
            "f1" => VirtualKeyShort.F1,
            "f2" => VirtualKeyShort.F2,
            "f3" => VirtualKeyShort.F3,
            "f4" => VirtualKeyShort.F4,
            "f5" => VirtualKeyShort.F5,
            "f6" => VirtualKeyShort.F6,
            "f7" => VirtualKeyShort.F7,
            "f8" => VirtualKeyShort.F8,
            "f9" => VirtualKeyShort.F9,
            "f10" => VirtualKeyShort.F10,
            "f11" => VirtualKeyShort.F11,
            "f12" => VirtualKeyShort.F12,
            _ => 0
        };
        return vk != 0;
    }
}
