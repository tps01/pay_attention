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
    private readonly ScreenGrabber _grabber = new();
    private readonly OcrService _ocr = new();
    private readonly CaptureWriter _writer = new();
    private readonly TriggerFireGate _fireGate = new();
    private readonly FrameRingBuffer _ringBuffer;
    private readonly FrameRingBuffer _sequenceRing;
    private readonly AppLogger _logger;
    private readonly PhaseProfiler _profiler = new();
    private readonly object _configLock = new();

    private AppConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private OcrFrameQueue? _ocrQueue;
    private long _lastProfileFlushTimestamp = Stopwatch.GetTimestamp();
    private bool _paused;
    private bool _disposed;
    /// <summary>Force a full Grab after AllowResample or FG change.</summary>
    private bool _forceFullGrab = true;
    /// <summary>Last capture cycle skipped enqueue (scene unchanged).</summary>
    private bool _lastEnqueueSkipped;
    private string? _lastCaptureForeground;
    private int _skippedGrabCount;
    /// <summary>Still probe the screen at least every N skipped intervals when quiet.</summary>
    private const int MaxSkippedGrabs = 4;
    /// <summary>Last whitelist process that held OS focus (survives focus-lock steals).</summary>
    private string? _stickyWhitelistForeground;
    private DateTimeOffset _lastForegroundPulseUtc = DateTimeOffset.MinValue;
    private SynchronizationContext? _uiContext;

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
        _uiContext = SynchronizationContext.Current;
        _stickyWhitelistForeground = null;
        _lastProfileFlushTimestamp = Stopwatch.GetTimestamp();
        _forceFullGrab = true;
        _lastEnqueueSkipped = false;
        _lastCaptureForeground = null;
        _skippedGrabCount = 0;
        _cts = new CancellationTokenSource();
        int queueCapacity;
        bool backgroundPriority;
        lock (_configLock)
        {
            queueCapacity = _config.OcrQueueCapacity;
            backgroundPriority = _config.UseBackgroundPriority;
        }

        _ocrQueue = new OcrFrameQueue(queueCapacity);
        _loopTask = Task.Run(async () =>
        {
            if (backgroundPriority)
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                }
                catch
                {
                    // Priority is best-effort on locked-down images.
                }
            }

            await RunPipelineAsync(_cts.Token).ConfigureAwait(false);
        });
        RaiseStatus("Monitoring", "OCR monitoring started.");
        _logger.Info(
            $"Monitoring started (queue={queueCapacity}, backgroundPriority={backgroundPriority}).");
    }

    /// <summary>
    /// Stop then Start so queue capacity and worker priority pick up the latest config.
    /// No-op when monitoring is not running.
    /// </summary>
    public void RestartIfRunning()
    {
        if (!IsRunning)
            return;
        var wasPaused = _paused;
        Stop();
        Start();
        if (wasPaused)
            SetPaused(true);
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
        FlushProfile("stop");
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
                // Selected follows the real FG when it is listed; otherwise the sticky last
                // listed FG (focus-lock often steals back to the browser). A rare Alt pulse
                // lets same-process Activate (e.g. harness) take FG while sticky is empty.
                IEnumerable<string>? runningWhitelist = null;
                if (config.ApplicationScope == ApplicationScopeMode.Whitelisted)
                {
                    var whitelist = ApplicationScopeEvaluator.GetWhitelistProcessNames(config);
                    runningWhitelist = whitelist.Count > 0 && _grabber.AnyNamedProcessRunning(whitelist)
                        ? whitelist
                        : Array.Empty<string>();
                    if (whitelist.Count > 0)
                        foregroundProcess = ResolveWhitelistForeground(whitelist, foregroundProcess);
                }

                if (ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, foregroundProcess, runningWhitelist))
                {
                    await DelayCapturePeriodAsync(interval, cycle, token).ConfigureAwait(false);
                    continue;
                }

                var nowUtc = DateTimeOffset.UtcNow;
                var needsRecordRing = config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Record);
                var needsSequenceRing = config.Triggers.Any(t => t.Enabled && t.Action == CaptureAction.Sequence);
                var ringsNeedFrame =
                    (needsRecordRing && _ringBuffer.WouldAccept(nowUtc)) ||
                    (needsSequenceRing && _sequenceRing.WouldAccept(nowUtc));
                var fgNorm = ApplicationScopeEvaluator.NormalizeProcessName(foregroundProcess);
                var fgChanged = !string.Equals(_lastCaptureForeground, fgNorm, StringComparison.OrdinalIgnoreCase);
                // Quiet desktop: skip full Grab while the last scene was unchanged, unless
                // rings need a sample, resample was requested, FG changed, or we hit the probe cap.
                if (!_forceFullGrab &&
                    !ringsNeedFrame &&
                    !fgChanged &&
                    _lastEnqueueSkipped &&
                    _skippedGrabCount < MaxSkippedGrabs)
                {
                    _skippedGrabCount++;
                    MaybeFlushProfile(config);
                    await DelayCapturePeriodAsync(interval, cycle, token).ConfigureAwait(false);
                    continue;
                }

                _forceFullGrab = false;
                _skippedGrabCount = 0;
                _lastCaptureForeground = fgNorm;

                var grabStart = Stopwatch.GetTimestamp();
                using var frame = Grab(config);
                _profiler.Record("grab", grabStart, _logger);
                if (frame is null)
                {
                    MaybeFlushProfile(config);
                    await DelayCapturePeriodAsync(interval, cycle, token).ConfigureAwait(false);
                    continue;
                }

                var maskNames = ApplicationScopeEvaluator.GetBlacklistProcessNamesToMask(config);
                if (maskNames.Count > 0)
                {
                    var maskStart = Stopwatch.GetTimestamp();
                    var masked = _grabber.MaskProcessWindows(frame.Bitmap, frame.ScreenBounds, maskNames);
                    _profiler.Record("mask", maskStart, _logger);
                    if (masked == 0)
                        _logger.Warn(
                            $"Blacklist mask: no window overlap with capture bounds ({frame.ScreenBounds.Width}x{frame.ScreenBounds.Height} @({frame.ScreenBounds.X},{frame.ScreenBounds.Y})) for: {string.Join(", ", maskNames)}");
                }

                if (needsRecordRing || needsSequenceRing)
                {
                    var ringStart = Stopwatch.GetTimestamp();
                    if (needsRecordRing)
                        _ringBuffer.TryAdd(frame.Bitmap, frame.Timestamp, config.RecordJpegQuality);
                    if (needsSequenceRing)
                        _sequenceRing.TryAdd(frame.Bitmap, frame.Timestamp, config.RecordJpegQuality);
                    _profiler.Record("ring", ringStart, _logger);
                }

                var enqueued = queue.TryEnqueueIfChanged(frame, foregroundProcess, _profiler, _logger);
                _lastEnqueueSkipped = !enqueued;

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
                MaybeFlushProfile(config);
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
                    var matchStart = Stopwatch.GetTimestamp();
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
                    _profiler.Record("match", matchStart, _logger);

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
                // in a long list do not re-OCR forever (whitelist / many-trigger case).
                // Whitelist Selected: only count triggers allowed for this FG — OCR may see
                // another listed app's phrase without being allowed to fire it.
                // AllMinusBlacklisted: one pane's hit must not silence OCR while other panes'
                // triggers are still missing (multi-pane Alpha then Beta).
                var allMatched = TimedAllAllowedMatched(text, config, foregroundProcess);
                var anyMatched = TimedAnyAllowedMatched(text, config, foregroundProcess);
                var anyForQuiet = config.ApplicationScope == ApplicationScopeMode.AllMinusBlacklisted
                    ? allMatched
                    : anyMatched;
                if (OcrCascadePolicy.ShouldResample(
                        allMatched,
                        anyForQuiet,
                        ocr.CutShortForBacklog,
                        queue.PendingCount))
                {
                    queue.AllowResample();
                    _forceFullGrab = true;
                }
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

    /// <summary>
    /// Prefer OS FG when it is a listed app (updates sticky). Otherwise keep sticky so
    /// Selected does not flip when the browser steals focus. Pulse input so same-process
    /// Activate (e.g. harness) can reclaim FG; re-read after the pulse.
    /// </summary>
    private string? ResolveWhitelistForeground(IReadOnlyList<string> whitelist, string? osForeground)
    {
        var fgNorm = ApplicationScopeEvaluator.NormalizeProcessName(osForeground);
        if (fgNorm.Length > 0 &&
            whitelist.Any(n => string.Equals(n, fgNorm, StringComparison.OrdinalIgnoreCase)))
        {
            _stickyWhitelistForeground = fgNorm;
            return fgNorm;
        }

        // Help same-process Activate take FG (focus lock). Pulse on the UI thread when
        // available so WinForms timers in the same process can SetForegroundWindow.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastForegroundPulseUtc > TimeSpan.FromMilliseconds(500))
        {
            _lastForegroundPulseUtc = now;
            PulseForegroundPermissionOnUiThread();
            fgNorm = ApplicationScopeEvaluator.NormalizeProcessName(_grabber.GetForegroundProcessName());
            if (fgNorm.Length > 0 &&
                whitelist.Any(n => string.Equals(n, fgNorm, StringComparison.OrdinalIgnoreCase)))
            {
                _stickyWhitelistForeground = fgNorm;
                return fgNorm;
            }
        }

        // Activate often raises Z-order among listed TopMost panes without winning OS FG
        // (browser focus lock). Follow the top-most listed window so Selected can switch
        // Alpha → Beta after the harness raises the next pane.
        var topListed = _grabber.GetTopmostProcessAmong(whitelist);
        if (!string.IsNullOrEmpty(topListed))
        {
            _stickyWhitelistForeground = topListed;
            return topListed;
        }

        if (!string.IsNullOrEmpty(_stickyWhitelistForeground) &&
            whitelist.Any(n => string.Equals(n, _stickyWhitelistForeground, StringComparison.OrdinalIgnoreCase)) &&
            _grabber.AnyNamedProcessRunning([_stickyWhitelistForeground]))
            return _stickyWhitelistForeground;

        // Single listed app that is alive: Selected applies to that app even when OS FG
        // is elsewhere (typical for "only this process" / self-whitelist harnesses).
        var runningListed = whitelist.Where(n => _grabber.AnyNamedProcessRunning([n])).ToList();
        if (runningListed.Count == 1)
        {
            _stickyWhitelistForeground = runningListed[0];
            return runningListed[0];
        }

        // Outside the list and no sticky yet: deny Selected (caller keeps os FG name).
        return osForeground;
    }

    private void PulseForegroundPermissionOnUiThread()
    {
        if (_uiContext is null)
        {
            _grabber.PulseForegroundPermission();
            return;
        }

        try
        {
            _uiContext.Send(_ => _grabber.PulseForegroundPermission(), null);
        }
        catch
        {
            _grabber.PulseForegroundPermission();
        }
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
        var cascade = new CascadeContext(config, cascadeClock, pendingBehind, _profiler, _logger);

        // AllMinusBlacklisted: OCR each non-masked visible window crop so side-by-side
        // panes (e.g. Alpha + Beta) are recoverable; keep the full masked frame for save.
        if (config.ApplicationScope == ApplicationScopeMode.AllMinusBlacklisted &&
            config.CaptureScope == CaptureScope.Monitor)
        {
            var windowResult = await RecognizeNonBlacklistedWindowsAsync(
                config, frame, cascade, token, onText).ConfigureAwait(false);
            if (windowResult is not null)
                return windowResult.Value;
        }

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
                    if (TimedAllEnabledMatched(soFar, config))
                        break;

                    // Fresh cascade budget per monitor; backlog pressure only on the first.
                    var monitorPending = parts.Count == 0 ? pendingBehind : static () => 0;
                    var monitorCascade = new CascadeContext(config, Stopwatch.StartNew(), monitorPending, _profiler, _logger);

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
                    if (TimedAllEnabledMatched(joinedMonitors, config))
                        return new OcrFrameResult(joinedMonitors, CutShortForBacklog: false);
                }

                return new OcrFrameResult(string.Join("\n", parts), cutShort);
            }
        }

        return await RecognizeBitmapMultiPassAsync(frame.Bitmap, cascade, token, onText).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-window OCR for AllMinusBlacklisted monitor captures. Returns null when no
    /// overlapping non-blacklisted windows were found (caller falls back to full-frame OCR).
    /// </summary>
    private async Task<OcrFrameResult?> RecognizeNonBlacklistedWindowsAsync(
        AppConfig config,
        CapturedFrame frame,
        CascadeContext sharedCascade,
        CancellationToken token,
        Action<string>? onText)
    {
        var exclude = ApplicationScopeEvaluator.GetBlacklistProcessNamesToMask(config);
        var windows = _grabber.GetVisibleProcessWindows(exclude);
        if (windows.Count == 0)
            return null;

        var maxWindows = Math.Clamp(config.OcrMaxWindows, 1, 12);
        var frameArea = frame.ScreenBounds.Width * (long)frame.ScreenBounds.Height;
        // Preserve EnumWindows Z-order (top → bottom). TopMost fixture panes sort above
        // ordinary browser windows; do not re-rank by area (that OCR'd huge Chrome first).
        var targets = new List<(IntPtr Hwnd, Rectangle Bounds)>();
        foreach (var (hwnd, windowBounds) in windows)
        {
            var overlap = Rectangle.Intersect(windowBounds, frame.ScreenBounds);
            if (overlap.Width < 32 || overlap.Height < 32)
                continue;
            var area = overlap.Width * (long)overlap.Height;
            // Skip near-fullscreen hosts and tiny tool windows.
            if (frameArea > 0 && (area >= frameArea * 3 / 4 || area < frameArea / 200))
                continue;
            targets.Add((hwnd, overlap));
            if (targets.Count >= maxWindows)
                break;
        }

        if (targets.Count == 0)
            return null;

        var parts = new List<string>(targets.Count);
        var cutShort = false;
        // One cascade clock for the whole frame — later windows inherit elapsed time.
        for (var i = 0; i < targets.Count; i++)
        {
            var soFar = string.Join("\n", parts);
            if (TimedAllEnabledMatched(soFar, config))
                break;
            if (IsCascadeOverBudget(sharedCascade))
            {
                cutShort = true;
                break;
            }

            // PrintWindow each HWND for OCR: CopyFromScreen crops often miss TextBox
            // scroll paints that PrintWindow still recovers (same as whitelist path).
            // Screenshot save stays the masked monitor frame from the queue.
            using var live = _grabber.TryCaptureWindow(targets[i].Hwnd);
            using var cropped = live is null
                ? ImageHelpers.CropAbsoluteRegion(
                    frame.Bitmap, frame.ScreenBounds, targets[i].Bounds)
                : null;
            var slice = live?.Bitmap ?? cropped;
            if (slice is null)
                continue;

            var part = await RecognizeBitmapMultiPassAsync(slice, sharedCascade, token, onText)
                .ConfigureAwait(false);
            parts.Add(part.Text);
            cutShort |= part.CutShortForBacklog;

            var joined = string.Join("\n", parts);
            if (TimedAllEnabledMatched(joined, config))
                return new OcrFrameResult(joined, CutShortForBacklog: false);
        }

        return parts.Count == 0
            ? null
            : new OcrFrameResult(string.Join("\n", parts), cutShort);
    }

    private async Task<OcrFrameResult> RecognizeBitmapMultiPassAsync(
        Bitmap bitmap,
        CascadeContext cascade,
        CancellationToken token,
        Action<string>? onText = null)
    {
        var chunks = new List<string>(8);
        var config = cascade.Config;
        var match = cascade.Match;
        var boost = PassUpscale(config, 200);

        // Baseline pass at configured scale (color, unmodified for OCR aside from scale).
        await AddOcrPassAsync(chunks, bitmap, config, config.OcrUpscalePercent, invert: false, OcrPrepMode.None, "ocr.baseline", token)
            .ConfigureAwait(false);
        var joined = Report(chunks, onText);
        match.Observe(joined);
        if (match.AllEnabledMatched())
            return new OcrFrameResult(joined, false);
        if (IsCascadeOverBudget(cascade))
            return new OcrFrameResult(joined, true);

        // Gray → adaptive stretch → invert (dark-terminal / low-contrast boost).
        if (config.OcrEnableAdaptive)
        {
            await AddOcrPassAsync(chunks, bitmap, config, boost, invert: true, OcrPrepMode.AdaptiveContrast, "ocr.adaptive", token)
                .ConfigureAwait(false);
            joined = Report(chunks, onText);
            match.Observe(joined);
            if (match.AllEnabledMatched())
                return new OcrFrameResult(joined, false);
            if (IsCascadeOverBudget(cascade))
                return new OcrFrameResult(joined, true);
        }

        // Neon-biased high-contrast without forcing invert (complements adaptive path).
        if (config.OcrEnableNeon)
        {
            await AddOcrPassAsync(chunks, bitmap, config, boost, invert: false, OcrPrepMode.NeonHighContrast, "ocr.neon", token)
                .ConfigureAwait(false);
            joined = Report(chunks, onText);
            match.Observe(joined);
            if (match.AllEnabledMatched())
                return new OcrFrameResult(joined, false);
            if (IsCascadeOverBudget(cascade))
                return new OcrFrameResult(joined, true);
        }

        if (config.OcrInvertColors && config.OcrUpscalePercent != boost)
        {
            await AddOcrPassAsync(chunks, bitmap, config, config.OcrUpscalePercent, invert: true, OcrPrepMode.None, "ocr.invert", token)
                .ConfigureAwait(false);
            joined = Report(chunks, onText);
            match.Observe(joined);
            if (match.AllEnabledMatched())
                return new OcrFrameResult(joined, false);
            if (IsCascadeOverBudget(cascade))
                return new OcrFrameResult(joined, true);
        }

        // Only when triggers are still missing: crop neon-terminal ROIs and re-OCR hard.
        if (config.OcrEnableTerminal && config.OcrTerminalMaxRegions > 0)
        {
            await RecognizeTerminalRegionsAsync(chunks, bitmap, cascade, token).ConfigureAwait(false);
            joined = Report(chunks, onText);
            match.Observe(joined);
            if (match.AllEnabledMatched())
                return new OcrFrameResult(joined, false);
            if (IsCascadeOverBudget(cascade))
                return new OcrFrameResult(joined, true);
        }

        // Fast-only misses: skip expensive tiles/bands (Thorough unmatched still dig deeper).
        if (!match.NeedsDeepCascade())
            return new OcrFrameResult(joined, false);

        await RecognizeTilesAsync(chunks, bitmap, cascade, token).ConfigureAwait(false);
        joined = Report(chunks, onText);
        match.Observe(joined);
        if (match.AllEnabledMatched())
            return new OcrFrameResult(joined, false);
        if (IsCascadeOverBudget(cascade))
            return new OcrFrameResult(joined, true);

        // Dense terminal lines: OCR overlapping horizontal bands at high upscale.
        await RecognizeHorizontalBandsAsync(chunks, bitmap, cascade, token).ConfigureAwait(false);
        joined = Report(chunks, onText);
        match.Observe(joined);
        if (match.AllEnabledMatched())
            return new OcrFrameResult(joined, false);
        return new OcrFrameResult(joined, IsCascadeOverBudget(cascade));
    }

    /// <summary>
    /// Upscale for a pass: max(user upscale, boost scaled from High's 200% floors).
    /// </summary>
    private static int PassUpscale(AppConfig config, int floorRelativeTo200) =>
        Math.Max(
            config.OcrUpscalePercent,
            PerformanceProfiles.ScaledUpscale(config.OcrBoostUpscalePercent, floorRelativeTo200));

    private static string Report(List<string> chunks, Action<string>? onText)
    {
        var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
        onText?.Invoke(joined);
        return joined;
    }

    private sealed class CascadeMatchState
    {
        private readonly HashSet<Guid> _matchedIds = new();
        private readonly AppConfig _config;
        private readonly int _threshold;
        private readonly PhaseProfiler _profiler;
        private readonly AppLogger _logger;

        public CascadeMatchState(
            AppConfig config,
            int threshold,
            PhaseProfiler profiler,
            AppLogger logger)
        {
            _config = config;
            _threshold = threshold;
            _profiler = profiler;
            _logger = logger;
        }

        public void Observe(string text)
        {
            var start = Stopwatch.GetTimestamp();
            foreach (var match in TriggerMatcher.FindMatches(text, _config.Triggers, _threshold))
                _matchedIds.Add(match.Trigger.Id);
            _profiler.Record("match", start, _logger);
        }

        public bool AllEnabledMatched() =>
            OcrCascadePolicy.AllEnabledMatched(_matchedIds, _config);

        public bool NeedsDeepCascade() =>
            OcrCascadePolicy.NeedsDeepCascade(_matchedIds, _config);

        public bool UnmatchedAreAllFast() =>
            OcrCascadePolicy.UnmatchedAreAllFast(_matchedIds, _config);
    }

    private readonly struct CascadeContext
    {
        private readonly Func<int> _pendingBehind;

        public CascadeContext(
            AppConfig config,
            Stopwatch clock,
            Func<int> pendingBehind,
            PhaseProfiler profiler,
            AppLogger logger)
        {
            Config = config;
            Clock = clock;
            _pendingBehind = pendingBehind;
            Match = new CascadeMatchState(config, config.FastMatchThresholdPercent, profiler, logger);
        }

        public AppConfig Config { get; }
        public Stopwatch Clock { get; }
        public CascadeMatchState Match { get; }
        /// <summary>Frames waiting behind the one currently being OCR'd (re-read each check).</summary>
        public int PendingBehind => _pendingBehind();
        public int FastMatchThresholdPercent => Config.FastMatchThresholdPercent;
    }

    private static IEnumerable<TriggerConfig> EnabledTriggers(AppConfig config) =>
        config.Triggers.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Text));

    private bool TimedAllEnabledMatched(string text, AppConfig config)
    {
        var start = Stopwatch.GetTimestamp();
        var result = OcrCascadePolicy.AllEnabledMatched(text, config);
        _profiler.Record("match", start, _logger);
        return result;
    }

    /// <summary>
    /// Like AllEnabledMatched, but only triggers allowed for the current foreground row.
    /// Empty allowed set (whitelist FG outside list) counts as complete so we stay quiet.
    /// </summary>
    private bool TimedAllAllowedMatched(string text, AppConfig config, string? foregroundProcess)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var allowed = EnabledTriggers(config)
                .Where(t => ApplicationScopeEvaluator.IsTriggerAllowed(config, foregroundProcess, t.Id))
                .ToList();
            if (allowed.Count == 0)
                return true;

            var matchedIds = TriggerMatcher.FindMatches(text, config.Triggers, config.FastMatchThresholdPercent)
                .Select(m => m.Trigger.Id)
                .ToHashSet();
            return allowed.All(t => matchedIds.Contains(t.Id));
        }
        finally
        {
            _profiler.Record("match", start, _logger);
        }
    }

    private bool TimedAnyAllowedMatched(string text, AppConfig config, string? foregroundProcess)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var allowed = EnabledTriggers(config)
                .Where(t => ApplicationScopeEvaluator.IsTriggerAllowed(config, foregroundProcess, t.Id))
                .ToList();
            if (allowed.Count == 0)
                return true;

            var matchedIds = TriggerMatcher.FindMatches(text, config.Triggers, config.FastMatchThresholdPercent)
                .Select(m => m.Trigger.Id)
                .ToHashSet();
            return allowed.Any(t => matchedIds.Contains(t.Id));
        }
        finally
        {
            _profiler.Record("match", start, _logger);
        }
    }

    /// <summary>
    /// Cut the cascade short under backlog (or always, when OcrApplyBudgetWhenIdle) so OCR
    /// can catch up. Ultra keeps idle workers uncapped; leaner profiles apply budgets always.
    /// </summary>
    private static bool ShouldApplyCascadeBudget(CascadeContext cascade) =>
        cascade.Config.OcrApplyBudgetWhenIdle || cascade.PendingBehind > 0;

    private static TimeSpan CascadeBudgetFor(CascadeContext cascade) =>
        TimeSpan.FromMilliseconds(
            cascade.Match.UnmatchedAreAllFast()
                ? cascade.Config.OcrCascadeBudgetFastMs
                : cascade.Config.OcrCascadeBudgetThoroughMs);

    private static bool IsCascadeOverBudget(CascadeContext cascade) =>
        ShouldApplyCascadeBudget(cascade) && cascade.Clock.Elapsed >= CascadeBudgetFor(cascade);

    private static bool ShouldStopCascade(string text, CascadeContext cascade)
    {
        cascade.Match.Observe(text);
        return cascade.Match.AllEnabledMatched() || IsCascadeOverBudget(cascade);
    }

    private async Task RecognizeTerminalRegionsAsync(
        List<string> chunks,
        Bitmap bitmap,
        CascadeContext cascade,
        CancellationToken token)
    {
        var config = cascade.Config;
        var regions = ImageHelpers.FindTerminalLikeRegions(bitmap, maxRegions: config.OcrTerminalMaxRegions);
        var scale = PassUpscale(config, 300);
        var hiScale = PassUpscale(config, 400);
        foreach (var region in regions)
        {
            var joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            token.ThrowIfCancellationRequested();
            using var crop = bitmap.Clone(region, bitmap.PixelFormat);
            await AddOcrPassAsync(chunks, crop, config, scale, invert: true, OcrPrepMode.NeonHighContrast, "ocr.terminal", token)
                .ConfigureAwait(false);
            joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            await AddOcrPassAsync(chunks, crop, config, scale, invert: false, OcrPrepMode.AdaptiveContrast, "ocr.terminal", token)
                .ConfigureAwait(false);
            joined = string.Join("\n", chunks.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (ShouldStopCascade(joined, cascade))
                return;

            await AddOcrPassAsync(chunks, crop, config, hiScale, invert: true, OcrPrepMode.NeonHighContrast, "ocr.terminal", token)
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
        var regions = ImageHelpers.FindTerminalLikeRegions(bitmap, maxRegions: config.OcrTerminalMaxRegions);
        if (regions.Count == 0)
            regions = [new Rectangle(0, 0, bitmap.Width, bitmap.Height)];

        var scale = PassUpscale(config, 350);
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
                await AddOcrPassAsync(chunks, band, config, scale, invert: true, OcrPrepMode.AdaptiveContrast, "ocr.bands", token)
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
        var scale = PassUpscale(config, 250);

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
            await AddOcrPassAsync(chunks, tile, config, scale, invert: false, OcrPrepMode.AdaptiveContrast, "ocr.tiles", token)
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
            await AddOcrPassAsync(chunks, tile, config, scale, invert: true, OcrPrepMode.AdaptiveContrast, "ocr.tiles", token)
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
        AppConfig config,
        int upscalePercent,
        bool invert,
        OcrPrepMode prepMode,
        string phase,
        CancellationToken token)
    {
        Bitmap? prepBmp = null;
        var passStart = Stopwatch.GetTimestamp();
        try
        {
            var prepStart = Stopwatch.GetTimestamp();
            prepBmp = ImageHelpers.ApplyOcrPrep(bitmap, prepMode);
            var source = prepBmp ?? bitmap;
            using var prepared = ImageHelpers.PrepareForOcr(
                source,
                config.OcrMaxWidth,
                upscalePercent,
                invert,
                config.OcrAbsoluteMaxWidth);
            _profiler.Record("ocr.prep", prepStart, _logger);

            var recognizeStart = Stopwatch.GetTimestamp();
            var text = await _ocr.RecognizeAsync(prepared, token).ConfigureAwait(false);
            _profiler.Record("ocr.recognize", recognizeStart, _logger);

            if (!string.IsNullOrWhiteSpace(text))
                chunks.Add(text);
        }
        finally
        {
            _profiler.Record(phase, passStart, _logger);
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
        RaiseStatus("Processing", $"Potential trigger observed, processing... ('{triggerText}')");

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

        RaiseStatus("Saving", $"Saving '{triggerText}'...");

        var cancelToken = _cts?.Token ?? CancellationToken.None;

        _ = Task.Run(() =>
        {
            try
            {
                string path;
                if (action == CaptureAction.Record)
                {
                    var writeStart = Stopwatch.GetTimestamp();
                    if (recordFrames is null || recordFrames.Count == 0)
                    {
                        sidecar.Action = CaptureAction.Screenshot;
                        path = _writer.WriteScreenshot(
                            clone, sidecar, captureDirectory, screenshotFormat, jpegQuality, saveJson);
                        _profiler.Record("write", writeStart, _logger);
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
                        _profiler.Record("write", writeStart, _logger);
                    }
                }
                else if (action == CaptureAction.Sequence)
                {
                    var writeStart = Stopwatch.GetTimestamp();
                    path = _writer.WriteScreenshotSequence(
                        sequenceFrames ?? Array.Empty<BufferedFrame>(),
                        clone,
                        sidecar,
                        captureDirectory,
                        screenshotFormat,
                        jpegQuality,
                        saveJson);
                    _profiler.Record("write", writeStart, _logger);

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

                            var postGrabStart = Stopwatch.GetTimestamp();
                            using var postFrame = Grab(config);
                            _profiler.Record("grab", postGrabStart, _logger);
                            if (postFrame is null)
                                break;
                            var stillStart = Stopwatch.GetTimestamp();
                            _writer.WriteSequenceStill(
                                path,
                                triggerTime,
                                postFrame.Timestamp,
                                postFrame.Bitmap,
                                screenshotFormat,
                                jpegQuality);
                            _profiler.Record("write", stillStart, _logger);
                            nextAt = nextAt.AddSeconds(sequenceIntervalSeconds);
                        }
                    }
                }
                else
                {
                    var writeStart = Stopwatch.GetTimestamp();
                    path = _writer.WriteScreenshot(
                        clone, sidecar, captureDirectory, screenshotFormat, jpegQuality, saveJson);
                    _profiler.Record("write", writeStart, _logger);
                }

                var displayName = action == CaptureAction.Sequence
                    ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : Path.GetFileName(path);
                _logger.Info($"Trigger '{triggerText}' fired ({sidecar.Action}) -> {path}");
                RaiseStatus("Triggered", $"{triggerText} -> {displayName}");
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

    private static readonly TimeSpan ProfileFlushInterval = TimeSpan.FromSeconds(30);

    private void MaybeFlushProfile(AppConfig config)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastProfileFlushTimestamp) / (double)Stopwatch.Frequency;
        if (elapsed < ProfileFlushInterval.TotalSeconds)
            return;

        _lastProfileFlushTimestamp = now;
        FlushProfile("30s", config);
    }

    private void FlushProfile(string windowLabel, AppConfig? config = null)
    {
        _lastProfileFlushTimestamp = Stopwatch.GetTimestamp();
        AppConfig cfg;
        if (config is not null)
            cfg = config;
        else
            lock (_configLock)
                cfg = _config;

        var pending = _ocrQueue?.PendingCount ?? 0;
        var context = PhaseProfiler.FormatCaptureContext(cfg, pending);
        _profiler.FlushTo(_logger, windowLabel, context);
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
