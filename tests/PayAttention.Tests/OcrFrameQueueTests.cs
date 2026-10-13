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

    [Fact]
    public void ScheduleResample_zero_delay_allows_unchanged_reenqueue()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var frame = MakeSolidFrame(Color.Navy);

        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", out var first));
        Assert.Equal(OcrEnqueueDecision.Enqueued, first);
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", out var skipped));
        Assert.Equal(OcrEnqueueDecision.SkippedUnchanged, skipped);

        queue.ScheduleResample(TimeSpan.Zero);
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", out var forced));
        Assert.Equal(OcrEnqueueDecision.EnqueuedForcedResample, forced);
    }

    [Fact]
    public void ScheduleResample_keeps_earliest_deadline()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var frame = MakeSolidFrame(Color.Teal);

        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha"));
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha"));

        queue.ScheduleResample(TimeSpan.FromHours(1));
        queue.ScheduleResample(TimeSpan.Zero);
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", out var forced));
        Assert.Equal(OcrEnqueueDecision.EnqueuedForcedResample, forced);
    }

    [Fact]
    public void Unchanged_frame_stays_skipped_until_deadline()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var frame = MakeSolidFrame(Color.Maroon);

        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha"));
        queue.ScheduleResample(TimeSpan.FromMinutes(5));
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", out var skipped));
        Assert.Equal(OcrEnqueueDecision.SkippedUnchanged, skipped);
    }

    private static CapturedFrame MakeSolidFrame(Color color)
    {
        var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(color);
        return new CapturedFrame
        {
            Bitmap = bitmap,
            Timestamp = DateTimeOffset.UtcNow,
            MonitorIndex = 0,
            MonitorName = "Primary",
            ScreenBounds = new Rectangle(0, 0, 32, 32)
        };
    }
}
