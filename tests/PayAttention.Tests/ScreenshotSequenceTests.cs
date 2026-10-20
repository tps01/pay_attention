using System.Drawing;
using PayAttention.Capture;
using PayAttention.Core.Models;
using Xunit;

namespace PayAttention.Tests;

public sealed class ScreenshotSequenceTests
{
    [Fact]
    public void WriteScreenshotSequence_creates_subfolder_with_trigger_and_pre_frames()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_seq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var triggerTime = DateTimeOffset.Now;
            var sidecar = new CaptureSidecar
            {
                Timestamp = triggerTime,
                TriggerText = "err#42v2",
                MatchedText = "err#42v2",
                MatchMode = MatchMode.Contains,
                Action = CaptureAction.Sequence
            };

            using var triggerBmp = new Bitmap(64, 48);
            using (var g = Graphics.FromImage(triggerBmp))
                g.Clear(Color.DarkGreen);

            var pre = new List<BufferedFrame>
            {
                MakeFrame(triggerBmp, triggerTime.AddSeconds(-6)),
                MakeFrame(triggerBmp, triggerTime.AddSeconds(-3))
            };

            var folder = CaptureWriter.WriteScreenshotSequence(
                pre,
                triggerBmp,
                sidecar,
                root,
                ScreenshotFormat.Jpeg,
                jpegQuality: 70,
                saveJson: true);

            Assert.True(Directory.Exists(folder));
            Assert.EndsWith("_sequence", Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("err42v2", Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase);

            var files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(f => f).ToArray();
            Assert.Contains("t-006.jpg", files);
            Assert.Contains("t-003.jpg", files);
            Assert.Contains("t+000.jpg", files);
            Assert.Contains("sidecar.json", files);

            CaptureWriter.WriteSequenceStill(
                folder,
                triggerTime,
                triggerTime.AddSeconds(3),
                triggerBmp,
                ScreenshotFormat.Jpeg,
                70);
            Assert.True(File.Exists(Path.Combine(folder, "t+003.jpg")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Retention_deletes_old_sequence_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_ret_" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, PayAttention.Core.Services.CaptureOwnership.CreateName(DateTimeOffset.Now, "test", "sequence", Guid.NewGuid()));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "t+000.jpg"), "x");

        try
        {
            // Age the folder so max-age retention removes it.
            Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddDays(-10));
            var deleted = PayAttention.Core.Services.RetentionService.Enforce(root, maxAgeDays: 1, maxFiles: 0);
            Assert.True(deleted >= 1);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Retention_counts_record_still_and_video_as_one_item()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_ret_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var newer = Path.Combine(root, PayAttention.Core.Services.CaptureOwnership.CreateName(DateTimeOffset.Now, "err", "record", Guid.NewGuid()));
        var older = Path.Combine(root, PayAttention.Core.Services.CaptureOwnership.CreateName(DateTimeOffset.Now, "other", "screenshot", Guid.NewGuid()));
        File.WriteAllText(newer + ".jpg", "still");
        File.WriteAllText(newer + ".avi", "video");
        File.WriteAllText(newer + ".json", "{}");
        File.WriteAllText(older + ".jpg", "shot");
        var now = DateTime.UtcNow;
        File.SetCreationTimeUtc(newer + ".jpg", now);
        File.SetCreationTimeUtc(newer + ".avi", now);
        File.SetCreationTimeUtc(newer + ".json", now);
        File.SetCreationTimeUtc(older + ".jpg", now.AddMinutes(-5));

        try
        {
            var deleted = PayAttention.Core.Services.RetentionService.Enforce(root, maxAgeDays: 0, maxFiles: 1);
            Assert.Equal(1, deleted);
            Assert.True(File.Exists(newer + ".jpg"));
            Assert.True(File.Exists(newer + ".avi"));
            Assert.True(File.Exists(newer + ".json"));
            Assert.False(File.Exists(older + ".jpg"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PlaybackFps_follows_actual_frame_spacing()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var frames = Enumerable.Range(0, 30)
            .Select(i => new BufferedFrame
            {
                JpegBytes = [1],
                Timestamp = start.AddMilliseconds(500 * i),
                Width = 8,
                Height = 8
            })
            .ToList();

        Assert.Equal(2, CaptureWriter.PlaybackFps(frames, requestedFps: 3));
    }

    [Fact]
    public void Mjpeg_riff_size_matches_the_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_avi_" + Guid.NewGuid().ToString("N") + ".avi");
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(Color.Red);
        var frame = new BufferedFrame
        {
            JpegBytes = ImageHelpers.EncodeJpeg(bmp, 70),
            Timestamp = DateTimeOffset.UtcNow,
            Width = bmp.Width,
            Height = bmp.Height
        };

        try
        {
            MjpegAviWriter.Write(root, [frame], fps: 3);
            var bytes = File.ReadAllBytes(root);
            var riffSize = BitConverter.ToInt32(bytes, 4);
            Assert.Equal(bytes.Length - 8, riffSize);
        }
        finally
        {
            if (File.Exists(root))
                File.Delete(root);
        }
    }

    private static BufferedFrame MakeFrame(Bitmap bmp, DateTimeOffset timestamp)
    {
        var jpeg = ImageHelpers.EncodeJpeg(bmp, 70);
        return new BufferedFrame
        {
            JpegBytes = jpeg,
            Timestamp = timestamp,
            Width = bmp.Width,
            Height = bmp.Height
        };
    }
}
