using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PayAttention.Capture;

public static class ImageHelpers
{
    public static Bitmap Downscale(Bitmap source, int maxWidth)
    {
        if (source.Width <= maxWidth)
            return (Bitmap)source.Clone();

        var ratio = (double)maxWidth / source.Width;
        var width = maxWidth;
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        return ScaleTo(source, width, height);
    }

    public static Bitmap Upscale(Bitmap source, int percent)
    {
        percent = Math.Clamp(percent, 100, 400);
        if (percent <= 100)
            return (Bitmap)source.Clone();

        var width = Math.Max(1, (int)Math.Round(source.Width * (percent / 100.0)));
        var height = Math.Max(1, (int)Math.Round(source.Height * (percent / 100.0)));
        return ScaleTo(source, width, height);
    }

    /// <summary>
    /// Convert to high-contrast grayscale to help OCR on dark terminals / faint text.
    /// </summary>
    public static Bitmap ToHighContrastGray(Bitmap source)
    {
        var src = EnsureArgb32(source, out var ownsSrc);
        var result = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var srcData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var height = src.Height;
            var width = src.Width;
            var srcStride = srcData.Stride;
            var dstStride = dstData.Stride;
            var row = new byte[Math.Abs(srcStride)];

            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(srcData.Scan0 + y * srcStride, row, 0, width * 4);
                for (var x = 0; x < width; x++)
                {
                    var i = x * 4;
                    var b = row[i];
                    var g = row[i + 1];
                    var r = row[i + 2];
                    var lum = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                    // Neon terminal colors (green/amber on black): prefer the bright channel.
                    var peak = Math.Max(r, Math.Max(g, b));
                    if (peak > lum + 25)
                        lum = (lum + peak) / 2;
                    lum = Math.Clamp((lum - 20) * 255 / 180, 0, 255);
                    row[i] = (byte)lum;
                    row[i + 1] = (byte)lum;
                    row[i + 2] = (byte)lum;
                    row[i + 3] = 255;
                }

                System.Runtime.InteropServices.Marshal.Copy(row, 0, dstData.Scan0 + y * dstStride, width * 4);
            }
        }
        finally
        {
            src.UnlockBits(srcData);
            result.UnlockBits(dstData);
            if (ownsSrc)
                src.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Locate regions that look like dark terminals with bright green/amber text,
    /// so small console overlays can be OCR'd without the rest of the desktop noise.
    /// </summary>
    public static List<Rectangle> FindTerminalLikeRegions(Bitmap source, int minSize = 120)
    {
        const int cell = 48;
        var cols = Math.Max(1, (source.Width + cell - 1) / cell);
        var rows = Math.Max(1, (source.Height + cell - 1) / cell);
        var hot = new bool[rows, cols];

        var src = EnsureArgb32(source, out var ownsSrc);
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var rowBuf = new byte[Math.Abs(stride)];
            for (var y = 0; y < src.Height; y += 2)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * stride, rowBuf, 0, src.Width * 4);
                var cellY = y / cell;
                for (var x = 0; x < src.Width; x += 2)
                {
                    var i = x * 4;
                    var b = rowBuf[i];
                    var g = rowBuf[i + 1];
                    var r = rowBuf[i + 2];
                    // Neon green / amber on near-black
                    var brightFg = (g > 110 && g >= r + 25 && g >= b + 15) ||
                                   (r > 120 && g > 80 && b < 70 && r >= g);
                    if (!brightFg)
                        continue;
                    hot[cellY, x / cell] = true;
                }
            }
        }
        finally
        {
            src.UnlockBits(data);
            if (ownsSrc)
                src.Dispose();
        }

        var regions = new List<Rectangle>();
        var visited = new bool[rows, cols];
        for (var cy = 0; cy < rows; cy++)
        {
            for (var cx = 0; cx < cols; cx++)
            {
                if (!hot[cy, cx] || visited[cy, cx])
                    continue;

                var minC = cx;
                var maxC = cx;
                var minR = cy;
                var maxR = cy;
                var queue = new Queue<(int R, int C)>();
                queue.Enqueue((cy, cx));
                visited[cy, cx] = true;
                while (queue.Count > 0)
                {
                    var (rr, cc) = queue.Dequeue();
                    minC = Math.Min(minC, cc);
                    maxC = Math.Max(maxC, cc);
                    minR = Math.Min(minR, rr);
                    maxR = Math.Max(maxR, rr);
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var ny = rr + dy;
                        var nx = cc + dx;
                        if (ny < 0 || nx < 0 || ny >= rows || nx >= cols)
                            continue;
                        if (!hot[ny, nx] || visited[ny, nx])
                            continue;
                        visited[ny, nx] = true;
                        queue.Enqueue((ny, nx));
                    }
                }

                var pad = cell;
                var x0 = Math.Max(0, minC * cell - pad);
                var y0 = Math.Max(0, minR * cell - pad);
                var x1 = Math.Min(source.Width, (maxC + 1) * cell + pad);
                var y1 = Math.Min(source.Height, (maxR + 1) * cell + pad);
                var w = x1 - x0;
                var h = y1 - y0;
                if (w >= minSize && h >= Math.Max(40, minSize / 3))
                    regions.Add(new Rectangle(x0, y0, w, h));
            }
        }

        // Prefer larger regions first; cap count for performance.
        return regions
            .OrderByDescending(r => r.Width * r.Height)
            .Take(4)
            .ToList();
    }

    public static Bitmap InvertColors(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;

        var matrix = new ColorMatrix(
        [
            [-1, 0, 0, 0, 0],
            [0, -1, 0, 0, 0],
            [0, 0, -1, 0, 0],
            [0, 0, 0, 1, 0],
            [1, 1, 1, 0, 1]
        ]);

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        g.DrawImage(
            source,
            new Rectangle(0, 0, source.Width, source.Height),
            0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return result;
    }

    /// <summary>
    /// Prepares a bitmap for OCR: downscale to maxWidth, optional upscale, optional invert.
    /// Caller owns the returned bitmap.
    /// </summary>
    public static Bitmap PrepareForOcr(Bitmap source, int maxWidth, int upscalePercent, bool invertColors)
    {
        Bitmap current = Downscale(source, maxWidth);
        try
        {
            if (upscalePercent > 100)
            {
                var upscaled = Upscale(current, upscalePercent);
                current.Dispose();
                current = upscaled;
            }

            if (invertColors)
            {
                var inverted = InvertColors(current);
                current.Dispose();
                current = inverted;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static Bitmap EnsureArgb32(Bitmap source, out bool ownsCopy)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb)
        {
            ownsCopy = false;
            return source;
        }

        ownsCopy = true;
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        return copy;
    }

    private static Bitmap ScaleTo(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        // Nearest-neighbor keeps glyph edges hard when magnifying tiny terminal fonts.
        var magnifying = width > source.Width || height > source.Height;
        g.InterpolationMode = magnifying
            ? InterpolationMode.NearestNeighbor
            : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = magnifying ? PixelOffsetMode.Half : PixelOffsetMode.Default;
        g.DrawImage(source, 0, 0, width, height);
        return result;
    }

    public static byte[] EncodeJpeg(Bitmap bitmap, long quality = 70L)
    {
        using var ms = new MemoryStream();
        var encoder = GetEncoder(ImageFormat.Jpeg);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(ms, encoder, parameters);
        return ms.ToArray();
    }

    public static void SavePng(Bitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, ImageFormat.Png);
    }

    public static void SaveJpeg(Bitmap bitmap, string path, long quality = 85L)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = GetEncoder(ImageFormat.Jpeg);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(path, encoder, parameters);
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == format.Guid);
    }
}
