using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

public sealed class CapturedFrame : IDisposable
{
    public required Bitmap Bitmap { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int MonitorIndex { get; init; }
    public string MonitorName { get; init; } = string.Empty;
    public Rectangle ScreenBounds { get; init; }

    public void Dispose() => Bitmap.Dispose();
}

public static class ScreenGrabber
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        return System.Windows.Forms.Screen.AllScreens
            .Select((s, i) => new MonitorInfo
            {
                Index = i,
                Name = s.DeviceName,
                IsPrimary = s.Primary,
                Bounds = s.Bounds
            })
            .ToList();
    }

    public static CapturedFrame CapturePrimary()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen
            ?? System.Windows.Forms.Screen.AllScreens[0];
        return CaptureBounds(screen.Bounds, 0, screen.DeviceName);
    }

    public static CapturedFrame CaptureMonitor(int index)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (index < 0 || index >= screens.Length)
            index = 0;
        var screen = screens[index];
        return CaptureBounds(screen.Bounds, index, screen.DeviceName);
    }

    public static CapturedFrame CaptureVirtualDesktop()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        return CaptureBounds(bounds, -1, "AllMonitors");
    }

    /// <summary>
    /// Captures the foreground window. Returns null if unavailable / minimized / invalid.
    /// </summary>
    public static CapturedFrame? TryCaptureForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        if (NativeMethods.IsIconic(hwnd))
            return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return null;

        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (bounds.Width < 16 || bounds.Height < 16)
            return null;

        // Intersect with virtual screen so off-screen edges don't blow up CopyFromScreen
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        bounds = Rectangle.Intersect(bounds, virtualScreen);
        if (bounds.Width < 16 || bounds.Height < 16)
            return null;

        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var index = Array.IndexOf(System.Windows.Forms.Screen.AllScreens, screen);
        if (index < 0)
            index = 0;

        return CaptureBounds(bounds, index, "ForegroundWindow");
    }

    public static string? GetForegroundProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        return GetProcessNameForWindow(hwnd);
    }

    /// <summary>Normalized process name for a window HWND, or null if unavailable.</summary>
    public static string? GetProcessNameForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return null;

        try
        {
            using var process = Process.GetProcessById(unchecked((int)pid));
            return ApplicationScopeEvaluator.NormalizeProcessName(process.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Top-most visible window (Z-order) whose process is in <paramref name="processNames"/>.
    /// Used when OS FG is outside the whitelist but Activate/raise changed Z-order among listed apps.
    /// </summary>
    public static string? GetTopmostProcessAmong(IEnumerable<string> processNames)
    {
        var targets = processNames
            .Select(ApplicationScopeEvaluator.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
            return null;

        string? found = null;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                return true;
            // Thread id unused; pid is what we need.
#pragma warning disable CA1806
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
#pragma warning restore CA1806
            if (pid == 0)
                return true;
            try
            {
                using var process = Process.GetProcessById(unchecked((int)pid));
                var name = ApplicationScopeEvaluator.NormalizeProcessName(process.ProcessName);
                if (!targets.Contains(name))
                    return true;
                if (!TryGetVisibleWindowBounds(hwnd, out var bounds) ||
                    bounds.Width < 8 || bounds.Height < 8)
                    return true;
                found = name;
                return false; // EnumWindows is top-to-bottom
            }
            catch
            {
                return true;
            }
        }, IntPtr.Zero);
        return found;
    }

    public static IReadOnlyList<string> GetRunningProcessNames()
    {
        return Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    return ApplicationScopeEvaluator.NormalizeProcessName(p.ProcessName);
                }
                catch
                {
                    return string.Empty;
                }
                finally
                {
                    p.Dispose();
                }
            })
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True if any of the given process names has at least one running instance.
    /// Uses GetProcessesByName (cheap for a small whitelist) rather than enumerating all processes.
    /// </summary>
    public static bool AnyNamedProcessRunning(IEnumerable<string> processNames)
    {
        foreach (var raw in processNames)
        {
            var name = ApplicationScopeEvaluator.NormalizeProcessName(raw);
            if (name.Length == 0)
                continue;

            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            try
            {
                if (procs.Length > 0)
                    return true;
            }
            finally
            {
                foreach (var p in procs)
                    p.Dispose();
            }
        }

        return false;
    }

    /// <summary>
    /// Fills black over top-level windows belonging to the given process names,
    /// relative to the capture's screen coordinates.
    /// Returns how many window overlaps were painted (0 = nothing masked).
    /// </summary>
    public static int MaskProcessWindows(Bitmap bitmap, Rectangle screenBounds, IEnumerable<string> processNames)
    {
        var targets = processNames
            .Select(ApplicationScopeEvaluator.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0 || bitmap.Width < 1 || bitmap.Height < 1)
            return 0;

        var absolute = CollectProcessWindowRectsAbsolute(targets);
        if (absolute.Count == 0)
            return 0;

        var painted = 0;
        using var g = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(Color.Black);
        foreach (var windowBounds in absolute)
        {
            var overlap = Rectangle.Intersect(windowBounds, screenBounds);
            if (overlap.Width < 1 || overlap.Height < 1)
                continue;
            g.FillRectangle(brush, new Rectangle(
                overlap.X - screenBounds.X,
                overlap.Y - screenBounds.Y,
                overlap.Width,
                overlap.Height));
            painted++;
        }

        return painted;
    }

    /// <summary>
    /// Visible top-level windows (Z-order top→bottom) for processes not in
    /// <paramref name="excludeProcessNames"/> (e.g. blacklist masks).
    /// </summary>
    public static IReadOnlyList<(IntPtr Hwnd, Rectangle Bounds)> GetVisibleProcessWindows(
        IEnumerable<string>? excludeProcessNames)
    {
        var exclude = (excludeProcessNames ?? [])
            .Select(ApplicationScopeEvaluator.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<IntPtr>();
        var windows = new List<(IntPtr, Rectangle)>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !seen.Add(hwnd))
                return true;
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                return true;
            if (!TryGetVisibleWindowBounds(hwnd, out var windowBounds))
                return true;
            if (windowBounds.Width < 8 || windowBounds.Height < 8)
                return true;

#pragma warning disable CA1806
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
#pragma warning restore CA1806
            if (pid == 0)
                return true;

            try
            {
                using var process = Process.GetProcessById(unchecked((int)pid));
                var name = ApplicationScopeEvaluator.NormalizeProcessName(process.ProcessName);
                if (exclude.Contains(name))
                    return true;
                windows.Add((hwnd, windowBounds));
            }
            catch
            {
                // ignore inaccessible processes
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Captures a specific window by HWND via PrintWindow (correct under DPI;
    /// works while other apps remain focused if the target is TopMost).
    /// </summary>
    public static CapturedFrame? TryCaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            return null;
        if (!TryGetVisibleWindowBounds(hwnd, out var bounds))
            return null;

        // Ensure child controls (e.g. TextBox) have painted before PrintWindow snapshots.
        NativeMethods.RedrawWindow(
            hwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RdwInvalidate | NativeMethods.RdwErase | NativeMethods.RdwUpdateNow | NativeMethods.RdwAllChildren);

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PwRenderFullContent) &&
                    !NativeMethods.PrintWindow(hwnd, hdc, 0))
                {
                    bitmap.Dispose();
                    return null;
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        return new CapturedFrame
        {
            Bitmap = bitmap,
            Timestamp = DateTimeOffset.Now,
            MonitorIndex = -3,
            MonitorName = "Window",
            ScreenBounds = bounds
        };
    }

    /// <summary>
    /// Captures visible top-level windows for the given process names via PrintWindow
    /// and composites them onto a black canvas (union bounds).
    /// </summary>
    public static CapturedFrame? TryCaptureProcessWindows(IEnumerable<string> processNames)
    {
        var targets = processNames
            .Select(ApplicationScopeEvaluator.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
            return null;

        var windows = CollectProcessWindows(targets);
        if (windows.Count == 0)
            return null;

        var union = windows[0].Bounds;
        for (var i = 1; i < windows.Count; i++)
            union = Rectangle.Union(union, windows[i].Bounds);
        if (union.Width < 16 || union.Height < 16)
            return null;

        var canvas = new Bitmap(union.Width, union.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black);
            foreach (var (hwnd, bounds) in windows)
            {
                using var part = TryCaptureWindow(hwnd);
                if (part is null)
                    continue;
                g.DrawImage(part.Bitmap, bounds.X - union.X, bounds.Y - union.Y);
            }
        }

        return new CapturedFrame
        {
            Bitmap = canvas,
            Timestamp = DateTimeOffset.Now,
            MonitorIndex = -2,
            MonitorName = "WhitelistedApps",
            ScreenBounds = union
        };
    }

    private static List<Rectangle> CollectProcessWindowRectsAbsolute(HashSet<string> processNames) =>
        CollectProcessWindows(processNames).Select(w => w.Bounds).ToList();

    private static List<(IntPtr Hwnd, Rectangle Bounds)> CollectProcessWindows(HashSet<string> processNames)
    {
        var seen = new HashSet<IntPtr>();
        var windows = new List<(IntPtr, Rectangle)>();
        void TryAdd(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !seen.Add(hwnd))
                return;
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                return;
            // Empty titles are OK (Edge/Chromium often uses blank top-level hosts).
            if (!TryGetVisibleWindowBounds(hwnd, out var windowBounds))
                return;
            windows.Add((hwnd, windowBounds));
        }

        // Top-level enum (normal apps).
        NativeMethods.EnumWindows((hwnd, _) =>
        {
#pragma warning disable CA1806
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
#pragma warning restore CA1806
            if (pid == 0)
                return true;

            try
            {
                using var process = Process.GetProcessById(unchecked((int)pid));
                var name = ApplicationScopeEvaluator.NormalizeProcessName(process.ProcessName);
                if (processNames.Contains(name))
                    TryAdd(hwnd);
            }
            catch
            {
                // ignore inaccessible processes
            }

            return true;
        }, IntPtr.Zero);

        // Thread windows catch tool windows / hosts that EnumWindows alone can miss.
        foreach (var name in processNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var process in procs)
            {
                try
                {
                    TryAdd(process.MainWindowHandle);
                    foreach (ProcessThread thread in process.Threads)
                    {
                        NativeMethods.EnumThreadWindows(
                            (uint)thread.Id,
                            (hwnd, _) =>
                            {
                                TryAdd(hwnd);
                                return true;
                            },
                            IntPtr.Zero);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return windows;
    }

    /// <summary>
    /// Visible window bounds in virtual-screen coordinates. Prefers DWM extended frame
    /// (DPI-accurate) with GetWindowRect fallback.
    /// </summary>
    internal static bool TryGetVisibleWindowBounds(IntPtr hwnd, out Rectangle bounds)
    {
        bounds = default;
        if (hwnd == IntPtr.Zero)
            return false;

        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DwmwaExtendedFrameBounds,
                out var dwmRect,
                Marshal.SizeOf<NativeMethods.Rect>()) == 0)
        {
            bounds = Rectangle.FromLTRB(dwmRect.Left, dwmRect.Top, dwmRect.Right, dwmRect.Bottom);
            if (bounds.Width >= 16 && bounds.Height >= 16)
                return true;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width >= 16 && bounds.Height >= 16;
    }

    /// <summary>Intersection helper for tests (virtual-screen coords).</summary>
    public static Rectangle IntersectCapture(Rectangle windowBounds, Rectangle screenBounds) =>
        Rectangle.Intersect(windowBounds, screenBounds);

    private static CapturedFrame CaptureBounds(Rectangle bounds, int index, string name)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        return new CapturedFrame
        {
            Bitmap = bitmap,
            Timestamp = DateTimeOffset.Now,
            MonitorIndex = index,
            MonitorName = name,
            ScreenBounds = bounds
        };
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        public const int DwmwaExtendedFrameBounds = 9;

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            out Rect pvAttribute,
            int cbAttribute);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        public const int PwRenderFullContent = 2;
        public const uint RdwInvalidate = 0x0001;
        public const uint RdwErase = 0x0004;
        public const uint RdwAllChildren = 0x0080;
        public const uint RdwUpdateNow = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}

public sealed class MonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public Rectangle Bounds { get; init; }
}
