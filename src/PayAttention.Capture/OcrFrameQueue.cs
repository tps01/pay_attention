using System.Diagnostics;
using System.Drawing;
using System.Threading.Channels;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

/// <summary>
/// Why <see cref="OcrFrameQueue.TryEnqueueIfChanged"/> accepted or rejected a frame.
/// </summary>
internal enum OcrEnqueueDecision
{
    Enqueued,
    SkippedUnchanged,
    EnqueuedForcedResample,
    Dropped
}

/// <summary>
/// Bounded queue of screen frames awaiting OCR. Capture enqueues clones; OCR worker drains.
/// On overflow, oldest frames are disposed so the worker prefers fresher samples.
/// </summary>
internal sealed class OcrFrameQueue : IDisposable
{
    public const int DefaultCapacity = 4;

    /// <summary>
    /// Content hashes must be equal to suppress a frame; line changes must enqueue.
    /// </summary>
    public const int EnqueueMaxHammingDistance = 0;

    /// <summary>
    /// Re-OCR an unchanged scene after this long when enabled triggers remain unmatched
    /// (avoids silencing sticky text forever after a partial hit).
    /// </summary>
    public const int StaticSceneResampleSeconds = 10;

    private readonly Channel<OcrQueuedFrame> _channel;
    private readonly object _fingerprintLock = new();
    private ulong? _lastEnqueuedFingerprint;
    private string? _lastEnqueuedForeground;
    private DateTimeOffset? _forceResampleAfterUtc;
    private bool _disposed;

    public OcrFrameQueue(int capacity = DefaultCapacity)
    {
        var options = new BoundedChannelOptions(Math.Max(1, capacity))
        {
            // Writer may TryRead to drop-oldest and dispose; OCR worker also reads.
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        };
        _channel = Channel.CreateBounded<OcrQueuedFrame>(options);
    }

    /// <summary>
    /// Enqueues a clone of <paramref name="frame"/> when the scene changed enough,
    /// the foreground process changed, or a scheduled static resample is due.
    /// Returns false if skipped or the channel is completed.
    /// On overflow, oldest frames are dropped so the worker prefers fresher samples.
    /// </summary>
    public bool TryEnqueueIfChanged(
        CapturedFrame frame,
        string? foregroundProcess,
        ulong fingerprint) =>
        TryEnqueueIfChanged(frame, foregroundProcess, fingerprint, out _);

    /// <summary>
    /// Same as <see cref="TryEnqueueIfChanged(CapturedFrame, string?, ulong)"/>, and reports why.
    /// When <paramref name="profiler"/> and <paramref name="logger"/> are both set, records
    /// <c>enqueue.clone</c> (clone only when a frame is queued).
    /// </summary>
    public bool TryEnqueueIfChanged(
        CapturedFrame frame,
        string? foregroundProcess,
        ulong fingerprint,
        out OcrEnqueueDecision decision,
        PhaseProfiler? profiler = null,
        AppLogger? logger = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fg = ApplicationScopeEvaluator.NormalizeProcessName(foregroundProcess);
        var forcedResample = false;
        lock (_fingerprintLock)
        {
            var fgChanged = !string.Equals(_lastEnqueuedForeground, fg, StringComparison.OrdinalIgnoreCase);
            var sceneUnchanged = _lastEnqueuedFingerprint is ulong prev &&
                                 FrameFingerprint.IsSimilar(prev, fingerprint, EnqueueMaxHammingDistance);
            var forceDue = _forceResampleAfterUtc is DateTimeOffset deadline &&
                           DateTimeOffset.UtcNow >= deadline;

            if (sceneUnchanged && !fgChanged && !forceDue)
            {
                decision = OcrEnqueueDecision.SkippedUnchanged;
                return false;
            }

            forcedResample = sceneUnchanged && !fgChanged && forceDue;
        }

        var cloneStart = Stopwatch.GetTimestamp();
        var queued = new OcrQueuedFrame(
            (Bitmap)frame.Bitmap.Clone(),
            frame.Timestamp,
            frame.MonitorIndex,
            frame.MonitorName,
            frame.ScreenBounds,
            fingerprint,
            foregroundProcess,
            frame.Epoch,
            frame.RecordFrames,
            frame.SequenceFrames);

        while (!_channel.Writer.TryWrite(queued))
        {
            if (_channel.Reader.TryRead(out var oldest))
            {
                oldest.Dispose();
                continue;
            }

            // Completed or raced empty while "full" — drop this frame.
            queued.Dispose();
            if (profiler is not null && logger is not null)
                profiler.Record("enqueue.clone", cloneStart, logger);
            decision = OcrEnqueueDecision.Dropped;
            return false;
        }

        // Commit the gate only after the frame is queued. A failed write must not
        // look like this scene was already sampled.
        lock (_fingerprintLock)
        {
            _lastEnqueuedFingerprint = fingerprint;
            _lastEnqueuedForeground = fg;
            _forceResampleAfterUtc = null;
        }

        if (profiler is not null && logger is not null)
            profiler.Record("enqueue.clone", cloneStart, logger);
        decision = forcedResample
            ? OcrEnqueueDecision.EnqueuedForcedResample
            : OcrEnqueueDecision.Enqueued;
        return true;
    }

    /// <summary>
    /// Hamming distance from the last enqueued fingerprint, or null when none yet.
    /// </summary>
    public int? HammingVsLastEnqueued(ulong fingerprint)
    {
        lock (_fingerprintLock)
        {
            return _lastEnqueuedFingerprint is ulong prev
                ? FrameFingerprint.HammingDistance(prev, fingerprint)
                : null;
        }
    }

    /// <summary>Frames still waiting behind the one currently being OCR'd (best-effort).</summary>
    public int PendingCount
    {
        get
        {
            try { return _channel.Reader.Count; }
            catch { return 0; }
        }
    }

    public async ValueTask<OcrQueuedFrame?> ReadAsync(CancellationToken token)
    {
        try
        {
            // Drop-oldest on the capture thread can steal the item WaitToReadAsync observed.
            // Retry; null means the channel is completed and empty (worker shutdown).
            while (await _channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                if (_channel.Reader.TryRead(out var frame))
                    return frame;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Clear the enqueue fingerprint / FG so the next grab is sampled even if the
    /// screen looks unchanged (needed after a Thorough miss on a static fixture).
    /// </summary>
    public void AllowResample()
    {
        lock (_fingerprintLock)
        {
            _lastEnqueuedFingerprint = null;
            _lastEnqueuedForeground = null;
            _forceResampleAfterUtc = null;
        }
    }

    /// <summary>
    /// After <paramref name="delay"/>, allow one enqueue of an unchanged scene.
    /// Keeps the earliest pending deadline when called again.
    /// </summary>
    public void ScheduleResample(TimeSpan delay)
    {
        var due = DateTimeOffset.UtcNow + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        lock (_fingerprintLock)
        {
            if (_forceResampleAfterUtc is DateTimeOffset existing && existing <= due)
                return;
            _forceResampleAfterUtc = due;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();

    public void ClearPending()
    {
        while (_channel.Reader.TryRead(out var frame))
            frame.Dispose();
        AllowResample();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var leftover))
            leftover.Dispose();
    }
}

internal sealed class OcrQueuedFrame : IDisposable
{
    public OcrQueuedFrame(
        Bitmap bitmap,
        DateTimeOffset timestamp,
        int monitorIndex,
        string monitorName,
        Rectangle screenBounds,
        ulong fingerprint,
        string? foregroundProcess,
        long epoch = 0,
        IReadOnlyList<BufferedFrame>? recordFrames = null,
        IReadOnlyList<BufferedFrame>? sequenceFrames = null)
    {
        Bitmap = bitmap;
        Timestamp = timestamp;
        MonitorIndex = monitorIndex;
        MonitorName = monitorName;
        ScreenBounds = screenBounds;
        Fingerprint = fingerprint;
        ForegroundProcess = foregroundProcess;
        Epoch = epoch;
        RecordFrames = recordFrames;
        SequenceFrames = sequenceFrames;
    }

    public Bitmap Bitmap { get; }
    public DateTimeOffset Timestamp { get; }
    public int MonitorIndex { get; }
    public string MonitorName { get; }
    public Rectangle ScreenBounds { get; }
    public ulong Fingerprint { get; }
    public string? ForegroundProcess { get; }
    public long Epoch { get; }
    public IReadOnlyList<BufferedFrame>? RecordFrames { get; }
    public IReadOnlyList<BufferedFrame>? SequenceFrames { get; }

    public CapturedFrame ToCapturedFrame() => new()
    {
        Bitmap = Bitmap,
        Timestamp = Timestamp,
        MonitorIndex = MonitorIndex,
        MonitorName = MonitorName,
        ScreenBounds = ScreenBounds,
        Epoch = Epoch,
        RecordFrames = RecordFrames,
        SequenceFrames = SequenceFrames
    };

    public void Dispose() => Bitmap.Dispose();
}
