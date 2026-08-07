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
            var writer = new CaptureWriter();
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

            var folder = writer.WriteScreenshotSequence(
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

            writer.WriteSequenceStill(
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
        var folder = Path.Combine(root, "20260101_120000_000_test_sequence");
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
