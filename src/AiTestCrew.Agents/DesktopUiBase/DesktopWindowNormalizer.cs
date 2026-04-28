using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AiTestCrew.Agents.DesktopUiBase;

/// <summary>
/// Forces the target app's main window to a fixed size at launch and on every
/// detected window transition (e.g. login dialog → main form). Both recorder
/// and replay invoke the same normalization, so window-relative click
/// coordinates round-trip across monitors of different size and DPI.
/// </summary>
public static class DesktopWindowNormalizer
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    /// <summary>
    /// If the largest visible window owned by <paramref name="processId"/> is
    /// not already at the target dimensions (within a 16-pixel tolerance for
    /// border/shadow noise), un-maximize it and resize to (targetWidth ×
    /// targetHeight) at screen position (0, 0). No-ops if the process has no
    /// visible window or if the largest window is much smaller than the target
    /// (likely a transient dialog rather than the main form). Returns the HWND
    /// that was processed, or IntPtr.Zero if nothing was done.
    /// </summary>
    public static IntPtr TryNormalize(uint processId, int targetWidth, int targetHeight, ILogger logger)
    {
        if (targetWidth <= 0 || targetHeight <= 0) return IntPtr.Zero;

        var (hwnd, currentRect) = FindLargestVisibleWindow(processId);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        var currentWidth = currentRect.Right - currentRect.Left;
        var currentHeight = currentRect.Bottom - currentRect.Top;

        // Skip transient dialogs — only resize windows that look like a main
        // form. A login dialog is typically <600px wide; we don't want to
        // stretch it. The post-login main form will be larger and will be
        // caught on the next normalize call.
        if (currentWidth < 500 || currentHeight < 400)
        {
            logger.LogDebug(
                "[DesktopWindowNormalizer] Skip — largest visible window is too small to be a main form ({W}x{H})",
                currentWidth, currentHeight);
            return IntPtr.Zero;
        }

        // Already at target — no-op.
        if (Math.Abs(currentWidth - targetWidth) <= 16
            && Math.Abs(currentHeight - targetHeight) <= 16
            && currentRect.Left == 0 && currentRect.Top == 0)
        {
            return hwnd;
        }

        try
        {
            // Un-maximize first; SetWindowPos won't actually resize a maximized
            // window — Windows treats the maximized rect as authoritative.
            ShowWindow(hwnd, SW_RESTORE);

            var ok = SetWindowPos(hwnd, HWND_TOP, 0, 0, targetWidth, targetHeight,
                SWP_NOZORDER | SWP_NOACTIVATE);

            if (ok)
            {
                logger.LogInformation(
                    "[DesktopWindowNormalizer] Normalized window 0x{Hwnd:X} from {OldW}x{OldH} at ({OldL},{OldT}) → {NewW}x{NewH} at (0,0)",
                    hwnd.ToInt64(), currentWidth, currentHeight, currentRect.Left, currentRect.Top,
                    targetWidth, targetHeight);
            }
            else
            {
                logger.LogWarning(
                    "[DesktopWindowNormalizer] SetWindowPos returned false for 0x{Hwnd:X}",
                    hwnd.ToInt64());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[DesktopWindowNormalizer] Failed to normalize window 0x{Hwnd:X}",
                hwnd.ToInt64());
        }

        return hwnd;
    }

    /// <summary>
    /// Bring the largest visible window of <paramref name="processId"/> to the
    /// foreground so subsequent <c>FromPoint</c>, OCR, and click operations
    /// land on the right window. Cheap and idempotent — safe to call before
    /// every step. No-op when the target window is already foreground.
    ///
    /// Required for replay because: (1) coord-based <c>FromPoint</c> returns
    /// whatever window is on top at the click pixel, not whatever window we
    /// "meant"; (2) OCR captures pixels of the topmost window. Without this,
    /// any other window that drifts on top of the target between steps
    /// (browser, IDE, notification) corrupts the assertion.
    ///
    /// Uses the standard "make Windows let us SetForegroundWindow" toolkit:
    /// SW_RESTORE, fake Alt keypress (grants foreground-set permission),
    /// AttachThreadInput, BringWindowToTop, SetForegroundWindow, and
    /// SwitchToThisWindow / topmost-toggle as last resorts.
    /// </summary>
    public static IntPtr EnsureForeground(uint processId, ILogger logger)
    {
        var (hwnd, _) = FindLargestVisibleWindow(processId);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        // Already foreground — fast path. Avoids the Alt-key flicker from
        // running the full toolkit on every step.
        var fg = GetForegroundWindow();
        if (fg == hwnd) return hwnd;

        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            ShowWindow(hwnd, SW_SHOW);

            // Alt keypress grants the calling process foreground-set permission
            // for a brief window, bypassing the SetForegroundWindow lock that
            // kicks in when the agent has been polling without recent user input.
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            var foregroundWnd = fg;
            uint foregroundThreadId = GetWindowThreadProcessId(foregroundWnd, out _);
            uint currentThreadId = GetCurrentThreadId();

            var attached = false;
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);

            BringWindowToTop(hwnd);
            var fgOk = SetForegroundWindow(hwnd);

            // Topmost-toggle as last resort. Always works, then immediately
            // demote so the user can still Alt-Tab normally.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            if (!fgOk) SwitchToThisWindow(hwnd, true);
            if (attached) AttachThreadInput(currentThreadId, foregroundThreadId, false);

            var actualFg = GetForegroundWindow();
            if (actualFg != hwnd)
                logger.LogDebug(
                    "[DesktopWindowNormalizer] EnsureForeground: target 0x{Target:X} but got 0x{Actual:X}",
                    hwnd.ToInt64(), actualFg.ToInt64());
        }
        catch (Exception ex)
        {
            logger.LogDebug("[DesktopWindowNormalizer] EnsureForeground threw: {Msg}", ex.Message);
        }

        return hwnd;
    }

    private static (IntPtr Hwnd, RECT Rect) FindLargestVisibleWindow(uint processId)
    {
        var largest = IntPtr.Zero;
        var largestRect = default(RECT);
        long largestArea = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != processId) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            long w = rect.Right - rect.Left;
            long h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return true;
            long area = w * h;
            if (area > largestArea)
            {
                largestArea = area;
                largest = hwnd;
                largestRect = rect;
            }
            return true;
        }, IntPtr.Zero);

        return (largest, largestRect);
    }
}
