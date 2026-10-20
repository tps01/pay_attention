using System.Drawing;
using System.Text;
using System.Text.Json;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Harness;

internal static class MediaProof
{
    public static bool Ready(string? path)
    {
        try { var proof = Read(path!); proof.Receipt.Dispose(); return true; }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or System.Runtime.InteropServices.ExternalException)
        { return false; }
    }

    private static (JsonDocument Receipt, string Still) Read(string path)
    {
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        if (File.Exists(stem + ".pending")) throw new IOException("Capture still pending");
        using var receipt = JsonDocument.Parse(File.ReadAllText(stem + ".capture.json"));
        var root = receipt.RootElement;
        if (!root.GetProperty("complete").GetBoolean() || root.GetProperty("captureFile").GetString() != Path.GetFileName(path))
            throw new InvalidOperationException("Incomplete or mismatched receipt");
        var stillName = root.GetProperty("screenshotFile").GetString()!;
        if (Path.GetFileName(stillName) != stillName) throw new InvalidOperationException("Invalid still path");
        var still = Path.Combine(Path.GetDirectoryName(path)!, stillName);
        using (var image = new Bitmap(still)) _ = image.GetPixel(image.Width - 1, image.Height - 1);
        if (Path.GetExtension(path).Equals(".avi", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "AVI ")
                throw new InvalidOperationException("Invalid AVI");
            if (BitConverter.ToUInt32(bytes, 4) + 8L != bytes.Length || CountFrames(bytes, 12, bytes.Length) == 0)
                throw new InvalidOperationException("Incomplete AVI");
        }
        else if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            using var gif = Image.FromFile(path);
            foreach (var dimension in gif.FrameDimensionsList)
                for (var i = 0; i < gif.GetFrameCount(new System.Drawing.Imaging.FrameDimension(dimension)); i++)
                    gif.SelectActiveFrame(new System.Drawing.Imaging.FrameDimension(dimension), i);
        }
        return (JsonDocument.Parse(root.GetRawText()), still);
    }

    private static int CountFrames(byte[] bytes, int start, int end)
    {
        var count = 0;
        for (var pos = start; pos < end;)
        {
            if (pos + 8 > end) throw new InvalidOperationException("Truncated chunk");
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = checked((int)BitConverter.ToUInt32(bytes, pos + 4));
            var next = checked(pos + 8 + size);
            if (next > end) throw new InvalidOperationException("Truncated payload");
            if (id == "LIST") count += CountFrames(bytes, pos + 12, next);
            if (id == "00dc")
            {
                using var stream = new MemoryStream(bytes, pos + 8, size);
                using var frame = new Bitmap(stream);
                _ = frame.GetPixel(frame.Width - 1, frame.Height - 1);
                count++;
            }
            pos = next + (size & 1);
        }
        return count;
    }

    public static void Require(string path, AppConfig config)
    {
        var (receipt, still) = Read(path);
        using (receipt)
        {
            var id = receipt.RootElement.GetProperty("triggerId").GetGuid();
            var trigger = config.Triggers.Single(t => t.Id == id && t.Enabled);
            using var bitmap = new Bitmap(still);
            using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(Path.GetDirectoryName(path)!, "proof-logs")) { MinLevel = LogLevel.Verbose });
            var evidence = receipt.RootElement.TryGetProperty("ocrEvidence", out var metadata) && metadata.ValueKind != JsonValueKind.Null
                ? metadata.Deserialize<OcrEvidence>(JsonDefaults.Options) : null;
            if (!monitor.VerifySavedImageAsync(bitmap, trigger, evidence).GetAwaiter().GetResult())
                throw new InvalidOperationException($"Saved pixels do not match trigger {id}: {path}");
        }
    }
}
