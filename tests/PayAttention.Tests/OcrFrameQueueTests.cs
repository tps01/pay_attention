using System.Drawing;
using System.Drawing.Imaging;
using PayAttention.Capture;
using Xunit;

namespace PayAttention.Tests;

public sealed class OcrFrameQueueTests
{
    [Fact]
    public void Same_bitmap_different_foreground_enqueues()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(Color.DarkBlue);

        using var frame1 = new CapturedFrame
        {
            Bitmap = (Bitmap)bitmap.Clone(),
            Timestamp = DateTimeOffset.UtcNow,
            MonitorIndex = 0,
            MonitorName = "Primary",
            ScreenBounds = new Rectangle(0, 0, 64, 64)
        };
        using var frame2 = new CapturedFrame
        {
            Bitmap = (Bitmap)bitmap.Clone(),
            Timestamp = DateTimeOffset.UtcNow,
            MonitorIndex = 0,
            MonitorName = "Primary",
            ScreenBounds = new Rectangle(0, 0, 64, 64)
        };

        Assert.True(queue.TryEnqueueIfChanged(frame1, "PaWhiteAlpha"));
        Assert.False(queue.TryEnqueueIfChanged(frame2, "PaWhiteAlpha"));
        Assert.True(queue.TryEnqueueIfChanged(frame2, "PaWhiteBeta"));
    }

    [Fact]
    public void AllowResample_clears_foreground_gate()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(Color.Black);

        using var frame = new CapturedFrame
        {
            Bitmap = (Bitmap)bitmap.Clone(),
            Timestamp = DateTimeOffset.UtcNow,
            MonitorIndex = 0,
            MonitorName = "Primary",
            ScreenBounds = new Rectangle(0, 0, 32, 32)
        };

        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha"));
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha"));
        queue.AllowResample();
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha"));
    }
}
