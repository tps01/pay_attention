using System.Drawing;
using System.Text.Json;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public sealed class OcrReplayTests
{
    [Fact]
    public async Task Terminal_delimiter_fallback_recovers_an_unreadable_full_line()
    {
        using var dir = new AuditDirectory();
        using var image = new Bitmap(1000, 600);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.Lime, 10, 30, 50, 8);
            graphics.FillRectangle(Brushes.Lime, 80, 30, 6, 8);
        }
        var trigger = new TriggerConfig { Text = "TRIM_TOKEN", Enabled = true, OcrMode = OcrSpeedMode.Thorough };
        var config = new AppConfig { Triggers = [trigger] };
        var evidence = new OcrEvidence { Windows = [new(0, 0, 300, 200)] };
        evidence.SaveSettings(config, null);
        var logger = new AppLogger(dir.Root) { MinLevel = LogLevel.Verbose };
        using var monitor = new CaptureMonitor(config, logger, new CaptureServices
        {
            Recognize = (bitmap, _) => Task.FromResult(bitmap.Width == 222 && bitmap.Height == 48 ? "TRIM_TOKEN" : "")
        });
        Assert.True(await monitor.VerifySavedImageAsync(image, trigger, evidence));
        Assert.Contains("pass=ocr.line-trim", File.ReadAllText(Directory.GetFiles(dir.Root, "*.log").Single()));
        Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(82, 32).ToArgb());
    }

    [Fact]
    public async Task Completed_receipt_preserves_capture_geometry_and_settings_through_the_queue()
    {
        using var dir = new AuditDirectory();
        var trigger = new TriggerConfig { Text = "RECEIPT_TOKEN", Enabled = true };
        var config = new AppConfig
        {
            Triggers = [trigger], CaptureDirectory = dir.Root, ApplicationScope = ApplicationScopeMode.All,
            OcrUpscalePercent = 150
        };
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), new CaptureServices
        {
            ForegroundProcess = () => "fixture",
            WindowBounds = _ => [new Rectangle(-90, 230, 200, 100)],
            Grab = _ => new CapturedFrame
            {
                Bitmap = new Bitmap(1000, 600), Timestamp = DateTimeOffset.UtcNow,
                ScreenBounds = new Rectangle(-100, 200, 1000, 600)
            },
            Recognize = (_, _) => Task.FromResult("RECEIPT_TOKEN")
        });
        monitor.TriggerFired += (_, e) => fired.TrySetResult(e.OutputPath);
        monitor.Start();
        var path = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync();
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".capture.json")));
        var evidence = receipt.RootElement.GetProperty("ocrEvidence").Deserialize<OcrEvidence>(JsonDefaults.Options)!;
        Assert.Equal(-100, evidence.ScreenX);
        Assert.Equal(200, evidence.ScreenY);
        Assert.Equal(new OcrRegion(10, 30, 200, 100), Assert.Single(evidence.Windows));
        Assert.Equal(150, evidence.ReplayConfig(trigger).OcrUpscalePercent);
        Assert.True(receipt.RootElement.GetProperty("completedAt").GetDateTimeOffset() >= receipt.RootElement.GetProperty("timestamp").GetDateTimeOffset());
        Assert.DoesNotContain("RECEIPT_TOKEN", receipt.RootElement.GetProperty("ocrEvidence").GetRawText());
    }

    [Fact]
    public void Terminal_line_regions_exclude_large_bright_backgrounds_and_preserve_bounds()
    {
        using var image = new Bitmap(300, 120);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, 0, 0, 300, 20);
            graphics.FillRectangle(Brushes.Lime, 10, 30, 80, 8);
            graphics.FillRectangle(Brushes.Lime, 10, 60, 90, 8);
        }
        var regions = ImageHelpers.FindTerminalTextLines(image);
        Assert.Equal(new[] { new Rectangle(6, 26, 88, 16), new Rectangle(6, 56, 98, 16) }, regions);
        Assert.Single(ImageHelpers.FindTerminalTextLines(image, 1));
        Assert.Empty(ImageHelpers.FindTerminalTextLines(image, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_never_enumerates_live_windows_and_preserves_crop_coordinates(bool legacy)
    {
        using var dir = new AuditDirectory();
        var config = new AppConfig { OcrMaxWidth = 960, OcrUpscalePercent = 150 };
        var trigger = new TriggerConfig { Text = "REPLAY_TOKEN", Enabled = true };
        var evidence = new OcrEvidence
        {
            ScreenX = -1920, ScreenY = 200,
            Windows = [new OcrRegion(30, 40, 200, 100)]
        };
        evidence.SaveSettings(config, null);
        // Exercise the persisted representation, not just in-memory objects.
        evidence = JsonSerializer.Deserialize<OcrEvidence>(JsonSerializer.Serialize(evidence, JsonDefaults.Options), JsonDefaults.Options)!;
        var widths = new List<int>();
        var services = new CaptureServices
        {
            WindowBounds = _ => throw new InvalidOperationException("Live desktop accessed during replay"),
            Recognize = (bitmap, _) =>
            {
                widths.Add(bitmap.Width);
                return Task.FromResult("REPLAY_TOKEN");
            }
        };
        using var monitor = new CaptureMonitor(config, new AppLogger(dir.Root), services);
        using var image = new Bitmap(1000, 600);
        Assert.True(await monitor.VerifySavedImageAsync(image, trigger, legacy ? null : evidence));
        Assert.Equal(legacy ? 1000 : 400, widths[0]);
        if (!legacy)
        {
            Assert.Equal(150, evidence.ReplayConfig(trigger).OcrUpscalePercent);
            Assert.Equal(960, evidence.ReplayConfig(trigger).OcrMaxWidth);
        }
    }

    [Fact]
    public async Task Every_window_gets_a_baseline_and_later_window_gets_preprocessing()
    {
        using var dir = new AuditDirectory();
        var trigger = new TriggerConfig { Text = "BETA_TOKEN", Enabled = true, OcrMode = OcrSpeedMode.Thorough };
        var config = new AppConfig { Triggers = [trigger] };
        var evidence = new OcrEvidence
        {
            Windows = [new(0, 0, 200, 100), new(300, 0, 220, 100)]
        };
        evidence.SaveSettings(config, null);
        var widths = new List<int>();
        var betaPasses = 0;
        var services = new CaptureServices
        {
            WindowBounds = _ => throw new InvalidOperationException("Live desktop accessed"),
            Recognize = (bitmap, _) =>
            {
                widths.Add(bitmap.Width);
                // Beta's baseline misses; the boosted adaptive pass must recover it.
                var beta = bitmap.Width == 440 && ++betaPasses >= 2;
                return Task.FromResult(beta ? "BETA_TOKEN" : "");
            }
        };
        using var monitor = new CaptureMonitor(config, new AppLogger(dir.Root), services);
        using var image = new Bitmap(1000, 600);
        Assert.True(await monitor.VerifySavedImageAsync(image, trigger, evidence));
        Assert.Equal(new[] { 400, 440 }, widths.Take(2));
        Assert.True(betaPasses >= 2);
    }

    [Fact]
    public async Task Replay_splits_using_saved_monitors_and_rejects_invalid_crops()
    {
        using var dir = new AuditDirectory();
        var config = new AppConfig();
        var trigger = new TriggerConfig { Text = "RIGHT_MONITOR", Enabled = true };
        var evidence = new OcrEvidence
        {
            ScreenX = -1000,
            Monitors = [new(0, 0, 500, 600), new(500, 0, 500, 600)]
        };
        evidence.SaveSettings(config, null);
        var calls = 0;
        var services = new CaptureServices
        {
            Recognize = (bitmap, _) => Task.FromResult(bitmap.Width == 500 && ++calls > 1 ? "RIGHT_MONITOR" : "")
        };
        using var monitor = new CaptureMonitor(config, new AppLogger(dir.Root), services);
        using var image = new Bitmap(1000, 600);
        Assert.True(await monitor.VerifySavedImageAsync(image, trigger, evidence));
        evidence.Windows = [new(999, 0, 200, 100)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.VerifySavedImageAsync(image, trigger, evidence));
    }

    [Theory]
    [InlineData(LogLevel.Info, false, false)]
    [InlineData(LogLevel.Debug, true, false)]
    [InlineData(LogLevel.Verbose, true, true)]
    public void Verbose_is_opt_in_and_includes_debug(LogLevel level, bool debug, bool verbose)
    {
        using var dir = new AuditDirectory();
        var logger = new AppLogger(dir.Root) { MinLevel = level };
        logger.Info("info-message"); logger.Debug("debug-message"); logger.Verbose("verbose-message");
        var text = File.ReadAllText(Directory.GetFiles(dir.Root, "*.log").Single());
        Assert.Contains("info-message", text);
        Assert.Equal(debug, text.Contains("debug-message"));
        Assert.Equal(verbose, text.Contains("verbose-message"));
    }
}
