using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PayAttention;

public partial class App : Application, IDisposable
{
    private static Mutex? _instanceMutex;
    private ConfigService _configService = null!;
    private AppLogger _logger = null!;
    private AppConfig _config = null!;
    private CaptureMonitor _monitor = null!;
    private NotifyIcon? _tray;
    private Icon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private HotkeyService? _hotkey;
    private ToolStripMenuItem? _startStopItem;
    private ToolStripMenuItem? _pauseItem;
    private System.Windows.Threading.DispatcherTimer? _retentionTimer;
    private Task _maintenance = Task.CompletedTask;
    private bool _exiting;
    private string _statusText = "Stopped";
    private bool _cleanedUp;
    /// <summary>CLI: show Settings as the main window (for FlaUI / Colosseum).</summary>
    private bool _settingsOnly;
    /// <summary>CLI: start monitoring without using the tray menu.</summary>
    private bool _startOnLaunch;
    /// <summary>Skip the NotifyIcon when Settings-only or isolated automation asks for it.</summary>
    private bool _suppressTray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ParseLaunchArgs(e.Args);

        var appData = ConfigService.ResolveAppDataDirectory();
        var mutexName = InstanceMutexName(appData);
        _instanceMutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "pay_attention is already running (check the system tray).",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _configService = new ConfigService(appData);
        _logger = new AppLogger(_configService.LogDirectory);
        var load = _configService.LoadDetailed();
        _config = load.Config;
        _logger.MinLevel = _config.LogLevel;

        if (_configService.IsIsolatedAppData)
            _logger.Info($"Isolated app data: {_configService.AppDataDirectory}");

        if (load.ResetToDefaults)
        {
            var backupNote = string.IsNullOrEmpty(load.CorruptBackupPath)
                ? ""
                : $"\n\nA backup of the old file was saved as:\n{load.CorruptBackupPath}";
            MessageBox.Show(
                "Settings could not be read, so defaults were restored." + backupNote,
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _logger.Warn("Config reset to defaults after load failure. Backup: " + (load.CorruptBackupPath ?? "(none)"));
        }
        else if (!string.IsNullOrEmpty(load.Warning))
        {
            MessageBox.Show(load.Warning, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Warning);
            _logger.Warn(load.Warning);
        }

        // Never rewrite the user's Run key when using a temp app-data root.
        if (string.IsNullOrEmpty(load.Warning))
            _configService.ApplyAutostart(_config, Environment.ProcessPath ?? string.Empty);

        _monitor = new CaptureMonitor(_config, _logger);
        _monitor.StatusChanged += (_, args) =>
        {
            _statusText = args.Status;
            Dispatcher.BeginInvoke(UpdateTrayText);
            if (!string.IsNullOrWhiteSpace(args.Detail))
                _logger.Info(args.Detail);

            // Early feedback when a trigger is accepted but the capture write is still running.
            if (string.Equals(args.Status, "Processing", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args.Detail))
            {
                var detail = args.Detail!;
                Dispatcher.BeginInvoke(() =>
                {
                    _tray?.ShowBalloonTip(2000, "pay_attention", detail, ToolTipIcon.Info);
                });
            }
        };
        _monitor.TriggerFired += (_, args) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _tray?.ShowBalloonTip(
                    2000,
                    "pay_attention",
                    $"Captured '{args.TriggerText}' ({args.Action})",
                    ToolTipIcon.Info);
            });
        };

        if (!_suppressTray)
            BuildTray();
        RegisterHotkey();

        if (!_monitor.OcrAvailable)
        {
            MessageBox.Show(
                "Windows OCR is not available. Install a language pack with OCR support and restart.",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else if (!load.ResetToDefaults && string.IsNullOrEmpty(load.Warning) && (_startOnLaunch || _config.StartMonitoringOnLaunch))
        {
            _monitor.Start();
            if (_startOnLaunch)
                _logger.Info("Monitoring started via --start.");
        }

        UpdateTrayText();
        RetentionService.Enforce(
            _config.CaptureDirectory,
            _config.RetentionMaxAgeDays,
            _config.RetentionMaxFiles,
            _logger);
        _logger.EnforceRetention();
        _retentionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _retentionTimer.Tick += (_, _) =>
        {
            if (!_maintenance.IsCompleted) return;
            var config = _config;
            _maintenance = Task.Run(() =>
            {
                try
                {
                    RetentionService.Enforce(config.CaptureDirectory, config.RetentionMaxAgeDays, config.RetentionMaxFiles, _logger);
                    _logger.EnforceRetention();
                }
                catch (Exception ex) { _logger.Error("Retention failed", ex); }
            });
        };
        _retentionTimer.Start();

        if (_settingsOnly)
        {
            OpenSettingsAsMainWindow();
        }
        else if (_suppressTray)
        {
            // No tray and no Settings window — stay alive until Exit / process kill.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }

    private void ParseLaunchArgs(string[] args)
    {
        foreach (var raw in args)
        {
            var a = raw.Trim();
            if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase))
            {
                _settingsOnly = true;
                _suppressTray = true;
            }
            else if (string.Equals(a, "--start", StringComparison.OrdinalIgnoreCase))
            {
                _startOnLaunch = true;
            }
            else if (string.Equals(a, "--no-tray", StringComparison.OrdinalIgnoreCase))
            {
                _suppressTray = true;
            }
        }

        // Headless monitoring for Colosseum: no tray, no Settings window.
        if (_startOnLaunch && !_settingsOnly)
            _suppressTray = true;
    }

    private static string InstanceMutexName(string appDataDirectory)
    {
        // Default installs keep the historical name. Isolated roots get their own mutex
        // so Colosseum can run beside a normal tray instance.
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pay_attention");
        if (string.Equals(
                Path.GetFullPath(appDataDirectory),
                Path.GetFullPath(defaultDir),
                StringComparison.OrdinalIgnoreCase))
            return @"Local\pay_attention_single_instance";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appDataDirectory)))[..16];
        return $@"Local\pay_attention_single_instance_{hash}";
    }

    private void BuildTray()
    {
        _startStopItem = new ToolStripMenuItem("Start monitoring", null, (_, _) => ToggleMonitoring());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startStopItem);
        _pauseItem = new ToolStripMenuItem("Pause monitoring", null, (_, _) =>
        {
            if (_monitor.IsRunning) _monitor.TogglePause();
            UpdateTrayText();
        });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem(
            "Optimize for this PC...",
            null,
            async (_, _) => await OptimizeForThisPcFromTrayAsync()));
        menu.Items.Add(new ToolStripMenuItem("Open captures folder", null, (_, _) => OpenCaptures()));
        menu.Items.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => OpenLogs()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("About...", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _trayIcon = LoadAppIcon();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu,
            Text = "pay_attention"
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                    return icon;
            }
        }
        catch
        {
            // fall through
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static void ShowAbout()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "1.0.0";
        var company = assembly
            .GetCustomAttribute<AssemblyCompanyAttribute>()
            ?.Company
            ?? "tps01";

        MessageBox.Show(
            $"pay_attention\nVersion {version}\nAuthor: {company}\n\nWindows text-triggered screen logger.",
            "About pay_attention",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ToggleMonitoring()
    {
        if (_monitor.IsRunning)
            await _monitor.StopAsync();
        else
            _monitor.Start();
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_tray is null)
            return;

        var state = !_monitor.IsRunning
            ? "Stopped"
            : _monitor.IsPaused ? "Paused" : _statusText;

        _tray.Text = Truncate($"pay_attention - {state}");
        if (_startStopItem is not null)
            _startStopItem.Text = _monitor.IsRunning ? "Stop monitoring" : "Start monitoring";
        if (_pauseItem is not null)
        {
            _pauseItem.Enabled = _monitor.IsRunning;
            _pauseItem.Text = _monitor.IsPaused ? "Resume monitoring" : "Pause monitoring";
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 63 ? text : text[..63];

    private void OpenSettingsAsMainWindow()
    {
        _settingsWindow = new SettingsWindow(_config, _configService, _monitor, _logger);
        _settingsWindow.ConfigSaved += (_, cfg) =>
        {
            _config = cfg;
            _logger.MinLevel = cfg.LogLevel;
            RegisterHotkey();
            UpdateTrayText();
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ExitApp();
        };
        MainWindow = _settingsWindow;
        _settingsWindow.Show();
        _logger.Info("Settings opened via --settings.");
    }

    private void OpenSettings(bool showAdvanced = false)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            if (showAdvanced)
                _settingsWindow.ShowAdvancedTab();
            return;
        }

        _settingsWindow = new SettingsWindow(_config, _configService, _monitor, _logger);
        _settingsWindow.ConfigSaved += (_, cfg) =>
        {
            _config = cfg;
            _logger.MinLevel = cfg.LogLevel;
            RegisterHotkey();
            UpdateTrayText();
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        if (showAdvanced)
            _settingsWindow.ShowAdvancedTab();
    }

    private async Task OptimizeForThisPcFromTrayAsync()
    {
        try
        {
            OpenSettings(showAdvanced: true);
            if (_settingsWindow is null)
                return;

            var result = await OptimizeForThisPcRunner.RunAsync(
                _settingsWindow,
                _settingsWindow.SnapshotConfig(),
                _monitor,
                _logger);
            if (result is null)
                return;

            // Fill Advanced for review; user still clicks Save.
            _settingsWindow.ApplyCalibrationResult(result.Config);
            MessageBox.Show(
                _settingsWindow,
                $"Chose {PerformanceProfiles.DisplayName(result.Chosen)}.\n\n{result.Summary}\n\nReview Advanced settings, then click Save.",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("Optimize for this PC (tray) failed", ex);
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenCaptures()
    {
        try
        {
            Directory.CreateDirectory(_config.CaptureDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _config.CaptureDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open captures folder", ex);
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_configService.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _configService.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open logs folder", ex);
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;

        if (string.IsNullOrWhiteSpace(_config.PauseHotkey))
            return;

        try
        {
            _hotkey = new HotkeyService();
            _hotkey.HotkeyPressed += (_, _) =>
            {
                if (_monitor.IsRunning)
                {
                    _monitor.TogglePause();
                    Dispatcher.BeginInvoke(UpdateTrayText);
                }
            };
            _hotkey.Register(_config.PauseHotkey);
            _logger.Info($"Pause hotkey registered: {_config.PauseHotkey}");
        }
        catch (Exception ex)
        {
            _hotkey?.Dispose();
            _hotkey = null;
            _logger.Warn($"Could not register hotkey '{_config.PauseHotkey}': {ex.Message}");
            MessageBox.Show(
                $"Could not register pause hotkey '{_config.PauseHotkey}'.\n{ex.Message}\n\nChange it in Settings if needed.",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _retentionTimer?.Stop();
        await _monitor.StopAsync();
        await _maintenance;
        Cleanup();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;
        _retentionTimer?.Stop();

        _hotkey?.Dispose();
        _hotkey = null;

        try { _monitor?.Dispose(); } catch { /* ignore */ }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;

        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch { /* ignore */ }
        _instanceMutex = null;
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
