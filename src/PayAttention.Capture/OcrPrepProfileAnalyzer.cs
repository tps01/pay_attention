using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

/// <summary>
/// Samples a frame’s color profile to decide which OCR prep passes Auto should enable.
/// Results are cached per process and full frame fingerprint so a later, different
/// frame in the same app does not reuse a neon or terminal decision.
/// </summary>
public sealed class OcrPrepProfileAnalyzer
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Analyze without cache (tests / forced refresh).</summary>
    public static OcrPrepFlags AnalyzeBitmap(Bitmap source) =>
        Decide(SampleProfile(source));

    public OcrPrepFlags GetOrAnalyze(
        Bitmap source,
        string? processName,
        ulong fingerprint,
        Action<string>? logDebug = null)
    {
        var key = ApplicationScopeEvaluator.NormalizeProcessName(processName);
        if (key.Length == 0)
            key = "(unknown)";

        if (_cache.TryGetValue(key, out var hit) &&
            hit.Fingerprint == fingerprint &&
            DateTimeOffset.UtcNow - hit.At < CacheTtl)
        {
            return hit.Flags;
        }

        var flags = AnalyzeBitmap(source);
        _cache[key] = new CacheEntry(flags, fingerprint, DateTimeOffset.UtcNow);
        logDebug?.Invoke(
            $"OCR prep Auto for {key}: adaptive={flags.Adaptive} neon={flags.Neon} " +
            $"gray={flags.Gray} invert={flags.Invert} terminal={flags.Terminal}");
        return flags;
    }

    public void ClearCache() => _cache.Clear();

    /// <summary>Exposed for unit tests.</summary>
    public static ColorProfile SampleProfile(Bitmap source)
    {
        var (low, high, mean) = ImageHelpers.SampleLumaStats(source);
        var src = EnsureArgb32(source, out var ownsSrc);
        try
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var row = new byte[Math.Abs(stride)];
                long samples = 0;
                long dark = 0;
                long neonHot = 0;
                long chromaSum = 0;

                // Subsample for speed (same spirit as FindTerminalLikeRegions).
                for (var y = 0; y < src.Height; y += 4)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        data.Scan0 + y * stride, row, 0, src.Width * 4);
                    for (var x = 0; x < src.Width; x += 4)
                    {
                        var i = x * 4;
                        var b = row[i];
                        var g = row[i + 1];
                        var r = row[i + 2];
                        var lum = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                        samples++;
                        if (lum < 55)
                            dark++;

                        var peak = Math.Max(r, Math.Max(g, b));
                        var minC = Math.Min(r, Math.Min(g, b));
                        chromaSum += peak - minC;

                        var brightFg =
                            (g > 85 && g >= r + 18 && g >= b + 10) ||
                            (r > 120 && g > 80 && b < 70 && r >= g) ||
                            (b > 100 && g > 90 && b >= r + 15) ||
                            (r > 160 && g > 160 && b > 160 && Math.Min(r, Math.Min(g, b)) > 140);
                        if (brightFg && lum < 200)
                            neonHot++;
                    }
                }

                var darkFraction = samples == 0 ? 0 : dark / (double)samples;
                var neonFraction = samples == 0 ? 0 : neonHot / (double)samples;
                var meanChroma = samples == 0 ? 0 : chromaSum / (double)samples;
                return new ColorProfile(mean, high - low, darkFraction, neonFraction, meanChroma);
            }
            finally
            {
                src.UnlockBits(data);
            }
        }
        finally
        {
            if (ownsSrc)
                src.Dispose();
        }
    }

    /// <summary>Decision matrix (tuned against synthetic bitmaps in tests).</summary>
    public static OcrPrepFlags Decide(ColorProfile p)
    {
        // Bright UI / documents: baseline only.
        if (p.MeanLuma >= 140 || (p.DarkFraction < 0.25 && p.NeonFraction < 0.01))
        {
            return new OcrPrepFlags(
                Adaptive: false,
                Neon: false,
                Gray: false,
                Invert: false,
                Terminal: false);
        }

        var neon = p.NeonFraction >= 0.008 && p.DarkFraction >= 0.35;
        var terminal = neon || (p.NeonFraction >= 0.004 && p.DarkFraction >= 0.45);
        var adaptive = !neon && p.DarkFraction >= 0.40 && p.ContrastSpread < 140;
        // Invert helps very dark, low-chroma panes; skip when neon already boosts channels.
        var invert = !neon && p.MeanLuma < 40 && p.DarkFraction >= 0.55;
        // Plain gray Auto rarely beats adaptive/neon in experiments — keep Off under Auto.
        var gray = false;

        return new OcrPrepFlags(adaptive, neon, gray, invert, terminal);
    }

    private static Bitmap EnsureArgb32(Bitmap source, out bool owns)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb)
        {
            owns = false;
            return source;
        }

        owns = true;
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        return copy;
    }

    public readonly record struct ColorProfile(
        double MeanLuma,
        int ContrastSpread,
        double DarkFraction,
        double NeonFraction,
        double MeanChroma);

    private sealed record CacheEntry(OcrPrepFlags Flags, ulong Fingerprint, DateTimeOffset At);
}
