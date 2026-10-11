using System.Drawing;
using PayAttention.Capture;
using Xunit;

namespace PayAttention.Tests;

public sealed class BlacklistMaskGeometryTests
{
    [Fact]
    public void IntersectCapture_handles_negative_virtual_desktop_coords()
    {
        // C43J89x-style ultrawide at (-3840,0)
        var screen = new Rectangle(-3840, 0, 3840, 1200);
        var edgeWindow = new Rectangle(-3760, 60, 1700, 1000);

        var overlap = ScreenGrabber.IntersectCapture(edgeWindow, screen);
        Assert.Equal(new Rectangle(-3760, 60, 1700, 1000), overlap);

        // Relative to capture bitmap origin:
        var relative = new Rectangle(
            overlap.X - screen.X,
            overlap.Y - screen.Y,
            overlap.Width,
            overlap.Height);
        Assert.Equal(new Rectangle(80, 60, 1700, 1000), relative);
    }

    [Fact]
    public void IntersectCapture_empty_when_window_on_other_monitor()
    {
        var ultrawide = new Rectangle(-3840, 0, 3840, 1200);
        var primaryWindow = new Rectangle(100, 100, 800, 600);
        var overlap = ScreenGrabber.IntersectCapture(primaryWindow, ultrawide);
        Assert.True(overlap.Width < 1 || overlap.Height < 1);
    }
}
