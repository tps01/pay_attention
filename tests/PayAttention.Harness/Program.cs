using System.Drawing;
using System.Text;
using System.Windows.Forms;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Harness;

/// <summary>
/// Hard on-screen OCR fixture: tiny terminal-style text, noisy clutter, weird tokens.
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

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var timeoutSec = args.Length > 0 && int.TryParse(args[0], out var t) ? t : 90;
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        var textPath = Path.Combine(workDir, "triggers.txt");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        var fixtureBody = BuildFixtureText();
        // Artifact for debugging failed runs (overlay uses in-memory text).
        File.WriteAllText(textPath, fixtureBody);
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
            overlay = CreateHardOverlay(fixtureBody);

            var config = new AppConfig
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

                if (overlay is { IsDisposed: false })
                {
                    overlay.TopMost = true;
                    overlay.BringToFront();
                }

                shotPath ??= Directory.GetFiles(captureDir, "*_screenshot.*")
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();
                recPath ??= Directory.GetFiles(captureDir, "*_record.avi")
                    .Concat(Directory.GetFiles(captureDir, "*_record.gif"))
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();

                if (shotPath is not null && recPath is not null &&
                    File.Exists(shotPath) && File.Exists(recPath) &&
                    new FileInfo(shotPath).Length >= 100 &&
                    new FileInfo(recPath).Length >= 100)
                {
                    Console.WriteLine("PASS");
                    Console.WriteLine($"  screenshot: {shotPath} ({new FileInfo(shotPath).Length} bytes)");
                    Console.WriteLine($"  recording:  {recPath} ({new FileInfo(recPath).Length} bytes)");
                    TryFinish(0);
                    return;
                }

                if ((DateTime.UtcNow - started).TotalSeconds < timeoutSec)
                    return;

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

                TryFinish(1);
            };

            overlay.Shown += (_, _) =>
            {
                overlay.Activate();
                overlay.BringToFront();
                // Brief settle so the overlay is actually painted before OCR starts.
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
            pollTimer?.Dispose();
            monitor?.Dispose();
            try
            {
                overlay?.Dispose();
            }
            catch { /* ignore */ }

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

        return exitCode;
    }

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

    private static Form CreateHardOverlay(string text)
    {
        // Dark terminal palette + tiny font — closer to MobaXterm serial sessions.
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var form = new Form
        {
            Name = "pay_attention_e2e_hard",
            Text = "serial-session-sim",
            StartPosition = FormStartPosition.Manual,
            // Only cover a portion of the screen so the app must find small text in a larger capture.
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
            ScrollBars = ScrollBars.None
        };
        form.Controls.Add(box);
        return form;
    }

    private static void DumpDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var f in Directory.GetFiles(dir))
            Console.Error.WriteLine($"  file: {f} ({new FileInfo(f).Length})");
    }
}
