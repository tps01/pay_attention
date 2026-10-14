using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PayAttention.Capture;

public sealed class OcrService
{
    private readonly OcrEngine? _engine;
    /// <summary>Grow-only packed BGRA scratch for ToSoftwareBitmap; reused across RecognizeAsync calls.</summary>
    private byte[] _packedBuffer = Array.Empty<byte>();
    private readonly object _bufferLock = new();

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"));
    }

    public bool IsAvailable => _engine is not null;

    public async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        if (_engine is null)
            return string.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        using var softwareBitmap = ToSoftwareBitmap(bitmap);
        var result = await _engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return result.Text ?? string.Empty;
    }

    private SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        Bitmap? owned = null;
        var source = bitmap;
        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
        {
            owned = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(owned);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
            source = owned;
        }

        try
        {
            var rect = new Rectangle(0, 0, source.Width, source.Height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var height = data.Height;
                var width = data.Width;
                var needed = width * height * 4;

                lock (_bufferLock)
                {
                    if (_packedBuffer.Length < needed)
                        _packedBuffer = new byte[needed];

                    var packed = _packedBuffer;
                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * stride, packed, y * width * 4, width * 4);
                    }

                    var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
                    // Slice to exact size — CopyFromBuffer rejects a longer IBuffer.
                    softwareBitmap.CopyFromBuffer(packed.AsBuffer(0, needed));
                    return softwareBitmap;
                }
            }
            finally
            {
                source.UnlockBits(data);
            }
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
