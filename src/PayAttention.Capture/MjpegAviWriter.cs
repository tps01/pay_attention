using System.IO;
using System.Text;

namespace PayAttention.Capture;

/// <summary>
/// Minimal MJPEG AVI writer — dependency-free short recordings playable in common media players.
/// </summary>
public static class MjpegAviWriter
{
    public static void Write(string path, IReadOnlyList<BufferedFrame> frames, int fps)
    {
        if (frames.Count == 0)
            throw new InvalidOperationException("No frames available to write recording.");

        fps = Math.Clamp(fps, 1, 30);
        var width = frames[0].Width;
        var height = frames[0].Height;

        // Normalize dimensions: re-encode isn't available here; use first frame size as AVI header.
        // Frames may vary if resolution changed; we still write bytes as-is (players tolerate minor variance poorly),
        // so skip frames that don't match.
        var usable = frames.Where(f => f.Width == width && f.Height == height).ToList();
        if (usable.Count == 0)
            usable = frames.ToList();

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

        var frameCount = usable.Count;
        var microSecPerFrame = (int)Math.Round(1_000_000.0 / fps);
        var maxBytesPerSec = usable.Max(f => f.JpegBytes.Length) * fps;
        // LIST size includes the 4-byte list type ("movi"), same as hdrl/strl above.
        var moviSize = 4 + usable.Sum(f => 8 + AlignEven(f.JpegBytes.Length));
        var idx1Size = frameCount * 16;
        // hdrl: avih(64) + strl(strh 56 + strf 48 + junk?) + list overhead
        const int avihSize = 56;
        const int strhSize = 56;
        const int strfSize = 40; // BITMAPINFOHEADER
        var strlSize = 4 + (8 + strhSize) + (8 + strfSize);
        var hdrlSize = 4 + (8 + avihSize) + (8 + strlSize);
        var riffSize = 4 + (8 + hdrlSize) + (8 + moviSize) + (8 + idx1Size);

        // RIFF header
        WriteFourCC(bw, "RIFF");
        bw.Write(riffSize);
        WriteFourCC(bw, "AVI ");

        // hdrl
        WriteFourCC(bw, "LIST");
        bw.Write(hdrlSize);
        WriteFourCC(bw, "hdrl");

        // avih
        WriteFourCC(bw, "avih");
        bw.Write(avihSize);
        bw.Write(microSecPerFrame);
        bw.Write(maxBytesPerSec);
        bw.Write(0); // padding
        bw.Write(0x10); // AVIF_HASINDEX
        bw.Write(frameCount);
        bw.Write(0); // initial frames
        bw.Write(1); // streams
        bw.Write(0); // suggested buffer (0 = unknown)
        bw.Write(width);
        bw.Write(height);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);

        // strl
        WriteFourCC(bw, "LIST");
        bw.Write(strlSize);
        WriteFourCC(bw, "strl");

        // strh
        WriteFourCC(bw, "strh");
        bw.Write(strhSize);
        WriteFourCC(bw, "vids");
        WriteFourCC(bw, "MJPG");
        bw.Write(0); // flags
        bw.Write((short)0); // priority
        bw.Write((short)0); // language
        bw.Write(0); // initial frames
        bw.Write(1); // scale
        bw.Write(fps); // rate
        bw.Write(0); // start
        bw.Write(frameCount); // length
        bw.Write(usable.Max(f => f.JpegBytes.Length)); // suggested buffer
        bw.Write(0); // quality
        bw.Write(0); // sample size
        bw.Write((short)0); // left
        bw.Write((short)0); // top
        bw.Write((short)width);
        bw.Write((short)height);

        // strf (BITMAPINFOHEADER)
        WriteFourCC(bw, "strf");
        bw.Write(strfSize);
        bw.Write(40); // biSize
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1); // planes
        bw.Write((short)24); // bit count
        WriteFourCC(bw, "MJPG");
        bw.Write(0); // size image
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);

        // movi
        WriteFourCC(bw, "LIST");
        bw.Write(moviSize);
        WriteFourCC(bw, "movi");

        var indexEntries = new List<(int offset, int size)>(frameCount);
        var moviDataStart = fs.Position;

        foreach (var frame in usable)
        {
            var offset = (int)(fs.Position - moviDataStart + 4); // relative to 'movi' + 4? standard is from movi start
            // Common practice: offset from start of 'movi' FourCC
            offset = (int)(fs.Position - (moviDataStart - 4));

            WriteFourCC(bw, "00dc");
            var size = frame.JpegBytes.Length;
            bw.Write(size);
            bw.Write(frame.JpegBytes);
            if ((size & 1) == 1)
                bw.Write((byte)0);

            indexEntries.Add((offset, size));
        }

        // idx1
        WriteFourCC(bw, "idx1");
        bw.Write(idx1Size);
        foreach (var (offset, size) in indexEntries)
        {
            WriteFourCC(bw, "00dc");
            bw.Write(0x10); // AVIIF_KEYFRAME
            bw.Write(offset);
            bw.Write(size);
        }
    }

    private static int AlignEven(int n) => n + (n & 1);

    private static void WriteFourCC(BinaryWriter bw, string fourCc)
    {
        if (fourCc.Length != 4)
            throw new ArgumentException("FourCC must be 4 chars.", nameof(fourCc));
        bw.Write(Encoding.ASCII.GetBytes(fourCc));
    }
}
