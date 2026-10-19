using System.Drawing;
using System.Text.Json;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public sealed class CapturePipelineTests
{
    private static AppConfig Config(string root, CaptureAction action = CaptureAction.Screenshot) => new()
    {
        CaptureDirectory = Path.Combine(root, "captures"),
        ApplicationScope = ApplicationScopeMode.All,
        OcrIntervalMs = 30,
        OcrMaxWidth = 960,
        OcrEnableAdaptive = false,
        OcrEnableNeon = false,
        OcrEnableTerminal = false,
        SequenceBeforeSeconds = 0,
        SequenceAfterSeconds = 2,
        SequenceIntervalSeconds = 1,
        Triggers = [new() { Text = "PIPELINE_TOKEN", Enabled = true, Action = action }]
    };

    private static CaptureServices Services(Func<Bitmap, CancellationToken, Task<string>> recognize, Func<Color>? color = null) => new()
    {
        ForegroundProcess = () => "fixture",
        Recognize = recognize,
        Grab = _ =>
        {
            var bitmap = new Bitmap(96, 64); using (var g = Graphics.FromImage(bitmap)) g.Clear(color?.Invoke() ?? Color.Blue);
            return new CapturedFrame { Bitmap = bitmap, Timestamp = DateTimeOffset.UtcNow, ScreenBounds = new Rectangle(0, 0, 96, 64) };
        }
    };

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }

    [Fact]
    public async Task Pause_invalidates_inflight_ocr_and_resume_can_capture_the_same_scene()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; }
            return "PIPELINE_TOKEN";
        }));
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.TriggerFired += (_, e) => fired.TrySetResult(e.OutputPath);
        monitor.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.SetPaused(true); release.SetResult(); await Task.Delay(150);
        Assert.False(fired.Task.IsCompleted);
        monitor.SetPaused(false);
        Assert.True(File.Exists(await fired.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        await monitor.StopAsync(); Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task Saved_pixels_are_the_matched_snapshot_despite_newer_queued_frames()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root);
        var color = Color.Blue;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services(async (image, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Assert.True(image.GetPixel(5, 5).B > 200);
                entered.SetResult(); await release.Task;
            }
            return "PIPELINE_TOKEN";
        }, () => color));
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.TriggerFired += (_, e) => fired.TrySetResult(e.OutputPath);
        monitor.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        color = Color.Red; await Task.Delay(120); release.SetResult();
        var path = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync();
        using var saved = new Bitmap(path);
        Assert.True(saved.GetPixel(5, 5).B > 200 && saved.GetPixel(5, 5).R < 20);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".capture.json")));
        Assert.Equal(config.Triggers[0].Id, receipt.RootElement.GetProperty("triggerId").GetGuid());
    }

    [Fact]
    public async Task Stop_waits_for_an_already_committed_write_without_blocking_its_caller()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root);
        using var blocked = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services((_, _) => Task.FromResult("PIPELINE_TOKEN")));
        monitor.StatusChanged += (_, e) =>
        {
            if (e.Status == "Saving") { entered.TrySetResult(); Assert.True(blocked.Wait(TimeSpan.FromSeconds(6))); }
        };
        monitor.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = monitor.StopAsync();
        Assert.False(stop.IsCompleted); Assert.True(monitor.IsRunning);
        blocked.Set(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(monitor.IsRunning);
        Assert.Single(Directory.GetFiles(config.CaptureDirectory, "*.capture.json"));
    }

    [Fact]
    public async Task Failed_write_is_retried_on_the_same_scene_after_the_destination_is_repaired()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root);
        File.WriteAllText(config.CaptureDirectory, "A file occupies the intended folder");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services((_, _) => Task.FromResult("PIPELINE_TOKEN")));
        monitor.StatusChanged += (_, e) => { if (e.Status == "Warning") failed.TrySetResult(); };
        monitor.TriggerFired += (_, e) => fired.TrySetResult(e.OutputPath);
        monitor.Start(); await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.Delete(config.CaptureDirectory); Directory.CreateDirectory(config.CaptureDirectory);
        Assert.True(File.Exists(await fired.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        await monitor.StopAsync();
    }

    [Fact]
    public async Task Sequence_pause_marks_interrupted_and_stops_append_activity()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root, CaptureAction.Sequence);
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services((_, _) => Task.FromResult("PIPELINE_TOKEN")));
        monitor.Start(); await Until(() => Directory.Exists(config.CaptureDirectory) && Directory.GetDirectories(config.CaptureDirectory).Length == 1);
        var folder = Directory.GetDirectories(config.CaptureDirectory).Single();
        await Until(() => File.Exists(Path.Combine(folder, "capture.json")));
        monitor.SetPaused(true); await monitor.StopAsync();
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "capture.json")));
        Assert.False(receipt.RootElement.GetProperty("complete").GetBoolean());
        Assert.False(File.Exists(Path.Combine(folder, ".pending")));
        var count = Directory.GetFiles(folder).Length; await Task.Delay(100);
        Assert.Equal(count, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task Explicit_stop_during_restart_prevents_an_unwanted_new_session()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services(async (_, _) =>
        {
            entered.TrySetResult(); await release.Task; return "";
        }));
        monitor.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restart = monitor.RestartIfRunningAsync();
        var stop = monitor.StopAsync();
        release.SetResult();
        await Task.WhenAll(restart, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task Record_sampling_can_run_faster_than_the_ocr_interval()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root, CaptureAction.Record);
        config.OcrIntervalMs = 1000; config.RecordFps = 10;
        var grabs = 0; var recognitions = 0;
        var source = Services((_, _) => { Interlocked.Increment(ref recognitions); return Task.FromResult(""); });
        var services = new CaptureServices
        {
            ForegroundProcess = source.ForegroundProcess,
            Recognize = source.Recognize,
            Grab = c => { Interlocked.Increment(ref grabs); return source.Grab(c); }
        };
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), services);
        monitor.Start(); await Until(() => Volatile.Read(ref grabs) >= 6); await monitor.StopAsync();
        Assert.InRange(grabs, 6, 15);
        Assert.InRange(recognitions, 1, 2);
    }

    [Fact]
    public async Task Bounded_writers_eventually_retry_a_trigger_that_could_not_be_admitted()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root, CaptureAction.Sequence);
        config.Triggers = Enumerable.Range(0, 5).Select(i => new TriggerConfig { Text = $"PIPELINE_TOKEN_{i}", Action = CaptureAction.Sequence, Enabled = true }).ToList();
        var text = string.Join(" ", config.Triggers.Select(t => t.Text));
        var fired = 0;
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services((_, _) => Task.FromResult(text)));
        monitor.TriggerFired += (_, _) => Interlocked.Increment(ref fired);
        monitor.Start();
        await Until(() => Directory.Exists(config.CaptureDirectory) && Directory.GetDirectories(config.CaptureDirectory).Length >= 4);
        Assert.Equal(4, Directory.GetDirectories(config.CaptureDirectory).Length);
        await Until(() => Volatile.Read(ref fired) == 5);
        await monitor.StopAsync();
        Assert.Equal(5, Directory.GetDirectories(config.CaptureDirectory).Length);
    }

    [Fact]
    public async Task Slow_ocr_recovers_historical_post_frames_without_grabbing_after_the_deadline()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root, CaptureAction.Sequence);
        var first = 0;
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), Services(async (_, _) =>
        {
            if (Interlocked.Increment(ref first) == 1) await Task.Delay(2400);
            return "PIPELINE_TOKEN";
        }));
        monitor.TriggerFired += (_, e) => fired.TrySetResult(e.OutputPath);
        monitor.Start(); var folder = await fired.Task.WaitAsync(TimeSpan.FromSeconds(6));
        await monitor.StopAsync();
        var pictures = Directory.GetFiles(folder, "*.jpg").Select(Path.GetFileName).ToArray();
        Assert.Contains("t+000.jpg", pictures);
        Assert.Contains(pictures, name => name!.StartsWith("t+001", StringComparison.Ordinal));
        Assert.DoesNotContain(pictures, name => name!.StartsWith("t+003", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_sequence_images_use_the_same_masked_producer_path()
    {
        using var dir = new AuditDirectory(); var config = Config(dir.Root, CaptureAction.Sequence);
        config.ApplicationScope = ApplicationScopeMode.AllMinusBlacklisted;
        config.Applications = [new() { ProcessName = "private", ListMode = ApplicationListMode.Blacklist }];
        var services = Services((_, _) => Task.FromResult("PIPELINE_TOKEN"), () => Color.Red);
        var masks = 0;
        services = new CaptureServices
        {
            Recognize = services.Recognize,
            Grab = services.Grab,
            ForegroundProcess = services.ForegroundProcess,
            Mask = (frame, _) => { using var g = Graphics.FromImage(frame.Bitmap); g.Clear(Color.Black); Interlocked.Increment(ref masks); return 1; }
        };
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new CaptureMonitor(config, new AppLogger(Path.Combine(dir.Root, "logs")), services);
        monitor.TriggerFired += (_, e) => fired.TrySetResult(e.OutputPath);
        monitor.Start(); var folder = await fired.Task.WaitAsync(TimeSpan.FromSeconds(6));
        await monitor.StopAsync();
        Assert.True(masks > 1);
        var pictures = Directory.GetFiles(folder, "*.jpg"); Assert.True(pictures.Length >= 2);
        foreach (var path in pictures) { using var image = new Bitmap(path); Assert.True(image.GetPixel(4, 4).R < 10); }
    }
}
