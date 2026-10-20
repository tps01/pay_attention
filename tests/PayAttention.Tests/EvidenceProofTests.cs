using System.Drawing;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using PayAttention.Harness;
using Xunit;

namespace PayAttention.Tests;

public sealed class EvidenceProofTests
{
    [Theory]
    [InlineData(OcrSpeedMode.Fast)]
    [InlineData(OcrSpeedMode.Thorough)]
    public void Punctuation_contains_does_not_match_an_empty_folded_needle(OcrSpeedMode mode)
    {
        var trigger = new TriggerConfig { Text = "!!!", Match = MatchMode.Contains, OcrMode = mode, Enabled = true };
        Assert.Empty(TriggerMatcher.FindMatches("unrelated words", [trigger]));
        Assert.Single(TriggerMatcher.FindMatches("failure!!!", [trigger]));
    }

    [Fact]
    public void Fast_similarity_agrees_with_an_unpruned_distance_search()
    {
        static int Distance(string a, string b)
        {
            var costs = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) costs[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) costs[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                    costs[i, j] = Math.Min(costs[i - 1, j] + 1, Math.Min(costs[i, j - 1] + 1,
                        costs[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
            return costs[a.Length, b.Length];
        }
        var random = new Random(9381);
        for (var trial = 0; trial < 100; trial++)
        {
            string Text(int length) => new(Enumerable.Range(0, length).Select(_ => "abcdef"[random.Next(6)]).ToArray());
            var needle = Text(random.Next(6, 11));
            var haystack = Text(random.Next(6, 24));
            var cap = (int)Math.Ceiling(needle.Length * 0.5);
            var expected = 0;
            for (var length = needle.Length - cap; length <= needle.Length + cap; length++)
                for (var start = 0; start + length <= haystack.Length; start++)
                {
                    var distance = Distance(haystack.Substring(start, length), needle);
                    if (distance <= cap) expected = Math.Max(expected, (int)Math.Round(100.0 * (1.0 - (double)distance / Math.Max(length, needle.Length))));
                }
            Assert.Equal(expected, TriggerMatcher.BestCompactSimilarityPercent(haystack, needle));
        }
    }

    [Fact]
    public void Proof_rejects_empty_and_corrupt_media_and_requires_a_completed_receipt()
    {
        using var dir = new AuditDirectory();
        using var bitmap = new Bitmap(32, 32);
        var path = CaptureWriter.WriteScreenshot(bitmap, new CaptureSidecar { TriggerId = Guid.NewGuid() }, dir.Root);
        Assert.True(MediaProof.Ready(path));
        File.WriteAllBytes(path, []);
        Assert.False(MediaProof.Ready(path));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
        File.WriteAllText(Path.ChangeExtension(path, ".pending"), "");
        Assert.False(MediaProof.Ready(path));
        File.Delete(Path.ChangeExtension(path, ".pending"));
        File.Delete(Path.ChangeExtension(path, ".capture.json"));
        Assert.False(MediaProof.Ready(path));
    }

    [Fact]
    public void Proof_decodes_avi_frames_and_rejects_a_truncated_container()
    {
        using var dir = new AuditDirectory();
        using var bitmap = new Bitmap(32, 32);
        var frames = new[] { new BufferedFrame { Timestamp = DateTimeOffset.UtcNow, Width = 32, Height = 32, JpegBytes = ImageHelpers.EncodeJpeg(bitmap, 85) } };
        var path = CaptureWriter.WriteRecording(frames, bitmap, new CaptureSidecar { TriggerId = Guid.NewGuid() }, dir.Root, 3, RecordFormat.Avi);
        Assert.True(MediaProof.Ready(path));
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^10]);
        Assert.False(MediaProof.Ready(path));
    }

    [Fact]
    public void Calibration_preserves_the_latest_unsaved_user_settings()
    {
        var latest = new AppConfig
        {
            CaptureDirectory = "unsaved",
            DebounceSeconds = 17,
            StartWithWindows = true,
            Triggers = [new() { Text = "edited during benchmark" }],
            Applications = [new() { ProcessName = "new-app" }]
        };
        var calibrated = new AppConfig { OcrIntervalMs = 777, LastCalibrationSummary = "trial" };
        PerformanceProfiles.MergeCalibrationSettings(latest, calibrated);
        Assert.Equal(777, latest.OcrIntervalMs);
        Assert.Equal("trial", latest.LastCalibrationSummary);
        Assert.Equal("unsaved", latest.CaptureDirectory);
        Assert.Equal(17, latest.DebounceSeconds);
        Assert.True(latest.StartWithWindows);
        Assert.Equal("edited during benchmark", Assert.Single(latest.Triggers).Text);
        Assert.Equal("new-app", Assert.Single(latest.Applications).ProcessName);
    }
}
