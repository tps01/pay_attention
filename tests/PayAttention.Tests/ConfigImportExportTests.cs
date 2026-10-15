using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public sealed class ConfigImportExportTests
{
    [Fact]
    public void Export_then_import_round_trips_triggers_and_sequence_settings()
    {
        var service = new ConfigService();
        var path = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var original = new AppConfig
            {
                CaptureDirectory = service.DefaultCaptureDirectory,
                SequenceIntervalSeconds = 5,
                SequenceBeforeSeconds = 30,
                SequenceAfterSeconds = 45,
                OcrIntervalMs = 500,
                DebounceSeconds = 10,
                LogLevel = LogLevel.Debug,
                Triggers =
                [
                    new TriggerConfig
                    {
                        Text = "zX9mQ2kP",
                        Match = MatchMode.Contains,
                        Action = CaptureAction.Sequence,
                        Enabled = true
                    },
                    new TriggerConfig
                    {
                        Text = "err#42v2",
                        Match = MatchMode.Contains,
                        Action = CaptureAction.Record,
                        Enabled = false
                    }
                ]
            };

            service.ExportTo(path, original);
            Assert.True(File.Exists(path));

            var exportedJson = File.ReadAllText(path);
            Assert.Contains("\"captureDirectory\": \"\"", exportedJson);

            var imported = service.ImportFrom(path);
            Assert.Equal(service.DefaultCaptureDirectory, imported.CaptureDirectory);
            Assert.Equal(5, imported.SequenceIntervalSeconds);
            Assert.Equal(30, imported.SequenceBeforeSeconds);
            Assert.Equal(45, imported.SequenceAfterSeconds);
            Assert.Equal(500, imported.OcrIntervalMs);
            Assert.Equal(LogLevel.Debug, imported.LogLevel);
            Assert.Equal(2, imported.Triggers.Count);
            Assert.Equal(CaptureAction.Sequence, imported.Triggers[0].Action);
            Assert.Equal("zX9mQ2kP", imported.Triggers[0].Text);
            Assert.True(imported.Triggers[0].Enabled);
            Assert.Equal(CaptureAction.Record, imported.Triggers[1].Action);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Import_clamps_out_of_range_sequence_values()
    {
        var service = new ConfigService();
        var path = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, """
                {
                  "sequenceIntervalSeconds": 99,
                  "sequenceBeforeSeconds": -3,
                  "sequenceAfterSeconds": 120,
                  "triggers": []
                }
                """);

            var imported = service.ImportFrom(path);
            Assert.Equal(30, imported.SequenceIntervalSeconds);
            Assert.Equal(0, imported.SequenceBeforeSeconds);
            Assert.Equal(60, imported.SequenceAfterSeconds);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Import_missing_file_throws()
    {
        var service = new ConfigService();
        Assert.ThrowsAny<Exception>(() =>
            service.ImportFrom(Path.Combine(Path.GetTempPath(), "pa_missing_" + Guid.NewGuid().ToString("N") + ".json")));
    }

    [Fact]
    public void Import_clamps_performance_knobs_and_allows_ocr_max_width_960()
    {
        var service = new ConfigService();
        var path = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(path, """
                {
                  "ocrMaxWidth": 1280,
                  "ocrBoostUpscalePercent": 50,
                  "ocrAbsoluteMaxWidth": 99999,
                  "ocrTerminalMaxRegions": 9,
                  "ocrMaxWindows": 0,
                  "ocrQueueCapacity": 8,
                  "ocrCascadeBudgetFastMs": 10,
                  "ocrCascadeBudgetThoroughMs": 999999,
                  "triggers": []
                }
                """);

            var imported = service.ImportFrom(path);
            Assert.Equal(1280, imported.OcrMaxWidth);
            Assert.Equal(100, imported.OcrBoostUpscalePercent);
            Assert.Equal(3840, imported.OcrAbsoluteMaxWidth);
            Assert.Equal(4, imported.OcrTerminalMaxRegions);
            Assert.Equal(1, imported.OcrMaxWindows);
            Assert.Equal(4, imported.OcrQueueCapacity);
            Assert.Equal(100, imported.OcrCascadeBudgetFastMs);
            Assert.Equal(60_000, imported.OcrCascadeBudgetThoroughMs);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ResolveAppDataDirectory_honors_PAY_ATTENTION_APPDATA()
    {
        var previous = Environment.GetEnvironmentVariable("PAY_ATTENTION_APPDATA");
        var root = Path.Combine(Path.GetTempPath(), "pa_appdata_" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("PAY_ATTENTION_APPDATA", root);
            var resolved = ConfigService.ResolveAppDataDirectory();
            Assert.Equal(Path.GetFullPath(root), resolved);
            var service = new ConfigService();
            Assert.True(service.IsIsolatedAppData);
            Assert.Equal(Path.GetFullPath(root), service.AppDataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAY_ATTENTION_APPDATA", previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_corrupt_json_resets_and_keeps_a_backup()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N"));
        var service = new ConfigService(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(service.ConfigPath, "{");

        try
        {
            var load = service.LoadDetailed();
            Assert.True(load.ResetToDefaults);
            Assert.False(string.IsNullOrEmpty(load.CorruptBackupPath));
            Assert.True(File.Exists(load.CorruptBackupPath));
            Assert.Contains("\"triggers\"", File.ReadAllText(service.ConfigPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_locked_file_does_not_overwrite_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N"));
        var service = new ConfigService(root);
        Directory.CreateDirectory(root);
        const string original = """{ "debounceSeconds": 12 }""";
        File.WriteAllText(service.ConfigPath, original);

        try
        {
            using var held = new FileStream(service.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var load = service.LoadDetailed();
            Assert.False(load.ResetToDefaults);
            Assert.False(string.IsNullOrEmpty(load.Warning));
            held.Dispose();
            Assert.Equal(original, File.ReadAllText(service.ConfigPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_missing_applications_property_adds_default_blacklist()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N"));
        var service = new ConfigService(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(service.ConfigPath, """
            {
              "triggers": [ { "text": "applications", "enabled": true, "match": "contains" } ]
            }
            """);

        try
        {
            var load = service.LoadDetailed();
            Assert.Contains(load.Config.Applications, a =>
                string.Equals(a.ProcessName, "payattention", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(load.Config.Triggers, t => t.Text == "applications");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_empty_applications_array_is_not_replaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N"));
        var service = new ConfigService(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(service.ConfigPath, """{ "applications": [] }""");

        try
        {
            var load = service.LoadDetailed();
            Assert.Empty(load.Config.Applications);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

