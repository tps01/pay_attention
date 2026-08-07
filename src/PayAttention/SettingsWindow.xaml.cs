using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using MessageBox = System.Windows.MessageBox;

namespace PayAttention;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly CaptureMonitor _monitor;
    private readonly AppLogger _logger;
    private readonly ObservableCollection<TriggerConfig> _triggers;
    private readonly ScreenGrabber _grabber = new();

    public event EventHandler<AppConfig>? ConfigSaved;

    public SettingsWindow(AppConfig config, ConfigService configService, CaptureMonitor monitor, AppLogger logger)
    {
        InitializeComponent();
        _configService = configService;
        _monitor = monitor;
        _logger = logger;

        MatchColumn.ItemsSource = Enum.GetValues(typeof(MatchMode));
        ActionColumn.ItemsSource = Enum.GetValues(typeof(CaptureAction));
        MonitorModeBox.ItemsSource = Enum.GetValues(typeof(MonitorSelection));
        CaptureScopeBox.ItemsSource = Enum.GetValues(typeof(CaptureScope));
        RecordFormatBox.ItemsSource = Enum.GetValues(typeof(RecordFormat));
        ScreenshotFormatBox.ItemsSource = Enum.GetValues(typeof(ScreenshotFormat));

        _triggers = new ObservableCollection<TriggerConfig>();
        TriggersGrid.ItemsSource = _triggers;
        ApplyConfigToUi(config);
    }

    private void ApplyConfigToUi(AppConfig config)
    {
        _triggers.Clear();
        foreach (var t in config.Triggers)
        {
            _triggers.Add(new TriggerConfig
            {
                Id = t.Id,
                Text = t.Text,
                Match = t.Match,
                Action = t.Action,
                Enabled = t.Enabled
            });
        }

        CaptureDirectoryBox.Text = config.CaptureDirectory;
        OcrIntervalBox.Text = config.OcrIntervalMs.ToString();
        DebounceBox.Text = config.DebounceSeconds.ToString();
        BufferSecondsBox.Text = config.RecordBufferSeconds.ToString();
        RecordFpsBox.Text = config.RecordFps.ToString();
        RecordFormatBox.SelectedItem = config.RecordFormat;
        RecordJpegQualityBox.Text = config.RecordJpegQuality.ToString();
        ScreenshotFormatBox.SelectedItem = config.ScreenshotFormat;
        SequenceIntervalBox.Text = config.SequenceIntervalSeconds.ToString();
        SequenceBeforeBox.Text = config.SequenceBeforeSeconds.ToString();
        SequenceAfterBox.Text = config.SequenceAfterSeconds.ToString();
        OcrMaxWidthBox.Text = config.OcrMaxWidth.ToString();
        OcrUpscaleBox.Text = config.OcrUpscalePercent.ToString();
        OcrInvertBox.IsChecked = config.OcrInvertColors;
        CaptureScopeBox.SelectedItem = config.CaptureScope;
        StartWithWindowsBox.IsChecked = config.StartWithWindows;
        StartMonitoringBox.IsChecked = config.StartMonitoringOnLaunch;
        DebugJsonBox.IsChecked = config.DebugSaveJsonSidecars;
        HotkeyBox.Text = config.PauseHotkey;
        RetentionDaysBox.Text = config.RetentionMaxAgeDays.ToString();
        RetentionFilesBox.Text = config.RetentionMaxFiles.ToString();

        MonitorModeBox.SelectedItem = config.MonitorSelection;
        RefreshMonitors(config.SpecificMonitorIndex);
        MonitorMode_Changed(null, null!);
    }

    private void RefreshMonitors(int selectedIndex)
    {
        var monitors = _grabber.GetMonitors()
            .Select(m => new MonitorOption
            {
                Index = m.Index,
                Display = $"{m.Index}: {m.Name} ({m.Bounds.Width}x{m.Bounds.Height})" + (m.IsPrimary ? " [Primary]" : "")
            })
            .ToList();

        SpecificMonitorBox.ItemsSource = monitors;
        SpecificMonitorBox.DisplayMemberPath = nameof(MonitorOption.Display);
        SpecificMonitorBox.SelectedValuePath = nameof(MonitorOption.Index);
        if (monitors.Count == 0)
            return;

        var match = monitors.FirstOrDefault(m => m.Index == selectedIndex) ?? monitors[0];
        SpecificMonitorBox.SelectedItem = match;
    }

    private void MonitorMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        SpecificMonitorBox.IsEnabled = MonitorModeBox.SelectedItem is MonitorSelection.Specific;
    }

    private void AddTrigger_Click(object sender, RoutedEventArgs e)
    {
        _triggers.Add(new TriggerConfig
        {
            Text = "New trigger",
            Match = MatchMode.Contains,
            Action = CaptureAction.Screenshot,
            Enabled = true
        });
    }

    private void RemoveTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (TriggersGrid.SelectedItem is TriggerConfig selected)
            _triggers.Remove(selected);
    }

    private void BrowseCapture_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select capture output folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(CaptureDirectoryBox.Text)
                ? CaptureDirectoryBox.Text
                : _configService.DefaultCaptureDirectory
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            CaptureDirectoryBox.Text = dialog.SelectedPath;
    }

    private void OpenCaptures_Click(object sender, RoutedEventArgs e)
    {
        var path = string.IsNullOrWhiteSpace(CaptureDirectoryBox.Text)
            ? _configService.DefaultCaptureDirectory
            : CaptureDirectoryBox.Text;
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TriggersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TriggersGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export pay_attention config",
                Filter = "JSON config (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                FileName = "pay_attention_config.json",
                AddExtension = true
            };

            if (dialog.ShowDialog() != true)
                return;

            var config = BuildConfigFromUi();
            _configService.ExportTo(dialog.FileName, config);
            _logger.Info($"Config exported to {dialog.FileName}");
            MessageBox.Show("Config exported.", "pay_attention", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to export config", ex);
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import pay_attention config",
                Filter = "JSON config (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            var confirm = MessageBox.Show(
                "Import will replace your current settings and triggers. Continue?",
                "pay_attention",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            var imported = _configService.ImportFrom(dialog.FileName);
            ApplyConfigToUi(imported);
            _configService.Save(imported);
            AutostartService.SetEnabled(imported.StartWithWindows, Environment.ProcessPath ?? string.Empty);
            _monitor.UpdateConfig(imported);
            ConfigSaved?.Invoke(this, imported);
            _logger.Info($"Config imported from {dialog.FileName}");
            MessageBox.Show(
                "Config imported and saved.\n\nCapture folder was set to this PC's default if the imported path was missing.",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to import config", ex);
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TriggersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TriggersGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var config = BuildConfigFromUi();
            _configService.Save(config);
            // Reflect clamps / normalized paths back into the form.
            ApplyConfigToUi(config);
            AutostartService.SetEnabled(config.StartWithWindows, Environment.ProcessPath ?? string.Empty);
            _monitor.UpdateConfig(config);
            ConfigSaved?.Invoke(this, config);
            _logger.Info("Settings saved.");
            MessageBox.Show("Settings saved.", "pay_attention", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to save settings", ex);
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private AppConfig BuildConfigFromUi()
    {
        if (!int.TryParse(OcrIntervalBox.Text, out var ocrInterval))
            throw new InvalidOperationException("OCR interval must be an integer.");
        if (!int.TryParse(DebounceBox.Text, out var debounce))
            throw new InvalidOperationException("Debounce must be an integer.");
        if (!int.TryParse(BufferSecondsBox.Text, out var bufferSeconds))
            throw new InvalidOperationException("Record buffer seconds must be an integer.");
        if (!int.TryParse(RecordFpsBox.Text, out var fps))
            throw new InvalidOperationException("Record FPS must be an integer.");
        if (!int.TryParse(SequenceIntervalBox.Text, out var sequenceInterval))
            throw new InvalidOperationException("Sequence spacing must be an integer.");
        if (!int.TryParse(SequenceBeforeBox.Text, out var sequenceBefore))
            throw new InvalidOperationException("Sequence before seconds must be an integer.");
        if (!int.TryParse(SequenceAfterBox.Text, out var sequenceAfter))
            throw new InvalidOperationException("Sequence after seconds must be an integer.");
        if (!int.TryParse(RetentionDaysBox.Text, out var retentionDays))
            throw new InvalidOperationException("Retention days must be an integer.");
        if (!int.TryParse(RetentionFilesBox.Text, out var retentionFiles))
            throw new InvalidOperationException("Retention max files must be an integer.");
        if (!int.TryParse(OcrMaxWidthBox.Text, out var ocrMaxWidth))
            throw new InvalidOperationException("OCR max width must be an integer.");
        if (!int.TryParse(OcrUpscaleBox.Text, out var ocrUpscale))
            throw new InvalidOperationException("OCR upscale percent must be an integer.");
        if (!int.TryParse(RecordJpegQualityBox.Text, out var jpegQuality))
            throw new InvalidOperationException("JPEG quality must be an integer.");

        var monitorMode = MonitorModeBox.SelectedItem is MonitorSelection ms ? ms : MonitorSelection.Primary;
        var specificIndex = SpecificMonitorBox.SelectedValue is int idx ? idx : 0;
        var captureScope = CaptureScopeBox.SelectedItem is CaptureScope scope ? scope : CaptureScope.Monitor;
        var recordFormat = RecordFormatBox.SelectedItem is RecordFormat rf ? rf : RecordFormat.Avi;
        var screenshotFormat = ScreenshotFormatBox.SelectedItem is ScreenshotFormat sf ? sf : ScreenshotFormat.Jpeg;

        var triggers = _triggers.Select(t => new TriggerConfig
            {
                Id = t.Id == Guid.Empty ? Guid.NewGuid() : t.Id,
                Text = t.Text?.Trim() ?? string.Empty,
                Match = t.Match,
                Action = t.Action,
                Enabled = t.Enabled
            }).Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();

        foreach (var trigger in triggers.Where(t => t.Enabled && t.Match == MatchMode.Regex))
        {
            try
            {
                _ = new Regex(trigger.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid regex for trigger \"{trigger.Text}\": {ex.Message}");
            }
        }

        return new AppConfig
        {
            OcrIntervalMs = ocrInterval,
            DebounceSeconds = debounce,
            RecordBufferSeconds = bufferSeconds,
            RecordFps = fps,
            RecordFormat = recordFormat,
            RecordJpegQuality = jpegQuality,
            ScreenshotFormat = screenshotFormat,
            SequenceIntervalSeconds = sequenceInterval,
            SequenceBeforeSeconds = sequenceBefore,
            SequenceAfterSeconds = sequenceAfter,
            OcrMaxWidth = ocrMaxWidth,
            CaptureScope = captureScope,
            OcrInvertColors = OcrInvertBox.IsChecked == true,
            OcrUpscalePercent = ocrUpscale,
            CaptureDirectory = string.IsNullOrWhiteSpace(CaptureDirectoryBox.Text)
                ? _configService.DefaultCaptureDirectory
                : CaptureDirectoryBox.Text.Trim(),
            MonitorSelection = monitorMode,
            SpecificMonitorIndex = specificIndex,
            StartWithWindows = StartWithWindowsBox.IsChecked == true,
            StartMonitoringOnLaunch = StartMonitoringBox.IsChecked == true,
            DebugSaveJsonSidecars = DebugJsonBox.IsChecked == true,
            PauseHotkey = HotkeyBox.Text.Trim(),
            RetentionMaxAgeDays = retentionDays,
            RetentionMaxFiles = retentionFiles,
            Triggers = triggers
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class MonitorOption
    {
        public int Index { get; init; }
        public string Display { get; init; } = string.Empty;
    }
}
