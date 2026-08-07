using System.Drawing;
using System.Text;
using System.Text.Json;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Capture;

public sealed class CaptureWriter
{
    public string WriteScreenshot(
        Bitmap bitmap,
        CaptureSidecar sidecar,
        string captureDirectory,
        ScreenshotFormat format = ScreenshotFormat.Jpeg,
        int jpegQuality = 85,
        bool saveJson = false)
    {
        Directory.CreateDirectory(captureDirectory);
        var stamp = sidecar.Timestamp.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
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

    public string WriteRecording(
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
        var stamp = sidecar.Timestamp.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");
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
