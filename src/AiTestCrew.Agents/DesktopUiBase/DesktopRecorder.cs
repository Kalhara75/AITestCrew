using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using AiTestCrew.Agents.Shared;
using AiTestCrew.Core.Configuration;

namespace AiTestCrew.Agents.DesktopUiBase;

/// <summary>
/// Records a desktop UI test case by watching the user interact with a live WinForms application.
/// Steps are captured with real UI Automation element properties — no LLM involved.
///
/// Usage:
///   var testCase = await DesktopRecorder.RecordAsync(appPath, appArgs, caseName, config, logger, ct);
///
/// The target application is launched automatically. A floating control panel provides
/// assertion buttons and a "Save &amp; Stop" button. The resulting DesktopUiTestCase is
/// ready for deterministic replay.
///
/// Event capture approach:
///   - UI Automation FocusChanged events detect when the user focuses a new element
///   - A low-level mouse hook (WH_MOUSE_LL) captures click events and resolves the
///     clicked element via AutomationElement.FromPoint()
///   - A low-level keyboard hook (WH_KEYBOARD_LL) captures keystrokes and coalesces
///     them into fill steps targeting the currently focused element
/// </summary>
public static class DesktopRecorder
{
    // Win32 hook constants and imports
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_KEYDOWN = 0x0100;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;

    // Message pump — required for low-level hooks to fire
    private const int PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public static async Task<DesktopUiTestCase> RecordAsync(
        string appPath,
        string? appArgs,
        string caseName,
        TestEnvironmentConfig config,
        ILogger logger,
        CancellationToken ct = default)
    {
        var steps = new List<DesktopUiStep>();
        var stopSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        logger.LogInformation("[DesktopRecorder] Starting recording for '{Name}' — launching {App}",
            caseName, Path.GetFileName(appPath));

        using var automation = new UIA3Automation();

        // Launch the target application — set WorkingDirectory to the app's own folder
        // so it can find sibling DLLs (many WinForms apps load assemblies relative to their exe).
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = appPath,
            Arguments = appArgs ?? "",
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? ""
        };
        var app = Application.Launch(psi);

        IntPtr mouseHook = IntPtr.Zero;
        IntPtr keyboardHook = IntPtr.Zero;

        // Track keyboard input state
        var keyBuffer = new System.Text.StringBuilder();
        AutomationElement? currentFocusedElement = null;
        DesktopUiStep? currentFocusSelector = null;
        AutomationElement? lastClickedElement = null; // for assertion text extraction
        var targetProcessId = (uint)app.ProcessId;

        // Control panel window handle (to filter from recording)
        var controlPanelProcessId = (uint)Process.GetCurrentProcess().Id;

        try
        {
            // Wait for the main window
            var timeout = TimeSpan.FromSeconds(config.WinFormsAppLaunchTimeoutSeconds);
            var sw = Stopwatch.StartNew();
            Window? mainWindow = null;

            while (sw.Elapsed < timeout)
            {
                var windows = app.GetAllTopLevelWindows(automation);
                if (windows.Length > 0)
                {
                    mainWindow = windows[0];
                    break;
                }
                Thread.Sleep(250);
            }

            if (mainWindow is null)
                throw new TimeoutException(
                    $"Application did not show a window within {config.WinFormsAppLaunchTimeoutSeconds}s");

            logger.LogInformation("[DesktopRecorder] Main window: \"{Title}\"", mainWindow.Title);

            // When the recorder is invoked from a long-running agent process (polling, no
            // recent user input), Windows' SetForegroundWindow restrictions cause the newly
            // launched app to open behind the browser/console — the user never sees it.
            // Replay doesn't hit this because later Invoke/Click calls activate the window
            // as a side effect; recording has no programmatic interaction, so force it here.
            BringAppToForeground(mainWindow.Properties.NativeWindowHandle.ValueOrDefault, logger);

            // ── Flush keyboard buffer ──
            void FlushKeyBuffer()
            {
                if (keyBuffer.Length > 0 && currentFocusSelector is not null)
                {
                    var fillStep = new DesktopUiStep
                    {
                        Action = "fill",
                        AutomationId = currentFocusSelector.AutomationId,
                        Name = currentFocusSelector.Name,
                        ClassName = currentFocusSelector.ClassName,
                        ControlType = currentFocusSelector.ControlType,
                        TreePath = currentFocusSelector.TreePath,
                        Value = keyBuffer.ToString()
                    };

                    // Deduplicate: update if the last step is a fill on the same element
                    if (steps.Count > 0 && steps[^1].Action == "fill" &&
                        steps[^1].AutomationId == fillStep.AutomationId &&
                        steps[^1].Name == fillStep.Name)
                    {
                        steps[^1] = fillStep;
                        logger.LogDebug("[DesktopRecorder] Updated fill: {Id}", fillStep.AutomationId ?? fillStep.Name);
                    }
                    else
                    {
                        steps.Add(fillStep);
                        logger.LogDebug("[DesktopRecorder] Captured fill: {Id} = {Val}",
                            fillStep.AutomationId ?? fillStep.Name, fillStep.Value);
                    }

                    keyBuffer.Clear();
                }
            }

            // ── Mouse hook ──
            HookProc mouseProc = (int nCode, IntPtr wParam, IntPtr lParam) =>
            {
                if (nCode >= 0)
                {
                    var msg = wParam.ToInt32();
                    if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_LBUTTONDBLCLK)
                    {
                        try
                        {
                            // Check if the click is on the target app (not our control panel)
                            var fgWnd = GetForegroundWindow();
                            GetWindowThreadProcessId(fgWnd, out var fgProcId);

                            if (fgProcId == targetProcessId)
                            {
                                FlushKeyBuffer();

                                GetCursorPos(out var pt);
                                var element = automation.FromPoint(new System.Drawing.Point(pt.X, pt.Y));

                                if (element is not null)
                                {
                                    // Skip non-interactive chrome and system UI elements
                                    if (IsWindowChrome(element) || IsSystemElement(element))
                                    {
                                        // Skip — not a target app interaction
                                    }
                                    else
                                    {
                                    var selector = DesktopElementResolver.BuildSelector(element, mainWindow);
                                    var action = msg switch
                                    {
                                        WM_RBUTTONDOWN => "right-click",
                                        WM_LBUTTONDBLCLK => "double-click",
                                        _ => "click"
                                    };

                                    selector.Action = action;
                                    steps.Add(selector);
                                    logger.LogDebug("[DesktopRecorder] Captured {Action}: {Id}",
                                        action, selector.AutomationId ?? selector.Name ?? selector.TreePath);

                                    // Update tracking
                                    lastClickedElement = element;
                                    currentFocusedElement = element;
                                    currentFocusSelector = DesktopElementResolver.BuildSelector(element, mainWindow);
                                    } // end else (non-chrome click)
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug("[DesktopRecorder] Mouse hook error: {Msg}", ex.Message);
                        }
                    }
                }
                return CallNextHookEx(mouseHook, nCode, wParam, lParam);
            };

            // ── Keyboard hook ──
            HookProc keyboardProc = (int nCode, IntPtr wParam, IntPtr lParam) =>
            {
                if (nCode >= 0 && wParam.ToInt32() == WM_KEYDOWN)
                {
                    try
                    {
                        var fgWnd = GetForegroundWindow();
                        GetWindowThreadProcessId(fgWnd, out var fgProcId);

                        if (fgProcId == targetProcessId)
                        {
                            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                            var vk = hookStruct.vkCode;
                            var ctrlHeld = (GetKeyState(VK_CONTROL) & 0x8000) != 0;

                            // ── Ctrl+key combinations ──
                            if (ctrlHeld)
                            {
                                if (vk == 0x56) // Ctrl+V — Paste
                                {
                                    FlushKeyBuffer();
                                    // Read clipboard content and record as a fill step
                                    // with the actual pasted value, not just "V"
                                    try
                                    {
                                        string? clipText = null;
                                        // Clipboard must be accessed from an STA thread
                                        var clipThread = new Thread(() =>
                                        {
                                            try { clipText = System.Windows.Forms.Clipboard.GetText(); }
                                            catch { }
                                        });
                                        clipThread.SetApartmentState(ApartmentState.STA);
                                        clipThread.Start();
                                        clipThread.Join(1000);

                                        if (!string.IsNullOrEmpty(clipText) && currentFocusSelector is not null)
                                        {
                                            var fillStep = new DesktopUiStep
                                            {
                                                Action = "fill",
                                                AutomationId = currentFocusSelector.AutomationId,
                                                Name = currentFocusSelector.Name,
                                                ClassName = currentFocusSelector.ClassName,
                                                ControlType = currentFocusSelector.ControlType,
                                                TreePath = currentFocusSelector.TreePath,
                                                Value = clipText.Trim()
                                            };
                                            steps.Add(fillStep);
                                            logger.LogDebug("[DesktopRecorder] Captured paste: {Id} = {Val}",
                                                fillStep.AutomationId ?? fillStep.Name, clipText.Trim());
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogDebug("[DesktopRecorder] Clipboard read failed: {Msg}", ex.Message);
                                    }
                                }
                                // Ctrl+A, Ctrl+C, Ctrl+X — ignore (select/copy/cut don't produce text)
                                // Don't add these characters to keyBuffer
                            }
                            // ── Special keys ──
                            else if (vk == 0x0D) // Enter
                            {
                                FlushKeyBuffer();
                                steps.Add(new DesktopUiStep { Action = "press", Value = "Enter" });
                                logger.LogDebug("[DesktopRecorder] Captured press: Enter");
                            }
                            else if (vk == 0x1B) // Escape
                            {
                                FlushKeyBuffer();
                                steps.Add(new DesktopUiStep { Action = "press", Value = "Escape" });
                                logger.LogDebug("[DesktopRecorder] Captured press: Escape");
                            }
                            else if (vk == 0x09) // Tab
                            {
                                FlushKeyBuffer();
                                steps.Add(new DesktopUiStep { Action = "press", Value = "Tab" });
                                logger.LogDebug("[DesktopRecorder] Captured press: Tab");
                            }
                            else if (vk == 0x08) // Backspace
                            {
                                if (keyBuffer.Length > 0)
                                    keyBuffer.Remove(keyBuffer.Length - 1, 1);
                            }
                            else if (vk >= 0x20 && vk <= 0x7E)
                            {
                                // Printable characters — add to buffer
                                keyBuffer.Append((char)vk);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("[DesktopRecorder] Keyboard hook error: {Msg}", ex.Message);
                    }
                }
                return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
            };

            // Install hooks
            var moduleHandle = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, moduleHandle, 0);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, moduleHandle, 0);

            if (mouseHook == IntPtr.Zero)
                logger.LogWarning("[DesktopRecorder] Failed to install mouse hook");
            if (keyboardHook == IntPtr.Zero)
                logger.LogWarning("[DesktopRecorder] Failed to install keyboard hook");

            // ── Console control panel (for headless/CLI mode) ──
            Console.WriteLine();
            Console.WriteLine("  ┌─────────────────────────────────────────────────────┐");
            Console.WriteLine("  │  Application is open. Perform your test scenario.    │");
            Console.WriteLine("  │                                                      │");
            Console.WriteLine("  │  Press keys in the console to add assertions:        │");
            Console.WriteLine("  │    [T] Assert Text  — then click target element      │");
            Console.WriteLine("  │    [V] Assert Visible — then click target element    │");
            Console.WriteLine("  │    [E] Assert Enabled — then click target element    │");
            Console.WriteLine("  │    [S] Save & Stop                                   │");
            Console.WriteLine("  └─────────────────────────────────────────────────────┘");
            Console.WriteLine();

            // ── Assertion pick mode ──
            var pendingAssertType = "";

            // Run a background task to read console keys for assertions and stop
            _ = Task.Run(() =>
            {
                while (!stopSignal.Task.IsCompleted)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        switch (key.Key)
                        {
                            case ConsoleKey.T:
                                pendingAssertType = "assert-text";
                                Console.WriteLine("  → Click the target element to assert its text...");
                                break;
                            case ConsoleKey.V:
                                pendingAssertType = "assert-visible";
                                Console.WriteLine("  → Click the target element to assert visibility...");
                                break;
                            case ConsoleKey.E:
                                pendingAssertType = "assert-enabled";
                                Console.WriteLine("  → Click the target element to assert enabled state...");
                                break;
                            case ConsoleKey.S:
                                FlushKeyBuffer();
                                stopSignal.TrySetResult(true);
                                Console.WriteLine("  → Saving recording...");
                                break;
                        }
                    }
                    Thread.Sleep(50);
                }
            }, ct);

            // Watch for assertion picks: when pendingAssertType is set and the user clicks,
            // convert the last captured click step into an assertion step.
            // This is checked in a polling loop alongside the stop signal.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15));

            var lastStepCount = 0;
            var loopSw = Stopwatch.StartNew();
            var timeoutMs = 15 * 60 * 1000; // 15 minutes

            while (!stopSignal.Task.IsCompleted && loopSw.ElapsedMilliseconds < timeoutMs)
            {
                if (ct.IsCancellationRequested) break;

                // ── Pump Windows messages — this is what makes the hooks fire ──
                // Low-level hooks (WH_MOUSE_LL, WH_KEYBOARD_LL) deliver callbacks
                // via the message queue of the thread that installed them.
                // Without pumping, hook callbacks never execute.
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                // Check if a new step was added and we have a pending assertion
                if (!string.IsNullOrEmpty(pendingAssertType) && steps.Count > lastStepCount)
                {
                    var lastStep = steps[^1];
                    if (lastStep.Action == "click")
                    {
                        // Convert the click to an assertion
                        lastStep.Action = pendingAssertType;

                        if (pendingAssertType == "assert-text")
                        {
                            // Use the stored element reference directly — re-searching
                            // via FindElement can fail when mainWindow is stale (after
                            // navigating through MDI forms, dialogs, etc.)
                            try
                            {
                                var text = ExtractTextFromElement(lastClickedElement);
                                lastStep.Value = text;
                                if (!string.IsNullOrEmpty(text))
                                    logger.LogDebug("[DesktopRecorder] Extracted text: '{Text}'", text);
                                else
                                    logger.LogDebug("[DesktopRecorder] No text found on element");
                            }
                            catch { /* best effort */ }
                        }

                        var displayId = lastStep.AutomationId ?? lastStep.Name ?? "(element)";
                        var displayVal = !string.IsNullOrEmpty(lastStep.Value)
                            ? $" = \"{(lastStep.Value.Length > 40 ? lastStep.Value[..40] + "..." : lastStep.Value)}\""
                            : "";
                        logger.LogDebug("[DesktopRecorder] Converted click to {Assert}", pendingAssertType);
                        Console.WriteLine($"  + {pendingAssertType} added for {displayId}{displayVal}");
                        pendingAssertType = "";
                    }
                }

                lastStepCount = steps.Count;

                // Check if the app was closed
                if (app.HasExited)
                {
                    logger.LogInformation("[DesktopRecorder] Application was closed — stopping recording.");
                    stopSignal.TrySetResult(true);
                    break;
                }

                Thread.Sleep(50); // Small yield — stay on same thread to keep hooks alive
            }

            FlushKeyBuffer();
            logger.LogInformation("[DesktopRecorder] Recording stopped. {Count} steps captured.", steps.Count);
        }
        finally
        {
            // Unhook
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);

            // Close the app
            try
            {
                if (!app.HasExited) { app.Close(); app.WaitWhileBusy(TimeSpan.FromSeconds(3)); }
                if (!app.HasExited) app.Kill();
            }
            catch { /* best effort */ }
        }

        // Post-recording validation
        ValidateRecordedSteps(steps, logger);

        return new DesktopUiTestCase
        {
            Name = caseName,
            Description = $"Recorded desktop test case: {caseName}",
            Steps = steps,
            TakeScreenshotOnFailure = true
        };
    }

    private static void BringAppToForeground(IntPtr hwnd, ILogger logger)
    {
        if (hwnd == IntPtr.Zero)
        {
            logger.LogWarning("[DesktopRecorder] Main window has no native HWND — cannot foreground.");
            return;
        }

        logger.LogInformation("[DesktopRecorder] Bringing app window to foreground (hwnd=0x{Hwnd:X}).",
            hwnd.ToInt64());

        try
        {
            // 1. Make sure it isn't minimized.
            ShowWindow(hwnd, SW_RESTORE);
            ShowWindow(hwnd, SW_SHOW);

            // 2. Alt-key trick: Windows grants the calling process foreground-set
            //    permission for a brief window after a keyboard event. Simulating an
            //    Alt keypress bypasses the SetForegroundWindow lock that kicks in
            //    when the agent has been idle-polling without recent user input.
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            // 3. AttachThreadInput fallback — share the input queue with the current
            //    foreground thread so SetForegroundWindow is allowed.
            var foregroundWnd = GetForegroundWindow();
            uint foregroundThreadId = GetWindowThreadProcessId(foregroundWnd, out _);
            uint currentThreadId = GetCurrentThreadId();

            var attached = false;
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);

            BringWindowToTop(hwnd);
            var fgOk = SetForegroundWindow(hwnd);

            // 4. Topmost-toggle as last resort — SetWindowPos(HWND_TOPMOST) always works,
            //    then immediately demote back to non-topmost so the user can still
            //    Alt-Tab normally.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            if (!fgOk)
                SwitchToThisWindow(hwnd, true);

            if (attached)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);

            // Verify.
            var actualFg = GetForegroundWindow();
            logger.LogInformation("[DesktopRecorder] Foreground now hwnd=0x{Hwnd:X} (target was 0x{Target:X}).",
                actualFg.ToInt64(), hwnd.ToInt64());
        }
        catch (Exception ex)
        {
            logger.LogWarning("[DesktopRecorder] Foreground call failed: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Scans recorded steps for potential replay issues and logs warnings.
    /// Advisory only — does not block saving.
    /// </summary>
    private static void ValidateRecordedSteps(List<DesktopUiStep> steps, ILogger logger)
    {
        if (steps.Count == 0)
        {
            logger.LogWarning("[DesktopRecorder] No steps recorded.");
            return;
        }

        // Warn on steps with only TreePath (fragile)
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (string.IsNullOrEmpty(s.AutomationId) &&
                string.IsNullOrEmpty(s.Name) &&
                !string.IsNullOrEmpty(s.TreePath))
            {
                logger.LogWarning(
                    "[DesktopRecorder] Step {Idx}: only TreePath selector — may break if UI layout changes.",
                    i + 1);
            }
        }

        // Warn on consecutive clicks without waits
        for (var i = 1; i < steps.Count; i++)
        {
            if (steps[i - 1].Action.Contains("click") && steps[i].Action.Contains("click"))
                logger.LogWarning(
                    "[DesktopRecorder] Steps {Prev} and {Curr}: consecutive clicks — consider adding a wait.",
                    i, i + 1);
        }

        // Warn on no assertions
        if (!steps.Any(s => s.Action.StartsWith("assert-", StringComparison.OrdinalIgnoreCase)))
            logger.LogWarning("[DesktopRecorder] No assertion steps recorded. Use [T]/[V]/[E] to add assertions.");
    }

    /// <summary>
    /// Thoroughly extract visible text from an element. Handles:
    ///   1. ValuePattern (text boxes, editable controls)
    ///   2. Name property (labels, buttons, tree items)
    ///   3. Child text elements (when the clicked element is a container like Pane/Group)
    ///   4. All descendant Name values concatenated (last resort for complex controls)
    /// </summary>
    private static string ExtractTextFromElement(FlaUI.Core.AutomationElements.AutomationElement? element)
    {
        if (element is null) return "";

        // 1. ValuePattern — text boxes, editable inputs
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var val = element.Patterns.Value.Pattern.Value.Value;
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
        }
        catch { /* pattern not available */ }

        // 2. Name property — labels, buttons, tree items, menu items
        var name = element.Properties.Name.ValueOrDefault;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();

        // 3. Search immediate children for text (handles containers like Pane/Group)
        try
        {
            var children = element.FindAllChildren();
            foreach (var child in children)
            {
                // Check child ValuePattern
                try
                {
                    if (child.Patterns.Value.IsSupported)
                    {
                        var val = child.Patterns.Value.Pattern.Value.Value;
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                    }
                }
                catch { }

                // Check child Name
                var childName = child.Properties.Name.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(childName)) return childName.Trim();
            }
        }
        catch { }

        // 4. Search all Text/Edit descendants (deeper search for complex layouts)
        try
        {
            var textDescendants = element.FindAllDescendants(
                element.ConditionFactory.ByControlType(FlaUI.Core.Definitions.ControlType.Text));
            foreach (var td in textDescendants)
            {
                var tdName = td.Properties.Name.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(tdName)) return tdName.Trim();
            }

            var editDescendants = element.FindAllDescendants(
                element.ConditionFactory.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
            foreach (var ed in editDescendants)
            {
                try
                {
                    if (ed.Patterns.Value.IsSupported)
                    {
                        var val = ed.Patterns.Value.Pattern.Value.Value;
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                    }
                }
                catch { }
            }
        }
        catch { }

        return "";
    }

    private static bool IsWindowChrome(FlaUI.Core.AutomationElements.AutomationElement element)
    {
        try
        {
            var ctrlType = element.Properties.ControlType.ValueOrDefault;
            var autoId = element.Properties.AutomationId.ValueOrDefault ?? "";
            return autoId.Equals("TitleBar", StringComparison.OrdinalIgnoreCase) ||
                   ctrlType == FlaUI.Core.Definitions.ControlType.TitleBar ||
                   ctrlType == FlaUI.Core.Definitions.ControlType.ScrollBar ||
                   ctrlType == FlaUI.Core.Definitions.ControlType.Thumb;
        }
        catch { return false; }
    }

    /// <summary>
    /// Detect OS shell / system UI elements that should never be recorded:
    /// taskbar buttons, system tray, Start menu, notification area, etc.
    /// Uses class name patterns rather than PID so we don't accidentally
    /// filter out legitimate app elements with unexpected PIDs.
    /// </summary>
    private static bool IsSystemElement(FlaUI.Core.AutomationElements.AutomationElement element)
    {
        try
        {
            var className = element.Properties.ClassName.ValueOrDefault ?? "";
            var autoId = element.Properties.AutomationId.ValueOrDefault ?? "";

            // Taskbar button automation peers (UWP app buttons on taskbar)
            if (className.Contains("TaskListButton", StringComparison.OrdinalIgnoreCase))
                return true;

            // Shell tray / notification area
            if (className.StartsWith("Shell_", StringComparison.OrdinalIgnoreCase))
                return true;

            // Windows taskbar itself
            if (className.Equals("MSTaskListWClass", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("MSTaskSwWClass", StringComparison.OrdinalIgnoreCase))
                return true;

            // UWP app IDs on the taskbar (e.g. Microsoft.WindowsTerminal_xxx!App)
            if (autoId.Contains("!App", StringComparison.Ordinal) &&
                autoId.Contains("_", StringComparison.Ordinal))
                return true;

            return false;
        }
        catch { return false; }
    }
}
