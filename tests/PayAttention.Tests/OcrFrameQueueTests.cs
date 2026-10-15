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

        var fp1 = FrameFingerprint.ComputeAverageHash(frame1.Bitmap);
        var fp2 = FrameFingerprint.ComputeAverageHash(frame2.Bitmap);
        Assert.True(queue.TryEnqueueIfChanged(frame1, "PaWhiteAlpha", fp1));
        Assert.False(queue.TryEnqueueIfChanged(frame2, "PaWhiteAlpha", fp2));
        Assert.True(queue.TryEnqueueIfChanged(frame2, "PaWhiteBeta", fp2));
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

        var fp = FrameFingerprint.ComputeAverageHash(frame.Bitmap);
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp));
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", fp));
        queue.AllowResample();
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp));
    }

    [Fact]
    public void ScheduleResample_zero_delay_allows_unchanged_reenqueue()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var frame = MakeSolidFrame(Color.Navy);
        var fp = FrameFingerprint.ComputeAverageHash(frame.Bitmap);

        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp, out var first));
        Assert.Equal(OcrEnqueueDecision.Enqueued, first);
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", fp, out var skipped));
        Assert.Equal(OcrEnqueueDecision.SkippedUnchanged, skipped);

        queue.ScheduleResample(TimeSpan.Zero);
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp, out var forced));
        Assert.Equal(OcrEnqueueDecision.EnqueuedForcedResample, forced);
    }

    [Fact]
    public void ScheduleResample_keeps_earliest_deadline()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var frame = MakeSolidFrame(Color.Teal);
        var fp = FrameFingerprint.ComputeAverageHash(frame.Bitmap);

        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp));
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", fp));

        queue.ScheduleResample(TimeSpan.FromHours(1));
        queue.ScheduleResample(TimeSpan.Zero);
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp, out var forced));
        Assert.Equal(OcrEnqueueDecision.EnqueuedForcedResample, forced);
    }

    [Fact]
    public void Unchanged_frame_stays_skipped_until_deadline()
    {
        using var queue = new OcrFrameQueue(capacity: 4);
        using var frame = MakeSolidFrame(Color.Maroon);
        var fp = FrameFingerprint.ComputeAverageHash(frame.Bitmap);

        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp));
        queue.ScheduleResample(TimeSpan.FromMinutes(5));
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", fp, out var skipped));
        Assert.Equal(OcrEnqueueDecision.SkippedUnchanged, skipped);
    }

    [Fact]
    public void Dropped_enqueue_does_not_stick_the_fingerprint_gate()
    {
        using var queue = new OcrFrameQueue(capacity: 1);
        using var frame = MakeSolidFrame(Color.Olive);
        var fp = FrameFingerprint.ComputeAverageHash(frame.Bitmap);
        queue.Complete();

        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", fp, out var first));
        Assert.Equal(OcrEnqueueDecision.Dropped, first);
        Assert.False(queue.TryEnqueueIfChanged(frame, "alpha", fp, out var second));
        Assert.Equal(OcrEnqueueDecision.Dropped, second);
    }

    [Fact]
    public async Task ReadAsync_returns_queued_frame_then_null_after_complete()
    {
        using var queue = new OcrFrameQueue(capacity: 1);
        using var frame = MakeSolidFrame(Color.Navy);
        var fp = FrameFingerprint.ComputeAverageHash(frame.Bitmap);
        Assert.True(queue.TryEnqueueIfChanged(frame, "alpha", fp));
        queue.Complete();

        var read = await queue.ReadAsync(CancellationToken.None);
        Assert.NotNull(read);
        read.Dispose();
        Assert.Null(await queue.ReadAsync(CancellationToken.None));
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
