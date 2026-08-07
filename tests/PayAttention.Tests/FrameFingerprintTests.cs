using System.Drawing;
using System.Drawing.Imaging;
using PayAttention.Capture;
using Xunit;

namespace PayAttention.Tests;

public class FrameFingerprintTests
{
    [Fact]
    public void Identical_frames_are_similar()
    {
        using var a = MakePattern(seed: 1);
        using var b = (Bitmap)a.Clone();
        var ha = FrameFingerprint.ComputeAverageHash(a);
        var hb = FrameFingerprint.ComputeAverageHash(b);
        Assert.Equal(ha, hb);
        Assert.True(FrameFingerprint.IsSimilar(ha, hb));
    }

    [Fact]
    public void Small_cursor_like_blob_still_similar()
    {
        using var baseFrame = MakePattern(seed: 42);
        using var withCursor = (Bitmap)baseFrame.Clone();
        using (var g = Graphics.FromImage(withCursor))
        using (var brush = new SolidBrush(Color.White))
            g.FillRectangle(brush, 180, 120, 10, 14);

        var ha = FrameFingerprint.ComputeAverageHash(baseFrame);
        var hb = FrameFingerprint.ComputeAverageHash(withCursor);
        Assert.True(
            FrameFingerprint.IsSimilar(ha, hb),
            $"distance={FrameFingerprint.HammingDistance(ha, hb)}");
    }

    [Fact]
    public void Heavily_different_pattern_is_not_similar()
    {
        using var a = MakePattern(seed: 1);
        using var b = MakePattern(seed: 99);
        // Flip contrast so averages diverge strongly.
        using (var g = Graphics.FromImage(b))
            g.Clear(Color.White);

        var ha = FrameFingerprint.ComputeAverageHash(a);
        var hb = FrameFingerprint.ComputeAverageHash(b);
        Assert.False(FrameFingerprint.IsSimilar(ha, hb, maxDistance: 10));
        Assert.True(FrameFingerprint.HammingDistance(ha, hb) > 10);
    }

    [Fact]
    public void Scrolled_content_is_not_similar()
    {
        using var a = new Bitmap(320, 240, PixelFormat.Format32bppArgb);
        using var b = new Bitmap(320, 240, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(a))
        {
            g.Clear(Color.Black);
            using var font = new Font("Consolas", 12f);
            using var brush = new SolidBrush(Color.Lime);
            for (var i = 0; i < 12; i++)
                g.DrawString($"line {i} PAYATTN_ACCEPT_SHOT", font, brush, 8, 8 + i * 18);
        }

        using (var g = Graphics.FromImage(b))
        {
            g.Clear(Color.Black);
            using var font = new Font("Consolas", 12f);
            using var brush = new SolidBrush(Color.Lime);
            for (var i = 0; i < 12; i++)
                g.DrawString($"line {i + 40} other noise {Guid.NewGuid():N}", font, brush, 8, 8 + i * 18);
        }

        var ha = FrameFingerprint.ComputeAverageHash(a);
        var hb = FrameFingerprint.ComputeAverageHash(b);
        Assert.False(
            FrameFingerprint.IsSimilar(ha, hb),
            $"distance={FrameFingerprint.HammingDistance(ha, hb)}");
    }

    private static Bitmap MakePattern(int seed)
    {
        var bmp = new Bitmap(320, 240, PixelFormat.Format32bppArgb);
        var rng = new Random(seed);
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var v = (byte)((x * 3 + y * 5 + rng.Next(20)) % 180);
            bmp.SetPixel(x, y, Color.FromArgb(255, v, (byte)(v / 2), 20));
        }

        return bmp;
    }
}

public class TriggerFireGateTests
{
    [Fact]
    public void Same_scene_blocked_even_after_debounce()
    {
        var gate = new TriggerFireGate();
        const string key = "t1:TOKEN";
        Assert.True(gate.ShouldFire(key, TimeSpan.FromMilliseconds(1), fingerprint: 0xAAAAUL));
        Thread.Sleep(5);
        Assert.False(gate.ShouldFire(key, TimeSpan.FromMilliseconds(1), fingerprint: 0xAAAAUL));
    }

    [Fact]
    public void Different_scene_allowed_after_debounce()
    {
        var gate = new TriggerFireGate();
        const string key = "t1:TOKEN";
        Assert.True(gate.ShouldFire(key, TimeSpan.FromMilliseconds(1), fingerprint: 0x0UL));
        Thread.Sleep(5);
        // Flip many bits so Hamming distance exceeds default threshold.
        Assert.True(gate.ShouldFire(key, TimeSpan.FromMilliseconds(1), fingerprint: ulong.MaxValue));
    }

    [Fact]
    public void Debounce_blocks_before_fingerprint_check()
    {
        var gate = new TriggerFireGate();
        const string key = "t1:TOKEN";
        Assert.True(gate.ShouldFire(key, TimeSpan.FromSeconds(30), fingerprint: 0x1UL));
        Assert.False(gate.ShouldFire(key, TimeSpan.FromSeconds(30), fingerprint: ulong.MaxValue));
    }

    [Fact]
    public void Reset_allows_immediate_refire()
    {
        var gate = new TriggerFireGate();
        const string key = "t1:TOKEN";
        Assert.True(gate.ShouldFire(key, TimeSpan.FromSeconds(30), fingerprint: 0x1UL));
        Assert.False(gate.ShouldFire(key, TimeSpan.FromSeconds(30), fingerprint: 0x1UL));
        gate.Reset();
        Assert.True(gate.ShouldFire(key, TimeSpan.FromSeconds(30), fingerprint: 0x1UL));
    }
}
