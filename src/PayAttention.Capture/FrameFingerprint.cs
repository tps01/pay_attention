using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PayAttention.Capture;

/// <summary>
/// Perceptual-ish average hash of a frame for "has the screen meaningfully changed?" checks.
/// Heavy downscale ignores mouse-sized / clock-tick noise.
/// </summary>
public static class FrameFingerprint
{
    public const int GridSize = 8;
    /// <summary>Max Hamming distance (of 64 bits) treated as the same scene.</summary>
    public const int DefaultMaxDistance = 10;

    public static ulong ComputeAverageHash(Bitmap source)
    {
        using var tiny = DownscaleToGrayGrid(source, GridSize);
        var samples = new double[GridSize * GridSize];
        var rect = new Rectangle(0, 0, GridSize, GridSize);
        var data = tiny.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var row = new byte[Math.Abs(stride)];
            double sum = 0;
            for (var y = 0; y < GridSize; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, GridSize * 4);
                for (var x = 0; x < GridSize; x++)
                {
                    var i = x * 4;
                    var lum = 0.299 * row[i + 2] + 0.587 * row[i + 1] + 0.114 * row[i];
                    samples[y * GridSize + x] = lum;
                    sum += lum;
                }
            }

            var mean = sum / samples.Length;
            ulong hash = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                if (samples[i] >= mean)
                    hash |= 1UL << i;
            }

            return hash;
        }
        finally
        {
            tiny.UnlockBits(data);
        }
    }

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static bool IsSimilar(ulong a, ulong b, int maxDistance = DefaultMaxDistance) =>
        HammingDistance(a, b) <= maxDistance;

    private static Bitmap DownscaleToGrayGrid(Bitmap source, int size)
    {
        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return result;
    }
}
