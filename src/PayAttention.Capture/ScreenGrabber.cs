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

public sealed class ScreenGrabber
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
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

    public CapturedFrame CapturePrimary()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen
            ?? System.Windows.Forms.Screen.AllScreens[0];
        return CaptureBounds(screen.Bounds, 0, screen.DeviceName);
    }

    public CapturedFrame CaptureMonitor(int index)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (index < 0 || index >= screens.Length)
            index = 0;
        var screen = screens[index];
        return CaptureBounds(screen.Bounds, index, screen.DeviceName);
    }

    public CapturedFrame CaptureVirtualDesktop()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        return CaptureBounds(bounds, -1, "AllMonitors");
    }

    /// <summary>
    /// Captures the foreground window. Returns null if unavailable / minimized / invalid.
    /// </summary>
    public CapturedFrame? TryCaptureForegroundWindow()
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

    public string? GetForegroundProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
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

    public IReadOnlyList<string> GetRunningProcessNames()
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
    /// Fills black over top-level windows belonging to the given process names,
    /// relative to the capture's screen coordinates.
    /// </summary>
    public void MaskProcessWindows(Bitmap bitmap, Rectangle screenBounds, IEnumerable<string> processNames)
    {
        var targets = processNames
            .Select(ApplicationScopeEvaluator.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0 || bitmap.Width < 1 || bitmap.Height < 1)
            return;

        var rects = new List<Rectangle>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return true;

            string? name = null;
            try
            {
                using var process = Process.GetProcessById(unchecked((int)pid));
                name = ApplicationScopeEvaluator.NormalizeProcessName(process.ProcessName);
            }
            catch
            {
                return true;
            }

            if (name is null || !targets.Contains(name))
                return true;

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                return true;

            var windowBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var overlap = Rectangle.Intersect(windowBounds, screenBounds);
            if (overlap.Width < 1 || overlap.Height < 1)
                return true;

            rects.Add(new Rectangle(
                overlap.X - screenBounds.X,
                overlap.Y - screenBounds.Y,
                overlap.Width,
                overlap.Height));
            return true;
        }, IntPtr.Zero);

        if (rects.Count == 0)
            return;

        using var g = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(Color.Black);
        foreach (var r in rects)
            g.FillRectangle(brush, r);
    }

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
