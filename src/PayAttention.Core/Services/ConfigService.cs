using System.Text.Json;
using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public sealed class ConfigService
{
    public string AppDataDirectory { get; }
    public string ConfigPath { get; }
    public string DefaultCaptureDirectory { get; }
    public string LogDirectory { get; }

    public ConfigService()
    {
        AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pay_attention");
        ConfigPath = Path.Combine(AppDataDirectory, "config.json");
        DefaultCaptureDirectory = Path.Combine(AppDataDirectory, "captures");
        LogDirectory = Path.Combine(AppDataDirectory, "logs");
    }

    public AppConfig Load()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(DefaultCaptureDirectory);
        Directory.CreateDirectory(LogDirectory);

        if (!File.Exists(ConfigPath))
        {
            var defaults = CreateDefault();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonDefaults.Options) ?? CreateDefault();
            Normalize(config);
            return config;
        }
        catch
        {
            // Preserve the broken file so settings aren't silently destroyed.
            try
            {
                var backup = ConfigPath + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(ConfigPath, backup, overwrite: true);
            }
            catch
            {
                // ignore backup failures
            }

            var defaults = CreateDefault();
            Save(defaults);
            return defaults;
        }
    }

    public void Save(AppConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(config.CaptureDirectory);
        var json = JsonSerializer.Serialize(config, JsonDefaults.Options);
        File.WriteAllText(ConfigPath, json);
    }

    private AppConfig CreateDefault()
    {
        return new AppConfig
        {
            CaptureDirectory = DefaultCaptureDirectory,
            Triggers =
            [
                new TriggerConfig { Text = "Error", Match = MatchMode.Contains, Action = CaptureAction.Screenshot, Enabled = false },
                new TriggerConfig { Text = "Exception", Match = MatchMode.Contains, Action = CaptureAction.Record, Enabled = false }
            ]
        };
    }

    private void Normalize(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.CaptureDirectory))
            config.CaptureDirectory = DefaultCaptureDirectory;

        config.OcrIntervalMs = Math.Clamp(config.OcrIntervalMs, 250, 10_000);
        config.DebounceSeconds = Math.Clamp(config.DebounceSeconds, 1, 3600);
        config.RecordBufferSeconds = Math.Clamp(config.RecordBufferSeconds, 5, 120);
        config.RecordFps = Math.Clamp(config.RecordFps, 1, 15);
        config.RecordJpegQuality = Math.Clamp(config.RecordJpegQuality, 40, 95);
        config.OcrMaxWidth = Math.Clamp(config.OcrMaxWidth, 1600, 3840);
        config.OcrUpscalePercent = Math.Clamp(config.OcrUpscalePercent, 100, 400);
        config.RetentionMaxAgeDays = Math.Clamp(config.RetentionMaxAgeDays, 0, 3650);
        config.RetentionMaxFiles = Math.Clamp(config.RetentionMaxFiles, 0, 100_000);
        config.Triggers ??= [];
    }
}
