using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

public static class CaptureWriter
{
    public static string WriteScreenshot(
        Bitmap bitmap,
        CaptureSidecar sidecar,
        string captureDirectory,
        ScreenshotFormat format = ScreenshotFormat.Jpeg,
        int jpegQuality = 85,
        bool saveJson = false)
    {
        Directory.CreateDirectory(captureDirectory);
        var stamp = sidecar.Timestamp.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var safe = Sanitize(sidecar.TriggerText);
        var extension = format == ScreenshotFormat.Png ? ".png" : ".jpg";
        var baseName = $"{stamp}_{safe}_screenshot";
        var path = Path.Combine(captureDirectory, baseName + extension);

        if (format == ScreenshotFormat.Png)
            ImageHelpers.SavePng(bitmap, path);
        else
            ImageHelpers.SaveJpeg(bitmap, path, jpegQuality);

        sidecar.CaptureFile = Path.GetFileName(path) ?? baseName + extension;
        sidecar.ScreenshotFile = sidecar.CaptureFile;
        if (saveJson)
            File.WriteAllText(Path.Combine(captureDirectory, baseName + ".json"), JsonSerializer.Serialize(sidecar, JsonDefaults.Options));
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
        var stamp = sidecar.Timestamp.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var safe = Sanitize(sidecar.TriggerText);
        var baseName = $"{stamp}_{safe}_record";
        var extension = format == RecordFormat.Gif ? ".gif" : ".avi";
        var mediaPath = Path.Combine(captureDirectory, baseName + extension);

        if (stillFrame is not null)
        {
            var stillExt = stillFormat == ScreenshotFormat.Png ? ".png" : ".jpg";
            var stillPath = Path.Combine(captureDirectory, baseName + stillExt);
            if (stillFormat == ScreenshotFormat.Png)
                ImageHelpers.SavePng(stillFrame, stillPath);
            else
                ImageHelpers.SaveJpeg(stillFrame, stillPath, jpegQuality);
            sidecar.ScreenshotFile = Path.GetFileName(stillPath) ?? baseName + stillExt;
        }

        if (format == RecordFormat.Gif)
            GifWriter.Write(mediaPath, frames, fps);
        else
            MjpegAviWriter.Write(mediaPath, frames, fps);

        sidecar.CaptureFile = Path.GetFileName(mediaPath) ?? baseName + extension;
        if (saveJson)
            File.WriteAllText(Path.Combine(captureDirectory, baseName + ".json"), JsonSerializer.Serialize(sidecar, JsonDefaults.Options));
        return mediaPath;
    }

    /// <summary>
    /// Creates a per-event sequence folder, writes pre-trigger buffered stills and the
    /// trigger-moment frame. Returns the folder path. Caller may append post-trigger frames.
    /// </summary>
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
        var stamp = sidecar.Timestamp.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var safe = Sanitize(sidecar.TriggerText);
        var folderName = $"{stamp}_{safe}_sequence";
        var folder = Path.Combine(captureDirectory, folderName);
        Directory.CreateDirectory(folder);

        var triggerTime = sidecar.Timestamp;
        foreach (var frame in preFrames.OrderBy(f => f.Timestamp))
        {
            var offsetSec = (int)Math.Round((triggerTime - frame.Timestamp).TotalSeconds);
            if (offsetSec < 0)
                offsetSec = 0;
            // Skip a duplicate of the trigger instant; trigger frame is written as t+000.
            if (offsetSec == 0)
                continue;

            var path = Path.Combine(folder, SequenceFileName(-offsetSec, format));
            WriteJpegBytesOrBitmap(path, frame.JpegBytes, format, jpegQuality);
        }

        var triggerPath = Path.Combine(folder, SequenceFileName(0, format));
        if (format == ScreenshotFormat.Png)
            ImageHelpers.SavePng(triggerFrame, triggerPath);
        else
            ImageHelpers.SaveJpeg(triggerFrame, triggerPath, jpegQuality);

        sidecar.CaptureFile = folderName;
        sidecar.ScreenshotFile = Path.GetFileName(triggerPath);

        if (saveJson)
            File.WriteAllText(Path.Combine(folder, "sidecar.json"), JsonSerializer.Serialize(sidecar, JsonDefaults.Options));

        return folder;
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
        var offsetSec = (int)Math.Round((frameTime - triggerTime).TotalSeconds);
        var path = Path.Combine(sequenceFolder, SequenceFileName(offsetSec, format));
        if (format == ScreenshotFormat.Png)
            ImageHelpers.SavePng(bitmap, path);
        else
            ImageHelpers.SaveJpeg(bitmap, path, jpegQuality);
    }

    private static string SequenceFileName(int offsetSeconds, ScreenshotFormat format)
    {
        var ext = format == ScreenshotFormat.Png ? ".png" : ".jpg";
        var abs = Math.Abs(offsetSeconds);
        return offsetSeconds < 0 ? $"t-{abs:000}{ext}" : $"t+{abs:000}{ext}";
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
