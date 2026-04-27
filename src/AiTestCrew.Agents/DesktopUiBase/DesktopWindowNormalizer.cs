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
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

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
