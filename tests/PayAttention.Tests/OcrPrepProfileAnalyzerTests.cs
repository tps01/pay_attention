using System.Drawing;
using PayAttention.Capture;
using Xunit;

namespace PayAttention.Tests;

public class OcrPrepProfileAnalyzerTests
{
    [Fact]
    public void Analyze_dark_neon_enables_neon_and_terminal()
    {
        using var bmp = MakeNeonTerminal(320, 200);
        var flags = OcrPrepProfileAnalyzer.AnalyzeBitmap(bmp);
        Assert.True(flags.Neon);
        Assert.True(flags.Terminal);
        Assert.False(flags.Adaptive);
        Assert.False(flags.Gray);
    }

    [Fact]
    public void Analyze_bright_ui_disables_all_auto_passes()
    {
        using var bmp = MakeSolid(320, 200, Color.FromArgb(245, 245, 248));
        var flags = OcrPrepProfileAnalyzer.AnalyzeBitmap(bmp);
        Assert.False(flags.Adaptive);
        Assert.False(flags.Neon);
        Assert.False(flags.Gray);
        Assert.False(flags.Invert);
        Assert.False(flags.Terminal);
    }

    [Fact]
    public void Analyze_dark_low_chroma_enables_adaptive()
    {
        using var bmp = MakeDarkGrayPanel(320, 200);
        var flags = OcrPrepProfileAnalyzer.AnalyzeBitmap(bmp);
        Assert.True(flags.Adaptive);
        Assert.False(flags.Neon);
        Assert.False(flags.Gray);
    }

    [Fact]
    public void Analyze_very_dark_enables_invert()
    {
        using var bmp = MakeSolid(320, 200, Color.FromArgb(12, 12, 14));
        // Sprinkle a few mid-gray pixels so contrast isn't zero-only.
        using var g = Graphics.FromImage(bmp);
        g.FillRectangle(new SolidBrush(Color.FromArgb(40, 40, 42)), 20, 20, 80, 20);
        var flags = OcrPrepProfileAnalyzer.AnalyzeBitmap(bmp);
        Assert.True(flags.Invert);
        Assert.False(flags.Neon);
    }

    [Fact]
    public void Cache_reuses_decision_for_same_process_and_fingerprint()
    {
        var analyzer = new OcrPrepProfileAnalyzer();
        using var neon = MakeNeonTerminal(200, 120);
        using var bright = MakeSolid(200, 120, Color.White);
        var logs = 0;
        var fingerprint = 0xABCD_0000_1234_5678UL;
        var first = analyzer.GetOrAnalyze(neon, "wt", fingerprint, _ => logs++);
        var second = analyzer.GetOrAnalyze(bright, "wt", fingerprint, _ => logs++);
        Assert.True(first.Neon);
        Assert.True(second.Neon);
        Assert.Equal(1, logs);

        var third = analyzer.GetOrAnalyze(bright, "wt", fingerprint: 0xABCD_0000_FFFF_FFFFUL, _ => logs++);
        Assert.False(third.Neon);
        Assert.Equal(2, logs);
    }

    private static Bitmap MakeSolid(int w, int h, Color color)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    private static Bitmap MakeNeonTerminal(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        using var brush = new SolidBrush(Color.FromArgb(0, 220, 70));
        for (var y = 16; y < h - 16; y += 14)
            g.FillRectangle(brush, 12, y, w - 24, 3);
        return bmp;
    }

    private static Bitmap MakeDarkGrayPanel(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(28, 28, 30));
        using var brush = new SolidBrush(Color.FromArgb(70, 70, 72));
        g.FillRectangle(brush, 24, 40, w - 48, 24);
        g.FillRectangle(brush, 24, 80, w / 2, 16);
        return bmp;
    }
}
