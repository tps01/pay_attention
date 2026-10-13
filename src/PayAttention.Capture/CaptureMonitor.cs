using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

public sealed class MonitorStatusEventArgs : EventArgs
{
    public required string Status { get; init; }
    public string? Detail { get; init; }
}

public sealed class TriggerFiredEventArgs : EventArgs
{
    public required string TriggerText { get; init; }
    public required CaptureAction Action { get; init; }
    public required string OutputPath { get; init; }
}

public sealed class CaptureMonitor : IDisposable
{
    /// <summary>Fast unmatched + backlog: prefer fresher frames after a short first-tier try.</summary>
    private static readonly TimeSpan OcrCascadeBudgetBacklogFast = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Thorough unmatched + backlog: deep enough for ROI/tiles/bands, but still bail so
    /// scroll-era frames cannot monopolize the worker past a late inject.
    /// </summary>
    private static readonly TimeSpan OcrCascadeBudgetBacklogThorough = TimeSpan.FromMilliseconds(8000);

    private readonly ScreenGrabber _grabber = new();
    private readonly OcrService _ocr = new();
    private readonly CaptureWriter _writer = new();
    private readonly TriggerFireGate _fireGate = new();
    private readonly FrameRingBuffer _ringBuffer;
    private readonly FrameRingBuffer _sequenceRing;
    private readonly AppLogger _logger;
    private readonly object _configLock = new();

    private AppConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private OcrFrameQueue? _ocrQueue;
    private bool _paused;
    private bool _disposed;

    public CaptureMonitor(AppConfig config, AppLogger logger)
    {
        _config = CloneConfig(config);
        _logger = logger;
        _ringBuffer = new FrameRingBuffer(
            TimeSpan.FromSeconds(_config.RecordBufferSeconds),
            _config.RecordFps);
        _sequenceRing = new FrameRingBuffer(
            TimeSpan.FromSeconds(_config.SequenceBeforeSeconds),
            TimeSpan.FromSeconds(_config.SequenceIntervalSeconds));
    }

    public bool IsRunning => _loopTask is { IsCompleted: false };
    public bool IsPaused => _paused;
    public bool OcrAvailable => _ocr.IsAvailable;

    public event EventHandler<MonitorStatusEventArgs>? StatusChanged;
    public event EventHandler<TriggerFiredEventArgs>? TriggerFired;

    public void UpdateConfig(AppConfig config)
    {
        lock (_configLock)
        {
            _config = CloneConfig(config);
            _ringBuffer.Configure(
                TimeSpan.FromSeconds(_config.RecordBufferSeconds),
                _config.RecordFps);
            _sequenceRing.Configure(
                TimeSpan.FromSeconds(_config.SequenceBeforeSeconds),
                TimeSpan.FromSeconds(_config.SequenceIntervalSeconds));

            if (!_config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Record))
                _ringBuffer.Clear();
            if (!_config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Sequence))
                _sequenceRing.Clear();
        }
    }

    public void Start()
    {
        if (IsRunning)
            return;

        if (!_ocr.IsAvailable)
        {
            RaiseStatus("Error", "Windows OCR is unavailable on this system.");
            _logger.Error("Windows OCR engine could not be created.");
            return;
        }

        _paused = false;
        _cts = new CancellationTokenSource();
        _ocrQueue = new OcrFrameQueue();
        _loopTask = Task.Run(() => RunPipelineAsync(_cts.Token));
        RaiseStatus("Monitoring", "OCR monitoring started.");
        _logger.Info("Monitoring started.");
    }

    public void Stop()
    {
        if (_cts is null && _loopTask is null)
            return;

        _cts?.Cancel();
        _ocrQueue?.Complete();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // ignore cancellation
        }

        _ocrQueue?.Dispose();
        _ocrQueue = null;
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
        _ringBuffer.Clear();
        _sequenceRing.Clear();
        _fireGate.Reset();
        RaiseStatus("Stopped", "Monitoring stopped.");
        _logger.Info("Monitoring stopped.");
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        RaiseStatus(paused ? "Paused" : "Monitoring", paused ? "Monitoring paused." : "Monitoring resumed.");
        _logger.Info(paused ? "Monitoring paused." : "Monitoring resumed.");
    }

    public void TogglePause() => SetPaused(!_paused);

    private async Task RunPipelineAsync(CancellationToken token)
    {
        try
        {
            await Task.WhenAll(
                RunCaptureLoopAsync(token),
                RunOcrWorkerAsync(token)).ConfigureAwait(false);
        }
        finally
        {
            _ocrQueue?.Complete();
        }
    }

    /// <summary>
    /// High-frequency grab producer: capture on OcrIntervalMs period, enqueue clones for OCR.
    /// Never awaits OCR so short-lived scrolling lines still get sampled.
    /// </summary>
    private async Task RunCaptureLoopAsync(CancellationToken token)
    {
        var retentionCounter = 0;
        var queue = _ocrQueue ?? throw new InvalidOperationException("OCR queue not initialized.");

        while (!token.IsCancellationRequested)
        {
            AppConfig config;
            lock (_configLock)
                config = _config;

            var interval = TimeSpan.FromMilliseconds(Math.Max(1, config.OcrIntervalMs));
            var cycle = Stopwatch.StartNew();

            try
            {
                if (_paused)
                {
                    await DelayCapturePeriodAsync(interval, cycle, token).ConfigureAwait(false);
                    continue;
                }

                var foregroundProcess = _grabber.GetForegroundProcessName();

                // Whitelist-only: tell the skip gate which listed processes are alive (cheap
                // GetProcessesByName). All / AllMinusBlacklisted ignore this snapshot.
                IEnumerable<string>? runningWhitelist = null;
                if (config.ApplicationScope == ApplicationScopeMode.Whitelisted)
                {
                    var whitelist = ApplicationScopeEvaluator.GetWhitelistProcessNames(config);
                    runningWhitelist = whitelist.Count > 0 && _grabber.AnyNamedProcessRunning(whitelist)
                        ? whitelist
                        : Array.Empty<string>();
                }

                if (ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, foregroundProcess, runningWhitelist))
                {
                    await DelayCapturePeriodAsync(interval, cycle, token).ConfigureAwait(false);
                    continue;
                }

                using var frame = Grab(config);
                if (frame is null)
                {
                    await DelayCapturePeriodAsync(interval, cycle, token).ConfigureAwait(false);
                    continue;
                }

                var maskNames = ApplicationScopeEvaluator.GetBlacklistProcessNamesToMask(config);
                if (maskNames.Count > 0)
                {
                    var masked = _grabber.MaskProcessWindows(frame.Bitmap, frame.ScreenBounds, maskNames);
                    if (masked == 0)
                        _logger.Warn(
                            $"Blacklist mask: no window overlap with capture bounds ({frame.ScreenBounds.Width}x{frame.ScreenBounds.Height} @({frame.ScreenBounds.X},{frame.ScreenBounds.Y})) for: {string.Join(", ", maskNames)}");
                }

                var needsRecordRing = config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Record);
                if (needsRecordRing)
                    _ringBuffer.TryAdd(frame.Bitmap, frame.Timestamp, config.RecordJpegQuality);

                var needsSequenceRing = config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Sequence);
                if (needsSequenceRing)
                    _sequenceRing.TryAdd(frame.Bitmap, frame.Timestamp, config.RecordJpegQuality);

                queue.TryEnqueueIfChanged(frame, foregroundProcess);

                retentionCounter++;
                if (retentionCounter % 60 == 0)
                {
                    var dir = config.CaptureDirectory;
                    var age = config.RetentionMaxAgeDays;
                    var maxFiles = config.RetentionMaxFiles;
                    _ = Task.Run(() => RetentionService.Enforce(dir, age, maxFiles, _logger));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Capture loop iteration failed", ex);
                RaiseStatus("Warning", ex.Message);
            }

            try
            {
                await DelayCapturePeriodAsync(interval, cycle, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// OCR consumer: drain queued frames, match triggers, queue capture writes.
    /// </summary>
    private async Task RunOcrWorkerAsync(CancellationToken token)
    {
        var queue = _ocrQueue ?? throw new InvalidOperationException("OCR queue not initialized.");

        while (!token.IsCancellationRequested)
        {
            OcrQueuedFrame? queued = null;
            try
            {
                queued = await queue.ReadAsync(token).ConfigureAwait(false);
                if (queued is null)
                    break;

                AppConfig config;
                lock (_configLock)
                    config = _config;

                var captured = queued.ToCapturedFrame();
                // Freeze rings at dequeue so long OCR cannot roll past the prompting scene.
                var recordFramesAtDequeue = _ringBuffer.Snapshot();
                var sequenceFramesAtDequeue = _sequenceRing.Snapshot();

                var foregroundProcess = queued.ForegroundProcess;
                var published = new HashSet<Guid>();
                void PublishMatches(string ocrText)
                {
                    var found = TriggerMatcher.FindMatches(ocrText, config.Triggers, config.FastMatchThresholdPercent)
                        .Where(m => ApplicationScopeEvaluator.IsTriggerAllowed(config, foregroundProcess, m.Trigger.Id))
                        .ToList();
                    var debounce = TimeSpan.FromSeconds(
                        ApplicationScopeEvaluator.GetEffectiveDebounceSeconds(config, foregroundProcess));
                    var fires = TriggerFireCoalescer.SelectFires(found, match =>
                    {
                        if (!published.Add(match.Trigger.Id))
                            return false;

                        var key = $"{match.Trigger.Id}:{match.MatchedPortion}";
                        if (_fireGate.ShouldFire(key, debounce, queued.Fingerprint))
                        {
                            _logger.Debug($"Trigger '{match.Trigger.Text}' accepted (matched '{match.MatchedPortion}').");
                            return true;
                        }

                        _logger.Debug($"Trigger '{match.Trigger.Text}' suppressed (debounce or unchanged scene).");
                        return false;
                    });

                    foreach (var match in fires)
                    {
                        QueueCaptureWrite(
                            config,
                            captured,
                            match,
                            ocrText,
                            recordFramesAtDequeue,
                            sequenceFramesAtDequeue);
                    }
                }

                // Live pending count: capture keeps enqueueing while OCR runs. A snapshot at
                // start would miss backlog that arrives mid-cascade (stream shot→REC inject).
                // Publish as soon as a pass matches so a hit is not stuck behind later passes
                // that are still looking for other triggers.
                var ocr = await RecognizeFrameAsync(
                        config,
                        captured,
                        token,
                        () => queue.PendingCount,
                        PublishMatches)
                    .ConfigureAwait(false);
                var text = ocr.Text;
                PublishMatches(text);

                // Total miss, or a search cut short with an idle queue: sample the static scene
                // again. A finished search that already matched stays quiet so absent triggers
                // in a long list do not re-OCR forever.
                if (OcrCascadePolicy.ShouldResample(
                        AllEnabledMatched(text, config),
                        OcrCascadePolicy.AnyEnabledMatched(text, config),
                        ocr.CutShortForBacklog,
                        queue.PendingCount))
                    queue.AllowResample();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("OCR worker iteration failed", ex);
                RaiseStatus("Warning", ex.Message);
            }
            finally
            {
                queued?.Dispose();
            }
        }
    }

    private static async Task DelayCapturePeriodAsync(TimeSpan interval, Stopwatch cycle, CancellationToken token)
    {
        var remaining = interval - cycle.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, token).ConfigureAwait(false);
    }

    private CapturedFrame? Grab(AppConfig config)
    {
        var whitelistNames = ApplicationScopeEvaluator.GetWhitelistProcessNames(config);
        if (whitelistNames.Count > 0)
            return _grabber.TryCaptureProcessWindows(whitelistNames);

        if (config.CaptureScope == CaptureScope.ForegroundWindow)
        {
            var foreground = _grabber.TryCaptureForegroundWindow();
            if (foreground is not null)
                return foreground;
        }

        return config.MonitorSelection switch
        {
            MonitorSelection.All => _grabber.CaptureVirtualDesktop(),
            MonitorSelection.Specific => _grabber.CaptureMonitor(config.SpecificMonitorIndex),
            _ => _grabber.CapturePrimary()
        };
    }

    /// <summary>
    /// OCR the grabbed frame with multiple passes (scale / invert / contrast) so
    /// small or dark-terminal text is more likely to be recovered.
    /// When capturing the full virtual desktop, OCR each physical monitor separately.
    /// </summary>
    private readonly record struct OcrFrameResult(string Text, bool CutShortForBacklog);

    private async Task<OcrFrameResult> RecognizeFrameAsync(
        AppConfig config,
        CapturedFrame frame,
        CancellationToken token,
        Func<int>? pendingBehind = null,
        Action<string>? onText = null)
    {
        pendingBehind ??= static () => 0;
        var cascadeClock = Stopwatch.StartNew();
        var cascade = new CascadeContext(config, cascadeClock, pendingBehind);

        if (config.CaptureScope == CaptureScope.Monitor &&
            config.MonitorSelection == MonitorSelection.All)
        {
            var monitors = _grabber.GetMonitors();
            if (monitors.Count > 1)
            {
                var parts = new List<string>(monitors.Count);
                var cutShort = false;
                foreach (var monitor in monitors)
                {
                    var soFar = string.Join("\n", parts);
                    // Only stop the All-pass when every trigger matched — never because
                    // monitor 0 burned the Fast time budget (each monitor gets its own clock).
                    if (AllEnabledMatched(soFar, config))
                        break;

                    // Fresh cascade budget per monitor; backlog pressure only on the first.
                    var monitorPending = parts.Count == 0 ? pendingBehind : static () => 0;
                    var monitorCascade = new CascadeContext(config, Stopwatch.StartNew(), monitorPending);

                    // Crop from the queued virtual-desktop frame — never live re-grab, so
                    // match text and the saved screenshot share the same frozen moment.
                    using var slice = ImageHelpers.CropAbsoluteRegion(
                        frame.Bitmap, frame.ScreenBounds, monitor.Bounds);
                    if (slice is null)
                        continue;

                    var part = await RecognizeBitmapMultiPassAsync(slice, monitorCascade, token, onText)
                        .ConfigureAwait(false);
                    parts.Add(part.Text);
                    cutShort |= part.CutShortForBacklog;

                    var joinedMonitors = string.Join("\n", parts);
                    if (AllEnabledMatched(joinedMonitors, config))
                        return new OcrFrameResult(joinedMonitors, CutShortForBacklog: false);
                }

                return new OcrFrameResult(string.Join("\n", parts), cutShort);
            }
        }

        return await RecognizeBitmapMultiPassAsync(frame.Bitmap, cascade, token, onText).ConfigureAwait(false);
    }

    private async Task<OcrFrameResult> RecognizeBitmapMultiPassAsync(
        Bitmap bitmap,
        CascadeContext cascade,
        CancellationToken token,
        Action<string>? onText = null)
    {
        var chunks = new List<string>(8);
        var config = cascade.Config;

        // Baseline pass at configured scale (color, unmodified for OCR aside from scale).
        await AddOcrPassAsync(chunks, bitmap, config.OcrMaxWidth, config.OcrUpscalePercent, invert: false, OcrPrepMode.None, token)
            .ConfigureAwait(false);

        // Gray → adaptive stretch → invert (dark-terminal / low-contrast boost).
        var boost = Math.Max(config.OcrUpscalePercent, 200);
        await AddOcrPassAsync(chunks, bitmap, config.OcrMaxWidth, boost, invert: true, OcrPrepMode.AdaptiveContrast, token)
            .ConfigureAwait(false);

        // Neon-biased high-contrast without forcing invert (complements adaptive path).
        await AddOcrPassAsync(chunks, bitmap, config.OcrMaxWidth, boost, invert: false, OcrPrepMode.NeonHighContrast, token)
            .ConfigureAwait(false);

        if (config.OcrInvertColors && config.OcrUpscalePercent != boost)
        {
            await AddOcrPassAsync(chunks, bitmap, config.OcrMaxWidth, config.OcrUpscalePercent, invert: true, OcrPrepMode.None, token)
                .ConfigureAwait(false);
        }

        var joined = Report(chunks, onText);
        if (AllEnabledMatched(joined, cascade.Config, cascade.FastMatchThresholdPercent))
            return new OcrFrameResult(joined, false);
        if (IsCascadeOverBudget(joined, cascade))
            return new OcrFrameResult(joined, true);

        // Only when triggers are still missing: crop neon-terminal ROIs and re-OCR hard.
        await RecognizeTerminalRegionsAsync(chunks, bitmap, cascade, token).ConfigureAwait(false);
        joined = Report(chunks, onText);
        if (AllEnabledMatched(joined, cascade.Config, cascade.FastMatchThresholdPercent))
            return new OcrFrameResult(joined, false);
        if (IsCascadeOverBudget(joined, cascade))
            return new OcrFrameResult(joined, true);

        await RecognizeTilesAsync(chunks, bitmap, cascade, token).ConfigureAwait(false);
        joined = Report(chunks, onText);
        if (AllEnabledMatched(joined, cascade.Config, cascade.FastMatchThresholdPercent))
            return new OcrFrameResult(joined, false);
        if (IsCascadeOverBudget(joined, cascade))
            return new OcrFrameResult(joined, true);

        // Dense terminal lines: OCR overlapping horizontal bands at high upscale.
        await RecognizeHorizontalBandsAsync(chunks, bitmap, cascade, token).ConfigureAwait(false);
        joined = Report(chunks, onText);
        if (AllEnabledMatched(joined, cascade.Config, cascade.FastMatchThresholdPercent))
            return new OcrFrameResult(joined, false);
        return new OcrFrameResult(joined, IsCascadeOverBudget(joined, cascade));
    }

    private static string Report(List<string> chunks, Action<string>? onText)
    {
        var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
        onText?.Invoke(joined);
        return joined;
    }

    private readonly struct CascadeContext
    {
        private readonly Func<int> _pendingBehind;

        public CascadeContext(AppConfig config, Stopwatch clock, Func<int> pendingBehind)
        {
            Config = config;
            Clock = clock;
            _pendingBehind = pendingBehind;
        }

        public AppConfig Config { get; }
        public Stopwatch Clock { get; }
        /// <summary>Frames waiting behind the one currently being OCR'd (re-read each check).</summary>
        public int PendingBehind => _pendingBehind();
        public int FastMatchThresholdPercent => Config.FastMatchThresholdPercent;
    }

    private static IEnumerable<TriggerConfig> EnabledTriggers(AppConfig config) =>
        config.Triggers.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Text));

    /// <summary>
    /// Stop when every enabled trigger matched (screenshot+record with the same text still stop together).
    /// </summary>
    private static bool AllEnabledMatched(string text, AppConfig config) =>
        OcrCascadePolicy.AllEnabledMatched(text, config);

    private static bool AllEnabledMatched(string text, AppConfig config, int fastMatchThresholdPercent) =>
        OcrCascadePolicy.AllEnabledMatched(text, config, fastMatchThresholdPercent);

    /// <summary>
    /// When newer frames are already queued, cut the cascade short so OCR can catch up
    /// (e.g. scroll noise after a shot inject before a later REC fixture). Idle workers
    /// (no backlog) always run the full cascade — Fast and Thorough alike.
    /// </summary>
    private static bool ShouldApplyCascadeBudget(CascadeContext cascade) =>
        cascade.PendingBehind > 0;

    private static TimeSpan CascadeBudgetFor(string text, CascadeContext cascade)
    {
        var matchedIds = TriggerMatcher.FindMatches(text, cascade.Config.Triggers, cascade.FastMatchThresholdPercent)
            .Select(m => m.Trigger.Id)
            .ToHashSet();
        var unmatched = EnabledTriggers(cascade.Config).Where(t => !matchedIds.Contains(t.Id)).ToList();
        if (unmatched.Count == 0)
            return OcrCascadeBudgetBacklogFast;
        return unmatched.All(t => t.OcrMode == OcrSpeedMode.Fast)
            ? OcrCascadeBudgetBacklogFast
            : OcrCascadeBudgetBacklogThorough;
    }

    private static bool IsCascadeOverBudget(string text, CascadeContext cascade) =>
        ShouldApplyCascadeBudget(cascade) && cascade.Clock.Elapsed >= CascadeBudgetFor(text, cascade);

    private static bool ShouldStopCascade(string text, CascadeContext cascade) =>
        AllEnabledMatched(text, cascade.Config, cascade.FastMatchThresholdPercent) ||
        IsCascadeOverBudget(text, cascade);

    private async Task RecognizeTerminalRegionsAsync(
        List<string> chunks,
        Bitmap bitmap,
        CascadeContext cascade,
        CancellationToken token)
    {
        var config = cascade.Config;
        var regions = ImageHelpers.FindTerminalLikeRegions(bitmap);
        var scale = Math.Max(config.OcrUpscalePercent, 300);
        var hiScale = Math.Max(config.OcrUpscalePercent, 400);
        foreach (var region in regions)
        {
            var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            token.ThrowIfCancellationRequested();
            using var crop = bitmap.Clone(region, bitmap.PixelFormat);
            await AddOcrPassAsync(chunks, crop, config.OcrMaxWidth, scale, invert: true, OcrPrepMode.NeonHighContrast, token)
                .ConfigureAwait(false);
            joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            await AddOcrPassAsync(chunks, crop, config.OcrMaxWidth, scale, invert: false, OcrPrepMode.AdaptiveContrast, token)
                .ConfigureAwait(false);
            joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            await AddOcrPassAsync(chunks, crop, config.OcrMaxWidth, hiScale, invert: true, OcrPrepMode.NeonHighContrast, token)
                .ConfigureAwait(false);
            joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;
        }
    }

    /// <summary>
    /// Split terminal ROIs (or the full frame) into overlapping horizontal strips so
    /// dense console lines do not get dropped by full-frame OCR.
    /// </summary>
    private async Task RecognizeHorizontalBandsAsync(
        List<string> chunks,
        Bitmap bitmap,
        CascadeContext cascade,
        CancellationToken token)
    {
        var config = cascade.Config;
        var regions = ImageHelpers.FindTerminalLikeRegions(bitmap);
        if (regions.Count == 0)
            regions = [new Rectangle(0, 0, bitmap.Width, bitmap.Height)];

        var scale = Math.Max(config.OcrUpscalePercent, 350);
        var bandPasses = 0;
        const int maxBandPasses = 8;

        foreach (var region in regions)
        {
            var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (bandPasses >= maxBandPasses || ShouldStopCascade(joined, cascade))
                break;

            var bandHeight = Math.Max(64, region.Height / 4);
            var overlap = Math.Max(24, bandHeight / 3);
            var step = Math.Max(1, bandHeight - overlap);

            for (var y = region.Top; y < region.Bottom && bandPasses < maxBandPasses; y += step)
            {
                joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (ShouldStopCascade(joined, cascade))
                    return;

                token.ThrowIfCancellationRequested();
                var h = Math.Min(bandHeight, region.Bottom - y);
                if (h < 40 || region.Width < 120)
                    continue;

                using var band = bitmap.Clone(new Rectangle(region.Left, y, region.Width, h), bitmap.PixelFormat);
                await AddOcrPassAsync(chunks, band, config.OcrMaxWidth, scale, invert: true, OcrPrepMode.AdaptiveContrast, token)
                    .ConfigureAwait(false);
                bandPasses++;
            }
        }
    }

    private async Task RecognizeTilesAsync(
        List<string> chunks,
        Bitmap bitmap,
        CascadeContext cascade,
        CancellationToken token)
    {
        var config = cascade.Config;
        var grid = ImageHelpers.ChooseOcrTileGrid(bitmap.Width, bitmap.Height);
        var maxTiles = grid * grid;
        var overlap = Math.Max(80, Math.Min(bitmap.Width, bitmap.Height) / (grid * 10));
        var tileW = Math.Max(320, (bitmap.Width + overlap * (grid - 1)) / grid);
        var tileH = Math.Max(240, (bitmap.Height + overlap * (grid - 1)) / grid);
        var stepX = Math.Max(1, tileW - overlap);
        var stepY = Math.Max(1, tileH - overlap);
        var scale = Math.Max(config.OcrUpscalePercent, 250);

        var tiles = new List<Rectangle>(maxTiles);
        for (var y = 0; y < bitmap.Height && tiles.Count < maxTiles; y += stepY)
        {
            for (var x = 0; x < bitmap.Width && tiles.Count < maxTiles; x += stepX)
            {
                var w = Math.Min(tileW, bitmap.Width - x);
                var h = Math.Min(tileH, bitmap.Height - y);
                if (w < 160 || h < 120)
                    continue;
                tiles.Add(new Rectangle(x, y, w, h));
            }
        }

        // Pass 1: adaptive contrast per tile.
        foreach (var rect in tiles)
        {
            var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            token.ThrowIfCancellationRequested();
            using var tile = bitmap.Clone(rect, bitmap.PixelFormat);
            await AddOcrPassAsync(chunks, tile, config.OcrMaxWidth, scale, invert: false, OcrPrepMode.AdaptiveContrast, token)
                .ConfigureAwait(false);
        }

        // Pass 2 (only if still missing): same tiles with invert + adaptive contrast.
        foreach (var rect in tiles)
        {
            var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            token.ThrowIfCancellationRequested();
            using var tile = bitmap.Clone(rect, bitmap.PixelFormat);
            await AddOcrPassAsync(chunks, tile, config.OcrMaxWidth, scale, invert: true, OcrPrepMode.AdaptiveContrast, token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// OCR prep creates disposable bitmaps only. Never mutate <paramref name="bitmap"/> —
    /// saved screenshots/recordings always use the original color frame via QueueCaptureWrite.
    /// </summary>
    private async Task AddOcrPassAsync(
        List<string> chunks,
        Bitmap bitmap,
        int maxWidth,
        int upscalePercent,
        bool invert,
        OcrPrepMode prepMode,
        CancellationToken token)
    {
        Bitmap? prepBmp = null;
        try
        {
            prepBmp = ImageHelpers.ApplyOcrPrep(bitmap, prepMode);
            var source = prepBmp ?? bitmap;

            using var prepared = ImageHelpers.PrepareForOcr(source, maxWidth, upscalePercent, invert);
            var text = await _ocr.RecognizeAsync(prepared, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                chunks.Add(text);
        }
        finally
        {
            prepBmp?.Dispose();
        }
    }

    /// <summary>
    /// Persist captures from the original color frame. OCR preprocessing never feeds this path.
    /// </summary>
    private void QueueCaptureWrite(
        AppConfig config,
        CapturedFrame frame,
        TriggerMatchResult match,
        string ocrText,
        IReadOnlyList<BufferedFrame>? recordFramesAtDequeue = null,
        IReadOnlyList<BufferedFrame>? sequenceFramesAtDequeue = null)
    {
        var triggerText = match.Trigger.Text;
        // StatusChanged logs this at Info and App shows an early tray balloon.
        RaiseStatus("Processing", $"Potential trigger observed, processing… ('{triggerText}')");

        var snippet = ocrText.Length <= 280 ? ocrText : ocrText[..280];
        var sidecar = new CaptureSidecar
        {
            Timestamp = frame.Timestamp,
            MatchedText = match.MatchedPortion,
            TriggerText = match.Trigger.Text,
            MatchMode = match.Trigger.Match,
            Action = match.Trigger.Action,
            MonitorIndex = frame.MonitorIndex,
            MonitorName = frame.MonitorName,
            OcrSnippet = snippet
        };

        var clone = (Bitmap)frame.Bitmap.Clone();
        var action = match.Trigger.Action;
        var captureDirectory = config.CaptureDirectory;
        var saveJson = config.DebugSaveJsonSidecars;
        var screenshotFormat = config.ScreenshotFormat;
        var jpegQuality = config.RecordJpegQuality;
        var recordFps = config.RecordFps;
        var recordFormat = config.RecordFormat;
        var sequenceAfterSeconds = config.SequenceAfterSeconds;
        var sequenceIntervalSeconds = config.SequenceIntervalSeconds;
        var triggerTime = frame.Timestamp;
        // Prefer rings frozen at OCR dequeue so a long cascade cannot roll past the scene.
        IReadOnlyList<BufferedFrame>? recordFrames = action == CaptureAction.Record
            ? recordFramesAtDequeue ?? _ringBuffer.Snapshot()
            : null;
        IReadOnlyList<BufferedFrame>? sequenceFrames = action == CaptureAction.Sequence
            ? sequenceFramesAtDequeue ?? _sequenceRing.Snapshot()
            : null;

        RaiseStatus("Saving", $"Saving '{triggerText}'…");

        var cancelToken = _cts?.Token ?? CancellationToken.None;

        _ = Task.Run(() =>
        {
            try
            {
                string path;
                if (action == CaptureAction.Record)
                {
                    if (recordFrames is null || recordFrames.Count == 0)
                    {
                        sidecar.Action = CaptureAction.Screenshot;
                        path = _writer.WriteScreenshot(
                            clone, sidecar, captureDirectory, screenshotFormat, jpegQuality, saveJson);
                        _logger.Warn($"Record buffer empty for '{triggerText}', wrote screenshot instead.");
                    }
                    else
                    {
                        path = _writer.WriteRecording(
                            recordFrames,
                            clone,
                            sidecar,
                            captureDirectory,
                            recordFps,
                            recordFormat,
                            screenshotFormat,
                            jpegQuality,
                            saveJson);
                    }
                }
                else if (action == CaptureAction.Sequence)
                {
                    path = _writer.WriteScreenshotSequence(
                        sequenceFrames ?? Array.Empty<BufferedFrame>(),
                        clone,
                        sidecar,
                        captureDirectory,
                        screenshotFormat,
                        jpegQuality,
                        saveJson);

                    if (sequenceAfterSeconds > 0)
                    {
                        var deadline = triggerTime.AddSeconds(sequenceAfterSeconds);
                        var nextAt = triggerTime.AddSeconds(sequenceIntervalSeconds);
                        while (DateTimeOffset.UtcNow < deadline && !cancelToken.IsCancellationRequested)
                        {
                            var delay = nextAt - DateTimeOffset.UtcNow;
                            if (delay > TimeSpan.Zero)
                            {
                                try
                                {
                                    Task.Delay(delay, cancelToken).GetAwaiter().GetResult();
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                            }

                            if (cancelToken.IsCancellationRequested)
                                break;

                            using var postFrame = Grab(config);
                            if (postFrame is null)
                                break;
                            _writer.WriteSequenceStill(
                                path,
                                triggerTime,
                                postFrame.Timestamp,
                                postFrame.Bitmap,
                                screenshotFormat,
                                jpegQuality);
                            nextAt = nextAt.AddSeconds(sequenceIntervalSeconds);
                        }
                    }
                }
                else
                {
                    path = _writer.WriteScreenshot(
                        clone, sidecar, captureDirectory, screenshotFormat, jpegQuality, saveJson);
                }

                var displayName = action == CaptureAction.Sequence
                    ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : Path.GetFileName(path);
                _logger.Info($"Trigger '{triggerText}' fired ({sidecar.Action}) -> {path}");
                RaiseStatus("Triggered", $"{triggerText} → {displayName}");
                TriggerFired?.Invoke(this, new TriggerFiredEventArgs
                {
                    TriggerText = triggerText,
                    Action = sidecar.Action,
                    OutputPath = path
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save capture for '{triggerText}'", ex);
                RaiseStatus("Warning", ex.Message);
            }
            finally
            {
                clone.Dispose();
            }
        }, cancelToken);
    }

    private void RaiseStatus(string status, string? detail)
    {
        StatusChanged?.Invoke(this, new MonitorStatusEventArgs { Status = status, Detail = detail });
    }

    private static AppConfig CloneConfig(AppConfig source) =>
        JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(source, JsonDefaults.Options),
            JsonDefaults.Options)!;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _ringBuffer.Dispose();
        _sequenceRing.Dispose();
    }
}
