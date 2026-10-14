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
}

