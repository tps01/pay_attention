using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace PayAttention;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly CaptureMonitor _monitor;
    private readonly AppLogger _logger;
    private readonly ObservableCollection<TriggerConfig> _triggers;
    private readonly ObservableCollection<ApplicationRow> _applications;
    private readonly ScreenGrabber _grabber = new();
    private ApplicationRow? _associatingApp;
    private bool _syncingTriggerSelection;

    public event EventHandler<AppConfig>? ConfigSaved;

    public SettingsWindow(AppConfig config, ConfigService configService, CaptureMonitor monitor, AppLogger logger)
    {
        InitializeComponent();
        _configService = configService;
        _monitor = monitor;
        _logger = logger;

        MatchColumn.ItemsSource = Enum.GetValues(typeof(MatchMode));
        ActionColumn.ItemsSource = Enum.GetValues(typeof(CaptureAction));
        ListModeColumn.ItemsSource = Enum.GetValues(typeof(ApplicationListMode));
        MonitorModeBox.ItemsSource = Enum.GetValues(typeof(MonitorSelection));
        CaptureScopeBox.ItemsSource = Enum.GetValues(typeof(CaptureScope));
        RecordFormatBox.ItemsSource = Enum.GetValues(typeof(RecordFormat));
        ScreenshotFormatBox.ItemsSource = Enum.GetValues(typeof(ScreenshotFormat));
        ApplicationScopeBox.ItemsSource = new[]
        {
            new ScopeOption(ApplicationScopeMode.All, "All Applications"),
            new ScopeOption(ApplicationScopeMode.Whitelisted, "Whitelisted Applications"),
            new ScopeOption(ApplicationScopeMode.AllMinusBlacklisted, "All Minus Blacklisted Applications")
        };

        _triggers = new ObservableCollection<TriggerConfig>();
        _applications = new ObservableCollection<ApplicationRow>();
        TriggersGrid.ItemsSource = _triggers;
        ApplicationsGrid.ItemsSource = _applications;
        ApplyConfigToUi(config);
    }

    private void ApplyConfigToUi(AppConfig config)
    {
        ExitTriggerAssociationMode();

        _triggers.Clear();
        foreach (var t in config.Triggers)
        {
            _triggers.Add(new TriggerConfig
            {
                Id = t.Id == Guid.Empty ? Guid.NewGuid() : t.Id,
                Text = t.Text,
                Match = t.Match,
                Action = t.Action,
                Enabled = t.Enabled
            });
        }

        _applications.Clear();
        foreach (var a in config.Applications ?? [])
        {
            var row = ApplicationRow.FromConfig(a);
            row.RefreshTriggersDisplay(_triggers);
            _applications.Add(row);
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
        ApplicationScopeBox.SelectedValue = config.ApplicationScope;
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
            Id = Guid.NewGuid(),
            Text = "New trigger",
            Match = MatchMode.Contains,
            Action = CaptureAction.Screenshot,
            Enabled = true
        });
        RefreshAllApplicationTriggerDisplays();
    }

    private void RemoveTrigger_Click(object sender, RoutedEventArgs e)
    {
        var toRemove = TriggersGrid.SelectedItems.Cast<TriggerConfig>().ToList();
        if (toRemove.Count == 0 && TriggersGrid.SelectedItem is TriggerConfig one)
            toRemove.Add(one);

        foreach (var selected in toRemove)
        {
            _triggers.Remove(selected);
            foreach (var app in _applications)
                app.TriggerIds.RemoveAll(id => id == selected.Id);
        }

        RefreshAllApplicationTriggerDisplays();
        if (_associatingApp is not null)
            SyncTriggerGridSelectionFromApp(_associatingApp);
    }

    private void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        var row = new ApplicationRow
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            ProcessName = "",
            ListMode = ApplicationListMode.Whitelist,
            DebounceSeconds = 30
        };
        row.RefreshTriggersDisplay(_triggers);
        _applications.Add(row);
    }

    private void RemoveApplication_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationsGrid.SelectedItem is ApplicationRow selected)
        {
            if (ReferenceEquals(_associatingApp, selected))
                ExitTriggerAssociationMode();
            _applications.Remove(selected);
        }
    }

    private void PickApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApplicationRow row })
            return;

        var names = _grabber.GetRunningProcessNames();
        var dialog = new Window
        {
            Title = "Pick application",
            Width = 360,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize
        };

        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = names,
            Margin = new Thickness(12)
        };
        if (!string.IsNullOrWhiteSpace(row.ProcessName))
        {
            var match = names.FirstOrDefault(n =>
                ApplicationScopeEvaluator.ProcessNamesMatch(n, row.ProcessName));
            if (match is not null)
                list.SelectedItem = match;
        }

        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is string name)
            {
                row.ProcessName = name;
                dialog.DialogResult = true;
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(12)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(list);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void ApplicationTriggers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApplicationRow row })
            return;

        EnterTriggerAssociationMode(row);
    }

    private void EnterTriggerAssociationMode(ApplicationRow row)
    {
        _associatingApp = row;
        TriggerAssociationHint.Text = "Select triggers in the table above";
        TriggersGrid.SelectionMode = DataGridSelectionMode.Extended;
        SyncTriggerGridSelectionFromApp(row);
    }

    private void ExitTriggerAssociationMode()
    {
        _associatingApp = null;
        TriggerAssociationHint.Text = string.Empty;
        _syncingTriggerSelection = true;
        try
        {
            TriggersGrid.SelectionMode = DataGridSelectionMode.Single;
            TriggersGrid.SelectedItems.Clear();
        }
        finally
        {
            _syncingTriggerSelection = false;
        }
    }

    private void SyncTriggerGridSelectionFromApp(ApplicationRow row)
    {
        _syncingTriggerSelection = true;
        try
        {
            TriggersGrid.SelectedItems.Clear();
            if (row.TriggerIds.Count == 0)
                return;

            foreach (var trigger in _triggers)
            {
                if (row.TriggerIds.Contains(trigger.Id))
                    TriggersGrid.SelectedItems.Add(trigger);
            }
        }
        finally
        {
            _syncingTriggerSelection = false;
        }
    }

    private void TriggersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTriggerSelection || _associatingApp is null)
            return;

        _associatingApp.TriggerIds = TriggersGrid.SelectedItems
            .Cast<TriggerConfig>()
            .Select(t => t.Id)
            .ToList();
        _associatingApp.RefreshTriggersDisplay(_triggers);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _associatingApp is not null)
        {
            ExitTriggerAssociationMode();
            e.Handled = true;
        }
    }

    private void RefreshAllApplicationTriggerDisplays()
    {
        foreach (var app in _applications)
            app.RefreshTriggersDisplay(_triggers);
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
            CommitGrids();

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
            CommitGrids();

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

    private void CommitGrids()
    {
        TriggersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TriggersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ApplicationsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ApplicationsGrid.CommitEdit(DataGridEditingUnit.Row, true);
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
        var applicationScope = ApplicationScopeBox.SelectedValue is ApplicationScopeMode asm
            ? asm
            : ApplicationScopeMode.AllMinusBlacklisted;
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

        var triggerIds = triggers.Select(t => t.Id).ToHashSet();
        var applications = _applications
            .Select(a => a.ToConfig())
            .Where(a => !string.IsNullOrWhiteSpace(a.ProcessName))
            .ToList();
        foreach (var app in applications)
        {
            app.ProcessName = ApplicationScopeEvaluator.NormalizeProcessName(app.ProcessName);
            app.TriggerIds = app.TriggerIds.Where(triggerIds.Contains).ToList();
            if (app.DebounceSeconds < 1)
                app.DebounceSeconds = 30;
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
            ApplicationScope = applicationScope,
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
            Triggers = triggers,
            Applications = applications
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class MonitorOption
    {
        public int Index { get; init; }
        public string Display { get; init; } = string.Empty;
    }

    private sealed class ScopeOption
    {
        public ScopeOption(ApplicationScopeMode value, string display)
        {
            Value = value;
            Display = display;
        }

        public ApplicationScopeMode Value { get; }
        public string Display { get; }
    }

    private sealed class ApplicationRow : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _processName = string.Empty;
        private ApplicationListMode _listMode = ApplicationListMode.Blacklist;
        private int _debounceSeconds = 30;
        private string _triggersDisplay = "All";

        public Guid Id { get; set; } = Guid.NewGuid();
        public List<Guid> TriggerIds { get; set; } = [];

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        public string ProcessName
        {
            get => _processName;
            set { _processName = value; OnPropertyChanged(); }
        }

        public ApplicationListMode ListMode
        {
            get => _listMode;
            set { _listMode = value; OnPropertyChanged(); }
        }

        public int DebounceSeconds
        {
            get => _debounceSeconds;
            set { _debounceSeconds = value; OnPropertyChanged(); }
        }

        public string TriggersDisplay
        {
            get => _triggersDisplay;
            private set { _triggersDisplay = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void RefreshTriggersDisplay(IEnumerable<TriggerConfig> triggers)
        {
            TriggersDisplay = ApplicationScopeEvaluator.FormatTriggersSummary(ToConfig(), triggers);
        }

        public ApplicationConfig ToConfig() => new()
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Enabled = Enabled,
            ProcessName = ProcessName,
            ListMode = ListMode,
            DebounceSeconds = DebounceSeconds,
            TriggerIds = TriggerIds.ToList()
        };

        public static ApplicationRow FromConfig(ApplicationConfig config) => new()
        {
            Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id,
            Enabled = config.Enabled,
            ProcessName = config.ProcessName,
            ListMode = config.ListMode,
            DebounceSeconds = config.DebounceSeconds <= 0 ? 30 : config.DebounceSeconds,
            TriggerIds = config.TriggerIds?.ToList() ?? []
        };

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
