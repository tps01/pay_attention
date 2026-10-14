using System.Diagnostics;
using System.Drawing;
using System.Threading.Channels;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

/// <summary>
/// Bounded queue of screen frames awaiting OCR. Capture enqueues clones; OCR worker drains.
/// On overflow, oldest frames are disposed so the worker prefers fresher samples.
/// </summary>
internal sealed class OcrFrameQueue : IDisposable
{
    public const int DefaultCapacity = 4;

    /// <summary>
    /// Stricter than fire-gate similarity: scrolling a single console line must still enqueue.
    /// Default Hamming≤10 treats most line-scrolls as "same scene" after 8×8 downscale.
    /// </summary>
    public const int EnqueueMaxHammingDistance = 2;

    private readonly Channel<OcrQueuedFrame> _channel;
    private readonly object _fingerprintLock = new();
    private ulong? _lastEnqueuedFingerprint;
    private string? _lastEnqueuedForeground;
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
    /// or when the foreground process changed (same pixels, different Selected triggers).
    /// Returns false if skipped or the channel is completed.
    /// On overflow, oldest frames are dropped so the worker prefers fresher samples.
    /// When <paramref name="profiler"/> is provided, records <c>enqueue.hash</c> and
    /// <c>enqueue.clone</c> (clone only when a frame is queued).
    /// </summary>
    public bool TryEnqueueIfChanged(
        CapturedFrame frame,
        string? foregroundProcess,
        PhaseProfiler? profiler = null,
        AppLogger? logger = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hashStart = Stopwatch.GetTimestamp();
        var fingerprint = FrameFingerprint.ComputeAverageHash(frame.Bitmap);
        if (profiler is not null && logger is not null)
            profiler.Record("enqueue.hash", hashStart, logger);

        var fg = ApplicationScopeEvaluator.NormalizeProcessName(foregroundProcess);
        lock (_fingerprintLock)
        {
            var fgChanged = !string.Equals(_lastEnqueuedForeground, fg, StringComparison.OrdinalIgnoreCase);
            var sceneUnchanged = _lastEnqueuedFingerprint is ulong prev &&
                                 FrameFingerprint.IsSimilar(prev, fingerprint, EnqueueMaxHammingDistance);
            if (sceneUnchanged && !fgChanged)
                return false;

            _lastEnqueuedFingerprint = fingerprint;
            _lastEnqueuedForeground = fg;
        }

        var cloneStart = Stopwatch.GetTimestamp();
        var queued = new OcrQueuedFrame(
            (Bitmap)frame.Bitmap.Clone(),
            frame.Timestamp,
            frame.MonitorIndex,
            frame.MonitorName,
            frame.ScreenBounds,
            fingerprint,
            foregroundProcess);

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
            return false;
        }

        if (profiler is not null && logger is not null)
            profiler.Record("enqueue.clone", cloneStart, logger);
        return true;
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
            if (await _channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
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
        }
    }

    public void Complete() => _channel.Writer.TryComplete();

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
        string? foregroundProcess)
    {
        Bitmap = bitmap;
        Timestamp = timestamp;
        MonitorIndex = monitorIndex;
        MonitorName = monitorName;
        ScreenBounds = screenBounds;
        Fingerprint = fingerprint;
        ForegroundProcess = foregroundProcess;
    }

    public Bitmap Bitmap { get; }
    public DateTimeOffset Timestamp { get; }
    public int MonitorIndex { get; }
    public string MonitorName { get; }
    public Rectangle ScreenBounds { get; }
    public ulong Fingerprint { get; }
    public string? ForegroundProcess { get; }

    public CapturedFrame ToCapturedFrame() => new()
    {
        Bitmap = Bitmap,
        Timestamp = Timestamp,
        MonitorIndex = MonitorIndex,
        MonitorName = MonitorName,
        ScreenBounds = ScreenBounds
    };

    public void Dispose() => Bitmap.Dispose();
}
