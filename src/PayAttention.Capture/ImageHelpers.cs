using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PayAttention.Capture;

/// <summary>
/// How a frame is prepared before Windows OCR.
/// Prep always produces disposable copies — never mutate bitmaps that will be saved as captures.
/// </summary>
public enum OcrPrepMode
{
    /// <summary>Color frame as-is (aside from scale/invert in PrepareForOcr).</summary>
    None = 0,
    /// <summary>Standard luma grayscale, no contrast stretch.</summary>
    Gray = 1,
    /// <summary>Grayscale then percentile-based contrast stretch.</summary>
    AdaptiveContrast = 2,
    /// <summary>Neon-biased high-contrast gray (dark terminal boost).</summary>
    NeonHighContrast = 3
}

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
    /// Standard Rec.601 luma grayscale with no contrast stretch. Caller owns the result.
    /// </summary>
    public static Bitmap ToGrayscale(Bitmap source)
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
                    var lum = (byte)Math.Clamp(
                        (int)(0.299 * row[i + 2] + 0.587 * row[i + 1] + 0.114 * row[i]),
                        0, 255);
                    row[i] = lum;
                    row[i + 1] = lum;
                    row[i + 2] = lum;
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
    /// Stretch grayscale (or color-as-luma) contrast using sampled low/high percentiles.
    /// Caller owns the result. Input is not modified.
    /// </summary>
    public static Bitmap StretchContrast(Bitmap source, int lowPercentile = 5, int highPercentile = 95)
    {
        lowPercentile = Math.Clamp(lowPercentile, 0, 49);
        highPercentile = Math.Clamp(highPercentile, 51, 100);

        var src = EnsureArgb32(source, out var ownsSrc);
        try
        {
            var (low, high, _) = SampleLumaStats(src, lowPercentile, highPercentile);
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
                var span = Math.Max(1, high - low);

                for (var y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(srcData.Scan0 + y * srcStride, row, 0, width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        var i = x * 4;
                        var lum = (int)(0.299 * row[i + 2] + 0.587 * row[i + 1] + 0.114 * row[i]);
                        var stretched = Math.Clamp((lum - low) * 255 / span, 0, 255);
                        row[i] = (byte)stretched;
                        row[i + 1] = (byte)stretched;
                        row[i + 2] = (byte)stretched;
                        row[i + 3] = 255;
                    }

                    System.Runtime.InteropServices.Marshal.Copy(row, 0, dstData.Scan0 + y * dstStride, width * 4);
                }
            }
            finally
            {
                src.UnlockBits(srcData);
                result.UnlockBits(dstData);
            }

            return result;
        }
        finally
        {
            if (ownsSrc)
                src.Dispose();
        }
    }

    /// <summary>
    /// Grayscale then adaptive percentile stretch. Caller owns the result.
    /// </summary>
    public static Bitmap ToAdaptiveContrastGray(Bitmap source)
    {
        using var gray = ToGrayscale(source);
        return StretchContrast(gray);
    }

    /// <summary>
    /// Convert to high-contrast grayscale to help OCR on dark terminals / faint text.
    /// Neon channels are boosted before a fixed stretch. Caller owns the result.
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
    /// Apply an OCR prep mode. Returns null when mode is <see cref="OcrPrepMode.None"/>
    /// (caller should use the original bitmap). Otherwise caller owns the returned bitmap.
    /// </summary>
    public static Bitmap? ApplyOcrPrep(Bitmap source, OcrPrepMode mode) =>
        mode switch
        {
            OcrPrepMode.Gray => ToGrayscale(source),
            OcrPrepMode.AdaptiveContrast => ToAdaptiveContrastGray(source),
            OcrPrepMode.NeonHighContrast => ToHighContrastGray(source),
            _ => null
        };

    /// <summary>
    /// Tile grid size for large-screen OCR: 3×3 on big frames, else 2×2.
    /// </summary>
    public static int ChooseOcrTileGrid(int width, int height) =>
        width >= 2560 || height >= 1440 ? 3 : 2;

    /// <summary>
    /// Sampled luma low/high percentiles and mean (0–255). Used by stretch and tests.
    /// </summary>
    public static (int Low, int High, double Mean) SampleLumaStats(
        Bitmap source,
        int lowPercentile = 5,
        int highPercentile = 95)
    {
        var src = EnsureArgb32(source, out var ownsSrc);
        try
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var row = new byte[Math.Abs(stride)];
                var hist = new int[256];
                long sum = 0;
                var count = 0;
                // Subsample for speed on large frames.
                var stepY = Math.Max(1, src.Height / 240);
                var stepX = Math.Max(1, src.Width / 240);

                for (var y = 0; y < src.Height; y += stepY)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * stride, row, 0, src.Width * 4);
                    for (var x = 0; x < src.Width; x += stepX)
                    {
                        var i = x * 4;
                        var lum = (int)(0.299 * row[i + 2] + 0.587 * row[i + 1] + 0.114 * row[i]);
                        lum = Math.Clamp(lum, 0, 255);
                        hist[lum]++;
                        sum += lum;
                        count++;
                    }
                }

                if (count == 0)
                    return (0, 255, 0);

                var mean = sum / (double)count;
                var low = PercentileFromHist(hist, count, lowPercentile);
                var high = PercentileFromHist(hist, count, highPercentile);
                if (high <= low)
                    high = Math.Min(255, low + 1);
                return (low, high, mean);
            }
            finally
            {
                src.UnlockBits(data);
            }
        }
        finally
        {
            if (ownsSrc)
                src.Dispose();
        }
    }

    private static int PercentileFromHist(int[] hist, int count, int percentile)
    {
        var target = Math.Max(1, (int)Math.Ceiling(count * (percentile / 100.0)));
        var seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += hist[v];
            if (seen >= target)
                return v;
        }

        return 255;
    }

    /// <summary>
    /// Locate regions that look like dark terminals with bright green/amber text,
    /// so small console overlays can be OCR'd without the rest of the desktop noise.
    /// </summary>
    public static List<Rectangle> FindTerminalLikeRegions(
        Bitmap source,
        int minSize = 120,
        int maxRegions = 4)
    {
        maxRegions = Math.Clamp(maxRegions, 0, 32);
        if (maxRegions == 0)
            return [];

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
                    // Neon green / amber / cyan / bright white on near-black (dimmer greens too).
                    var brightFg =
                        (g > 85 && g >= r + 18 && g >= b + 10) ||
                        (r > 120 && g > 80 && b < 70 && r >= g) ||
                        (b > 100 && g > 90 && b >= r + 15) ||
                        (r > 160 && g > 160 && b > 160 && Math.Min(r, Math.Min(g, b)) > 140);
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
            .Take(maxRegions)
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
            new Rectangle(0, 0, result.Width, result.Height),
            0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return result;
    }

    /// <summary>
    /// Prepares a bitmap for OCR: downscale to maxWidth, optional upscale, optional invert,
    /// then hard-cap width at <paramref name="absoluteMaxWidth"/> (0 = no hard cap).
    /// Caller owns the returned bitmap. Does not mutate <paramref name="source"/>.
    /// </summary>
    public static Bitmap PrepareForOcr(
        Bitmap source,
        int maxWidth,
        int upscalePercent,
        bool invertColors,
        int absoluteMaxWidth = 0)
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

            if (absoluteMaxWidth > 0 && current.Width > absoluteMaxWidth)
            {
                var capped = Downscale(current, absoluteMaxWidth);
                current.Dispose();
                current = capped;
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

    /// <summary>
    /// Crops <paramref name="absoluteRegion"/> out of a bitmap whose pixels cover
    /// <paramref name="sourceScreenBounds"/> in screen coordinates. Returns null when
    /// there is no overlap. Caller owns the result.
    /// </summary>
    public static Bitmap? CropAbsoluteRegion(Bitmap source, Rectangle sourceScreenBounds, Rectangle absoluteRegion)
    {
        if (source.Width < 1 || source.Height < 1)
            return null;

        var overlap = Rectangle.Intersect(absoluteRegion, sourceScreenBounds);
        if (overlap.Width < 1 || overlap.Height < 1)
            return null;

        var local = new Rectangle(
            overlap.X - sourceScreenBounds.X,
            overlap.Y - sourceScreenBounds.Y,
            overlap.Width,
            overlap.Height);

        // Clamp to bitmap pixels (virtual desktop grab can differ by a pixel at edges).
        local.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (local.Width < 1 || local.Height < 1)
            return null;

        return source.Clone(local, source.PixelFormat);
    }

    /// <summary>
    /// Bitmap-local crops of visible windows to OCR before a full-monitor pass.
    /// Near-fullscreen windows stay with that pass. Caller order is Z-order, and
    /// that order is kept so the topmost fixture is OCR'd before smaller leftovers.
    /// </summary>
    public static IReadOnlyList<Rectangle> SelectFrozenWindowCrops(
        IReadOnlyList<Rectangle> windowScreenBounds,
        Rectangle screenBounds,
        int bitmapWidth,
        int bitmapHeight,
        int maxWindows)
    {
        if (maxWindows < 1 || bitmapWidth < 1 || bitmapHeight < 1 || windowScreenBounds.Count == 0)
            return [];

        var bitmapBounds = new Rectangle(0, 0, bitmapWidth, bitmapHeight);
        var frameArea = (long)bitmapWidth * bitmapHeight;
        var crops = new List<Rectangle>(windowScreenBounds.Count);
        foreach (var window in windowScreenBounds)
        {
            var overlap = Rectangle.Intersect(window, screenBounds);
            if (overlap.Width < 120 || overlap.Height < 80)
                continue;

            var local = new Rectangle(
                overlap.X - screenBounds.X,
                overlap.Y - screenBounds.Y,
                overlap.Width,
                overlap.Height);
            local.Intersect(bitmapBounds);
            if (local.Width < 120 || local.Height < 80)
                continue;

            var area = (long)local.Width * local.Height;
            if (area * 100 >= frameArea * 45)
                continue;

            crops.Add(local);
        }

        return crops.Take(maxWindows).ToList();
    }

    /// <summary>Isolate individual lines in dark terminal panes before OCR groups them with noise.</summary>
    public static IReadOnlyList<Rectangle> FindTerminalTextLines(Bitmap bitmap, int maxLines = 12)
    {
        var source = EnsureArgb32(bitmap, out var owned);
        var lines = new List<Rectangle>();
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[source.Width * 4];
            var start = -1;
            var lineLeft = source.Width;
            var lineRight = 0;
            for (var y = 0; y <= source.Height && lines.Count < maxLines; y++)
            {
                var ink = 0;
                var dark = 0;
                var inkLeft = source.Width;
                var inkRight = 0;
                if (y < source.Height)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    // Exclude window borders and vertical scrollbars: otherwise their
                    // bright pixels join every text row into one continuous region.
                    var left = Math.Min(4, source.Width / 10);
                    var right = Math.Max(left, source.Width - Math.Min(24, source.Width / 10));
                    for (var x = left; x < right; x++)
                    {
                        var i = x * 4;
                        var peak = Math.Max(row[i], Math.Max(row[i + 1], row[i + 2]));
                        if (peak >= 80)
                        {
                            ink++;
                            inkLeft = Math.Min(inkLeft, x);
                            inkRight = Math.Max(inkRight, x + 1);
                        }
                        if (peak < 64) dark++;
                    }
                }
                var textRow = ink >= 3 && dark > source.Width / 2;
                if (textRow && start < 0) start = y;
                if (textRow)
                {
                    lineLeft = Math.Min(lineLeft, inkLeft);
                    lineRight = Math.Max(lineRight, inkRight);
                }
                if (!textRow && start >= 0)
                {
                    if (y - start >= 4 && y - start <= 48)
                    {
                        var top = Math.Max(0, start - 4);
                        var left = Math.Max(0, lineLeft - 4);
                        lines.Add(new Rectangle(left, top, Math.Min(source.Width, lineRight + 4) - left,
                            Math.Min(source.Height, y + 4) - top));
                    }
                    start = -1;
                    lineLeft = source.Width;
                    lineRight = 0;
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
            if (owned) source.Dispose();
        }
        return lines;
    }

    /// <summary>Alternative crop excluding the last isolated glyph (e.g. a terminal delimiter).</summary>
    public static Rectangle? TrimTerminalLineEnding(Bitmap line)
    {
        bool HasInk(int x)
        {
            for (var y = 0; y < line.Height; y++)
            {
                var pixel = line.GetPixel(x, y);
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) >= 80) return true;
            }
            return false;
        }
        var right = line.Width - 1;
        while (right >= 0 && !HasInk(right)) right--;
        var start = right;
        while (start >= 0 && HasInk(start)) start--;
        if (start < line.Height * 2 || right - start > line.Height * 2) return null;
        return new Rectangle(0, 0, start + 1, line.Height);
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
