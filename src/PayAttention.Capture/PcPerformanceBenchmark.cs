using System.Diagnostics;
using System.Drawing;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

/// <summary>
/// Times VeryLow→Ultra OCR profiles against captured bitmaps (real GDI + Windows OCR).
/// Scores one cascade per frame (max of plain vs neon), matching a single capture interval.
/// </summary>
public sealed class PcPerformanceBenchmark
{
    public const int TrialsPerProfile = 3;

    private readonly OcrService _ocr = new();

    public bool OcrAvailable => _ocr.IsAvailable;

    /// <summary>
    /// Warm up, then run <see cref="TrialsPerProfile"/> trials for every rung (lean → rich).
    /// Median of per-trial max(plainCascadeMs, neonCascadeMs).
    /// </summary>
    public async Task<IReadOnlyList<PerformanceProfiles.ProfileTrialResult>> RunAsync(
        Bitmap plainFrame,
        Bitmap neonFrame,
        IProgress<string>? progress,
        CancellationToken token)
    {
        progress?.Report("Warming up OCR…");
        await WarmUpAsync(plainFrame, token).ConfigureAwait(false);

        var results = new List<PerformanceProfiles.ProfileTrialResult>(PerformanceProfiles.All.Count);
        foreach (var def in PerformanceProfiles.All)
        {
            token.ThrowIfCancellationRequested();
            var label = PerformanceProfiles.DisplayName(def.Kind);
            progress?.Report($"Timing {label}…");

            var times = new List<double>(TrialsPerProfile);
            var plainRead = false;
            var neonRead = false;
            for (var trial = 0; trial < TrialsPerProfile; trial++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"Timing {label} (trial {trial + 1}/{TrialsPerProfile})…");

                var scored = await TimeProfileOnceAsync(def, plainFrame, neonFrame, token)
                    .ConfigureAwait(false);
                times.Add(scored.ScoreMs);

                if (ContainsToken(scored.PlainText, PerformanceProfiles.PlainToken))
                    plainRead = true;
                if (ContainsToken(scored.NeonText, PerformanceProfiles.NeonToken))
                    neonRead = true;
            }

            times.Sort();
            var median = times.Count switch
            {
                0 => 0,
                1 => times[0],
                2 => (times[0] + times[1]) / 2.0,
                _ => times[times.Count / 2]
            };
            results.Add(new PerformanceProfiles.ProfileTrialResult(def.Kind, median, plainRead, neonRead));
        }

        return results;
    }

    /// <summary>One cheap baseline OCR so the first timed trial is not cold-start dominated.</summary>
    private async Task WarmUpAsync(Bitmap plainFrame, CancellationToken token)
    {
        var config = new AppConfig();
        PerformanceProfiles.Apply(config, PerformanceProfileKind.VeryLow);
        _ = await RecognizeLikeCascadeAsync(plainFrame, config, token).ConfigureAwait(false);
    }

    private async Task<(string PlainText, string NeonText, double ScoreMs)> TimeProfileOnceAsync(
        PerformanceProfiles.ProfileDefinition def,
        Bitmap plainFrame,
        Bitmap neonFrame,
        CancellationToken token)
    {
        var config = new AppConfig();
        PerformanceProfiles.Apply(config, def);

        var plainSw = Stopwatch.StartNew();
        var plainText = await RecognizeLikeCascadeAsync(plainFrame, config, token).ConfigureAwait(false);
        plainSw.Stop();

        var neonSw = Stopwatch.StartNew();
        var neonText = await RecognizeLikeCascadeAsync(neonFrame, config, token).ConfigureAwait(false);
        neonSw.Stop();

        var scoreMs = Math.Max(plainSw.Elapsed.TotalMilliseconds, neonSw.Elapsed.TotalMilliseconds);
        return (plainText, neonText, scoreMs);
    }

    private async Task<string> RecognizeLikeCascadeAsync(
        Bitmap bitmap,
        AppConfig config,
        CancellationToken token)
    {
        var chunks = new List<string>(8);
        var boost = Math.Max(
            config.OcrUpscalePercent,
            PerformanceProfiles.ScaledUpscale(config.OcrBoostUpscalePercent, 200));

        await AddPassAsync(chunks, bitmap, config, config.OcrUpscalePercent, invert: false, OcrPrepMode.None, token)
            .ConfigureAwait(false);

        if (config.OcrEnableAdaptive)
        {
            await AddPassAsync(chunks, bitmap, config, boost, invert: true, OcrPrepMode.AdaptiveContrast, token)
                .ConfigureAwait(false);
        }

        if (config.OcrEnableNeon)
        {
            await AddPassAsync(chunks, bitmap, config, boost, invert: false, OcrPrepMode.NeonHighContrast, token)
                .ConfigureAwait(false);
        }

        if (config.OcrEnableTerminal && config.OcrTerminalMaxRegions > 0)
        {
            var regions = ImageHelpers.FindTerminalLikeRegions(bitmap, maxRegions: config.OcrTerminalMaxRegions);
            var scale = Math.Max(
                config.OcrUpscalePercent,
                PerformanceProfiles.ScaledUpscale(config.OcrBoostUpscalePercent, 300));
            foreach (var region in regions)
            {
                token.ThrowIfCancellationRequested();
                using var crop = bitmap.Clone(region, bitmap.PixelFormat);
                await AddPassAsync(chunks, crop, config, scale, invert: true, OcrPrepMode.NeonHighContrast, token)
                    .ConfigureAwait(false);
            }
        }

        return string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private async Task AddPassAsync(
        List<string> chunks,
        Bitmap bitmap,
        AppConfig config,
        int upscalePercent,
        bool invert,
        OcrPrepMode prepMode,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Bitmap? prepBmp = null;
        try
        {
            prepBmp = ImageHelpers.ApplyOcrPrep(bitmap, prepMode);
            var source = prepBmp ?? bitmap;
            using var prepared = ImageHelpers.PrepareForOcr(
                source,
                config.OcrMaxWidth,
                upscalePercent,
                invert,
                config.OcrAbsoluteMaxWidth);
            var text = await _ocr.RecognizeAsync(prepared, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                chunks.Add(text);
        }
        finally
        {
            prepBmp?.Dispose();
        }
    }

    private static bool ContainsToken(string text, string token) =>
        !string.IsNullOrEmpty(text) &&
        text.Contains(token, StringComparison.OrdinalIgnoreCase);
}
