using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;

namespace PayAttention;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly CaptureMonitor _monitor;
    private readonly AppLogger _logger;
    private readonly ObservableCollection<TriggerConfig> _triggers;
    private readonly ObservableCollection<ApplicationRow> _applications;
    private readonly ScreenGrabber _grabber = new();
    private bool _loadingUi;
    private bool _handlingTriggerFilter;

    public event EventHandler<AppConfig>? ConfigSaved;

    public SettingsWindow(AppConfig config, ConfigService configService, CaptureMonitor monitor, AppLogger logger)
    {
        InitializeComponent();
        _configService = configService;
        _monitor = monitor;
        _logger = logger;

        MonitorModeBox.ItemsSource = Enum.GetValues(typeof(MonitorSelection));
        CaptureScopeBox.ItemsSource = Enum.GetValues(typeof(CaptureScope));
        RecordFormatBox.ItemsSource = Enum.GetValues(typeof(RecordFormat));
        ScreenshotFormatBox.ItemsSource = Enum.GetValues(typeof(ScreenshotFormat));
        LogLevelBox.ItemsSource = Enum.GetValues(typeof(LogLevel));
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
        _loadingUi = true;
        try
        {
            ApplyConfigToUiCore(config);
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ApplyConfigToUiCore(AppConfig config)
    {
        _triggers.Clear();
        foreach (var t in config.Triggers)
        {
            _triggers.Add(new TriggerConfig
            {
                Id = t.Id == Guid.Empty ? Guid.NewGuid() : t.Id,
                Text = t.Text,
                Match = t.Match,
                Action = t.Action,
                OcrMode = t.OcrMode,
                Enabled = t.Enabled
            });
        }

        _applications.Clear();
        foreach (var a in config.Applications ?? [])
            _applications.Add(ApplicationRow.FromConfig(a));

        CaptureDirectoryBox.Text = config.CaptureDirectory;
        OcrIntervalBox.Text = config.OcrIntervalMs.ToString();
        FastMatchThresholdSlider.Value = Math.Clamp(config.FastMatchThresholdPercent, 50, 100);
        UpdateFastMatchThresholdLabel();
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
        LogLevelBox.SelectedItem = config.LogLevel;
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
            OcrMode = OcrSpeedMode.Thorough,
            Enabled = true
        });
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
            {
                app.TriggerIds.RemoveAll(id => id == selected.Id);
                if (app.TriggerFilter == ApplicationTriggerFilter.Selected && app.TriggerIds.Count == 0)
                    app.TriggerFilter = ApplicationTriggerFilter.None;
            }
        }
    }

    private void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        _applications.Add(new ApplicationRow
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            ProcessName = "",
            ListMode = ApplicationListMode.Whitelist,
            DebounceSeconds = 30,
            TriggerFilter = ApplicationTriggerFilter.All
        });
    }

    private void RemoveApplication_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationsGrid.SelectedItem is ApplicationRow selected)
            _applications.Remove(selected);
    }

    private void PickApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApplicationRow row })
            return;

        var names = _grabber.GetRunningProcessNames();
        var dialog = new Window
        {
            Title = "Pick application",
            Width = 420,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize
        };

        var hint = new TextBlock
        {
            Text = "Select a running process, or browse to an .exe (process name is stored).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 12, 12, 0),
            Opacity = 0.75
        };

        var list = new ListBox
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

        var browse = new Button { Content = "Browse…", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        browse.Click += (_, _) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select application executable",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (ofd.ShowDialog(dialog) != true || string.IsNullOrWhiteSpace(ofd.FileName))
                return;

            var processName = ApplicationScopeEvaluator.NormalizeProcessName(ofd.FileName);
            if (string.IsNullOrWhiteSpace(processName))
                return;

            row.ProcessName = processName;
            dialog.DialogResult = true;
        };

        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is string name)
            {
                row.ProcessName = ApplicationScopeEvaluator.NormalizeProcessName(name);
                dialog.DialogResult = true;
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(12)
        };
        buttons.Children.Add(browse);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(buttons);
        root.Children.Add(hint);
        root.Children.Add(list);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void TriggerFilterCombo_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is ComboBox { DataContext: ApplicationRow row })
            row.FilterBeforeEdit = row.TriggerFilter;
    }

    private void TriggerFilterCombo_DropDownClosed(object sender, EventArgs e)
    {
        if (_loadingUi || _handlingTriggerFilter)
            return;
        if (sender is not ComboBox { DataContext: ApplicationRow row })
            return;

        _handlingTriggerFilter = true;
        try
        {
            if (row.TriggerFilter == ApplicationTriggerFilter.Selected)
            {
                if (!TryPickTriggers(row))
                {
                    row.TriggerFilter = row.FilterBeforeEdit;
                    return;
                }

                row.TriggerFilter = ApplicationTriggerFilter.Selected;
            }
            else
            {
                row.TriggerIds.Clear();
            }
        }
        finally
        {
            _handlingTriggerFilter = false;
        }
    }

    private bool TryPickTriggers(ApplicationRow row)
    {
        if (_triggers.Count == 0)
        {
            MessageBox.Show(
                "Add at least one trigger before choosing Selected.",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        var dialog = new Window
        {
            Title = "Select triggers",
            Width = 420,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize
        };

        var list = new ListBox
        {
            ItemsSource = _triggers,
            DisplayMemberPath = nameof(TriggerConfig.Text),
            SelectionMode = SelectionMode.Extended,
            Margin = new Thickness(12)
        };

        foreach (var trigger in _triggers)
        {
            if (row.TriggerIds.Contains(trigger.Id))
                list.SelectedItems.Add(trigger);
        }

        var hint = new TextBlock
        {
            Text = "Select one or more triggers (Ctrl+click for multiple).",
            Margin = new Thickness(12, 12, 12, 0),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        };

        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0)
            {
                MessageBox.Show(
                    "Select at least one trigger, or choose None / All from the dropdown.",
                    "pay_attention",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            row.TriggerIds = list.SelectedItems.Cast<TriggerConfig>().Select(t => t.Id).ToList();
            dialog.DialogResult = true;
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
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(list);
        dialog.Content = root;

        return dialog.ShowDialog() == true;
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
            _logger.MinLevel = imported.LogLevel;
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
            _logger.MinLevel = config.LogLevel;
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
                OcrMode = t.OcrMode,
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
            if (app.TriggerFilter is ApplicationTriggerFilter.All or ApplicationTriggerFilter.None)
                app.TriggerIds.Clear();
            else if (app.TriggerIds.Count == 0)
                app.TriggerFilter = ApplicationTriggerFilter.None;
        }

        return new AppConfig
        {
            OcrIntervalMs = ocrInterval,
            FastMatchThresholdPercent = (int)Math.Round(FastMatchThresholdSlider.Value),
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
            LogLevel = LogLevelBox.SelectedItem is LogLevel level ? level : LogLevel.Info,
            PauseHotkey = HotkeyBox.Text.Trim(),
            RetentionMaxAgeDays = retentionDays,
            RetentionMaxFiles = retentionFiles,
            Triggers = triggers,
            Applications = applications
        };
    }

    private void FastMatchThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;
        UpdateFastMatchThresholdLabel();
    }

    private void UpdateFastMatchThresholdLabel()
    {
        if (FastMatchThresholdLabel is null || FastMatchThresholdSlider is null)
            return;
        FastMatchThresholdLabel.Text = $"{(int)Math.Round(FastMatchThresholdSlider.Value)}%";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// DataGrid swallows mouse wheel even when it has nothing to scroll. Forward to the
    /// outer settings ScrollViewer when the grid cannot (or should not) consume the delta.
    /// </summary>
    private void DataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        var gridScroll = FindDescendantScrollViewer(grid);
        if (gridScroll is not null && CanScrollVertically(gridScroll, e.Delta))
            return;

        e.Handled = true;
        var offset = SettingsScrollViewer.VerticalOffset - e.Delta;
        SettingsScrollViewer.ScrollToVerticalOffset(offset);
    }

    private static bool CanScrollVertically(ScrollViewer scroll, int delta)
    {
        if (scroll.ScrollableHeight <= 0)
            return false;

        // Positive delta = scroll up (toward top); negative = scroll down.
        if (delta > 0)
            return scroll.VerticalOffset > 0;
        return scroll.VerticalOffset < scroll.ScrollableHeight;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            var nested = FindDescendantScrollViewer(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

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
        private ApplicationTriggerFilter _triggerFilter = ApplicationTriggerFilter.All;

        public Guid Id { get; set; } = Guid.NewGuid();
        public List<Guid> TriggerIds { get; set; } = [];
        public ApplicationTriggerFilter FilterBeforeEdit { get; set; } = ApplicationTriggerFilter.All;

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

        public ApplicationTriggerFilter TriggerFilter
        {
            get => _triggerFilter;
            set { _triggerFilter = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ApplicationConfig ToConfig() => new()
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Enabled = Enabled,
            ProcessName = ProcessName,
            ListMode = ListMode,
            DebounceSeconds = DebounceSeconds,
            TriggerFilter = TriggerFilter,
            TriggerIds = TriggerIds.ToList()
        };

        public static ApplicationRow FromConfig(ApplicationConfig config)
        {
            var filter = config.TriggerFilter;
            var ids = config.TriggerIds?.ToList() ?? [];
            if (filter == ApplicationTriggerFilter.All && ids.Count > 0)
                filter = ApplicationTriggerFilter.Selected;

            return new ApplicationRow
            {
                Id = config.Id == Guid.Empty ? Guid.NewGuid() : config.Id,
                Enabled = config.Enabled,
                ProcessName = config.ProcessName,
                ListMode = config.ListMode,
                DebounceSeconds = config.DebounceSeconds <= 0 ? 30 : config.DebounceSeconds,
                TriggerFilter = filter,
                TriggerIds = ids
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
