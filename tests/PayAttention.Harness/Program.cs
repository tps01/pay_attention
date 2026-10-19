using System.Diagnostics;
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
/// Modes: hard | stream | stream-long | dmesg | dmesg-long | many | scope-white |
/// scope-black | pairwise N | fixture (child window only).
/// Pairwise covers difficulty × scroll × speed × multi-pane × OCR × scope (10 cases).
/// </summary>
internal static class Program
{
    // Hostile but within what Windows OCR can recover at ~8pt green-on-black:
    // mixed case + digits. Mid-token punctuation like !/@ is dropped by OCR entirely
    // (probe: zX9!mQ2@kP is unreadable; zX9mQ2kP matches via confusable/fuzzy).
    private const string ShotTrigger = "zX9mQ2kP";
    private const string RecTrigger = "err#42v2";
    /// <summary>OCR-friendly REC token for easy pairwise fixtures (Fast OCR drops # in err#42v2).</summary>
    private const string EasyRecTrigger = "zRecOK42";
    private const string WeirdSentence = "Il0O|B8S5 fail-NOW: retry?";
    private const string RandomToken = "qW3$pL9&nX";
    // Extra OCR junk (not triggers) — must not Contains-match ShotTrigger / RecTrigger.
    private const string PunctuationSoup = "zX9!mQ2@kP $err#99v9";

    /// <summary>Max wall time from inject → TriggerFired for stream/dmesg modes.</summary>
    private const int DetectLatencyBudgetMs = 15_000;

    // Dmesg mode: one realistic MCE trigger phrase (screenshot + record both bind to it).
    // Punctuation in RIP !INEXACT! is folded away by TriggerMatcher (OCR often drops !).
    private const string DmesgTrigger = "RIP !INEXACT!";

    private const string ScopeAlphaTrigger = "kA7nP3qW";
    private const string ScopeBetaTrigger = "mR4tL8vC";
    private const string ScopeDecoyTrigger = "bZ2wH6yD";
    private const string ScopeAlphaExe = "PaWhiteAlpha.exe";
    private const string ScopeBetaExe = "PaWhiteBeta.exe";
    private const string ScopeDecoyExe = "PaScopeDecoy.exe";

    // MobaXterm-like: white body, red keyword highlights. Owner-drawn for PrintWindow.
    private static readonly Color DmesgFg = Color.FromArgb(235, 235, 235);
    private static readonly Color DmesgRed = Color.FromArgb(255, 70, 70);
    private static readonly Color DmesgBg = Color.FromArgb(12, 12, 12);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "inspect")
        {
            try
            {
                var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(args[2]), JsonDefaults.Options)!;
                MediaProof.Require(args[1], config);
                Console.WriteLine("PASS: completed capture and saved pixels verified");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        ParseArgs(args, out var mode, out var timeoutSec, out var manySlot, out var pairwiseId, out var fixtureOpts);
        return mode switch
        {
            "stream" => RunStreamMode(timeoutSec, noiseBeforeShot: 5, noiseBeforeRec: 5),
            "stream-long" => RunStreamMode(timeoutSec, noiseBeforeShot: 40, noiseBeforeRec: 40),
            "dmesg" => RunDmesgMode(timeoutSec, longForm: false),
            "dmesg-long" => RunDmesgMode(timeoutSec, longForm: true),
            "many" => RunManyMode(timeoutSec, manySlot),
            "scope-white" => RunScopeMode(timeoutSec, whitelist: true),
            "scope-black" => RunScopeMode(timeoutSec, whitelist: false),
            "pairwise" => RunPairwise(pairwiseId, timeoutSec),
            "fixture" => RunFixtureMode(fixtureOpts),
            _ => RunHardMode(timeoutSec)
        };
    }

    /// <summary>
    /// Args:
    ///   [hard|stream|stream-long|dmesg|dmesg-long|scope-white|scope-black] [timeoutSec]
    ///   pairwise N [timeoutSec]   (N = 1..10)
    ///   many [first|mid|last] [timeoutSec]
    ///   fixture --phrase TEXT --title TITLE --x N --y N --w N --h N [--easy] [--scroll MS]
    /// Legacy: [timeoutSec] → hard mode (keeps existing pytest invocation).
    /// </summary>
    private static void ParseArgs(
        string[] args,
        out string mode,
        out int timeoutSec,
        out string manySlot,
        out int pairwiseId,
        out FixtureOptions fixtureOpts)
    {
        mode = "hard";
        timeoutSec = 90;
        manySlot = "first";
        pairwiseId = 1;
        fixtureOpts = new FixtureOptions(
            Phrase: ScopeAlphaTrigger,
            Title: "fixture",
            Bounds: new Rectangle(40, 40, 360, 280),
            Easy: false,
            ScrollMs: 0);

        if (args.Length == 0)
            return;

        if (int.TryParse(args[0], out var legacyTimeout))
        {
            timeoutSec = legacyTimeout;
            return;
        }

        mode = args[0].ToLowerInvariant();
        timeoutSec = mode switch
        {
            "stream" or "dmesg" => 120,
            "stream-long" or "dmesg-long" or "scope-white" or "scope-black" => 180,
            "pairwise" => 30,
            "many" => 90,
            _ => 90
        };

        if (mode == "many")
        {
            if (args.Length > 1 &&
                (string.Equals(args[1], "first", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[1], "mid", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[1], "last", StringComparison.OrdinalIgnoreCase)))
            {
                manySlot = args[1].ToLowerInvariant();
                if (args.Length > 2 && int.TryParse(args[2], out var manyTimeout))
                    timeoutSec = manyTimeout;
            }
            else if (args.Length > 1 && int.TryParse(args[1], out var manyTimeoutOnly))
            {
                timeoutSec = manyTimeoutOnly;
            }

            return;
        }

        if (mode == "pairwise")
        {
            if (args.Length < 2 || !int.TryParse(args[1], out pairwiseId) || (pairwiseId < 1 || pairwiseId > PairwiseCases.Length))
            {
                Console.Error.WriteLine("FAIL: usage: pairwise <1..10> [timeoutSec]");
                pairwiseId = 0;
            }

            if (args.Length > 2 && int.TryParse(args[2], out var pairTimeout))
                timeoutSec = pairTimeout;
            return;
        }

        if (mode == "fixture")
        {
            fixtureOpts = ParseFixtureOptions(args.AsSpan(1));
            return;
        }

        if (args.Length > 1 && int.TryParse(args[1], out var t))
            timeoutSec = t;
    }

    /// <summary>
    /// Pairwise covering array (strength 2) over:
    /// Difficulty × Scrolling × TextSpeed × MultiPane × OcrMode × AppScope.
    /// </summary>
    private static readonly HarnessOpts[] PairwiseCases =
    [
        // 1 easy, static, slow*, single, Fast, whitelist
        new(Easy: true, Scrolling: false, FastScroll: false, MultiPane: false, OcrMode: OcrSpeedMode.Fast, WhitelistScope: true),
        // 2 easy, scroll, fast, multi, Thorough, blacklist
        new(Easy: true, Scrolling: true, FastScroll: true, MultiPane: true, OcrMode: OcrSpeedMode.Thorough, WhitelistScope: false),
        // 3 hard, static, fast*, multi, Fast, blacklist
        new(Easy: false, Scrolling: false, FastScroll: true, MultiPane: true, OcrMode: OcrSpeedMode.Fast, WhitelistScope: false),
        // 4 hard, scroll, slow, single, Thorough, whitelist
        new(Easy: false, Scrolling: true, FastScroll: false, MultiPane: false, OcrMode: OcrSpeedMode.Thorough, WhitelistScope: true),
        // 5 easy, static, fast*, multi, Thorough, whitelist
        new(Easy: true, Scrolling: false, FastScroll: true, MultiPane: true, OcrMode: OcrSpeedMode.Thorough, WhitelistScope: true),
        // 6 easy, scroll, slow, single, Fast, blacklist
        new(Easy: true, Scrolling: true, FastScroll: false, MultiPane: false, OcrMode: OcrSpeedMode.Fast, WhitelistScope: false),
        // 7 hard, static, slow*, single, Thorough, blacklist
        new(Easy: false, Scrolling: false, FastScroll: false, MultiPane: false, OcrMode: OcrSpeedMode.Thorough, WhitelistScope: false),
        // 8 hard, scroll, fast, multi, Fast, whitelist
        new(Easy: false, Scrolling: true, FastScroll: true, MultiPane: true, OcrMode: OcrSpeedMode.Fast, WhitelistScope: true),
        new(Easy: true, Scrolling: true, FastScroll: true, MultiPane: false, OcrMode: OcrSpeedMode.Fast, WhitelistScope: true),
        new(Easy: false, Scrolling: true, FastScroll: false, MultiPane: true, OcrMode: OcrSpeedMode.Thorough, WhitelistScope: false),
    ];

    private static int RunPairwise(int id, int timeoutSec)
    {
        if (id < 1 || id > PairwiseCases.Length)
        {
            Console.Error.WriteLine($"FAIL: pairwise id must be 1..{PairwiseCases.Length}, got {id}");
            return 2;
        }

        var opts = PairwiseCases[id - 1];
        Console.WriteLine($"Mode: pairwise");
        Console.WriteLine($"Pairwise: case={id}/{PairwiseCases.Length}");
        Console.WriteLine(
            $"Factors: difficulty={(opts.Easy ? "easy" : "hard")} scrolling={(opts.Scrolling ? "yes" : "no")} " +
            $"speed={(opts.Scrolling ? (opts.FastScroll ? "fast" : "slow") : "n/a")} " +
            $"multi={(opts.MultiPane ? "yes" : "no")} ocr={opts.OcrMode} " +
            $"scope={(opts.WhitelistScope ? "whitelist" : "blacklist")}");

        if (opts.MultiPane)
            return RunScopeMode(timeoutSec, opts.WhitelistScope, opts);

        if (opts.Scrolling)
        {
            if (opts.FastScroll)
                return RunDmesgMode(timeoutSec, longForm: !opts.Easy, opts);
            // Keep noise modest so shot+rec inject fit pairwise's short wall budget
            // (~30s). Hard still uses the hostile fixture text; easy uses the short one.
            var noise = opts.Easy ? 3 : 5;
            return RunStreamMode(timeoutSec, noiseBeforeShot: noise, noiseBeforeRec: noise, opts);
        }

        return RunHardMode(timeoutSec, opts);
    }

    private sealed record HarnessOpts(
        bool Easy,
        bool Scrolling,
        bool FastScroll,
        bool MultiPane,
        OcrSpeedMode OcrMode,
        bool WhitelistScope);

    private static FixtureOptions ParseFixtureOptions(ReadOnlySpan<string> args)
    {
        var phrase = ScopeAlphaTrigger;
        var title = "fixture";
        var x = 40;
        var y = 40;
        var w = 360;
        var h = 280;
        var easy = false;
        var scrollMs = 0;
        for (var i = 0; i < args.Length; i++)
        {
            static bool Next(ReadOnlySpan<string> a, int idx, out string value)
            {
                if (idx + 1 < a.Length)
                {
                    value = a[idx + 1];
                    return true;
                }

                value = "";
                return false;
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--phrase" when Next(args, i, out var p):
                    phrase = p;
                    i++;
                    break;
                case "--title" when Next(args, i, out var t):
                    title = t;
                    i++;
                    break;
                case "--x" when Next(args, i, out var xs) && int.TryParse(xs, out var xi):
                    x = xi;
                    i++;
                    break;
                case "--y" when Next(args, i, out var ys) && int.TryParse(ys, out var yi):
                    y = yi;
                    i++;
                    break;
                case "--w" when Next(args, i, out var ws) && int.TryParse(ws, out var wi):
                    w = wi;
                    i++;
                    break;
                case "--h" when Next(args, i, out var hs) && int.TryParse(hs, out var hi):
                    h = hi;
                    i++;
                    break;
                case "--easy":
                    easy = true;
                    break;
                case "--scroll" when Next(args, i, out var sm) && int.TryParse(sm, out var si):
                    scrollMs = Math.Max(0, si);
                    i++;
                    break;
            }
        }

        return new FixtureOptions(phrase, title, new Rectangle(x, y, w, h), easy, scrollMs);
    }

    private sealed record FixtureOptions(
        string Phrase,
        string Title,
        Rectangle Bounds,
        bool Easy,
        int ScrollMs);

    private static int RunHardMode(int timeoutSec, HarnessOpts? opts = null)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        var textPath = Path.Combine(workDir, "triggers.txt");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        var easy = opts?.Easy == true;
        var fixtureBody = BuildFixtureText(easy);
        File.WriteAllText(textPath, fixtureBody);
        Console.WriteLine(opts is null ? "Mode: hard" : "Mode: hard (pairwise)");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Wrote hard fixture: {textPath}");
        Console.WriteLine($"Shot trigger: {ShotTrigger}");
        Console.WriteLine($"Rec trigger:  {RecTrigger}");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        Form? overlay = null;
        System.Windows.Forms.Timer? pollTimer = null;
        var logger = new AppLogger(logDir) { MinLevel = Environment.GetEnvironmentVariable("PAY_ATTENTION_HARNESS_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info };
        string? shotPath = null;
        string? recPath = null;
        var started = DateTime.UtcNow;
        var finished = false;

        try
        {
            overlay = CreateConsoleOverlay("pay_attention_e2e_hard", fixtureBody, scrollBars: false, easy: easy);

            var config = BuildConfig(captureDir);
            ApplyHarnessOpts(config, opts);
            if (easy)
                UseEasyRecTrigger(config);
            // Record of punctuation-heavy tokens needs Thorough even when shot is Fast.
            foreach (var t in config.Triggers.Where(t => t.Action == CaptureAction.Record))
                t.OcrMode = OcrSpeedMode.Thorough;
            monitor = new CaptureMonitor(config, logger);
            if (!monitor.OcrAvailable)
            {
                Console.Error.WriteLine("FAIL: Windows OCR unavailable");
                return 2;
            }

            void TryFinish(int code, string? detail = null)
            {
                if (overlay is { InvokeRequired: true })
                {
                    overlay.BeginInvoke(new Action(() => TryFinish(code, detail)));
                    return;
                }
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
                    string.Equals(e.TriggerText, RecTrigger, StringComparison.Ordinal) ||
                    string.Equals(e.TriggerText, EasyRecTrigger, StringComparison.Ordinal))
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
                    PrintPass(shotPath!, recPath!, config);
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

    private static int RunStreamMode(
        int timeoutSec,
        int noiseBeforeShot,
        int noiseBeforeRec,
        HarnessOpts? opts = null)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_stream_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        var shotAt = noiseBeforeShot + 1;
        var recAt = shotAt + noiseBeforeRec + 1;
        Console.WriteLine(
            opts is not null ? "Mode: stream (pairwise)" :
            noiseBeforeShot > 5 || noiseBeforeRec > 5 ? "Mode: stream-long" : "Mode: stream");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Shot trigger: {ShotTrigger}");
        Console.WriteLine($"Rec trigger:  {RecTrigger}");
        Console.WriteLine($"Noise before shot/rec: {noiseBeforeShot}/{noiseBeforeRec}");
        Console.WriteLine($"Latency budget: {DetectLatencyBudgetMs}ms per trigger");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        Form? scrollOverlay = null;
        Form? overlay = null;
        TextBox? box = null;
        System.Windows.Forms.Timer? pollTimer = null;
        System.Windows.Forms.Timer? streamTimer = null;
        var logger = new AppLogger(logDir) { MinLevel = Environment.GetEnvironmentVariable("PAY_ATTENTION_HARNESS_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info };
        string? shotPath = null;
        string? recPath = null;
        var started = DateTime.UtcNow;
        var finished = false;
        var streamStep = 0;
        var easy = opts?.Easy == true;

        DateTime? shotInjectUtc = null;
        DateTime? recInjectUtc = null;
        int? shotLatencyMs = null;
        int? recLatencyMs = null;

        try
        {
            // Scroll phase overlay (lifetime host for Application.Run). REC inject swaps to a
            // fresh fixture form — assigning Text on a TextBox that was AppendText-scrolled
            // leaves PrintWindow/OCR unable to recover err#42v2 even when the line looks fine.
            scrollOverlay = CreateConsoleOverlay("pay_attention_e2e_stream", "", scrollBars: false, easy: easy);
            overlay = scrollOverlay;
            box = (TextBox)overlay.Controls[0];

            var config = BuildConfig(captureDir);
            config.OcrIntervalMs = 400;
            ApplyHarnessOpts(config, opts);
            if (easy)
                UseEasyRecTrigger(config);
            // Stream REC fixture needs Thorough; blacklist monitor OCR is noisier still.
            foreach (var t in config.Triggers)
            {
                if (t.Action == CaptureAction.Record || (opts is { WhitelistScope: false }))
                    t.OcrMode = OcrSpeedMode.Thorough;
            }
            monitor = new CaptureMonitor(config, logger);
            if (!monitor.OcrAvailable)
            {
                Console.Error.WriteLine("FAIL: Windows OCR unavailable");
                return 2;
            }

            void TryFinish(int code, string? detail = null)
            {
                var owner = overlay ?? scrollOverlay;
                if (owner is { InvokeRequired: true })
                {
                    owner.BeginInvoke(new Action(() => TryFinish(code, detail)));
                    return;
                }
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

                if (streamStep < shotAt)
                {
                    AppendLine(box, $"[{DateTime.Now:HH:mm:ss.fff}] boot noise {streamStep} status=ok");
                    return;
                }

                if (streamStep == shotAt)
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

                if (streamStep > shotAt && streamStep < recAt)
                {
                    AppendLine(box, $"scroll {streamStep} {WeirdSentence}");
                    return;
                }

                if (streamStep == recAt)
                {
                    // Timed REC inject: hide the scrolled TextBox and show a fresh hard-style
                    // fixture window. Reusing the scrolled TextBox via Text= breaks OCR of
                    // REC>err#42v2< under PrintWindow (shot still matches; rec never does).
                    streamTimer.Stop();
                    scrollOverlay?.Hide();
                    overlay = CreateConsoleOverlay(
                        "pay_attention_e2e_stream_rec",
                        BuildFixtureText(easy),
                        scrollBars: false,
                        easy: easy);
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

                    PrintPass(shotPath!, recPath!, config);
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
    private static int RunDmesgMode(int timeoutSec, bool longForm, HarnessOpts? opts = null)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_dmesg_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        Console.WriteLine(
            opts is not null ? "Mode: dmesg (pairwise)" :
            longForm ? "Mode: dmesg-long" : "Mode: dmesg");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Trigger: {DmesgTrigger}");
        Console.WriteLine($"Latency budget: {DetectLatencyBudgetMs}ms");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        Form? overlay = null;
        DmesgCanvas? canvas = null;
        System.Windows.Forms.Timer? pollTimer = null;
        System.Windows.Forms.Timer? streamTimer = null;
        var logger = new AppLogger(logDir) { MinLevel = Environment.GetEnvironmentVariable("PAY_ATTENTION_HARNESS_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info };
        string? shotPath = null;
        string? recPath = null;
        var started = DateTime.UtcNow;
        var finished = false;
        var streamStep = 0;

        DateTime? injectUtc = null;
        int? shotLatencyMs = null;
        int? recLatencyMs = null;

        var script = BuildDmesgScript(longForm);

        try
        {
            overlay = CreateColoredConsoleOverlay("pay_attention_e2e_dmesg", easy: opts?.Easy == true);
            canvas = overlay.Controls["dmesg"] as DmesgCanvas
                     ?? throw new InvalidOperationException("missing dmesg pane");

            var config = BuildDmesgConfig(captureDir);
            ApplyHarnessOpts(config, opts);
            monitor = new CaptureMonitor(config, logger);
            if (!monitor.OcrAvailable)
            {
                Console.Error.WriteLine("FAIL: Windows OCR unavailable");
                return 2;
            }

            void TryFinish(int code, string? detail = null)
            {
                if (overlay is { InvokeRequired: true })
                {
                    overlay.BeginInvoke(new Action(() => TryFinish(code, detail)));
                    return;
                }
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

                    PrintPass(shotPath!, recPath!, config);
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

    /// <summary>
    /// Static hard fixture with 50 Screenshot triggers; only ShotTrigger is on screen.
    /// Slot places that real row at first / mid (index 24) / last.
    /// </summary>
    private static int RunManyMode(int timeoutSec, string slot)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_many_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);

        var realIndex = slot switch
        {
            "mid" => 24,
            "last" => 49,
            _ => 0
        };

        Console.WriteLine("Mode: many");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Real trigger slot: {slot} (index {realIndex}/49)");
        Console.WriteLine($"Shot trigger: {ShotTrigger}");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        Form? overlay = null;
        System.Windows.Forms.Timer? pollTimer = null;
        var logger = new AppLogger(logDir) { MinLevel = Environment.GetEnvironmentVariable("PAY_ATTENTION_HARNESS_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info };
        string? shotPath = null;
        var started = DateTime.UtcNow;
        var finished = false;
        var badFire = false;

        try
        {
            overlay = CreateConsoleOverlay("pay_attention_e2e_many", BuildFixtureText(), scrollBars: false);
            var config = BuildManyTriggerConfig(captureDir, realIndex);
            monitor = new CaptureMonitor(config, logger);
            if (!monitor.OcrAvailable)
            {
                Console.Error.WriteLine("FAIL: Windows OCR unavailable");
                return 2;
            }

            void TryFinish(int code, string? detail = null)
            {
                if (overlay is { InvokeRequired: true })
                {
                    overlay.BeginInvoke(new Action(() => TryFinish(code, detail)));
                    return;
                }
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
                if (!string.Equals(e.TriggerText, ShotTrigger, StringComparison.Ordinal))
                {
                    badFire = true;
                    TryFinish(1, $"FAIL: unexpected trigger '{e.TriggerText}' (only {ShotTrigger} should fire)");
                    return;
                }

                if (e.Action == CaptureAction.Screenshot)
                    shotPath ??= e.OutputPath;
            };

            monitor.StatusChanged += (_, e) =>
                Console.WriteLine($"Status: {e.Status} {e.Detail}");

            pollTimer = new System.Windows.Forms.Timer { Interval = 200 };
            pollTimer.Tick += (_, _) =>
            {
                if (finished)
                    return;

                KeepOnTop(overlay);
                shotPath ??= Directory.GetFiles(captureDir, "*_screenshot.*").Where(IsStill)
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();

                if (!badFire &&
                    shotPath is not null &&
                    File.Exists(shotPath) &&
                    MediaProof.Ready(shotPath))
                {
                    MediaProof.Require(shotPath!, config);
                    Console.WriteLine("PASS");
                    Console.WriteLine($"  screenshot: {shotPath} ({new FileInfo(shotPath).Length} bytes)");
                    TryFinish(0);
                    return;
                }

                if ((DateTime.UtcNow - started).TotalSeconds < timeoutSec)
                    return;

                FailTimeout(shotPath, null, captureDir);
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
                    Console.WriteLine($"MANY E2E: monitoring up to {timeoutSec}s…");
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

    private static AppConfig BuildManyTriggerConfig(string captureDir, int realIndex)
    {
        var config = BuildConfig(captureDir);
        config.DebounceSeconds = 4;
        var triggers = new List<TriggerConfig>(50);
        for (var i = 0; i < 50; i++)
        {
            var text = i == realIndex ? ShotTrigger : $"qqT{i:D2}zz";
            triggers.Add(new TriggerConfig
            {
                Id = Guid.NewGuid(),
                Text = text,
                Match = MatchMode.Contains,
                Action = CaptureAction.Screenshot,
                OcrMode = OcrSpeedMode.Thorough,
                Enabled = true
            });
        }

        config.Triggers = triggers;
        Console.WriteLine($"Triggers: 50 (real '{ShotTrigger}' at index {realIndex})");
        return config;
    }

    /// <summary>Child process: show one console window and wait until closed.</summary>
    private static int RunFixtureMode(FixtureOptions opts)
    {
        Console.WriteLine("Mode: fixture");
        Console.WriteLine($"Phrase: {opts.Phrase}");
        Console.WriteLine($"Title: {opts.Title}");
        Console.WriteLine($"Bounds: {opts.Bounds}");

        var body = new StringBuilder();
        body.AppendLine($"=== {opts.Title} fixture ===");
        body.AppendLine(opts.Easy ? "noise: easy pane" : "noise: side-by-side scope window");
        body.AppendLine($"TOKEN>{opts.Phrase}<");
        body.AppendLine("noise: end of pane");

        // Scroll fixtures need a vertical scrollbar so AppendText moves content off the top.
        // Difficulty remains independent of scrolling in the covering array.
        var form = CreateConsoleOverlay(
            "pay_attention_e2e_fixture_" + opts.Title,
            body.ToString(),
            scrollBars: opts.ScrollMs > 0,
            easy: opts.Easy);
        form.Text = opts.Title;
        form.Bounds = opts.Bounds;
        form.FormClosed += (_, _) => Application.ExitThread();

        System.Windows.Forms.Timer? scrollTimer = null;
        var scrollStep = 0;
        form.Shown += (_, _) =>
        {
            form.TopMost = true;
            form.BringToFront();
            if (opts.ScrollMs <= 0 || form.Controls.Count == 0 || form.Controls[0] is not TextBox box)
                return;

            scrollTimer = new System.Windows.Forms.Timer { Interval = opts.ScrollMs };
            scrollTimer.Tick += (_, _) =>
            {
                scrollStep++;
                // Real console scroll: append noise, then re-emit the token so it stays
                // in the visible bottom of the pane for OCR (ScrollToCaret).
                AppendLine(box, $"[{DateTime.Now:HH:mm:ss.fff}] noise scroll {scrollStep}");
                AppendLine(box, $"TOKEN>{opts.Phrase}<");
            };
            scrollTimer.Start();
        };
        form.FormClosed += (_, _) =>
        {
            scrollTimer?.Stop();
            scrollTimer?.Dispose();
        };
        Application.Run(form);
        return 0;
    }

    private static int RunScopeMode(int timeoutSec, bool whitelist, HarnessOpts? opts = null)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pay_attention_e2e_scope_" + Guid.NewGuid().ToString("N")[..8]);
        var captureDir = Path.Combine(workDir, "captures");
        var logDir = Path.Combine(workDir, "logs");
        var binDir = Path.Combine(workDir, "bin");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(binDir);

        Console.WriteLine(
            opts is not null ? $"Mode: scope-{(whitelist ? "white" : "black")} (pairwise)" :
            whitelist ? "Mode: scope-white" : "Mode: scope-black");
        Console.WriteLine($"Work dir: {workDir}");
        Console.WriteLine($"Alpha: {ScopeAlphaTrigger}  Beta: {ScopeBetaTrigger}  Decoy: {ScopeDecoyTrigger}");

        var exitCode = 1;
        CaptureMonitor? monitor = null;
        System.Windows.Forms.Timer? pollTimer = null;
        Form? host = null;
        var logger = new AppLogger(logDir) { MinLevel = Environment.GetEnvironmentVariable("PAY_ATTENTION_HARNESS_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info };
        var children = new List<Process>();
        var started = DateTime.UtcNow;
        var finished = false;
        string? alphaPath = null;
        string? betaPath = null;
        var decoyFired = false;
        // Whitelist: 0 = Alpha foreground, 1 = Beta foreground. Blacklist: unused (both panes stay up).
        var phase = 0;
        var easy = opts?.Easy == true;
        // Multi-pane scroll must stay slow enough for OCR (esp. Thorough) to catch tokens
        // at the bottom of the pane; stream/dmesg single-window paths use their own timers.
        var scrollMs = opts is { Scrolling: true }
            ? (opts.FastScroll
                ? (opts.OcrMode == OcrSpeedMode.Thorough ? 220 : 120)
                : 350)
            : 0;

        try
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            var paneW = Math.Min(340, (area.Width - 80) / 3);
            var paneH = Math.Min(280, area.Height - 80);
            var y = area.Top + 40;
            var alphaBounds = new Rectangle(area.Left + 20, y, paneW, paneH);
            var betaBounds = new Rectangle(area.Left + 30 + paneW, y, paneW, paneH);
            var decoyBounds = new Rectangle(area.Left + 40 + 2 * paneW, y, paneW, paneH);

            EnsureScopeExeCopies(binDir);
            children.Add(StartFixtureChild(binDir, ScopeAlphaExe, ScopeAlphaTrigger, "Alpha", alphaBounds, easy, scrollMs));
            children.Add(StartFixtureChild(binDir, ScopeBetaExe, ScopeBetaTrigger, "Beta", betaBounds, easy, scrollMs));
            children.Add(StartFixtureChild(binDir, ScopeDecoyExe, ScopeDecoyTrigger, "Decoy", decoyBounds, easy, scrollMs));
            Thread.Sleep(1200);

            var alphaId = Guid.NewGuid();
            var betaId = Guid.NewGuid();
            var decoyId = Guid.NewGuid();
            var config = BuildScopeConfig(captureDir, whitelist, alphaId, betaId, decoyId);
            ApplyHarnessOpts(config, opts, preserveScopeApps: true);
            // Decoy stays masked on screen; disable its trigger so AllEnabledMatched can
            // early-stop once Alpha+Beta are found (avoids burning Thorough on more HWNDs).
            if (opts is { Scrolling: true })
            {
                foreach (var t in config.Triggers)
                {
                    if (string.Equals(t.Text, ScopeDecoyTrigger, StringComparison.Ordinal))
                        t.Enabled = false;
                }
            }
            monitor = new CaptureMonitor(config, logger);
            if (!monitor.OcrAvailable)
            {
                Console.Error.WriteLine("FAIL: Windows OCR unavailable");
                return 2;
            }

            host = new Form
            {
                Text = "pay_attention scope host",
                ShowInTaskbar = false,
                Opacity = 0,
                Width = 1,
                Height = 1,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000)
            };

            void TryFinish(int code, string? detail = null)
            {
                if (host is { InvokeRequired: true })
                {
                    host.BeginInvoke(new Action(() => TryFinish(code, detail)));
                    return;
                }
                if (finished)
                    return;
                finished = true;
                exitCode = code;
                if (detail is not null)
                    Console.Error.WriteLine(detail);
                pollTimer?.Stop();
                try { monitor?.Stop(); } catch { /* ignore */ }
                host?.Close();
            }

            monitor.TriggerFired += (_, e) =>
            {
                Console.WriteLine($"Fired: {e.TriggerText} ({e.Action}) -> {e.OutputPath}");
                if (string.Equals(e.TriggerText, ScopeDecoyTrigger, StringComparison.Ordinal))
                {
                    decoyFired = true;
                    TryFinish(1, $"FAIL: decoy trigger '{ScopeDecoyTrigger}' fired");
                    return;
                }

                if (string.Equals(e.TriggerText, ScopeAlphaTrigger, StringComparison.Ordinal) &&
                    e.Action == CaptureAction.Screenshot)
                    alphaPath ??= e.OutputPath;
                if (string.Equals(e.TriggerText, ScopeBetaTrigger, StringComparison.Ordinal) &&
                    e.Action == CaptureAction.Screenshot)
                    betaPath ??= e.OutputPath;
            };

            monitor.StatusChanged += (_, e) =>
                Console.WriteLine($"Status: {e.Status} {e.Detail}");

            pollTimer = new System.Windows.Forms.Timer { Interval = 250 };
            pollTimer.Tick += (_, _) =>
            {
                if (finished)
                    return;

                alphaPath ??= FindShotFor(captureDir, ScopeAlphaTrigger);
                betaPath ??= FindShotFor(captureDir, ScopeBetaTrigger);

                if (whitelist)
                {
                    if (phase == 0)
                    {
                        ActivateProcessMainWindow(children[0]);
                        if (alphaPath is not null && File.Exists(alphaPath) && MediaProof.Ready(alphaPath))
                        {
                            if (betaPath is not null)
                            {
                                TryFinish(1, "FAIL: Beta fired while Alpha was foreground");
                                return;
                            }

                            Console.WriteLine("Phase: Alpha captured; switching to Beta foreground");
                            phase = 1;
                            ActivateProcessMainWindow(children[1]);
                        }
                    }
                    else
                    {
                        ActivateProcessMainWindow(children[1]);
                        if (betaPath is not null && File.Exists(betaPath) && MediaProof.Ready(betaPath))
                        {
                            if (decoyFired)
                                return;
                            MediaProof.Require(alphaPath!, config);
                            MediaProof.Require(betaPath!, config);
                            Console.WriteLine("PASS");
                            Console.WriteLine($"  alpha: {alphaPath}");
                            Console.WriteLine($"  beta:  {betaPath}");
                            Console.WriteLine("  screenshot: scope-white");
                            TryFinish(0);
                            return;
                        }
                    }
                }
                else
                {
                    // All three panes stay visible. Focus stays on Alpha so a fully
                    // blacklisted foreground cannot skip OCR. Alpha and Beta must both fire;
                    // the masked decoy must not.
                    ActivateProcessMainWindow(children[0]);
                    if (alphaPath is not null && betaPath is not null &&
                        File.Exists(alphaPath) && File.Exists(betaPath) &&
                        MediaProof.Ready(alphaPath) &&
                        MediaProof.Ready(betaPath))
                    {
                        if (decoyFired)
                            return;
                        MediaProof.Require(alphaPath!, config);
                        MediaProof.Require(betaPath!, config);
                        Console.WriteLine("PASS");
                        Console.WriteLine($"  alpha: {alphaPath}");
                        Console.WriteLine($"  beta:  {betaPath}");
                        Console.WriteLine("  screenshot: scope-black");
                        TryFinish(0);
                        return;
                    }
                }

                if ((DateTime.UtcNow - started).TotalSeconds < timeoutSec)
                    return;

                FailTimeout(alphaPath, betaPath, captureDir);
                TryFinish(1, "FAIL: timed out waiting for scoped captures");
            };

            host.Shown += (_, _) =>
            {
                var settle = new System.Windows.Forms.Timer { Interval = 900 };
                settle.Tick += (_, _) =>
                {
                    settle.Stop();
                    settle.Dispose();
                    ActivateProcessMainWindow(children[0]);
                    monitor.Start();
                    Console.WriteLine($"SCOPE E2E: monitoring up to {timeoutSec}s…");
                    pollTimer.Start();
                };
                settle.Start();
            };

            host.FormClosed += (_, _) => Application.ExitThread();
            Application.Run(host);
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
            foreach (var p in children)
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }

                try { p.Dispose(); } catch { /* ignore */ }
            }

            try { host?.Dispose(); } catch { /* ignore */ }

            try
            {
                if (Directory.Exists(workDir))
                    ArchiveEvidence(workDir);
            }
            catch
            {
                Console.WriteLine($"Note: left work dir at {workDir}");
            }
        }

        return exitCode;
    }

    private static AppConfig BuildScopeConfig(
        string captureDir,
        bool whitelist,
        Guid alphaId,
        Guid betaId,
        Guid decoyId)
    {
        var alphaProc = ApplicationScopeEvaluator.NormalizeProcessName(ScopeAlphaExe);
        var betaProc = ApplicationScopeEvaluator.NormalizeProcessName(ScopeBetaExe);
        var decoyProc = ApplicationScopeEvaluator.NormalizeProcessName(ScopeDecoyExe);

        var triggers = new List<TriggerConfig>
        {
            new()
            {
                Id = alphaId,
                Text = ScopeAlphaTrigger,
                Match = MatchMode.Contains,
                Action = CaptureAction.Screenshot,
                Enabled = true
            },
            new()
            {
                Id = betaId,
                Text = ScopeBetaTrigger,
                Match = MatchMode.Contains,
                Action = CaptureAction.Screenshot,
                Enabled = true
            },
            new()
            {
                Id = decoyId,
                Text = ScopeDecoyTrigger,
                Match = MatchMode.Contains,
                Action = CaptureAction.Screenshot,
                Enabled = true
            }
        };

        List<ApplicationConfig> apps;
        ApplicationScopeMode scope;
        if (whitelist)
        {
            scope = ApplicationScopeMode.Whitelisted;
            apps =
            [
                new ApplicationConfig
                {
                    Enabled = true,
                    ProcessName = alphaProc,
                    ListMode = ApplicationListMode.Whitelist,
                    DebounceSeconds = 2,
                    TriggerFilter = ApplicationTriggerFilter.Selected,
                    TriggerIds = [alphaId]
                },
                new ApplicationConfig
                {
                    Enabled = true,
                    ProcessName = betaProc,
                    ListMode = ApplicationListMode.Whitelist,
                    DebounceSeconds = 2,
                    TriggerFilter = ApplicationTriggerFilter.Selected,
                    TriggerIds = [betaId]
                }
            ];
            Console.WriteLine($"Application scope: whitelisted ({alphaProc}, {betaProc})");
        }
        else
        {
            scope = ApplicationScopeMode.AllMinusBlacklisted;
            apps =
            [
                new ApplicationConfig
                {
                    Enabled = true,
                    ProcessName = decoyProc,
                    ListMode = ApplicationListMode.Blacklist,
                    DebounceSeconds = 2,
                    TriggerFilter = ApplicationTriggerFilter.All
                },
                new ApplicationConfig
                {
                    Enabled = true,
                    ProcessName = "PayAttention",
                    ListMode = ApplicationListMode.Blacklist,
                    DebounceSeconds = 2,
                    TriggerFilter = ApplicationTriggerFilter.All
                }
            ];
            Console.WriteLine($"Application scope: all-minus-blacklisted (mask {decoyProc})");
        }

        return new AppConfig
        {
            OcrIntervalMs = 400,
            DebounceSeconds = 2,
            RecordBufferSeconds = 5,
            RecordFps = 3,
            RecordFormat = RecordFormat.Avi,
            RecordJpegQuality = 70,
            ScreenshotFormat = ScreenshotFormat.Jpeg,
            OcrMaxWidth = 1920,
            OcrUpscalePercent = 100,
            CaptureScope = CaptureScope.Monitor,
            MonitorSelection = MonitorSelection.Primary,
            ApplicationScope = scope,
            Applications = apps,
            CaptureDirectory = captureDir,
            DebugSaveJsonSidecars = false,
            Triggers = triggers
        };
    }

    private static void EnsureScopeExeCopies(string binDir)
    {
        var self = Environment.ProcessPath
                   ?? throw new InvalidOperationException("ProcessPath unavailable");
        var selfDir = Path.GetDirectoryName(self)
                      ?? throw new InvalidOperationException("missing exe directory");

        // Dependency DLL/deps must sit next to each renamed apphost.
        foreach (var file in Directory.GetFiles(selfDir))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(binDir, fileName), overwrite: true);
        }

        foreach (var name in new[] { ScopeAlphaExe, ScopeBetaExe, ScopeDecoyExe })
            File.Copy(self, Path.Combine(binDir, name), overwrite: true);
    }

    private static Process StartFixtureChild(
        string binDir,
        string exeName,
        string phrase,
        string title,
        Rectangle bounds,
        bool easy = false,
        int scrollMs = 0)
    {
        var exe = Path.Combine(binDir, exeName);
        var args =
            $"fixture --phrase {phrase} --title {title} --x {bounds.X} --y {bounds.Y} --w {bounds.Width} --h {bounds.Height}";
        if (easy)
            args += " --easy";
        if (scrollMs > 0)
            args += $" --scroll {scrollMs}";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            WorkingDirectory = binDir
        };
        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("failed to start " + exeName);
        Console.WriteLine($"Started {exeName} pid={proc.Id} bounds={bounds}");
        return proc;
    }

    private static void ActivateProcessMainWindow(Process proc)
    {
        try
        {
            proc.Refresh();
            if (proc.HasExited)
                return;
            var hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return;
            // Harness-only: brief Alt pulse so SetForegroundWindow can succeed under
            // Windows focus lock. Product CaptureMonitor must never inject keys.
            PulseForegroundPermission();
            NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch
        {
            // ignore activation races
        }
    }

    /// <summary>
    /// Marks this process as having recent input so SetForegroundWindow can succeed
    /// under focus lock. Used only by the e2e harness, never by production monitoring.
    /// </summary>
    private static void PulseForegroundPermission()
    {
        const byte vkMenu = 0x12;
        const uint keyeventfKeyup = 0x0002;
        NativeMethods.keybd_event(vkMenu, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(vkMenu, 0, keyeventfKeyup, UIntPtr.Zero);
    }

    private static string? FindShotFor(string captureDir, string triggerText)
    {
        var safe = new string(triggerText.Where(ch => char.IsLetterOrDigit(ch)).ToArray());
        if (safe.Length == 0)
            return null;
        return Directory.GetFiles(captureDir, $"*{safe}*_screenshot.*").Where(IsStill)
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
            System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
            System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
            System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
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

    /// <summary>
    /// Apply pairwise OCR mode and (for single-pane recipes) whitelist vs blacklist.
    /// Scope multi-pane configs already encode apps — only OCR mode is overwritten then.
    /// </summary>
    private static void ApplyHarnessOpts(AppConfig config, HarnessOpts? opts, bool preserveScopeApps = false)
    {
        if (opts is null)
            return;

        foreach (var t in config.Triggers ?? [])
            t.OcrMode = opts.OcrMode;

        if (preserveScopeApps)
            return;

        if (opts.WhitelistScope)
        {
            // BuildConfig already whitelists the harness process.
            return;
        }

        config.ApplicationScope = ApplicationScopeMode.AllMinusBlacklisted;
        config.Applications =
        [
            new ApplicationConfig
            {
                Enabled = true,
                ProcessName = "PayAttention",
                ListMode = ApplicationListMode.Blacklist,
                DebounceSeconds = config.DebounceSeconds,
                TriggerFilter = ApplicationTriggerFilter.All
            }
        ];
        Console.WriteLine("Application scope: all-minus-blacklisted (pairwise)");
    }

    private static void UseEasyRecTrigger(AppConfig config)
    {
        foreach (var t in config.Triggers ?? [])
        {
            if (t.Action == CaptureAction.Record ||
                string.Equals(t.Text, RecTrigger, StringComparison.Ordinal))
                t.Text = EasyRecTrigger;
        }
    }

    private static string BuildFixtureText(bool easy = false)
    {
        var sb = new StringBuilder();
        if (easy)
        {
            sb.AppendLine("=== pay_attention EASY fixture ===");
            sb.AppendLine("noise: clear readable lines");
            sb.AppendLine($"SHOT>{ShotTrigger}<");
            sb.AppendLine($"REC>{EasyRecTrigger}<");
            sb.AppendLine(EasyRecTrigger);
            sb.AppendLine(EasyRecTrigger);
            sb.AppendLine($"SHOT>{ShotTrigger}<");
            return sb.ToString();
        }

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

    private static Form CreateConsoleOverlay(string name, string text, bool scrollBars, bool easy = false)
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
            Font = new Font("Consolas", easy ? 12f : 8.25f, FontStyle.Regular),
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
    private static Form CreateColoredConsoleOverlay(string name, bool easy = false)
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

        form.Controls.Add(new DmesgCanvas(easy) { Name = "dmesg", Dock = DockStyle.Fill });
        return form;
    }

    /// <summary>
    /// Dense dmesg-style dump (~200+ lines, or ~2× when longForm): boot chatter → MCE → noise.
    /// </summary>
    private static List<DmesgEvent> BuildDmesgScript(bool longForm)
    {
        var script = new List<DmesgEvent>();
        void Add(string line, string? inject = null) => script.Add(new DmesgEvent(line, inject));
        var midCount = longForm ? 320 : 160;
        var tailCount = longForm ? 80 : 40;

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
        for (var i = 0; i < midCount; i++)
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

        if (longForm)
        {
            for (var cpu = 1; cpu <= 31; cpu++)
                Add($"[    {(2.900 + cpu * 0.012):0.000000}] x86: Re-probing SMP topology for CPU{cpu}");
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
        var modulePasses = longForm ? 2 : 1;
        for (var pass = 0; pass < modulePasses; pass++)
        {
            for (var m = 0; m < modules.Length; m++)
            {
                var ts = 1234567.028000 + (pass * modules.Length + m) * 0.012;
                Add($"[{ts:0.000000}] {modules[m]}: loading module");
                Add($"[{ts:0.000000}] {modules[m]}: module loaded");
            }
        }

        for (var i = 0; i < tailCount; i++)
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
        private readonly Font _font;
        private const int MaxLines = 48;
        private const int Pad = 8;

        public DmesgCanvas(bool easy = false)
        {
            _font = new Font("Consolas", easy ? 12f : 9f, FontStyle.Regular);
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
        shotPath ??= Directory.GetFiles(captureDir, "*_screenshot.*").Where(IsStill)
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault();
        recPath ??= Directory.GetFiles(captureDir, "*_record.avi")
            .Concat(Directory.GetFiles(captureDir, "*_record.gif"))
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsStill(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";

    private static bool CapturesReady(string? shotPath, string? recPath) => MediaProof.Ready(shotPath) && MediaProof.Ready(recPath);

    private static void PrintPass(string shotPath, string recPath, AppConfig config)
    {
        MediaProof.Require(shotPath, config);
        MediaProof.Require(recPath, config);
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
                ArchiveEvidence(workDir);
        }
        catch
        {
            Console.WriteLine($"Note: left work dir at {workDir}");
        }
    }

    private static void ArchiveEvidence(string workDir)
    {
        var targetRoot = Environment.GetEnvironmentVariable("PAY_ATTENTION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            Console.WriteLine($"Evidence preserved: {workDir}");
            return;
        }
        var source = Path.GetFullPath(workDir);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(source).StartsWith("pay_attention_e2e", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected harness evidence root");
        var target = Path.Combine(targetRoot, Path.GetFileName(source));
        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        Console.WriteLine($"Evidence archived: {target}");
        // The isolated source is retained too, so a failed copy never destroys evidence.
    }

    private static void DumpDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var f in Directory.GetFiles(dir))
            Console.Error.WriteLine($"  file: {f} ({new FileInfo(f).Length})");
    }
}
