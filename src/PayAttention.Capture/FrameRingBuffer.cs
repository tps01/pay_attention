using System.Drawing;

namespace PayAttention.Capture;

public sealed class BufferedFrame
{
    public required byte[] JpegBytes { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public sealed class FrameRingBuffer : IDisposable
{
    private readonly LinkedList<BufferedFrame> _frames = new();
    private readonly object _lock = new();
    private TimeSpan _capacity;
    private TimeSpan _minGap;
    private DateTimeOffset _lastAccepted = DateTimeOffset.MinValue;

    public FrameRingBuffer(TimeSpan capacity, int targetFps)
    {
        Configure(capacity, targetFps);
    }

    public FrameRingBuffer(TimeSpan capacity, TimeSpan minGap)
    {
        Configure(capacity, minGap);
    }

    public void Configure(TimeSpan capacity, int targetFps)
    {
        var fps = Math.Clamp(targetFps, 1, 30);
        Configure(capacity, TimeSpan.FromSeconds(1.0 / fps));
    }

    public void Configure(TimeSpan capacity, TimeSpan minGap)
    {
        lock (_lock)
        {
            _capacity = capacity < TimeSpan.Zero ? TimeSpan.Zero : capacity;
            _minGap = minGap < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : minGap;
            TrimUnlocked(DateTimeOffset.UtcNow);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
            _lastAccepted = DateTimeOffset.MinValue;
        }
    }

    public void TryAdd(Bitmap bitmap, DateTimeOffset timestamp, long jpegQuality = 60L)
    {
        lock (_lock)
        {
            if (_capacity <= TimeSpan.Zero)
                return;

            if (timestamp - _lastAccepted < _minGap)
                return;

            var jpeg = ImageHelpers.EncodeJpeg(bitmap, jpegQuality);
            _frames.AddLast(new BufferedFrame
            {
                JpegBytes = jpeg,
                Timestamp = timestamp,
                Width = bitmap.Width,
                Height = bitmap.Height
            });
            _lastAccepted = timestamp;
            TrimUnlocked(timestamp);
        }
    }

    public IReadOnlyList<BufferedFrame> Snapshot()
    {
        lock (_lock)
        {
            return _frames.ToList();
        }
    }

    private void TrimUnlocked(DateTimeOffset now)
    {
        if (_capacity <= TimeSpan.Zero)
        {
            _frames.Clear();
            return;
        }

        var cutoff = now - _capacity;
        while (_frames.First is not null && _frames.First.Value.Timestamp < cutoff)
            _frames.RemoveFirst();
    }

    public void Dispose() => Clear();
}
