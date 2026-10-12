using System.Drawing;
using System.Drawing.Imaging;
using PayAttention.Capture;
using Xunit;

namespace PayAttention.Tests;

public class ImageHelpersCropTests
{
    [Fact]
    public void CropAbsoluteRegion_extracts_monitor_slice_from_virtual_desktop()
    {
        // Two side-by-side 40x30 "monitors" painted into an 80x30 canvas.
        var virtualBounds = new Rectangle(100, 200, 80, 30);
        var leftMonitor = new Rectangle(100, 200, 40, 30);
        var rightMonitor = new Rectangle(140, 200, 40, 30);

        using var desktop = new Bitmap(80, 30, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(desktop))
        {
            g.Clear(Color.Black);
            using var leftBrush = new SolidBrush(Color.FromArgb(255, 200, 10, 10));
            using var rightBrush = new SolidBrush(Color.FromArgb(255, 10, 200, 10));
            g.FillRectangle(leftBrush, 0, 0, 40, 30);
            g.FillRectangle(rightBrush, 40, 0, 40, 30);
        }

        using var left = ImageHelpers.CropAbsoluteRegion(desktop, virtualBounds, leftMonitor);
        using var right = ImageHelpers.CropAbsoluteRegion(desktop, virtualBounds, rightMonitor);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(40, left!.Width);
        Assert.Equal(30, left.Height);
        Assert.Equal(40, right!.Width);
        Assert.Equal(30, right.Height);

        Assert.Equal(Color.FromArgb(255, 200, 10, 10), left.GetPixel(20, 15));
        Assert.Equal(Color.FromArgb(255, 10, 200, 10), right.GetPixel(20, 15));
    }

    [Fact]
    public void CropAbsoluteRegion_returns_null_when_no_overlap()
    {
        using var desktop = new Bitmap(40, 30, PixelFormat.Format32bppArgb);
        var virtualBounds = new Rectangle(0, 0, 40, 30);
        var elsewhere = new Rectangle(1000, 1000, 40, 30);

        Assert.Null(ImageHelpers.CropAbsoluteRegion(desktop, virtualBounds, elsewhere));
    }

    [Fact]
    public void CropAbsoluteRegion_does_not_mutate_source()
    {
        using var desktop = new Bitmap(20, 20, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(desktop))
            g.Clear(Color.Blue);

        var bounds = new Rectangle(0, 0, 20, 20);
        using var crop = ImageHelpers.CropAbsoluteRegion(desktop, bounds, new Rectangle(0, 0, 10, 10));
        Assert.NotNull(crop);

        Assert.Equal(Color.Blue.ToArgb(), desktop.GetPixel(15, 15).ToArgb());
        Assert.Equal(20, desktop.Width);
    }
}
