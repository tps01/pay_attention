using System.Drawing;
using System.Drawing.Imaging;
using PayAttention.Capture;
using Xunit;

namespace PayAttention.Tests;

public class ImageHelpersOcrPrepTests
{
    [Fact]
    public void ToGrayscale_makes_channels_equal_and_does_not_mutate_source()
    {
        using var source = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(source))
            g.Clear(Color.FromArgb(255, 40, 180, 60));

        var before = source.GetPixel(8, 8);
        using var gray = ImageHelpers.ToGrayscale(source);
        var afterSource = source.GetPixel(8, 8);
        Assert.Equal(before, afterSource);

        var p = gray.GetPixel(8, 8);
        Assert.Equal(p.R, p.G);
        Assert.Equal(p.G, p.B);
        var expected = (int)(0.299 * 40 + 0.587 * 180 + 0.114 * 60);
        Assert.InRange(p.R, expected - 1, expected + 1);
    }

    [Fact]
    public void StretchContrast_expands_low_dynamic_range()
    {
        using var source = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            // Narrow band: 80..120
            var v = 80 + (x + y) % 41;
            source.SetPixel(x, y, Color.FromArgb(255, v, v, v));
        }

        var (_, _, meanBefore) = ImageHelpers.SampleLumaStats(source);
        using var stretched = ImageHelpers.StretchContrast(source);
        var (low, high, _) = ImageHelpers.SampleLumaStats(stretched);

        Assert.True(high - low > 150, $"expected wide span after stretch, got {low}..{high}");
        Assert.InRange(meanBefore, 70, 130);
    }

    [Fact]
    public void StretchContrast_preserves_ordering_on_ramp()
    {
        using var source = new Bitmap(64, 8, PixelFormat.Format32bppArgb);
        for (var x = 0; x < 64; x++)
        {
            var v = 40 + x * 2; // 40..166
            for (var y = 0; y < 8; y++)
                source.SetPixel(x, y, Color.FromArgb(255, v, v, v));
        }

        using var stretched = ImageHelpers.StretchContrast(source);
        var prev = -1;
        for (var x = 0; x < 64; x++)
        {
            var lum = stretched.GetPixel(x, 4).R;
            Assert.True(lum >= prev, $"non-monotonic at x={x}: {prev} -> {lum}");
            prev = lum;
        }

        Assert.True(stretched.GetPixel(0, 4).R < 40);
        Assert.True(stretched.GetPixel(63, 4).R > 200);
    }

    [Fact]
    public void ToAdaptiveContrastGray_pipeline_does_not_mutate_source()
    {
        using var source = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(source))
            g.Clear(Color.FromArgb(255, 10, 90, 20));
        var before = source.GetPixel(1, 1);

        using var prepared = ImageHelpers.ToAdaptiveContrastGray(source);
        Assert.Equal(before, source.GetPixel(1, 1));
        Assert.Equal(prepared.GetPixel(1, 1).R, prepared.GetPixel(1, 1).G);
    }

    [Theory]
    [InlineData(1920, 1080, 2)]
    [InlineData(2560, 1440, 3)]
    [InlineData(3840, 2160, 3)]
    [InlineData(1280, 720, 2)]
    public void ChooseOcrTileGrid_uses_3x3_on_large_frames(int w, int h, int expected) =>
        Assert.Equal(expected, ImageHelpers.ChooseOcrTileGrid(w, h));

    [Fact]
    public void ApplyOcrPrep_none_returns_null()
    {
        using var source = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        Assert.Null(ImageHelpers.ApplyOcrPrep(source, OcrPrepMode.None));
    }

    [Fact]
    public void PrepareForOcr_absolute_max_width_caps_after_upscale()
    {
        using var source = new Bitmap(1000, 500, PixelFormat.Format32bppArgb);
        using var prepared = ImageHelpers.PrepareForOcr(
            source,
            maxWidth: 1920,
            upscalePercent: 200,
            invertColors: false,
            absoluteMaxWidth: 1600);

        Assert.True(prepared.Width <= 1600, $"expected width <= 1600, got {prepared.Width}");
    }
}

