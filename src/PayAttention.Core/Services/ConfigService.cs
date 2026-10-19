using System.Text.Json;
using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public sealed class ConfigLoadResult
{
    public required AppConfig Config { get; init; }
    public bool ResetToDefaults { get; init; }
    public string? CorruptBackupPath { get; init; }
    /// <summary>Set when defaults are in memory but the file on disk was left alone.</summary>
    public string? Warning { get; init; }
}

public sealed class ConfigService
{
    public string AppDataDirectory { get; }
    public string ConfigPath { get; }
    public string DefaultCaptureDirectory { get; }
    public string LogDirectory { get; }

    /// <summary>
    /// Uses <c>PAY_ATTENTION_APPDATA</c> when set (isolated Colosseum / harness runs);
    /// otherwise <c>%LocalAppData%\pay_attention</c>.
    /// </summary>
    public ConfigService()
        : this(ResolveAppDataDirectory())
    {
    }

    public ConfigService(string appDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDataDirectory))
            throw new ArgumentException("App data directory is required.", nameof(appDataDirectory));

        AppDataDirectory = Path.GetFullPath(appDataDirectory.Trim());
        ConfigPath = Path.Combine(AppDataDirectory, "config.json");
        DefaultCaptureDirectory = Path.Combine(AppDataDirectory, "captures");
        LogDirectory = Path.Combine(AppDataDirectory, "logs");
        IsIsolatedAppData = !string.Equals(
            AppDataDirectory,
            Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "pay_attention")),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when this process uses a non-default app-data root (tests / automation).</summary>
    public bool IsIsolatedAppData { get; }

    public static string ResolveAppDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("PAY_ATTENTION_APPDATA");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.GetFullPath(overrideDir.Trim());

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pay_attention");
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
            AppConfig config;
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(json, JsonDefaults.Options) ?? CreateDefault();
            }
            catch (JsonException)
            {
                return ResetCorruptFile();
            }

            // Upgrade older configs that never had an applications property.
            // A trigger whose text is "applications" must not count as that property.
            if (!RootHasProperty(json, "applications"))
                config.Applications = ApplicationScopeEvaluator.CreateDefaultApplications();
            Normalize(config);
            return new ConfigLoadResult { Config = config };
        }
        catch (InvalidDataException ex)
        {
            return new ConfigLoadResult
            {
                Config = CreateDefault(),
                Warning = "Settings are invalid (" + ex.Message +
                          "). Monitoring is disabled. The existing file was left in place."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigLoadResult
            {
                Config = CreateDefault(),
                Warning = "Settings could not be read (" + ex.Message +
                          "). Defaults are in use for this session. The existing file was left in place."
            };
        }
    }

    public void Save(AppConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(config.CaptureDirectory);
        var json = JsonSerializer.Serialize(config, JsonDefaults.Options);
        WriteAtomic(ConfigPath, json);
    }

    public void ApplyAutostart(AppConfig config, string executablePath, Action<bool, string>? writer = null)
    {
        if (!IsIsolatedAppData)
            (writer ?? AutostartService.SetEnabled)(config.StartWithWindows, executablePath);
    }

    private ConfigLoadResult ResetCorruptFile()
    {
        string? backup = null;
        try
        {
            backup = ConfigPath + ".corrupt." + DateTime.UtcNow.ToString(
                "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture);
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

    private static bool RootHasProperty(string json, string name)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return true;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        if (File.Exists(path))
            File.Replace(temp, path, destinationBackupFileName: null);
        else
            File.Move(temp, path);
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
        config.Triggers ??= [];
        config.Applications ??= [];
        RequireEnum(config.ApplicationScope, "applicationScope");
        RequireEnum(config.CaptureScope, "captureScope");
        RequireEnum(config.MonitorSelection, "monitorSelection");
        RequireEnum(config.RecordFormat, "recordFormat");
        RequireEnum(config.ScreenshotFormat, "screenshotFormat");
        RequireEnum(config.LogLevel, "logLevel");
        RequireEnum(config.AppliedPerformanceProfile, "appliedPerformanceProfile");
        var triggerIds = new HashSet<Guid>();
        foreach (var trigger in config.Triggers)
        {
            if (trigger is null)
                throw new InvalidDataException("triggers must not contain null entries.");
            RequireEnum(trigger.Match, "trigger.match");
            RequireEnum(trigger.Action, "trigger.action");
            RequireEnum(trigger.OcrMode, "trigger.ocrMode");
            trigger.Text ??= string.Empty;
            if (trigger.Id == Guid.Empty)
                trigger.Id = Guid.NewGuid();
            if (!triggerIds.Add(trigger.Id))
                throw new InvalidDataException("Trigger IDs must be unique.");
        }
        var applicationIds = new HashSet<Guid>();
        foreach (var app in config.Applications)
        {
            if (app is null)
                throw new InvalidDataException("applications must not contain null entries.");
            RequireEnum(app.ListMode, "application.listMode");
            RequireEnum(app.TriggerFilter, "application.triggerFilter");
            foreach (var prep in new[] { app.OcrAdaptive, app.OcrNeon, app.OcrGray, app.OcrInvert, app.OcrTerminal })
                RequireEnum(prep, "application.ocrPrep");
            if (app.Id == Guid.Empty)
                app.Id = Guid.NewGuid();
            if (!applicationIds.Add(app.Id))
                throw new InvalidDataException("Application IDs must be unique.");
        }
        config.PauseHotkey ??= string.Empty;
        if (string.IsNullOrWhiteSpace(config.CaptureDirectory))
            config.CaptureDirectory = DefaultCaptureDirectory;

        config.OcrIntervalMs = Math.Clamp(config.OcrIntervalMs, 250, 10_000);
        config.FastMatchThresholdPercent = Math.Clamp(config.FastMatchThresholdPercent, 50, 100);
        config.DebounceSeconds = Math.Clamp(config.DebounceSeconds, 1, 3600);
        config.RecordBufferSeconds = Math.Clamp(config.RecordBufferSeconds, 5, 120);
        config.RecordFps = Math.Clamp(config.RecordFps, 1, 15);
        config.RecordJpegQuality = Math.Clamp(config.RecordJpegQuality, 40, 95);
        config.OcrMaxWidth = Math.Clamp(config.OcrMaxWidth, 960, 3840);
        config.OcrUpscalePercent = Math.Clamp(config.OcrUpscalePercent, 100, 400);
        config.OcrBoostUpscalePercent = Math.Clamp(config.OcrBoostUpscalePercent, 100, 400);
        config.OcrAbsoluteMaxWidth = Math.Clamp(config.OcrAbsoluteMaxWidth, 960, 3840);
        config.OcrTerminalMaxRegions = Math.Clamp(config.OcrTerminalMaxRegions, 0, 4);
        config.OcrMaxWindows = Math.Clamp(config.OcrMaxWindows, 1, 12);
        config.OcrQueueCapacity = Math.Clamp(config.OcrQueueCapacity, 1, 4);
        config.OcrCascadeBudgetFastMs = Math.Clamp(config.OcrCascadeBudgetFastMs, 100, 30_000);
        config.OcrCascadeBudgetThoroughMs = Math.Clamp(config.OcrCascadeBudgetThoroughMs, 100, 60_000);
        config.LastCalibrationSummary ??= string.Empty;
        PerformanceProfiles.MigrateLegacyAppliedProfile(config);
        if (!Enum.IsDefined(config.AppliedPerformanceProfile))
            config.AppliedPerformanceProfile = PerformanceProfileKind.Ultra;
        ApplicationScopeEvaluator.MigratePayAttentionSettingsBlacklist(config);
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

    private static void RequireEnum<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidDataException($"Unknown {field}: {value}.");
    }
}
