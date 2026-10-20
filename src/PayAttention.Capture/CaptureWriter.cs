using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

public static class CaptureWriter
{
    /// <summary>
    /// Full-monitor stills at JPEG quality (even 95) drop the small text Windows OCR
    /// read from the raw frame, so the saved file no longer contains the trigger.
    /// Those evidence stills are PNG. Smaller stills keep the requested JPEG quality.
    /// Video frames are unchanged.
    /// </summary>
    internal static bool EvidenceStillIsLossless(Bitmap bitmap) =>
        bitmap.Width * (long)bitmap.Height >= 1600L * 900L;

    internal static int EvidenceStillQuality(Bitmap bitmap, int requested)
    {
        var quality = requested;
        if (EvidenceStillIsLossless(bitmap))
            quality = Math.Max(quality, 95);
        return Math.Clamp(quality, 40, 95);
    }

    private static void SaveEvidenceStill(Bitmap bitmap, string path, ScreenshotFormat format, int jpegQuality)
    {
        if (format == ScreenshotFormat.Png || EvidenceStillIsLossless(bitmap))
            ImageHelpers.SavePng(bitmap, path);
        else
            ImageHelpers.SaveJpeg(bitmap, path, EvidenceStillQuality(bitmap, jpegQuality));
    }

    public static string WriteScreenshot(
        Bitmap bitmap,
        CaptureSidecar sidecar,
        string captureDirectory,
        ScreenshotFormat format = ScreenshotFormat.Jpeg,
        int jpegQuality = 85,
        bool saveJson = false)
    {
        Directory.CreateDirectory(captureDirectory);
        var safe = Sanitize(sidecar.TriggerText);
        var png = format == ScreenshotFormat.Png || EvidenceStillIsLossless(bitmap);
        var extension = png ? ".png" : ".jpg";
        sidecar.EventId = Guid.NewGuid();
        var baseName = CaptureOwnership.CreateName(sidecar.Timestamp, safe, "screenshot", sidecar.EventId);
        using var pending = new PendingWrite(Path.Combine(captureDirectory, baseName + ".pending"));
        var path = Path.Combine(captureDirectory, baseName + extension);

        SaveEvidenceStill(bitmap, path, format, jpegQuality);

        sidecar.CaptureFile = Path.GetFileName(path) ?? baseName + extension;
        sidecar.ScreenshotFile = sidecar.CaptureFile;
        if (saveJson)
            File.WriteAllText(Path.Combine(captureDirectory, baseName + ".json"), JsonSerializer.Serialize(sidecar, JsonDefaults.Options));
        WriteReceipt(Path.Combine(captureDirectory, baseName + ".capture.json"), sidecar, complete: true);
        return path;
    }

    public static string WriteRecording(
        IReadOnlyList<BufferedFrame> frames,
        Bitmap? stillFrame,
        CaptureSidecar sidecar,
        string captureDirectory,
        int fps,
        RecordFormat format,
        ScreenshotFormat stillFormat = ScreenshotFormat.Jpeg,
        int jpegQuality = 85,
        bool saveJson = false)
    {
        Directory.CreateDirectory(captureDirectory);
        var safe = Sanitize(sidecar.TriggerText);
        sidecar.EventId = Guid.NewGuid();
        var baseName = CaptureOwnership.CreateName(sidecar.Timestamp, safe, "record", sidecar.EventId);
        using var pending = new PendingWrite(Path.Combine(captureDirectory, baseName + ".pending"));
        var extension = format == RecordFormat.Gif ? ".gif" : ".avi";
        var mediaPath = Path.Combine(captureDirectory, baseName + extension);

        if (stillFrame is not null)
        {
            var stillPng = stillFormat == ScreenshotFormat.Png || EvidenceStillIsLossless(stillFrame);
            var stillExt = stillPng ? ".png" : ".jpg";
            var stillPath = Path.Combine(captureDirectory, baseName + stillExt);
            SaveEvidenceStill(stillFrame, stillPath, stillFormat, jpegQuality);
            sidecar.ScreenshotFile = Path.GetFileName(stillPath) ?? baseName + stillExt;
        }

        var playbackFps = PlaybackFps(frames, fps);
        if (format == RecordFormat.Gif)
            GifWriter.Write(mediaPath, frames, Math.Max(1, (int)Math.Round(playbackFps)));
        else
            MjpegAviWriter.Write(mediaPath, frames, playbackFps);

        sidecar.CaptureFile = Path.GetFileName(mediaPath) ?? baseName + extension;
        if (saveJson)
            File.WriteAllText(Path.Combine(captureDirectory, baseName + ".json"), JsonSerializer.Serialize(sidecar, JsonDefaults.Options));
        WriteReceipt(Path.Combine(captureDirectory, baseName + ".capture.json"), sidecar, complete: true);
        return mediaPath;
    }

    /// <summary>
    /// Container frame rate that plays <paramref name="frames"/> in about real time.
    /// <paramref name="requestedFps"/> is the capture target; the ring may be slower when
    /// screen capture is slower than that target.
    /// </summary>
    internal static double PlaybackFps(IReadOnlyList<BufferedFrame> frames, int requestedFps)
    {
        requestedFps = Math.Clamp(requestedFps, 1, 30);
        if (frames.Count < 2)
            return requestedFps;

        var first = frames[0].Timestamp;
        var last = frames[^1].Timestamp;
        if (last < first)
        {
            first = last;
            last = frames[0].Timestamp;
        }

        var span = last - first;
        if (span < TimeSpan.FromMilliseconds(1))
            return requestedFps;

        var fps = (frames.Count - 1) / span.TotalSeconds;
        if (double.IsNaN(fps) || double.IsInfinity(fps))
            return requestedFps;
        return Math.Clamp(fps, 0.01, 30);
    }

    public static string WriteScreenshotSequence(
        IReadOnlyList<BufferedFrame> preFrames,
        Bitmap triggerFrame,
        CaptureSidecar sidecar,
        string captureDirectory,
        ScreenshotFormat format = ScreenshotFormat.Jpeg,
        int jpegQuality = 85,
        bool saveJson = false)
    {
        Directory.CreateDirectory(captureDirectory);
        var safe = Sanitize(sidecar.TriggerText);
        sidecar.EventId = Guid.NewGuid();
        var folderName = CaptureOwnership.CreateName(sidecar.Timestamp, safe, "sequence", sidecar.EventId);
        var folder = Path.Combine(captureDirectory, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".pending"), string.Empty);

        try
        {
            var triggerTime = sidecar.Timestamp;
            foreach (var frame in preFrames.Where(f => f.Timestamp < triggerTime).OrderBy(f => f.Timestamp))
            {
                var offsetSec = (triggerTime - frame.Timestamp).TotalSeconds;
                if (offsetSec < 0)
                    offsetSec = 0;
                // Skip a duplicate of the trigger instant; trigger frame is written as t+000.
                if (offsetSec == 0)
                    continue;

                var path = Path.Combine(folder, SequenceFileName(-offsetSec, format));
                WriteJpegBytesOrBitmap(path, frame.JpegBytes, format, jpegQuality);
            }

            var triggerFormat = format == ScreenshotFormat.Jpeg && EvidenceStillIsLossless(triggerFrame)
                ? ScreenshotFormat.Png
                : format;
            var triggerPath = Path.Combine(folder, SequenceFileName(0, triggerFormat));
            SaveEvidenceStill(triggerFrame, triggerPath, format, jpegQuality);

            sidecar.CaptureFile = folderName;
            sidecar.ScreenshotFile = Path.GetFileName(triggerPath);

            if (saveJson)
                File.WriteAllText(Path.Combine(folder, "sidecar.json"), JsonSerializer.Serialize(sidecar, JsonDefaults.Options));
            WriteReceipt(Path.Combine(folder, "capture.json"), sidecar, complete: false);

            return folder;
        }
        catch
        {
            File.Delete(Path.Combine(folder, ".pending"));
            throw;
        }
    }

    internal static void CompleteSequence(string folder, bool complete)
    {
        try
        {
            var receipt = Path.Combine(folder, "capture.json");
            var data = JsonNode.Parse(File.ReadAllText(receipt))!;
            data["complete"] = complete;
            data["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O");
            data["frames"] = JsonSerializer.SerializeToNode(Directory.GetFiles(folder, "t*.*").Select(Path.GetFileName).OrderBy(f => f));
            WriteReceiptText(receipt, data.ToJsonString(JsonDefaults.Options));
        }
        finally { File.Delete(Path.Combine(folder, ".pending")); }
    }

    private static void WriteReceipt(string path, CaptureSidecar sidecar, bool complete) =>
        WriteReceiptText(path, JsonSerializer.Serialize(new
        {
            sidecar.EventId,
            sidecar.TriggerId,
            sidecar.Timestamp,
            sidecar.Action,
            sidecar.CaptureFile,
            sidecar.ScreenshotFile,
            sidecar.OcrEvidence,
            completedAt = complete ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            complete
        }, JsonDefaults.Options));

    private static void WriteReceiptText(string path, string json)
    {
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed class PendingWrite : IDisposable
    {
        private readonly string _path;
        public PendingWrite(string path)
        {
            _path = path;
            using var file = new FileStream(path, FileMode.CreateNew);
        }
        public void Dispose() => File.Delete(_path);
    }

    public static void WriteSequenceStill(
        string sequenceFolder,
        DateTimeOffset triggerTime,
        DateTimeOffset frameTime,
        Bitmap bitmap,
        ScreenshotFormat format = ScreenshotFormat.Jpeg,
        int jpegQuality = 85)
    {
        Directory.CreateDirectory(sequenceFolder);
        var offsetSec = (frameTime - triggerTime).TotalSeconds;
        var path = Path.Combine(sequenceFolder, SequenceFileName(offsetSec, format));
        if (format == ScreenshotFormat.Png)
            ImageHelpers.SavePng(bitmap, path);
        else
            ImageHelpers.SaveJpeg(bitmap, path, jpegQuality);
    }

    internal static void WriteSequenceFrame(string folder, DateTimeOffset triggerTime, BufferedFrame frame, ScreenshotFormat format, int quality)
    {
        var path = Path.Combine(folder, SequenceFileName((frame.Timestamp - triggerTime).TotalSeconds, format));
        WriteJpegBytesOrBitmap(path, frame.JpegBytes, format, quality);
    }

    private static string SequenceFileName(double offsetSeconds, ScreenshotFormat format)
    {
        var ext = format == ScreenshotFormat.Png ? ".png" : ".jpg";
        var milliseconds = (long)Math.Round(Math.Abs(offsetSeconds) * 1000);
        var fraction = milliseconds % 1000 == 0 ? "" : $"_{milliseconds % 1000:000}";
        return $"t{(offsetSeconds < 0 ? "-" : "+")}{milliseconds / 1000:000}{fraction}{ext}";
    }

    private static void WriteJpegBytesOrBitmap(string path, byte[] jpegBytes, ScreenshotFormat format, int jpegQuality)
    {
        if (format == ScreenshotFormat.Jpeg)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, jpegBytes);
            return;
        }

        using var ms = new MemoryStream(jpegBytes);
        using var bmp = new Bitmap(ms);
        ImageHelpers.SavePng(bmp, path);
    }

    private static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "trigger";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (ch is ' ' or '-' or '_')
                sb.Append('_');
        }

        var result = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "trigger" : result[..Math.Min(result.Length, 40)];
    }
}
