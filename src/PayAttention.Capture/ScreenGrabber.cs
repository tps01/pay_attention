using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PayAttention.Capture;

public sealed class CapturedFrame : IDisposable
{
    public required Bitmap Bitmap { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int MonitorIndex { get; init; }
    public string MonitorName { get; init; } = string.Empty;

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
            MonitorName = name
        };
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

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
