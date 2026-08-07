using System.Drawing;
using System.Drawing.Imaging;

namespace PayAttention.Capture;

/// <summary>
/// Animated GIF writer from buffered JPEG frames (dependency-free via System.Drawing).
/// </summary>
public static class GifWriter
{
    public static void Write(string path, IReadOnlyList<BufferedFrame> frames, int fps)
    {
        if (frames.Count == 0)
            throw new InvalidOperationException("No frames available to write GIF.");

        fps = Math.Clamp(fps, 1, 30);
        var delayCs = Math.Max(1, (int)Math.Round(100.0 / fps));

        var encoder = ImageCodecInfo.GetImageDecoders().FirstOrDefault(c => c.FormatID == ImageFormat.Gif.Guid)
            ?? throw new InvalidOperationException("GIF encoder not available.");

        Bitmap? animation = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

            for (var i = 0; i < frames.Count; i++)
            {
                using var jpegStream = new MemoryStream(frames[i].JpegBytes);
                using var source = Image.FromStream(jpegStream);
                var frame = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(frame))
                    g.DrawImage(source, 0, 0, source.Width, source.Height);

                SetGifTiming(frame, delayCs);

                if (i == 0)
                {
                    animation = frame;
                    using var firstParams = new EncoderParameters(1);
                    firstParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
                    animation.Save(fs, encoder, firstParams);
                }
                else
                {
                    using var frameParams = new EncoderParameters(1);
                    frameParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
                    animation!.SaveAdd(frame, frameParams);
                    frame.Dispose();
                }
            }

            using var flushParams = new EncoderParameters(1);
            flushParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
            animation!.SaveAdd(flushParams);
        }
        finally
        {
            animation?.Dispose();
        }
    }

    private static void SetGifTiming(Image image, int delayCs)
    {
        // Clone a PropertyItem from a tiny GIF (PropertyItem has no public ctor).
        using var tiny = new Bitmap(1, 1);
        using var ms = new MemoryStream();
        tiny.Save(ms, ImageFormat.Gif);
        ms.Position = 0;
        using var template = Image.FromStream(ms);
        var proto = template.PropertyItems.FirstOrDefault()
            ?? throw new InvalidOperationException("Cannot create GIF PropertyItem.");

        var delay = (PropertyItem)proto.GetType()
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(proto, null)!;
        delay.Id = 0x5100; // FrameDelay (1/100 sec)
        delay.Type = 4;
        delay.Len = 4;
        delay.Value = BitConverter.GetBytes(delayCs);
        image.SetPropertyItem(delay);

        var loop = (PropertyItem)proto.GetType()
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(proto, null)!;
        loop.Id = 0x5101; // LoopCount (0 = infinite)
        loop.Type = 3;
        loop.Len = 2;
        loop.Value = BitConverter.GetBytes((short)0);
        image.SetPropertyItem(loop);
    }
}
