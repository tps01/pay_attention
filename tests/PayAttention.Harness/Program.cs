using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace PayAttention.Harness;

/// <summary>
/// On-screen OCR harness.
/// Modes: hard (static fixture) | stream (inject lines + latency) | dmesg (fast colored MCE dump).
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
    // Extra OCR junk (not triggers) — must not Contains-match ShotTrigger / RecTrigger.
    private const string PunctuationSoup = "zX9!mQ2@kP $err#99v9";

    /// <summary>Max wall time from inject → TriggerFired for stream/dmesg modes.</summary>
    private const int DetectLatencyBudgetMs = 15_000;

    // Dmesg mode: one realistic MCE trigger phrase (screenshot + record both bind to it).
    // Punctuation in RIP !INEXACT! is folded away by TriggerMatcher (OCR often drops !).
    private const string DmesgTrigger = "RIP !INEXACT!";

    // MobaXterm-like: white body, red keyword highlights. Owner-drawn for PrintWindow.
    private static readonly Color DmesgFg = Color.FromArgb(235, 235, 235);
    private static readonly Color DmesgRed = Color.FromArgb(255, 70, 70);
    private static readonly Color DmesgBg = Color.FromArgb(12, 12, 12);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        ParseArgs(args, out var mode, out var timeoutSec);
        return mode switch
        {
            "stream" => RunStreamMode(timeoutSec),
            "dmesg" => RunDmesgMode(timeoutSec),
            _ => RunHardMode(timeoutSec)
        };
    }

    /// <summary>
    /// Args: [hard|stream|dmesg] [timeoutSec]
    /// Legacy: [timeoutSec] → hard mode (keeps existing pytest invocation).
    /// </summary>
    private static void ParseArgs(string[] args, out string mode, out int timeoutSec)
    {
        mode = "hard";
        timeoutSec = 90;

        if (args.Length == 0)
            return;

        if (string.Equals(args[0], "hard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args[0], "stream", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args[0], "dmesg", StringComparison.OrdinalIgnoreCase))
        {
            mode = args[0].ToLowerInvariant();
            timeoutSec = mode is "stream" or "dmesg" ? 120 : 90;
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
        Form? scrollOverlay = null;
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
            // Scroll phase overlay (lifetime host for Application.Run). REC inject swaps to a
            // fresh fixture form — assigning Text on a TextBox that was AppendText-scrolled
            // leaves PrintWindow/OCR unable to recover err#42v2 even when the line looks fine.
            scrollOverlay = CreateConsoleOverlay("pay_attention_e2e_stream", "", scrollBars: false);
            overlay = scrollOverlay;
            box = (TextBox)overlay.Controls[0];

            var config = BuildConfig(captureDir);
            config.OcrIntervalMs = 400;
            // Thorough (default): fixture after inject needs the full OCR cascade (same tokens as hard).
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
                try { overlay?.Close(); } catch { /* ignore */ }
                try { scrollOverlay?.Close(); } catch { /* ignore */ }
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
                    box.Refresh();
                    overlay.Refresh();
                    System.Windows.Forms.Application.DoEvents();
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
                    // Timed REC inject: hide the scrolled TextBox and show a fresh hard-style
                    // fixture window. Reusing the scrolled TextBox via Text= breaks OCR of
                    // REC>err#42v2< under PrintWindow (shot still matches; rec never does).
                    streamTimer.Stop();
                    scrollOverlay?.Hide();
                    overlay = CreateConsoleOverlay(
                        "pay_attention_e2e_stream_rec",
                        BuildFixtureText(),
                        scrollBars: false);
                    box = (TextBox)overlay.Controls[0];
                    // Timestamp before Show so TriggerFired during first paint counts for latency.
                    recInjectUtc = DateTime.UtcNow;
                    Console.WriteLine($"INJECT rec at {recInjectUtc:O}");
                    overlay.Show();
                    overlay.Activate();
                    overlay.BringToFront();
                    System.Windows.Forms.Application.DoEvents();
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
                    overlay.BringToFront();
                    monitor.Start();
                    Console.WriteLine($"STREAM E2E: monitoring up to {timeoutSec}s…");
                    streamTimer.Start();
                    pollTimer.Start();
                };
                settle.Start();
            };

            // Run on the scroll host so hiding it for the fixture form does not end the loop.
            Application.Run(scrollOverlay);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            exitCode = 3;
        }
        finally
        {
            streamTimer?.Dispose();
            try { overlay?.Dispose(); } catch { /* ignore */ }
            Cleanup(pollTimer, monitor, scrollOverlay, workDir);
        }

        return exitCode;
    }

    /// <summary>
    /// Fast-flowing white/red dmesg with an MCE that scrolls through mid-stream.
    /// Single trigger phrase: RIP !INEXACT! (screenshot + record).
    /// </summary>
    private static int RunDmesgMode(int timeoutSec)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_dmesg_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        Console.WriteLine("Mode: dmesg");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Trigger: {DmesgTrigger}");
        Console.WriteLine($"Latency budget: {DetectLatencyBudgetMs}ms");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        Form? overlay = null;
        DmesgCanvas? canvas = null;
        System.Windows.Forms.Timer? pollTimer = null;
        System.Windows.Forms.Timer? streamTimer = null;
        var logger = new AppLogger(logDir);
        string? shotPath = null;
        string? recPath = null;
        var started = DateTime.UtcNow;
        var finished = false;
        var streamStep = 0;

        DateTime? injectUtc = null;
        int? shotLatencyMs = null;
        int? recLatencyMs = null;

        var script = BuildDmesgScript();

        try
        {
            overlay = CreateColoredConsoleOverlay("pay_attention_e2e_dmesg");
            canvas = overlay.Controls["dmesg"] as DmesgCanvas
                     ?? throw new InvalidOperationException("missing dmesg pane");

            var config = BuildDmesgConfig(captureDir);
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

                if (e.Action == CaptureAction.Screenshot)
                {
                    shotPath ??= e.OutputPath;
                    if (injectUtc is not null && shotLatencyMs is null)
                    {
                        shotLatencyMs = (int)(now - injectUtc.Value).TotalMilliseconds;
                        Console.WriteLine($"LATENCY shot={shotLatencyMs.Value}ms");
                        if (shotLatencyMs.Value > DetectLatencyBudgetMs)
                        {
                            TryFinish(1,
                                $"FAIL: shot detection latency {shotLatencyMs.Value}ms exceeds {DetectLatencyBudgetMs}ms");
                            return;
                        }
                    }
                }

                if (e.Action == CaptureAction.Record)
                {
                    recPath ??= e.OutputPath;
                    if (injectUtc is not null && recLatencyMs is null)
                    {
                        recLatencyMs = (int)(now - injectUtc.Value).TotalMilliseconds;
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

            // Continuous fast scroll — never pause or slow down.
            const int fastMs = 30;
            streamTimer = new System.Windows.Forms.Timer { Interval = fastMs };
            streamTimer.Tick += (_, _) =>
            {
                if (finished || canvas is null || canvas.IsDisposed)
                    return;

                KeepOnTop(overlay);
                streamStep++;

                string line;
                string? inject = null;
                if (streamStep <= script.Count)
                {
                    var ev = script[streamStep - 1];
                    line = ev.Line;
                    inject = ev.Inject;
                }
                else
                {
                    var n = streamStep - script.Count;
                    line = $"[ {2000 + n * 0.25:0.000000}] kernel: workqueue: squashed deferred probe ({n})";
                }

                canvas.AppendLine(line);

                if (inject == "rip" && injectUtc is null)
                {
                    injectUtc = DateTime.UtcNow;
                    Console.WriteLine($"INJECT at {injectUtc:O}");
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
                if (injectUtc is not null &&
                    (shotLatencyMs is null || recLatencyMs is null) &&
                    (now - injectUtc.Value).TotalMilliseconds > DetectLatencyBudgetMs)
                {
                    FailTimeout(shotPath, recPath, captureDir);
                    TryFinish(1, $"FAIL: trigger not fully detected within {DetectLatencyBudgetMs}ms of inject");
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
                    overlay.BringToFront();
                    monitor.Start();
                    Console.WriteLine($"DMESG E2E: monitoring up to {timeoutSec}s…");
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

    private static AppConfig BuildDmesgConfig(string captureDir)
    {
        var config = BuildConfig(captureDir);
        config.OcrIntervalMs = 300;
        config.DebounceSeconds = 1;
        if (config.Applications is { Count: > 0 })
            config.Applications[0].DebounceSeconds = 1;
        // Same phrase, two actions — both fire when RIP !INEXACT! scrolls into view.
        // Fast OCR: brief on-screen lifetime under fast scroll.
        config.Triggers =
        [
            new TriggerConfig
            {
                Text = DmesgTrigger,
                Match = MatchMode.Contains,
                Action = CaptureAction.Screenshot,
                OcrMode = OcrSpeedMode.Fast,
                Enabled = true
            },
            new TriggerConfig
            {
                Text = DmesgTrigger,
                Match = MatchMode.Contains,
                Action = CaptureAction.Record,
                OcrMode = OcrSpeedMode.Fast,
                Enabled = true
            }
        ];
        return config;
    }

    private static AppConfig BuildConfig(string captureDir)
    {
        // Whitelist this harness process so Monitor OCR ignores other apps (Twitch, etc.)
        // even while the user keeps working in them.
        var harnessProcess = ApplicationScopeEvaluator.NormalizeProcessName(
            System.Diagnostics.Process.GetCurrentProcess().ProcessName);
        Console.WriteLine($"Application scope: whitelisted ({harnessProcess})");

        return new AppConfig
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
            ApplicationScope = ApplicationScopeMode.Whitelisted,
            Applications =
            [
                new ApplicationConfig
                {
                    Enabled = true,
                    ProcessName = harnessProcess,
                    ListMode = ApplicationListMode.Whitelist,
                    DebounceSeconds = 4,
                    TriggerFilter = ApplicationTriggerFilter.All
                }
            ],
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
        box.Update();
    }

    private sealed record DmesgEvent(string Line, string? Inject = null);

    /// <summary>White/red owner-drawn dmesg canvas (MobaXterm-style).</summary>
    private static Form CreateColoredConsoleOverlay(string name)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var form = new Form
        {
            Name = name,
            Text = "dmesg - kernel console",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(area.Left + 40, area.Top + 40, Math.Min(900, area.Width - 80), Math.Min(640, area.Height - 80)),
            TopMost = true,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            BackColor = DmesgBg,
            ForeColor = DmesgFg,
            ShowInTaskbar = true
        };

        form.Controls.Add(new DmesgCanvas { Name = "dmesg", Dock = DockStyle.Fill });
        return form;
    }

    /// <summary>
    /// Dense dmesg-style dump (~200+ lines): boot chatter → MCE mid-stream → more noise.
    /// </summary>
    private static List<DmesgEvent> BuildDmesgScript()
    {
        var script = new List<DmesgEvent>();
        void Add(string line, string? inject = null) => script.Add(new DmesgEvent(line, inject));

        // Hand-authored head (looks like a real cold boot).
        string[] head =
        [
            "[    0.000000] Linux version 5.15.0-91-generic (buildd@lcy02) (gcc (Ubuntu 11.4.0-1ubuntu1~22.04) 11.4.0, GNU ld (GNU Binutils for Ubuntu) 2.38)",
            "[    0.000000] Command line: BOOT_IMAGE=/vmlinuz-5.15.0-91-generic root=UUID=a1b2c3d4-e5f6-7890-abcd-ef1234567890 ro quiet splash",
            "[    0.000000] KERNEL supported cpus: Intel GenuineIntel / AMD AuthenticAMD",
            "[    0.000000] x86/fpu: Supporting XSAVE feature 0x001: 'x87 floating point registers'",
            "[    0.000000] x86/fpu: Supporting XSAVE feature 0x002: 'SSE registers'",
            "[    0.000000] x86/fpu: Supporting XSAVE feature 0x004: 'AVX registers'",
            "[    0.000000] BIOS-e820: [mem 0x0000000000000000-0x000000000009ffff] usable",
            "[    0.000000] BIOS-e820: [mem 0x0000000000100000-0x000000007fffffff] usable",
            "[    0.000000] NX (Execute Disable) protection: active",
            "[    0.000000] SMBIOS 3.3.0 present.",
            "[    0.000000] DMI: System manufacturer System Product Name/ROG CROSSHAIR, BIOS 4201 03/15/2023",
            "[    0.000000] tsc: Fast TSC calibration using PIT",
            "[    0.000000] tsc: Detected 3499.999 MHz processor",
            "[    0.000642] e820: update [mem 0x00000000-0x00000fff] usable ==> reserved",
            "[    0.001102] ACPI: Early table checksum verification done",
            "[    0.001440] ACPI: RSDP 0x0000000079F00000 000024 (v02 ALASKA)",
            "[    0.002110] ACPI: XSDT 0x0000000079F000A0 0000B4 (v01 ALASKA A M I 01072009)",
            "[    0.012441] Zone ranges: DMA [mem 0x0000000000001000-0x0000000000ffffff]",
            "[    0.012880] Movable zone start for each node",
            "[    0.013220] Early memory node ranges",
            "[    0.148902] ACPI: PM-Timer IO Port: 0x808",
            "[    0.220114] PCI: Using configuration type 1 for base access",
            "[    0.330228] HugeTLB registered 1.00 GiB page size, pre-allocated 0 pages",
            "[    0.440112] HugeTLB registered 2.00 MiB page size, pre-allocated 0 pages",
            "[    0.512330] smpboot: CPU0: AMD Ryzen 9 5950X 16-Core Processor (family: 0x19, model: 0x21, stepping: 0x0)",
            "[    0.520441] Performance Events: Fam17h+ core perfctr, AMD PMU driver.",
            "[    0.530112] rcu: Hierarchical RCU implementation.",
            "[    0.540228] NR_IRQS: 524544, nr_irqs: 1472, preallocated irqs: 16",
            "[    0.550441] random: crng init done",
            "[    0.560112] Console: colour VGA+ 80x25",
            "[    0.570228] printk: console [tty0] enabled",
            "[    0.580441] ACPI: Core revision 20210730",
            "[    0.590112] clocksource: hpet: mask: 0xffffffff max_cycles: 0xffffffff, max_idle_ns: 133484882848 ns",
            "[    0.600228] APIC: Switched APIC routing to: physical flat",
            "[    0.610441] ..TIMER: vector=0x30 apic1=0 pin1=2 apic2=-1 pin2=-1",
            "[    0.620112] clocksource: tsc-early: mask: 0xffffffffffffffff max_cycles: 0x328f0a161b, max_idle_ns: 440795269093 ns",
            "[    0.630228] Calibrating delay loop (skipped), value calculated using timer frequency.. 6999.99 BogoMIPS",
            "[    0.640441] pid_max: default: 32768 minimum: 301",
            "[    0.650112] LSM: Security Framework initializing",
            "[    0.660228] landlock: Up and running.",
            "[    0.670441] Yama: becoming mindful.",
            "[    0.680112] AppArmor: AppArmor initialized",
            "[    0.690228] Mount-cache hash table entries: 65536 (order: 7, 524288 bytes, linear)",
            "[    0.700441] Mountpoint-cache hash table entries: 65536 (order: 7, 524288 bytes, linear)",
            "[    0.720112] x86/cpu: User Mode Instruction Prevention (UMIP) activated",
            "[    0.740228] Last level iTLB entries: 4KB 512, 2MB 255, 4MB 127",
            "[    0.760441] Last level dTLB entries: 4KB 2048, 2MB 255, 4MB 127",
            "[    0.780112] Spectre V1 : Mitigation: usercopy/swapgs barriers and __user pointer sanitization",
            "[    0.800228] Spectre V2 : Mitigation: Retpolines",
            "[    0.820441] Speculative Store Bypass: Mitigation: Speculative Store Bypass disabled via prctl and seccomp",
            "[    0.840112] Freeing SMP alternatives memory: 40K",
            "[    0.860228] smpboot: Highest APIC ID is 31",
            "[    0.880114] smp: Bringing up secondary CPUs ...",
        ];
        foreach (var line in head)
            Add(line);

        // Bulk boot / driver chatter — brings the dump into the “couple hundred lines” range.
        for (var cpu = 1; cpu <= 31; cpu++)
            Add($"[    {(0.900 + cpu * 0.012):0.000000}] x86: Booting SMP configuration for CPU{cpu}");

        string[] midTemplates =
        [
            "pci 0000:{0:D2}:00.0: [1022:1480] type 00 class 0x060000",
            "pci 0000:{0:D2}:00.1: PME# supported from D0 D3hot D3cold",
            "pci 0000:{0:D2}:01.0: bridge window [mem 0xf{0:x2}000000-0xf{0:x2}ffffff]",
            "acpi PNP0A08:{0:D2}: _OSC: OS supports [ExtendedConfig ASPM ClockPM Segments MSI HPX-Type3]",
            "usb usb{0}: New USB device found, idVendor=1d6b, idProduct=000{1}",
            "usb {0}-1: new high-speed USB device number {1} using xhci_hcd",
            "hid-generic {0:D4}: USB HID v1.11 Keyboard [vendor] on usb-{1}",
            "input: AT Translated Set 2 keyboard as /devices/platform/i8042/serio0/input/input{0}",
            "nvme nvme{0}: pci function 0000:01:00.{0}",
            "nvme nvme{0}: 32/0/0 default/read/poll queues",
            "EXT4-fs (nvme0n1p{0}): mounted filesystem with ordered data mode. Opts: (null).",
            "systemd[1]: Started {1}.",
            "audit: type=1400 audit({0}.000:{1}): apparmor=\"STATUS\" operation=\"profile_load\"",
            "NET: Registered PF_{1} protocol family",
            "cfg80211: Loading compiled-in X.509 certificates for regulatory database",
            "iwlwifi 0000:04:00.0: loaded firmware version {0}.ucode op_mode iwlmvm",
            "thermal thermal_zone{0}: bound trip point {1}",
            "amd_iommu: Lazy IO/TLB flushing enabled",
            "EDAC MC{0}: Giving out device to module amd64_edac controller F17h",
            "kvm: Nested Virtualization enabled",
            "svm: Nested Paging enabled",
            "Bluetooth: HCI device and connection manager initialized",
            "Bluetooth: HCI socket layer initialized",
            "systemd-journald[{0}]: Journal started",
            "kauditd_printk_skb: {0} callbacks suppressed",
            "IPv6: ADDRCONF(NETDEV_CHANGE): eth{0}: link becomes ready",
            "wlp3s0: authenticate with aa:bb:cc:dd:ee:{0:D2}",
            "wlp3s0: send auth to aa:bb:cc:dd:ee:{0:D2} (try {1}/3)",
            "wlp3s0: RX AssocResp from aa:bb:cc:dd:ee:{0:D2} (capab=0x411 status=0)",
            "device-mapper: ioctl: {0}.{1}.0-ioctl (2021-07-09) initialised: dm-devel@redhat.com",
            "RPC: Registered named UNIX socket transport module.",
            "Installing knfsd (copyright (C) 1996 okir@monad.swb.de).",
            "FS-Cache: Loaded",
            "loop: module loaded",
            "tun: Universal TUN/TAP device driver, 1.6",
            "ppp: Multiplexing control protocol initialized",
            "bridge: filtering via arp/ip/ip6tables is no longer available",
            "8021q: 802.1Q VLAN Support v1.8",
            "Key type dns_resolver registered",
            "sched_clock: using local TSC for sched_clock",
            "clocksource: Switched to clocksource tsc",
        ];

        var t = 1.200;
        for (var i = 0; i < 160; i++)
        {
            t += 0.037 + (i % 7) * 0.001;
            var tmpl = midTemplates[i % midTemplates.Length];
            var a = i % 32;
            var b = (i % 5) + 1;
            string body;
            try
            {
                body = string.Format(tmpl, a, b);
            }
            catch (FormatException)
            {
                body = tmpl;
            }

            Add($"[   {t,8:0.000000}] {body}");
        }

        Add("[   12.440112] systemd[1]: Startup finished in 2.104s (kernel) + 10.112s (userspace) = 12.216s.");
        Add("[   14.220441] kauditd_printk_skb: 12 callbacks suppressed");
        Add("[   35.440228] wlp3s0: link established");
        Add("[  120.001102] traps: chrome[8821] general protection fault ip:7f96d7c641d9");
        Add("[  340.112440] systemd[1]: Starting Daily apt download activities...");
        Add("[  340.220112] systemd[1]: apt-daily.service: Deactivated successfully.");

        // MCE arrives mid-stream (pattern from public Ryzen / Intel dmesg samples).
        Add("[1234567.025641] mce: [Hardware Error]: Machine check events logged");
        Add("[1234567.025642] mce: [Hardware Error]: CPU 11: Machine Check Exception: 0 Bank 6: baa0000000020118");
        Add("[1234567.025712] mce: [Hardware Error]: TSC 0 MISC d0130fff00000000 SYND 4d000000");
        Add("[1234567.025785] mce: [Hardware Error]: PROCESSOR 2:a20f10 TIME 1634830638 SOCKET 0 APIC 16");
        Add("[1234567.026001] mcelog: HARDWARE ERROR. This is NOT a software problem!");
        Add("[1234567.026112] mcelog: Please contact your hardware vendor");
        Add("[1234567.026228] mcelog: MCE 0 CPU 11 BANK 6");
        Add("[1234567.026340] mcelog: MCA: corrected filtering (some unreported errors in same region)");
        Add("[1234567.026441] mcelog: Threshold based error status reported");
        Add("[1234567.026512] mcelog: MCG status: RIPV MCIP");
        Add("[1234567.026640] Code: 0f 01 c8 0f ae f0 48 89 e5 53 48 83 ec 18");
        Add("[1234567.026712] Kernel Offset: 0x1a000000 from 0xffffffff81000000");
        Add("[1234567.026780] {1}[Hardware Error]: event severity: corrected");
        Add("[1234567.026840] {1}[Hardware Error]: section_type: physical memory error");
        Add("[1234567.026900] RIP !INEXACT! 10:ffffffff8102c8ca mce_panic+0x3a/0x1b0", "rip");
        Add("[1234567.027000] RSP: 0018:ffffc90000107e80 EFLAGS: 00010046");
        Add("[1234567.027100] RAX: 0000000000000000 RBX: ffff8881002a8000 RCX: 0000000000000001");
        Add("[1234567.027200] Kernel panic - not syncing: Machine check from unknown source");
        Add("[1234567.027300] Kernel Offset: disabled");
        Add("[1234567.027400] ---[ end Kernel panic - not syncing: Machine check ]---");

        string[] modules =
        [
            "ext4", "xfs", "btrfs", "nfs", "cifs", "fuse", "overlay", "dm_mod", "dm_crypt",
            "aesni_intel", "crc32c_intel", "raid1", "raid10", "md_mod", "sg", "sr_mod",
            "sd_mod", "ahci", "libata", "xhci_hcd", "ehci_hcd", "usb_storage", "uas",
            "hid_generic", "usbhid", "i2c_piix4", "i2c_core", "joydev", "evdev",
            "cfg80211", "mac80211", "iwlwifi", "iwlmvm", "btusb", "bluetooth",
            "snd_hda_intel", "snd_hda_codec", "snd_pcm", "soundcore", "drm", "amdgpu",
            "ttm", "drm_kms_helper", "video", "wmi", "acpi_cpufreq", "processor",
        ];
        for (var m = 0; m < modules.Length; m++)
        {
            var ts = 1234567.028000 + m * 0.012;
            Add($"[{ts:0.000000}] {modules[m]}: loading module");
            Add($"[{ts:0.000000}] {modules[m]}: module loaded");
        }

        for (var i = 0; i < 40; i++)
        {
            var ts = 1234568.100 + i * 0.05;
            Add(i switch
            {
                0 => $"[{ts:0.000000}] systemd[1]: Stopped target Multi-User System.",
                1 => $"[{ts:0.000000}] reboot: Restarting system",
                < 10 => $"[{ts:0.000000}] ACPI: Preparing to enter system sleep state S5",
                _ => $"[    {(i - 10) * 0.08:0.000000}] kernel: post-panic watchdog ping {i}"
            });
        }

        return script;
    }

    private static List<(int Start, int Length, Color Color)> CollectDmesgColorSpans(string line)
    {
        var raw = new List<(int Start, int Length, Color Color)>();

        void Add(string pattern, Color color)
        {
            foreach (Match m in Regex.Matches(line, pattern, RegexOptions.IgnoreCase))
                raw.Add((m.Index, m.Length, color));
        }

        Add(@"\[Hardware Error\]", DmesgRed);
        Add(@"\bMachine Check(?: Exception)?\b", DmesgRed);
        Add(@"\bHARDWARE ERROR\b", DmesgRed);
        Add(@"\b(?:error|errors|fail(?:ed|ure)?|exception|panic|oops|fault|critical|mce|inexact)\b", DmesgRed);
        Add(@"\bRIP\b", DmesgRed);

        var kept = new List<(int Start, int Length, Color Color)>();
        var cursor = 0;
        foreach (var span in raw.OrderBy(s => s.Start).ThenByDescending(s => s.Length))
        {
            if (span.Start < cursor)
                continue;
            kept.Add(span);
            cursor = span.Start + span.Length;
        }

        return kept;
    }

    /// <summary>Owner-drawn dmesg surface — GDI text is visible to PrintWindow/OCR.</summary>
    private sealed class DmesgCanvas : Panel
    {
        private readonly List<string> _lines = [];
        private readonly Font _font = new("Consolas", 9f, FontStyle.Regular);
        private const int MaxLines = 48;
        private const int Pad = 8;

        public DmesgCanvas()
        {
            DoubleBuffered = true;
            BackColor = DmesgBg;
            ResizeRedraw = true;
        }

        public void AppendLine(string line)
        {
            _lines.Add(line);
            while (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
            Invalidate();
            Update();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _font.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(DmesgBg);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            var lineHeight = _font.GetHeight(e.Graphics);
            float y = Pad;
            foreach (var line in _lines)
            {
                DrawColoredLine(e.Graphics, line, Pad, y);
                y += lineHeight;
                if (y > Height)
                    break;
            }
        }

        private void DrawColoredLine(Graphics g, string line, float x, float y)
        {
            // Keep the trigger phrase as a single white run — mid-line color splits hurt OCR.
            if (line.Contains(DmesgTrigger, StringComparison.OrdinalIgnoreCase))
            {
                DrawSpan(g, line, DmesgFg, x, y);
                return;
            }

            var spans = CollectDmesgColorSpans(line);
            var i = 0;
            foreach (var span in spans)
            {
                if (span.Start > i)
                    x = DrawSpan(g, line.Substring(i, span.Start - i), DmesgFg, x, y);
                x = DrawSpan(g, line.Substring(span.Start, span.Length), span.Color, x, y);
                i = span.Start + span.Length;
            }

            if (i < line.Length)
                DrawSpan(g, line.Substring(i), DmesgFg, x, y);
        }

        private float DrawSpan(Graphics g, string text, Color color, float x, float y)
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, _font, brush, x, y);
            return x + g.MeasureString(text, _font, int.MaxValue, StringFormat.GenericTypographic).Width;
        }
    }

    private static void KeepOnTop(Form? overlay)
    {
        // Keep visible above other windows without stealing keyboard focus.
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
            var grabber = new ScreenGrabber();
            var harness = ApplicationScopeEvaluator.NormalizeProcessName(
                System.Diagnostics.Process.GetCurrentProcess().ProcessName);
            using var frame = grabber.TryCaptureProcessWindows([harness]) ?? grabber.CapturePrimary();
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
