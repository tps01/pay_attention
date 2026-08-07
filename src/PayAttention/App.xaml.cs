using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PayAttention;

public partial class App : Application
{
    private ConfigService _configService = null!;
    private AppLogger _logger = null!;
    private AppConfig _config = null!;
    private CaptureMonitor _monitor = null!;
    private NotifyIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private HotkeyService? _hotkey;
    private ToolStripMenuItem? _startStopItem;
    private ToolStripMenuItem? _pauseItem;
    private string _statusText = "Stopped";
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _configService = new ConfigService();
        _logger = new AppLogger(_configService.LogDirectory);
        _config = _configService.Load();

        AutostartService.SetEnabled(_config.StartWithWindows, Environment.ProcessPath ?? string.Empty);

        _monitor = new CaptureMonitor(_config, _logger);
        _monitor.StatusChanged += (_, args) =>
        {
            _statusText = args.Status;
            Dispatcher.Invoke(UpdateTrayText);
            if (!string.IsNullOrWhiteSpace(args.Detail))
                _logger.Info(args.Detail);
        };
        _monitor.TriggerFired += (_, args) =>
        {
            Dispatcher.Invoke(() =>
            {
                _tray?.ShowBalloonTip(
                    2000,
                    "pay_attention",
                    $"Captured '{args.TriggerText}' ({args.Action})",
                    ToolTipIcon.Info);
            });
        };

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
        else if (_config.StartMonitoringOnLaunch)
        {
            _monitor.Start();
        }

        UpdateTrayText();
        RetentionService.Enforce(
            _config.CaptureDirectory,
            _config.RetentionMaxAgeDays,
            _config.RetentionMaxFiles,
            _logger);
    }

    private void BuildTray()
    {
        _startStopItem = new ToolStripMenuItem("Start monitoring", null, (_, _) => ToggleMonitoring());
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) =>
        {
            _monitor.TogglePause();
            UpdateTrayText();
        });

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startStopItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Open captures folder", null, (_, _) => OpenCaptures()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = menu,
            Text = "pay_attention"
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private void ToggleMonitoring()
    {
        if (_monitor.IsRunning)
            _monitor.Stop();
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
            _pauseItem.Text = _monitor.IsPaused ? "Resume" : "Pause";
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 63 ? text : text[..63];

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_config, _configService, _monitor, _logger);
        _settingsWindow.ConfigSaved += (_, cfg) =>
        {
            _config = cfg;
            RegisterHotkey();
            UpdateTrayText();
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
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
                    Dispatcher.Invoke(UpdateTrayText);
                }
            };
            _hotkey.Register(_config.PauseHotkey);
            _logger.Info($"Pause hotkey registered: {_config.PauseHotkey}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not register hotkey '{_config.PauseHotkey}': {ex.Message}");
        }
    }

    private void ExitApp()
    {
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

        _hotkey?.Dispose();
        _hotkey = null;

        _monitor.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
