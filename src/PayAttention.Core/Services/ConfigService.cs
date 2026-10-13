using System.Text.Json;
using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public sealed class ConfigLoadResult
{
    public required AppConfig Config { get; init; }
    public bool ResetToDefaults { get; init; }
    public string? CorruptBackupPath { get; init; }
}

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

    public AppConfig Load() => LoadDetailed().Config;

    public ConfigLoadResult LoadDetailed()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(DefaultCaptureDirectory);
        Directory.CreateDirectory(LogDirectory);

        if (!File.Exists(ConfigPath))
        {
            var defaults = CreateDefault();
            Save(defaults);
            return new ConfigLoadResult { Config = defaults };
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonDefaults.Options) ?? CreateDefault();
            // Upgrade older configs that never had an applications key.
            if (json.IndexOf("\"applications\"", StringComparison.OrdinalIgnoreCase) < 0)
                config.Applications = ApplicationScopeEvaluator.CreateDefaultApplications();
            Normalize(config);
            return new ConfigLoadResult { Config = config };
        }
        catch
        {
            string? backup = null;
            try
            {
                backup = ConfigPath + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(ConfigPath, backup, overwrite: true);
            }
            catch
            {
                // ignore backup failures
            }

            var defaults = CreateDefault();
            Save(defaults);
            return new ConfigLoadResult
            {
                Config = defaults,
                ResetToDefaults = true,
                CorruptBackupPath = backup
            };
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

    public void ExportTo(string path, AppConfig config)
    {
        Normalize(config);
        // Portable export: do not bake in this machine's absolute capture path.
        var portable = Clone(config);
        portable.CaptureDirectory = string.Empty;
        var json = JsonSerializer.Serialize(portable, JsonDefaults.Options);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    public AppConfig ImportFrom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Config file not found.", path);

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Config file did not contain a valid configuration.");
        // Always land captures on this machine's default unless the imported path already exists.
        if (string.IsNullOrWhiteSpace(config.CaptureDirectory) ||
            !Directory.Exists(config.CaptureDirectory))
            config.CaptureDirectory = DefaultCaptureDirectory;
        Normalize(config);
        return config;
    }

    private static AppConfig Clone(AppConfig source) =>
        JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(source, JsonDefaults.Options),
            JsonDefaults.Options)!;

    private AppConfig CreateDefault()
    {
        return new AppConfig
        {
            CaptureDirectory = DefaultCaptureDirectory,
            ApplicationScope = ApplicationScopeMode.AllMinusBlacklisted,
            Triggers =
            [
                // Distinctive placeholders — disabled. Avoid common words like "Error".
                new TriggerConfig
                {
                    Text = "PAYATTN_EXAMPLE",
                    Match = MatchMode.Contains,
                    Action = CaptureAction.Screenshot,
                    OcrMode = OcrSpeedMode.Fast,
                    Enabled = false
                }
            ],
            Applications = ApplicationScopeEvaluator.CreateDefaultApplications()
        };
    }

    private void Normalize(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.CaptureDirectory))
            config.CaptureDirectory = DefaultCaptureDirectory;

        config.OcrIntervalMs = Math.Clamp(config.OcrIntervalMs, 250, 10_000);
        config.FastMatchThresholdPercent = Math.Clamp(config.FastMatchThresholdPercent, 50, 100);
        config.DebounceSeconds = Math.Clamp(config.DebounceSeconds, 1, 3600);
        config.RecordBufferSeconds = Math.Clamp(config.RecordBufferSeconds, 5, 120);
        config.RecordFps = Math.Clamp(config.RecordFps, 1, 15);
        config.RecordJpegQuality = Math.Clamp(config.RecordJpegQuality, 40, 95);
        config.OcrMaxWidth = Math.Clamp(config.OcrMaxWidth, 1600, 3840);
        config.OcrUpscalePercent = Math.Clamp(config.OcrUpscalePercent, 100, 400);
        config.SequenceIntervalSeconds = Math.Clamp(config.SequenceIntervalSeconds, 1, 30);
        config.SequenceBeforeSeconds = Math.Clamp(config.SequenceBeforeSeconds, 0, 60);
        config.SequenceAfterSeconds = Math.Clamp(config.SequenceAfterSeconds, 0, 60);
        config.RetentionMaxAgeDays = Math.Clamp(config.RetentionMaxAgeDays, 0, 3650);
        config.RetentionMaxFiles = Math.Clamp(config.RetentionMaxFiles, 0, 100_000);
        config.Triggers ??= [];
        config.Applications ??= [];

        foreach (var trigger in config.Triggers)
        {
            if (trigger.Id == Guid.Empty)
                trigger.Id = Guid.NewGuid();
        }

        config.Applications = config.Applications
            .Where(a => !string.IsNullOrWhiteSpace(a.ProcessName))
            .ToList();

        foreach (var app in config.Applications)
        {
            if (app.Id == Guid.Empty)
                app.Id = Guid.NewGuid();
            app.ProcessName = ApplicationScopeEvaluator.NormalizeProcessName(app.ProcessName);
            app.DebounceSeconds = Math.Clamp(app.DebounceSeconds, 1, 3600);
            app.TriggerIds ??= [];
        }

        ApplicationScopeEvaluator.PruneTriggerIds(config);
    }
}
