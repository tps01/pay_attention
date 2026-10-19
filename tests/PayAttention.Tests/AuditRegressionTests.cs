using System.Drawing;
using System.Text.Json;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public sealed class AuditDirectory : IDisposable
{
    public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pa_audit_test_" + Guid.NewGuid().ToString("N")));
    public AuditDirectory() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Root), ignoreCase: true);
        Assert.StartsWith("pa_audit_test_", Path.GetFileName(Root));
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

public sealed class AuditRegressionTests
{
    [Fact]
    public void Retention_preserves_foreign_legacy_and_active_files_but_deletes_owned_units()
    {
        using var dir = new AuditDirectory();
        var foreign = Path.Combine(dir.Root, "notes.json");
        var legacy = Path.Combine(dir.Root, "20260101_120000_000_test_screenshot.jpg");
        var foreignSequence = Path.Combine(dir.Root, "my_sequence");
        File.WriteAllText(foreign, "{}"); File.WriteAllText(legacy, "photo"); Directory.CreateDirectory(foreignSequence);
        var owned = Path.Combine(dir.Root, CaptureOwnership.CreateName(DateTimeOffset.Now, "test", "record", Guid.NewGuid()));
        foreach (var extension in new[] { ".avi", ".jpg", ".capture.json" })
        {
            File.WriteAllText(owned + extension, "owned");
            File.SetCreationTimeUtc(owned + extension, DateTime.UtcNow.AddDays(-3));
        }
        var active = Path.Combine(dir.Root, CaptureOwnership.CreateName(DateTimeOffset.Now, "active", "sequence", Guid.NewGuid()));
        Directory.CreateDirectory(active); File.WriteAllText(Path.Combine(active, ".pending"), "");
        Directory.SetCreationTimeUtc(active, DateTime.UtcNow.AddDays(-3));
        Assert.Equal(1, RetentionService.Enforce(dir.Root, 1, 1));
        Assert.True(File.Exists(foreign)); Assert.True(File.Exists(legacy));
        Assert.True(Directory.Exists(foreignSequence)); Assert.True(Directory.Exists(active));
        Assert.False(File.Exists(owned + ".avi")); Assert.False(File.Exists(owned + ".capture.json"));
    }

    [Theory]
    [InlineData("{\"triggers\":[null]}")]
    [InlineData("{\"applications\":[null]}")]
    [InlineData("{\"applicationScope\":999,\"startMonitoringOnLaunch\":true}")]
    [InlineData("{\"captureScope\":999}")]
    [InlineData("{\"appliedPerformanceProfile\":999}")]
    [InlineData("{\"triggers\":[{\"match\":999}]}")]
    [InlineData("{\"applications\":[{\"processName\":\"private\",\"triggerFilter\":999}]}")]
    public void Invalid_config_is_preserved_and_cannot_activate_monitoring(string json)
    {
        using var dir = new AuditDirectory(); var service = new ConfigService(dir.Root);
        File.WriteAllText(service.ConfigPath, json);
        var result = service.LoadDetailed();
        Assert.NotNull(result.Warning); Assert.False(result.Config.StartMonitoringOnLaunch);
        Assert.DoesNotContain(result.Config.Triggers, t => t.Enabled);
        Assert.Equal(json, File.ReadAllText(service.ConfigPath));
        Assert.Throws<InvalidDataException>(() => service.ImportFrom(service.ConfigPath));
    }

    [Fact]
    public void Duplicate_trigger_ids_are_rejected_before_scope_and_debounce_use()
    {
        using var dir = new AuditDirectory(); var service = new ConfigService(dir.Root);
        var id = Guid.NewGuid();
        var config = new AppConfig { Triggers = [new() { Id = id, Text = "alpha" }, new() { Id = id, Text = "beta" }] };
        File.WriteAllText(service.ConfigPath, JsonSerializer.Serialize(config, JsonDefaults.Options));
        Assert.NotNull(service.LoadDetailed().Warning);
        Assert.Throws<InvalidDataException>(() => service.ImportFrom(service.ConfigPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Isolated_save_and_import_never_update_real_autostart(bool enabled)
    {
        using var dir = new AuditDirectory(); var service = new ConfigService(dir.Root);
        var config = new AppConfig { StartWithWindows = enabled };
        service.Save(config);
        var imported = service.ImportFrom(service.ConfigPath);
        service.ApplyAutostart(config, "test.exe", (_, _) => throw new InvalidOperationException("Registry must not be touched"));
        service.ApplyAutostart(imported, "test.exe", (_, _) => throw new InvalidOperationException("Registry must not be touched"));
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(90, false)]
    [InlineData(80, true)]
    [InlineData(70, true)]
    public void Fast_percentage_applies_to_previously_unconditional_fuzzy_matches(int threshold, bool expected)
    {
        var trigger = new TriggerConfig { Text = "abcdefghij", Enabled = true, OcrMode = OcrSpeedMode.Fast };
        Assert.Equal(expected, TriggerMatcher.HasAnyMatch("abcdxxghij", [trigger], threshold));
    }

    [Theory]
    [InlineData("错误", "成功")]
    [InlineData("ошибка", "готово")]
    [InlineData("!!!", "???")]
    public void Exact_matching_never_equates_empty_lossy_representations(string trigger, string text)
    {
        var config = new TriggerConfig { Text = trigger, Match = MatchMode.Exact, Enabled = true };
        Assert.False(TriggerMatcher.HasAnyMatch(text, [config]));
        Assert.True(TriggerMatcher.HasAnyMatch(trigger, [config]));
    }

    [Fact]
    public void Filename_collision_and_receipt_identity_preserve_both_events()
    {
        using var dir = new AuditDirectory(); using var bitmap = new Bitmap(32, 32);
        var timestamp = DateTimeOffset.UtcNow;
        var paths = new[] { "PAY:ERROR", "PAY?ERROR" }.Select(text => CaptureWriter.WriteScreenshot(bitmap,
            new CaptureSidecar { Timestamp = timestamp, TriggerText = text, TriggerId = Guid.NewGuid() }, dir.Root)).ToList();
        Assert.Equal(2, paths.Distinct().Count());
        foreach (var path in paths)
        {
            Assert.True(File.Exists(path));
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".capture.json")));
            Assert.True(receipt.RootElement.GetProperty("complete").GetBoolean());
            Assert.DoesNotContain("ocrSnippet", receipt.RootElement.ToString());
        }
    }

    [Fact]
    public void Pending_claim_is_released_on_failure_and_cannot_erase_a_newer_claim()
    {
        var gate = new TriggerFireGate();
        Assert.True(gate.TryClaim("event", TimeSpan.FromSeconds(30), 1, out var first, out _));
        Assert.False(gate.TryClaim("event", TimeSpan.Zero, 2, out _, out var reason));
        Assert.Equal(FireSuppressReason.Pending, reason);
        gate.Abandon("event", first);
        Assert.True(gate.TryClaim("event", TimeSpan.Zero, 1, out var second, out _));
        gate.Abandon("event", first);
        gate.Complete("event", second);
        Assert.False(gate.ShouldFire("event", TimeSpan.Zero, 1));
    }

    [Fact]
    public void History_snapshot_excludes_both_future_and_expired_frames()
    {
        var now = DateTimeOffset.UtcNow; using var bitmap = new Bitmap(16, 16);
        using var ring = new FrameRingBuffer(TimeSpan.FromSeconds(30), 3);
        foreach (var seconds in new[] { -10, -1, 0, 1, 2 }) ring.TryAdd(bitmap, now.AddSeconds(seconds));
        var history = ring.SnapshotAt(now, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { now.AddSeconds(-1), now }, history.Select(f => f.Timestamp));
    }

    [Fact]
    public void Sparse_video_retains_fractional_time_base()
    {
        using var dir = new AuditDirectory(); using var bitmap = new Bitmap(16, 16);
        var now = DateTimeOffset.UtcNow; var jpeg = ImageHelpers.EncodeJpeg(bitmap, 70);
        var frames = Enumerable.Range(0, 3).Select(i => new BufferedFrame { Timestamp = now.AddSeconds(i * 5), Width = 16, Height = 16, JpegBytes = jpeg }).ToList();
        var fps = CaptureWriter.PlaybackFps(frames, 3);
        Assert.Equal(0.2, fps, precision: 5);
        var path = Path.Combine(dir.Root, "sparse.avi"); MjpegAviWriter.Write(path, frames, fps);
        var bytes = File.ReadAllBytes(path);
        var header = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("strh", StringComparison.Ordinal);
        Assert.Equal(0.2, (double)BitConverter.ToInt32(bytes, header + 32) / BitConverter.ToInt32(bytes, header + 28), precision: 5);
    }

    [Fact]
    public void Sequence_selection_never_uses_after_deadline_or_fabricates_catchup_samples()
    {
        var now = DateTimeOffset.UtcNow;
        var frames = new[] { 1.2, 2.2, 2.3, 5.1, 8.0 }.Select(s => new BufferedFrame { Timestamp = now.AddSeconds(s), JpegBytes = [], Width = 1, Height = 1 });
        var selected = CaptureMonitor.SelectSequenceFrames(frames, now.AddSeconds(1), now.AddSeconds(6), 2);
        Assert.Equal(new[] { now.AddSeconds(1.2), now.AddSeconds(5.1) }, selected.Select(f => f.Timestamp));
        Assert.Empty(CaptureMonitor.SelectSequenceFrames(frames, now.AddSeconds(5), now.AddSeconds(2), 5));
    }

    [Fact]
    public void Foreground_capture_failure_does_not_expand_scope()
    {
        var config = new AppConfig { ApplicationScope = ApplicationScopeMode.All, CaptureScope = CaptureScope.ForegroundWindow };
        Assert.Null(CaptureMonitor.Grab(config, foregroundCapture: () => null));
    }

    [Fact]
    public void Content_identity_detects_single_line_change_at_desktop_resolution()
    {
        using var a = new Bitmap(1920, 1080); using var b = new Bitmap(1920, 1080);
        using var font = new Font("Consolas", 10);
        void Draw(Bitmap bitmap, string last)
        {
            using var g = Graphics.FromImage(bitmap); g.Clear(Color.Black);
            for (var i = 0; i < 45; i++) g.DrawString(i == 42 ? last : "normal service status: all systems ready", font, Brushes.Lime, 10, 20 + i * 20);
        }
        Draw(a, "normal service status: all systems ready"); Draw(b, "Machine check: critical failure");
        Assert.NotEqual(FrameFingerprint.ComputeContentHash(a), FrameFingerprint.ComputeContentHash(b));
        using var same = (Bitmap)a.Clone();
        Assert.Equal(FrameFingerprint.ComputeContentHash(a), FrameFingerprint.ComputeContentHash(same));
    }
}
