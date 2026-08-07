using System.Drawing;
using System.Text;
using System.Windows.Forms;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Harness;

/// <summary>
/// On-screen OCR harness.
/// Modes: hard (static fixture) | stream (inject lines over time + latency budget).
/// Requires both a screenshot and a recording capture to pass.
/// </summary>
internal static class Program
{
    // Hostile but within what Windows OCR can recover at ~8pt green-on-black:
    // mixed case + digits. Mid-token punctuation like !/@ is dropped by OCR entirely
    // (probe: zX9!mQ2@kP is unreadable; zX9mQ2kP matches via confusable/fuzzy).
    private const string ShotTrigger = "zX9mQ2kP";
    private const string RecTrigger = "err#42v2";
    private const string WeirdSentence = "Il0O|B8S5 fail-NOW: retry?";
    private const string RandomToken = "qW3$pL9&nX";
    // Extra OCR junk (not triggers) — punctuation soup that must not be required to match.
    private const string PunctuationSoup = "zX9!mQ2@kP $err#42v2";

    /// <summary>Max wall time from inject → TriggerFired for stream mode.</summary>
    private const int DetectLatencyBudgetMs = 15_000;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        ParseArgs(args, out var mode, out var timeoutSec);
        return mode == "stream"
            ? RunStreamMode(timeoutSec)
            : RunHardMode(timeoutSec);
    }

    /// <summary>
    /// Args: [hard|stream] [timeoutSec]
    /// Legacy: [timeoutSec] → hard mode (keeps existing pytest invocation).
    /// </summary>
    private static void ParseArgs(string[] args, out string mode, out int timeoutSec)
    {
        mode = "hard";
        timeoutSec = 90;

        if (args.Length == 0)
            return;

        if (string.Equals(args[0], "hard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args[0], "stream", StringComparison.OrdinalIgnoreCase))
        {
            mode = args[0].ToLowerInvariant();
            timeoutSec = mode == "stream" ? 120 : 90;
            if (args.Length > 1 && int.TryParse(args[1], out var t))
                timeoutSec = t;
            return;
        }

        if (int.TryParse(args[0], out var legacyTimeout))
            timeoutSec = legacyTimeout;
    }

    private static int RunHardMode(int timeoutSec)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        var textPath = Path.Combine(workDir, "triggers.txt");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        var fixtureBody = BuildFixtureText();
        File.WriteAllText(textPath, fixtureBody);
        Console.WriteLine($"Mode: hard");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Wrote hard fixture: {textPath}");
        Console.WriteLine($"Shot trigger: {ShotTrigger}");
        Console.WriteLine($"Rec trigger:  {RecTrigger}");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        Form? overlay = null;
        System.Windows.Forms.Timer? pollTimer = null;
        var logger = new AppLogger(logDir);
        string? shotPath = null;
        string? recPath = null;
        var started = DateTime.UtcNow;
        var finished = false;

        try
        {
            overlay = CreateConsoleOverlay("pay_attention_e2e_hard", fixtureBody, scrollBars: false);

            var config = BuildConfig(captureDir);
            monitor = new CaptureMonitor(config, logger);
            if (!monitor.OcrAvailable)
            {
                Console.Error.WriteLine("FAIL: Windows OCR unavailable");
                return 2;
            }

            void TryFinish(int code, string? detail = null)
            {
                if (finished)
                    return;
                finished = true;
                exitCode = code;
                if (detail is not null)
                    Console.Error.WriteLine(detail);
                pollTimer?.Stop();
                try { monitor?.Stop(); } catch { /* ignore */ }
                overlay?.Close();
            }

            monitor.TriggerFired += (_, e) =>
            {
                Console.WriteLine($"Fired: {e.TriggerText} ({e.Action}) -> {e.OutputPath}");
                if (e.Action == CaptureAction.Screenshot ||
                    string.Equals(e.TriggerText, ShotTrigger, StringComparison.Ordinal))
                    shotPath ??= e.OutputPath;

                if (e.Action == CaptureAction.Record ||
                    string.Equals(e.TriggerText, RecTrigger, StringComparison.Ordinal))
                    recPath ??= e.OutputPath;
            };

            monitor.StatusChanged += (_, e) =>
                Console.WriteLine($"Status: {e.Status} {e.Detail}");

            pollTimer = new System.Windows.Forms.Timer { Interval = 200 };
            pollTimer.Tick += (_, _) =>
            {
                if (finished)
                    return;

                KeepOnTop(overlay);
                FindCaptures(captureDir, ref shotPath, ref recPath);

                if (CapturesReady(shotPath, recPath))
                {
                    PrintPass(shotPath!, recPath!);
                    TryFinish(0);
                    return;
                }

                if ((DateTime.UtcNow - started).TotalSeconds < timeoutSec)
                    return;

                FailTimeout(shotPath, recPath, captureDir);
                TryFinish(1);
            };

            overlay.Shown += (_, _) =>
            {
                overlay.Activate();
                overlay.BringToFront();
                var settle = new System.Windows.Forms.Timer { Interval = 800 };
                settle.Tick += (_, _) =>
                {
                    settle.Stop();
                    settle.Dispose();
                    overlay.Activate();
                    overlay.BringToFront();
                    monitor.Start();
                    Console.WriteLine($"HARD E2E: monitoring up to {timeoutSec}s…");
                    pollTimer.Start();
                };
                settle.Start();
            };

            overlay.FormClosed += (_, _) => Application.ExitThread();
            Application.Run(overlay);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            exitCode = 3;
        }
        finally
        {
            Cleanup(pollTimer, monitor, overlay, workDir);
        }

        return exitCode;
    }

    private static int RunStreamMode(int timeoutSec)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_stream_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        Console.WriteLine("Mode: stream");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Shot trigger: {ShotTrigger}");
        Console.WriteLine($"Rec trigger:  {RecTrigger}");
        Console.WriteLine($"Latency budget: {DetectLatencyBudgetMs}ms per trigger");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        Form? overlay = null;
        TextBox? box = null;
        System.Windows.Forms.Timer? pollTimer = null;
        System.Windows.Forms.Timer? streamTimer = null;
        var logger = new AppLogger(logDir);
        string? shotPath = null;
        string? recPath = null;
        var started = DateTime.UtcNow;
        var finished = false;
        var streamStep = 0;

        DateTime? shotInjectUtc = null;
        DateTime? recInjectUtc = null;
        int? shotLatencyMs = null;
        int? recLatencyMs = null;

        try
        {
            // No scrollbars: keep the dense buffer fully painted so tokens stay visible.
            overlay = CreateConsoleOverlay("pay_attention_e2e_stream", "", scrollBars: false);
            box = (TextBox)overlay.Controls[0];

            var config = BuildConfig(captureDir);
            config.OcrIntervalMs = 400;
            monitor = new CaptureMonitor(config, logger);
            if (!monitor.OcrAvailable)
            {
                Console.Error.WriteLine("FAIL: Windows OCR unavailable");
                return 2;
            }

            void TryFinish(int code, string? detail = null)
            {
                if (finished)
                    return;
                finished = true;
                exitCode = code;
                if (detail is not null)
                    Console.Error.WriteLine(detail);
                pollTimer?.Stop();
                streamTimer?.Stop();
                try { monitor?.Stop(); } catch { /* ignore */ }
                overlay?.Close();
            }

            monitor.TriggerFired += (_, e) =>
            {
                var now = DateTime.UtcNow;
                Console.WriteLine($"Fired: {e.TriggerText} ({e.Action}) -> {e.OutputPath}");

                if (e.Action == CaptureAction.Screenshot ||
                    string.Equals(e.TriggerText, ShotTrigger, StringComparison.Ordinal))
                {
                    shotPath ??= e.OutputPath;
                    if (shotInjectUtc is not null && shotLatencyMs is null)
                    {
                        shotLatencyMs = (int)(now - shotInjectUtc.Value).TotalMilliseconds;
                        Console.WriteLine($"LATENCY shot={shotLatencyMs.Value}ms");
                        if (shotLatencyMs.Value > DetectLatencyBudgetMs)
                        {
                            TryFinish(1,
                                $"FAIL: shot detection latency {shotLatencyMs.Value}ms exceeds {DetectLatencyBudgetMs}ms");
                            return;
                        }
                    }
                }

                if (e.Action == CaptureAction.Record ||
                    string.Equals(e.TriggerText, RecTrigger, StringComparison.Ordinal))
                {
                    recPath ??= e.OutputPath;
                    if (recInjectUtc is not null && recLatencyMs is null)
                    {
                        recLatencyMs = (int)(now - recInjectUtc.Value).TotalMilliseconds;
                        Console.WriteLine($"LATENCY rec={recLatencyMs.Value}ms");
                        if (recLatencyMs.Value > DetectLatencyBudgetMs)
                        {
                            TryFinish(1,
                                $"FAIL: rec detection latency {recLatencyMs.Value}ms exceeds {DetectLatencyBudgetMs}ms");
                        }
                    }
                }
            };

            monitor.StatusChanged += (_, e) =>
                Console.WriteLine($"Status: {e.Status} {e.Detail}");

            streamTimer = new System.Windows.Forms.Timer { Interval = 350 };
            streamTimer.Tick += (_, _) =>
            {
                if (finished || box is null || box.IsDisposed)
                    return;

                KeepOnTop(overlay);
                streamStep++;

                if (streamStep <= 5)
                {
                    AppendLine(box, $"[{DateTime.Now:HH:mm:ss.fff}] boot noise {streamStep} status=ok");
                    return;
                }

                if (streamStep == 6)
                {
                    AppendLine(box, $"noise-punct:{PunctuationSoup}");
                    AppendLine(box, $"SHOT>{ShotTrigger}<");
                    shotInjectUtc = DateTime.UtcNow;
                    Console.WriteLine($"INJECT shot at {shotInjectUtc:O}");
                    return;
                }

                if (streamStep is >= 7 and <= 11)
                {
                    AppendLine(box, $"scroll {streamStep} {WeirdSentence}");
                    return;
                }

                if (streamStep == 12)
                {
                    // Re-state shot so dense scroll does not bury the only copy off-screen.
                    AppendLine(box, $"SHOT>{ShotTrigger}<");
                    AppendLine(box, $"REC>{RecTrigger}<");
                    AppendLine(box, $"rnd:{RandomToken}");
                    recInjectUtc = DateTime.UtcNow;
                    Console.WriteLine($"INJECT rec at {recInjectUtc:O}");
                    streamTimer.Stop();
                }
            };

            pollTimer = new System.Windows.Forms.Timer { Interval = 200 };
            pollTimer.Tick += (_, _) =>
            {
                if (finished)
                    return;

                KeepOnTop(overlay);
                FindCaptures(captureDir, ref shotPath, ref recPath);

                var now = DateTime.UtcNow;
                if (shotInjectUtc is not null && shotLatencyMs is null &&
                    (now - shotInjectUtc.Value).TotalMilliseconds > DetectLatencyBudgetMs)
                {
                    FailTimeout(shotPath, recPath, captureDir);
                    TryFinish(1, $"FAIL: shot not detected within {DetectLatencyBudgetMs}ms of inject");
                    return;
                }

                if (recInjectUtc is not null && recLatencyMs is null &&
                    (now - recInjectUtc.Value).TotalMilliseconds > DetectLatencyBudgetMs)
                {
                    FailTimeout(shotPath, recPath, captureDir);
                    TryFinish(1, $"FAIL: rec not detected within {DetectLatencyBudgetMs}ms of inject");
                    return;
                }

                if (CapturesReady(shotPath, recPath) &&
                    shotLatencyMs is not null && recLatencyMs is not null)
                {
                    if (shotLatencyMs > DetectLatencyBudgetMs || recLatencyMs > DetectLatencyBudgetMs)
                    {
                        TryFinish(1, "FAIL: latency over budget");
                        return;
                    }

                    PrintPass(shotPath!, recPath!);
                    Console.WriteLine($"LATENCY shot={shotLatencyMs.Value}ms");
                    Console.WriteLine($"LATENCY rec={recLatencyMs.Value}ms");
                    TryFinish(0);
                    return;
                }

                if ((DateTime.UtcNow - started).TotalSeconds < timeoutSec)
                    return;

                FailTimeout(shotPath, recPath, captureDir);
                TryFinish(1);
            };

            overlay.Shown += (_, _) =>
            {
                overlay.Activate();
                overlay.BringToFront();
                var settle = new System.Windows.Forms.Timer { Interval = 800 };
                settle.Tick += (_, _) =>
                {
                    settle.Stop();
                    settle.Dispose();
                    overlay.Activate();
                    overlay.BringToFront();
                    monitor.Start();
                    Console.WriteLine($"STREAM E2E: monitoring up to {timeoutSec}s…");
                    streamTimer.Start();
                    pollTimer.Start();
                };
                settle.Start();
            };

            overlay.FormClosed += (_, _) => Application.ExitThread();
            Application.Run(overlay);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            exitCode = 3;
        }
        finally
        {
            streamTimer?.Dispose();
            Cleanup(pollTimer, monitor, overlay, workDir);
        }

        return exitCode;
    }

    private static AppConfig BuildConfig(string captureDir) => new()
    {
        OcrIntervalMs = 500,
        DebounceSeconds = 4,
        RecordBufferSeconds = 5,
        RecordFps = 3,
        RecordFormat = RecordFormat.Avi,
        RecordJpegQuality = 70,
        ScreenshotFormat = ScreenshotFormat.Jpeg,
        OcrMaxWidth = 1920,
        OcrUpscalePercent = 100,
        OcrInvertColors = false,
        CaptureScope = CaptureScope.Monitor,
        MonitorSelection = MonitorSelection.Primary,
        CaptureDirectory = captureDir,
        DebugSaveJsonSidecars = false,
        Triggers =
        [
            new TriggerConfig
            {
                Text = ShotTrigger,
                Match = MatchMode.Contains,
                Action = CaptureAction.Screenshot,
                Enabled = true
            },
            new TriggerConfig
            {
                Text = RecTrigger,
                Match = MatchMode.Contains,
                Action = CaptureAction.Record,
                Enabled = true
            }
        ]
    };

    private static string BuildFixtureText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== pay_attention HARD fixture (do not enlarge) ===");
        sb.AppendLine("noise: lorem ipsum dolor sit amet consectetur adipiscing elit sed do");
        sb.AppendLine("noise: 0xDEADBEEF aabbcc status=ok path=C:\\tmp\\x.y delay=3ms");
        sb.AppendLine($"noise-punct:{PunctuationSoup}");
        sb.AppendLine($"SHOT>{ShotTrigger}<");
        sb.AppendLine("noise: ..... ..... ..... ..... ..... ..... ..... .....");
        sb.AppendLine($"REC>{RecTrigger}<");
        sb.AppendLine($"sent:{WeirdSentence}");
        sb.AppendLine($"rnd:{RandomToken}");
        sb.AppendLine("noise: incomplete line truncated mid-wor");
        sb.AppendLine("noise: {\"code\":500,\"msg\":\"ignored clutter\"}");
        sb.AppendLine($"SHOT>{ShotTrigger}<");
        return sb.ToString();
    }

    private static Form CreateConsoleOverlay(string name, string text, bool scrollBars)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var form = new Form
        {
            Name = name,
            Text = "serial-session-sim",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(area.Left + 40, area.Top + 40, Math.Min(720, area.Width - 80), Math.Min(420, area.Height - 80)),
            TopMost = true,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            BackColor = Color.FromArgb(12, 12, 12),
            ForeColor = Color.FromArgb(0, 220, 80),
            ShowInTaskbar = true
        };

        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(12, 12, 12),
            ForeColor = Color.FromArgb(0, 220, 80),
            Font = new Font("Consolas", 8.25f, FontStyle.Regular),
            Text = text,
            ScrollBars = scrollBars ? ScrollBars.Vertical : ScrollBars.None
        };
        form.Controls.Add(box);
        return form;
    }

    private static void AppendLine(TextBox box, string line)
    {
        box.AppendText(line + Environment.NewLine);
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    private static void KeepOnTop(Form? overlay)
    {
        if (overlay is { IsDisposed: false })
        {
            overlay.TopMost = true;
            overlay.BringToFront();
        }
    }

    private static void FindCaptures(string captureDir, ref string? shotPath, ref string? recPath)
    {
        shotPath ??= Directory.GetFiles(captureDir, "*_screenshot.*")
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault();
        recPath ??= Directory.GetFiles(captureDir, "*_record.avi")
            .Concat(Directory.GetFiles(captureDir, "*_record.gif"))
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault();
    }

    private static bool CapturesReady(string? shotPath, string? recPath) =>
        shotPath is not null && recPath is not null &&
        File.Exists(shotPath) && File.Exists(recPath) &&
        new FileInfo(shotPath).Length >= 100 &&
        new FileInfo(recPath).Length >= 100;

    private static void PrintPass(string shotPath, string recPath)
    {
        Console.WriteLine("PASS");
        Console.WriteLine($"  screenshot: {shotPath} ({new FileInfo(shotPath).Length} bytes)");
        Console.WriteLine($"  recording:  {recPath} ({new FileInfo(recPath).Length} bytes)");
    }

    private static void FailTimeout(string? shotPath, string? recPath, string captureDir)
    {
        Console.Error.WriteLine("FAIL: timed out waiting for captures");
        Console.Error.WriteLine($"  screenshot: {(shotPath is null ? "missing" : shotPath)}");
        Console.Error.WriteLine($"  recording:  {(recPath is null ? "missing" : recPath)}");
        DumpDir(captureDir);
        try
        {
            using var frame = new ScreenGrabber().CapturePrimary();
            using var prep = ImageHelpers.PrepareForOcr(frame.Bitmap, 1920, 200, invertColors: true);
            var ocrText = new OcrService().RecognizeAsync(prep).GetAwaiter().GetResult();
            Console.Error.WriteLine($"  OCR sample: {(ocrText.Length > 500 ? ocrText[..500] : ocrText)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("  OCR sample failed: " + ex.Message);
        }
    }

    private static void Cleanup(
        System.Windows.Forms.Timer? pollTimer,
        CaptureMonitor? monitor,
        Form? overlay,
        string workDir)
    {
        pollTimer?.Dispose();
        monitor?.Dispose();
        try { overlay?.Dispose(); } catch { /* ignore */ }

        try
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
        catch
        {
            Console.WriteLine($"Note: left work dir at {workDir}");
        }
    }

    private static void DumpDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var f in Directory.GetFiles(dir))
            Console.Error.WriteLine($"  file: {f} ({new FileInfo(f).Length})");
    }
}
