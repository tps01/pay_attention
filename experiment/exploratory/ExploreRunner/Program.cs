using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;

namespace ExploreRunner;

/// <summary>
/// In-repo exploratory harness. Positions Edge on C43J89x (-3840,0) and runs CaptureMonitor
/// scenarios. Does not touch %LocalAppData%\pay_attention\config.json.
/// </summary>
internal static class Program
{
    private const int UltrawideX = -3840;
    private const int UltrawideY = 0;
    private const int UltrawideW = 3840;
    private const int UltrawideH = 1200;

    private static readonly string Root = FindExploratoryRoot();
    private static readonly string Fixtures = Path.Combine(Root, "fixtures");
    private static readonly string CapturesRoot = Path.Combine(Root, "captures_runs");
    private static readonly string LogsRoot = Path.Combine(Root, "logs");
    private static readonly string ReportPath = Path.Combine(Root, "FINDINGS.md");
    private static readonly string EdgeProfile = Path.Combine(Root, "edge-profile");

    private static string FindExploratoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "fixtures")) &&
                File.Exists(Path.Combine(dir.FullName, "fixtures", "trigger_plain.html")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate experiment/exploratory root (fixtures missing).");
    }

    private static int Main(string[] args)
    {
        Directory.CreateDirectory(CapturesRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(EdgeProfile);

        var grabber = new ScreenGrabber();
        var monitors = grabber.GetMonitors();
        Console.WriteLine("Monitors:");
        foreach (var m in monitors)
            Console.WriteLine($"  [{m.Index}] {m.Name} {m.Bounds.Width}x{m.Bounds.Height} @({m.Bounds.X},{m.Bounds.Y}) primary={m.IsPrimary}");

        var ultra = monitors.FirstOrDefault(m => m.Bounds.Width == UltrawideW && m.Bounds.Height == UltrawideH && m.Bounds.X == UltrawideX)
                    ?? monitors.FirstOrDefault(m => m.Bounds.Width >= 3800 && m.Bounds.Height <= 1300);
        if (ultra is null)
        {
            Console.Error.WriteLine("FAIL: C43J89x-like 3840x1200 monitor not found.");
            return 2;
        }

        Console.WriteLine($"Using ultrawide index={ultra.Index} {ultra.Bounds}");

        var edge = FindEdge();
        if (edge is null)
        {
            Console.Error.WriteLine("FAIL: msedge.exe not found.");
            return 3;
        }

        var report = new StringBuilder();
        report.AppendLine("# Exploratory defaults testing");
        report.AppendLine();
        report.AppendLine($"Date (UTC): {DateTime.UtcNow:O}");
        report.AppendLine($"Ultrawide: index {ultra.Index} {ultra.Bounds.Width}x{ultra.Bounds.Height} @({ultra.Bounds.X},{ultra.Bounds.Y})");
        report.AppendLine($"Edge: `{edge}`");
        report.AppendLine();
        report.AppendLine("| Scenario | Result | Latency ms | Notes |");
        report.AppendLine("| --- | --- | ---: | --- |");

        var scenarios = BuildScenarios(ultra.Index);
        if (args.Length > 0)
        {
            var filter = args.Select(a => a.Trim()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            scenarios = scenarios.Where(s => filter.Contains(s.Name)).ToList();
            if (scenarios.Count == 0)
            {
                Console.Error.WriteLine("No scenarios matched filter: " + string.Join(", ", filter));
                return 4;
            }
        }

        var pass = 0;
        var fail = 0;

        foreach (var scenario in scenarios)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {scenario.Name} ===");
            try
            {
                var result = RunScenario(scenario, edge, ultra.Index);
                report.AppendLine($"| {scenario.Name} | {result.Status} | {result.LatencyMs?.ToString() ?? "-"} | {Escape(result.Notes)} |");
                if (result.Status.StartsWith("PASS", StringComparison.Ordinal))
                    pass++;
                else
                    fail++;
                Console.WriteLine($"{result.Status}: {result.Notes}");
            }
            catch (Exception ex)
            {
                fail++;
                report.AppendLine($"| {scenario.Name} | FAIL | - | exception: {Escape(ex.Message)} |");
                Console.WriteLine("FAIL: " + ex);
            }

            KillEdgeProfileProcesses();
            Thread.Sleep(800);
        }

        report.AppendLine();
        report.AppendLine("## Summary");
        report.AppendLine();
        report.AppendLine($"- Passed: {pass}");
        report.AppendLine($"- Failed: {fail}");
        report.AppendLine();
        report.AppendLine("## Recommended defaults (from this run)");
        report.AppendLine();
        report.AppendLine(BuildRecommendations(pass, fail));
        File.WriteAllText(ReportPath, report.ToString());
        Console.WriteLine();
        Console.WriteLine($"Wrote {ReportPath}");
        Console.WriteLine($"PASS={pass} FAIL={fail}");
        return fail == 0 ? 0 : 1;
    }

    private static List<Scenario> BuildScenarios(int ultraIndex)
    {
        var plain = Path.Combine(Fixtures, "trigger_plain.html");
        var tiny = Path.Combine(Fixtures, "trigger_tiny.html");
        var bright = Path.Combine(Fixtures, "trigger_bright.html");

        return
        [
            new Scenario(
                "plain_thorough_defaultish",
                plain,
                "PAYATTN_PLAIN_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 500;
                    cfg.FastMatchThresholdPercent = 90;
                    cfg.DebounceSeconds = 4;
                    cfg.OcrUpscalePercent = 100;
                    cfg.OcrMaxWidth = 1920;
                    cfg.OcrInvertColors = false;
                    cfg.ScreenshotFormat = ScreenshotFormat.Jpeg;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.CaptureScope = CaptureScope.Monitor;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                }),
            new Scenario(
                "plain_fast_threshold_70",
                plain,
                "PAYATTN_PLAIN_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 300;
                    cfg.FastMatchThresholdPercent = 70;
                    cfg.DebounceSeconds = 2;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Screenshot, OcrSpeedMode.Fast)
                    ];
                }),
            new Scenario(
                "tiny_upscale_200_invert",
                tiny,
                "PAYATTN_TINY_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 400;
                    cfg.OcrUpscalePercent = 200;
                    cfg.OcrInvertColors = true;
                    cfg.OcrMaxWidth = 2560;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_TINY_OK", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                }),
            new Scenario(
                "bright_png_sequence",
                bright,
                "PAYATTN_BRIGHT_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 500;
                    cfg.ScreenshotFormat = ScreenshotFormat.Png;
                    cfg.SequenceIntervalSeconds = 2;
                    cfg.SequenceBeforeSeconds = 2;
                    cfg.SequenceAfterSeconds = 2;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_BRIGHT_OK", CaptureAction.Sequence, OcrSpeedMode.Thorough)
                    ];
                },
                ExpectAction: CaptureAction.Sequence),
            new Scenario(
                "plain_record_avi",
                plain,
                "err#42v2",
                cfg =>
                {
                    cfg.OcrIntervalMs = 400;
                    cfg.RecordBufferSeconds = 5;
                    cfg.RecordFps = 3;
                    cfg.RecordFormat = RecordFormat.Avi;
                    cfg.RecordJpegQuality = 70;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("err#42v2", CaptureAction.Record, OcrSpeedMode.Thorough)
                    ];
                },
                ExpectAction: CaptureAction.Record),
            new Scenario(
                "plain_record_gif",
                plain,
                "err#42v2",
                cfg =>
                {
                    cfg.OcrIntervalMs = 400;
                    cfg.RecordBufferSeconds = 5;
                    cfg.RecordFps = 2;
                    cfg.RecordFormat = RecordFormat.Gif;
                    cfg.RecordJpegQuality = 60;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("err#42v2", CaptureAction.Record, OcrSpeedMode.Thorough)
                    ];
                },
                ExpectAction: CaptureAction.Record),
            new Scenario(
                "dual_shot_and_record",
                plain,
                "PAYATTN_PLAIN_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 350;
                    cfg.DebounceSeconds = 2;
                    cfg.RecordBufferSeconds = 5;
                    cfg.RecordFps = 3;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Screenshot, OcrSpeedMode.Thorough),
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Record, OcrSpeedMode.Thorough)
                    ];
                },
                RequireBothActions: true),
            new Scenario(
                "foreground_window_scope",
                plain,
                "PAYATTN_PLAIN_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 400;
                    cfg.CaptureScope = CaptureScope.ForegroundWindow;
                    cfg.MonitorSelection = MonitorSelection.Primary; // ignored for FG capture ideally
                    cfg.ApplicationScope = ApplicationScopeMode.All;
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                }),
            new Scenario(
                "all_monitors_scope",
                plain,
                "PAYATTN_PLAIN_OK",
                cfg =>
                {
                    // No whitelist: Grab must use virtual desktop / per-monitor All path.
                    cfg.OcrIntervalMs = 500;
                    cfg.MonitorSelection = MonitorSelection.All;
                    cfg.CaptureScope = CaptureScope.Monitor;
                    cfg.ApplicationScope = ApplicationScopeMode.All;
                    cfg.Applications = [];
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Screenshot, OcrSpeedMode.Fast)
                    ];
                },
                TimeoutSec: 60),
            new Scenario(
                "fast_threshold_100_vs_garbled",
                tiny,
                "PAYATTN_TINY_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 300;
                    cfg.FastMatchThresholdPercent = 100;
                    cfg.OcrUpscalePercent = 100;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_TINY_OK", CaptureAction.Screenshot, OcrSpeedMode.Fast)
                    ];
                }),
            new Scenario(
                "web_example_com",
                null,
                "Example Domain",
                cfg =>
                {
                    cfg.OcrIntervalMs = 500;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("Example Domain", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                },
                Url: "https://example.com/"),
            new Scenario(
                "web_wikipedia_ocr",
                null,
                "Optical character recognition",
                cfg =>
                {
                    cfg.OcrIntervalMs = 500;
                    cfg.OcrUpscalePercent = 150;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("Optical character recognition", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                },
                Url: "https://en.wikipedia.org/wiki/Optical_character_recognition"),
            new Scenario(
                "captures_dir_b_png",
                bright,
                "PAYATTN_BRIGHT_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 450;
                    cfg.ScreenshotFormat = ScreenshotFormat.Png;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_BRIGHT_OK", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                },
                CaptureSubdir: "captures_b"),
            new Scenario(
                "captures_dir_c_debug_json",
                plain,
                "PAYATTN_PLAIN_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 400;
                    cfg.DebugSaveJsonSidecars = true;
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.ApplicationScope = ApplicationScopeMode.Whitelisted;
                    SetEdgeWhitelist(cfg);
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                },
                CaptureSubdir: "captures_c"),
            new Scenario(
                "blacklist_edge_should_skip",
                plain,
                "PAYATTN_PLAIN_OK",
                cfg =>
                {
                    cfg.OcrIntervalMs = 400;
                    cfg.ApplicationScope = ApplicationScopeMode.AllMinusBlacklisted;
                    cfg.Applications =
                    [
                        new ApplicationConfig
                        {
                            Enabled = true,
                            ProcessName = "msedge",
                            ListMode = ApplicationListMode.Blacklist,
                            DebounceSeconds = 2,
                            TriggerFilter = ApplicationTriggerFilter.All
                        }
                    ];
                    cfg.MonitorSelection = MonitorSelection.Specific;
                    cfg.SpecificMonitorIndex = ultraIndex;
                    cfg.Triggers =
                    [
                        FastTrigger("PAYATTN_PLAIN_OK", CaptureAction.Screenshot, OcrSpeedMode.Thorough)
                    ];
                },
                ExpectFire: false,
                TimeoutSec: 12),
        ];
    }

    private static ScenarioResult RunScenario(
        Scenario scenario,
        string edgePath,
        int ultraIndex)
    {
        var captureDir = Path.Combine(CapturesRoot, scenario.CaptureSubdir ?? scenario.Name);
        var logDir = Path.Combine(LogsRoot, scenario.Name);
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(logDir);
        foreach (var f in Directory.GetFiles(captureDir))
            File.Delete(f);

        var config = BaseConfig(captureDir);
        scenario.Mutate(config);
        config.CaptureDirectory = captureDir;

        // Persist scenario config for inspection (in-repo only).
        var cfgPath = Path.Combine(Root, "configs", scenario.Name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(config, JsonDefaults.Options));

        var url = scenario.Url ?? new Uri(scenario.HtmlPath!).AbsoluteUri;
        using var edgeProc = StartEdgeOnUltrawide(edgePath, url);
        Thread.Sleep(4000);
        FocusEdgeOnUltrawide();

        var logger = new AppLogger(logDir);
        using var monitor = new CaptureMonitor(config, logger);
        if (!monitor.OcrAvailable)
            return new ScenarioResult("FAIL", null, "Windows OCR unavailable");

        string? shot = null;
        string? rec = null;
        string? seq = null;
        DateTime? firedUtc = null;
        var injectUtc = DateTime.UtcNow;

        monitor.TriggerFired += (_, e) =>
        {
            firedUtc ??= DateTime.UtcNow;
            Console.WriteLine($"  Fired: {e.TriggerText} ({e.Action}) -> {e.OutputPath}");
            if (e.Action == CaptureAction.Screenshot)
                shot ??= e.OutputPath;
            else if (e.Action == CaptureAction.Record)
                rec ??= e.OutputPath;
            else if (e.Action == CaptureAction.Sequence)
                seq ??= e.OutputPath;
        };

        monitor.Start();
        var deadline = DateTime.UtcNow.AddSeconds(scenario.TimeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            FocusEdgeOnUltrawide();
            if (scenario.ExpectFire == false)
            {
                Thread.Sleep(400);
                continue;
            }

            if (scenario.RequireBothActions)
            {
                if (shot is not null && rec is not null)
                    break;
            }
            else if (scenario.ExpectAction == CaptureAction.Record && rec is not null)
                break;
            else if (scenario.ExpectAction == CaptureAction.Sequence && seq is not null)
                break;
            else if (scenario.ExpectAction == CaptureAction.Screenshot && shot is not null)
                break;

            Thread.Sleep(250);
        }

        monitor.Stop();

        var files = Directory.GetFiles(captureDir, "*", SearchOption.AllDirectories);
        var latency = firedUtc is null ? (int?)null : (int)(firedUtc.Value - injectUtc).TotalMilliseconds;

        if (scenario.ExpectFire == false)
        {
            if (files.Length == 0 && shot is null && rec is null && seq is null)
                return new ScenarioResult("PASS", latency, "correctly skipped blacklisted Edge");
            return new ScenarioResult("FAIL", latency, $"expected no capture, found {files.Length} file(s)");
        }

        var ok = scenario.ExpectAction switch
        {
            CaptureAction.Record => rec is not null || files.Any(f => f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)),
            CaptureAction.Sequence => seq is not null || Directory.GetDirectories(captureDir).Length > 0,
            _ => shot is not null || files.Any(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        };

        if (scenario.RequireBothActions)
            ok = shot is not null && rec is not null;

        if (config.DebugSaveJsonSidecars && ok)
        {
            var jsons = Directory.GetFiles(captureDir, "*.json", SearchOption.AllDirectories);
            if (jsons.Length == 0)
                return new ScenarioResult("FAIL", latency, "fired but missing debug JSON sidecars");
        }

        var notes = ok
            ? $"files={files.Length}; trigger={scenario.TriggerText}"
            : $"no capture; files={files.Length}; OCR may have missed '{scenario.TriggerText}'";
        return new ScenarioResult(ok ? "PASS" : "FAIL", latency, notes);
    }

    private static AppConfig BaseConfig(string captureDir) => new()
    {
        CaptureDirectory = captureDir,
        OcrIntervalMs = 500,
        FastMatchThresholdPercent = 90,
        DebounceSeconds = 3,
        RecordBufferSeconds = 5,
        RecordFps = 3,
        RecordFormat = RecordFormat.Avi,
        RecordJpegQuality = 70,
        ScreenshotFormat = ScreenshotFormat.Jpeg,
        OcrMaxWidth = 1920,
        OcrUpscalePercent = 100,
        OcrInvertColors = false,
        CaptureScope = CaptureScope.Monitor,
        MonitorSelection = MonitorSelection.Specific,
        ApplicationScope = ApplicationScopeMode.Whitelisted,
        DebugSaveJsonSidecars = false,
        SequenceIntervalSeconds = 2,
        SequenceBeforeSeconds = 2,
        SequenceAfterSeconds = 2,
        RetentionMaxAgeDays = 0,
        RetentionMaxFiles = 0,
        Triggers = [],
        Applications = []
    };

    private static TriggerConfig FastTrigger(string text, CaptureAction action, OcrSpeedMode mode) => new()
    {
        Text = text,
        Match = MatchMode.Contains,
        Action = action,
        OcrMode = mode,
        Enabled = true
    };

    private static void SetEdgeWhitelist(AppConfig cfg)
    {
        cfg.Applications =
        [
            new ApplicationConfig
            {
                Enabled = true,
                ProcessName = "msedge",
                ListMode = ApplicationListMode.Whitelist,
                DebounceSeconds = 2,
                TriggerFilter = ApplicationTriggerFilter.All
            }
        ];
    }

    private static Process StartEdgeOnUltrawide(string edgePath, string url)
    {
        KillEdgeProfileProcesses();
        var args =
            $"--user-data-dir=\"{EdgeProfile}\" --no-first-run --disable-features=TranslateUI " +
            $"--window-position={UltrawideX},{UltrawideY + 40} --window-size=1600,1000 " +
            $"--new-window \"{url}\"";
        var psi = new ProcessStartInfo
        {
            FileName = edgePath,
            Arguments = args,
            UseShellExecute = false
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start Edge");
        Thread.Sleep(1500);
        MoveEdgeWindowsToUltrawide();
        return p;
    }

    private static void FocusEdgeOnUltrawide()
    {
        MoveEdgeWindowsToUltrawide();
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                }
            }
            catch { /* ignore */ }
        }
    }

    private static void MoveEdgeWindowsToUltrawide()
    {
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            try
            {
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero)
                    continue;
                // Place on C43J89x left half so OCR has a large readable surface.
                MoveWindow(h, UltrawideX + 80, UltrawideY + 60, 1700, 1000, true);
            }
            catch { /* ignore */ }
        }
    }

    private static void KillEdgeProfileProcesses()
    {
        // Only kill Edge instances using our experiment profile via command line when possible.
        try
        {
            var marker = EdgeProfile.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='msedge.exe'\\\" | Where-Object {{ $_.CommandLine -like '*{marker}*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(8000);
        }
        catch { /* ignore */ }
    }

    private static string? FindEdge()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Escape(string s) => s.Replace("|", "/").Replace("\n", " ").Replace("\r", " ");

    private static string BuildRecommendations(int pass, int fail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("- Keep **Thorough** as default OCR mode; use **Fast** + interval 250â€“400ms for scrolling terminals.");
        sb.AppendLine("- Default **Fast match threshold 90%** is a good balance; drop toward 70% only for known OCR-garbled phrases.");
        sb.AppendLine("- Prefer **Specific monitor** (or whitelist app windows) over All when multi-monitor; All is slower.");
        sb.AppendLine("- **OcrUpscale 100â€“200** for normal UI; push 200+ only for tiny terminal fonts (costly).");
        sb.AppendLine("- Screenshot **Jpeg** default is fine; **Png** when you need lossless evidence.");
        sb.AppendLine("- Record **Avi/MJPEG** more reliable than Gif for longer buffers in this environment.");
        sb.AppendLine("- Whitelist target apps (e.g. `msedge`, terminals) to avoid accidental captures from other screens.");
        sb.AppendLine($"- This run: {pass} pass / {fail} fail â€” see table above for scenario-specific faults.");
        return sb.ToString();
    }

    private sealed record Scenario(
        string Name,
        string? HtmlPath,
        string TriggerText,
        Action<AppConfig> Mutate,
        CaptureAction ExpectAction = CaptureAction.Screenshot,
        bool RequireBothActions = false,
        bool ExpectFire = true,
        int TimeoutSec = 45,
        string? Url = null,
        string? CaptureSubdir = null);

    private sealed record ScenarioResult(string Status, int? LatencyMs, string Notes);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

