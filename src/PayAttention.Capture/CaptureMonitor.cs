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
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        RaiseStatus("Monitoring", "OCR monitoring started.");
        _logger.Info("Monitoring started.");
    }

    public void Stop()
    {
        if (_cts is null && _loopTask is null)
            return;

        _cts?.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // ignore cancellation
        }

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

    private async Task RunLoopAsync(CancellationToken token)
    {
        var retentionCounter = 0;

        while (!token.IsCancellationRequested)
        {
            AppConfig config;
            lock (_configLock)
                config = _config;

            var interval = TimeSpan.FromMilliseconds(config.OcrIntervalMs);

            try
            {
                if (_paused)
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                    continue;
                }

                using var frame = Grab(config);
                var foregroundProcess = _grabber.GetForegroundProcessName();

                if (ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, foregroundProcess))
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                    continue;
                }

                var maskNames = ApplicationScopeEvaluator.GetBlacklistProcessNamesToMask(config);
                if (maskNames.Count > 0)
                    _grabber.MaskProcessWindows(frame.Bitmap, frame.ScreenBounds, maskNames);

                var needsRecordRing = config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Record);
                if (needsRecordRing)
                    _ringBuffer.TryAdd(frame.Bitmap, frame.Timestamp, config.RecordJpegQuality);

                var needsSequenceRing = config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Sequence);
                if (needsSequenceRing)
                    _sequenceRing.TryAdd(frame.Bitmap, frame.Timestamp, config.RecordJpegQuality);

                var text = await RecognizeFrameAsync(config, frame, token).ConfigureAwait(false);
                var matches = TriggerMatcher.FindMatches(text, config.Triggers)
                    .Where(m => ApplicationScopeEvaluator.IsTriggerAllowed(config, foregroundProcess, m.Trigger.Id));

                var debounceSeconds = ApplicationScopeEvaluator.GetEffectiveDebounceSeconds(config, foregroundProcess);
                foreach (var match in matches)
                {
                    var key = $"{match.Trigger.Id}:{match.MatchedPortion}";
                    var fingerprint = FrameFingerprint.ComputeAverageHash(frame.Bitmap);
                    if (_fireGate.ShouldFire(key, TimeSpan.FromSeconds(debounceSeconds), fingerprint))
                        QueueCaptureWrite(config, frame, match, text);
                }

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
                _logger.Error("Monitor loop iteration failed", ex);
                RaiseStatus("Warning", ex.Message);
            }

            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private CapturedFrame Grab(AppConfig config)
    {
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
    private async Task<string> RecognizeFrameAsync(AppConfig config, CapturedFrame frame, CancellationToken token)
    {
        if (config.CaptureScope == CaptureScope.Monitor &&
            config.MonitorSelection == MonitorSelection.All)
        {
            var monitors = _grabber.GetMonitors();
            if (monitors.Count > 1)
            {
                var parts = new List<string>(monitors.Count);
                foreach (var monitor in monitors)
                {
                    using var slice = _grabber.CaptureMonitor(monitor.Index);
                    parts.Add(await RecognizeBitmapMultiPassAsync(slice.Bitmap, config, token).ConfigureAwait(false));
                }

                return string.Join("\n", parts);
            }
        }

        return await RecognizeBitmapMultiPassAsync(frame.Bitmap, config, token).ConfigureAwait(false);
    }

    private async Task<string> RecognizeBitmapMultiPassAsync(Bitmap bitmap, AppConfig config, CancellationToken token)
    {
        var chunks = new List<string>(8);
        var enabledCount = config.Triggers.Count(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Text));

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

        var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (AllEnabledMatched(joined, config, enabledCount))
            return joined;

        // Only when triggers are still missing: crop neon-terminal ROIs and re-OCR hard.
        await RecognizeTerminalRegionsAsync(chunks, bitmap, config, token).ConfigureAwait(false);
        joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (AllEnabledMatched(joined, config, enabledCount))
            return joined;

        await RecognizeTilesAsync(chunks, bitmap, config, enabledCount, token).ConfigureAwait(false);
        joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (AllEnabledMatched(joined, config, enabledCount))
            return joined;

        // Dense terminal lines: OCR overlapping horizontal bands at high upscale.
        await RecognizeHorizontalBandsAsync(chunks, bitmap, config, token).ConfigureAwait(false);
        return string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static bool AllEnabledMatched(string text, AppConfig config, int enabledCount) =>
        enabledCount == 0 || TriggerMatcher.FindMatches(text, config.Triggers).Count >= enabledCount;

    private async Task RecognizeTerminalRegionsAsync(
        List<string> chunks,
        Bitmap bitmap,
        AppConfig config,
        CancellationToken token)
    {
        var regions = ImageHelpers.FindTerminalLikeRegions(bitmap);
        var scale = Math.Max(config.OcrUpscalePercent, 300);
        var hiScale = Math.Max(config.OcrUpscalePercent, 400);
        foreach (var region in regions)
        {
            token.ThrowIfCancellationRequested();
            using var crop = bitmap.Clone(region, bitmap.PixelFormat);
            await AddOcrPassAsync(chunks, crop, config.OcrMaxWidth, scale, invert: true, OcrPrepMode.NeonHighContrast, token)
                .ConfigureAwait(false);
            await AddOcrPassAsync(chunks, crop, config.OcrMaxWidth, scale, invert: false, OcrPrepMode.AdaptiveContrast, token)
                .ConfigureAwait(false);
            await AddOcrPassAsync(chunks, crop, config.OcrMaxWidth, hiScale, invert: true, OcrPrepMode.NeonHighContrast, token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Split terminal ROIs (or the full frame) into overlapping horizontal strips so
    /// dense console lines do not get dropped by full-frame OCR.
    /// </summary>
    private async Task RecognizeHorizontalBandsAsync(
        List<string> chunks,
        Bitmap bitmap,
        AppConfig config,
        CancellationToken token)
    {
        var regions = ImageHelpers.FindTerminalLikeRegions(bitmap);
        if (regions.Count == 0)
            regions = [new Rectangle(0, 0, bitmap.Width, bitmap.Height)];

        var scale = Math.Max(config.OcrUpscalePercent, 350);
        var bandPasses = 0;
        const int maxBandPasses = 8;

        foreach (var region in regions)
        {
            if (bandPasses >= maxBandPasses)
                break;

            var bandHeight = Math.Max(64, region.Height / 4);
            var overlap = Math.Max(24, bandHeight / 3);
            var step = Math.Max(1, bandHeight - overlap);

            for (var y = region.Top; y < region.Bottom && bandPasses < maxBandPasses; y += step)
            {
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
        AppConfig config,
        int enabledCount,
        CancellationToken token)
    {
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
            token.ThrowIfCancellationRequested();
            using var tile = bitmap.Clone(rect, bitmap.PixelFormat);
            await AddOcrPassAsync(chunks, tile, config.OcrMaxWidth, scale, invert: false, OcrPrepMode.AdaptiveContrast, token)
                .ConfigureAwait(false);
            var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (AllEnabledMatched(joined, config, enabledCount))
                return;
        }

        // Pass 2 (only if still missing): same tiles with invert + adaptive contrast.
        foreach (var rect in tiles)
        {
            token.ThrowIfCancellationRequested();
            using var tile = bitmap.Clone(rect, bitmap.PixelFormat);
            await AddOcrPassAsync(chunks, tile, config.OcrMaxWidth, scale, invert: true, OcrPrepMode.AdaptiveContrast, token)
                .ConfigureAwait(false);
            var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (AllEnabledMatched(joined, config, enabledCount))
                return;
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
    private void QueueCaptureWrite(AppConfig config, CapturedFrame frame, TriggerMatchResult match, string ocrText)
    {
        RaiseStatus("Saving", $"Saving '{match.Trigger.Text}'…");

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
        var triggerText = match.Trigger.Text;
        var captureDirectory = config.CaptureDirectory;
        var saveJson = config.DebugSaveJsonSidecars;
        var screenshotFormat = config.ScreenshotFormat;
        var jpegQuality = config.RecordJpegQuality;
        var recordFps = config.RecordFps;
        var recordFormat = config.RecordFormat;
        var sequenceAfterSeconds = config.SequenceAfterSeconds;
        var sequenceIntervalSeconds = config.SequenceIntervalSeconds;
        var triggerTime = frame.Timestamp;
        IReadOnlyList<BufferedFrame>? recordFrames = null;
        IReadOnlyList<BufferedFrame>? sequenceFrames = null;
        if (action == CaptureAction.Record)
            recordFrames = _ringBuffer.Snapshot();
        if (action == CaptureAction.Sequence)
            sequenceFrames = _sequenceRing.Snapshot();

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
