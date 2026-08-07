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
    private int _targetFps;
    private DateTimeOffset _lastAccepted = DateTimeOffset.MinValue;

    public FrameRingBuffer(TimeSpan capacity, int targetFps)
    {
        Configure(capacity, targetFps);
    }

    public void Configure(TimeSpan capacity, int targetFps)
    {
        lock (_lock)
        {
            _capacity = capacity;
            _targetFps = Math.Clamp(targetFps, 1, 30);
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
            var minGap = TimeSpan.FromSeconds(1.0 / _targetFps);
            if (timestamp - _lastAccepted < minGap)
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
        var cutoff = now - _capacity;
        while (_frames.First is not null && _frames.First.Value.Timestamp < cutoff)
            _frames.RemoveFirst();
    }

    public void Dispose() => Clear();
}
