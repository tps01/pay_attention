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
    private bool _loadingUi;
    private bool _handlingTriggerFilter;
    private PerformanceProfileKind _appliedPerformanceProfile = PerformanceProfileKind.Ultra;

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
        QualityPresetBox.ItemsSource = PerformanceProfiles.All
            .Select(d => new QualityPresetOption(d.Kind, PerformanceProfiles.DisplayName(d.Kind)))
            .ToList();
        QualityPresetBox.DisplayMemberPath = nameof(QualityPresetOption.Display);
        QualityPresetBox.SelectedValuePath = nameof(QualityPresetOption.Kind);

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
        OcrIntervalBox.Text = Inv(config.OcrIntervalMs);
        FastMatchThresholdSlider.Value = Math.Clamp(config.FastMatchThresholdPercent, 50, 100);
        UpdateFastMatchThresholdLabel();
        DebounceBox.Text = Inv(config.DebounceSeconds);
        BufferSecondsBox.Text = Inv(config.RecordBufferSeconds);
        RecordFpsBox.Text = Inv(config.RecordFps);
        RecordFormatBox.SelectedItem = config.RecordFormat;
        RecordJpegQualityBox.Text = Inv(config.RecordJpegQuality);
        ScreenshotFormatBox.SelectedItem = config.ScreenshotFormat;
        SequenceIntervalBox.Text = Inv(config.SequenceIntervalSeconds);
        SequenceBeforeBox.Text = Inv(config.SequenceBeforeSeconds);
        SequenceAfterBox.Text = Inv(config.SequenceAfterSeconds);
        OcrMaxWidthBox.Text = Inv(config.OcrMaxWidth);
        OcrUpscaleBox.Text = Inv(config.OcrUpscalePercent);
        OcrBoostUpscaleBox.Text = Inv(config.OcrBoostUpscalePercent);
        OcrAbsoluteMaxWidthBox.Text = Inv(config.OcrAbsoluteMaxWidth);
        OcrInvertBox.IsChecked = config.OcrInvertColors;
        OcrEnableGrayBox.IsChecked = config.OcrEnableGray;
        OcrEnableAdaptiveBox.IsChecked = config.OcrEnableAdaptive;
        OcrEnableNeonBox.IsChecked = config.OcrEnableNeon;
        OcrEnableTerminalBox.IsChecked = config.OcrEnableTerminal;
        OcrTerminalMaxRegionsBox.Text = Inv(config.OcrTerminalMaxRegions);
        OcrMaxWindowsBox.Text = Inv(config.OcrMaxWindows);
        OcrQueueCapacityBox.Text = Inv(config.OcrQueueCapacity);
        OcrCascadeBudgetFastBox.Text = Inv(config.OcrCascadeBudgetFastMs);
        OcrCascadeBudgetThoroughBox.Text = Inv(config.OcrCascadeBudgetThoroughMs);
        OcrApplyBudgetWhenIdleBox.IsChecked = config.OcrApplyBudgetWhenIdle;
        UseBackgroundPriorityBox.IsChecked = config.UseBackgroundPriority;
        CalibrationSummaryText.Text = string.IsNullOrWhiteSpace(config.LastCalibrationSummary)
            ? "Not calibrated on this PC yet."
            : config.LastCalibrationSummary;
        _appliedPerformanceProfile = config.AppliedPerformanceProfile;
        QualityPresetBox.SelectedValue = config.AppliedPerformanceProfile;
        CaptureScopeBox.SelectedItem = config.CaptureScope;
        ApplicationScopeBox.SelectedValue = config.ApplicationScope;
        StartWithWindowsBox.IsChecked = config.StartWithWindows;
        StartMonitoringBox.IsChecked = config.StartMonitoringOnLaunch;
        DebugJsonBox.IsChecked = config.DebugSaveJsonSidecars;
        LogLevelBox.SelectedItem = config.LogLevel;
        HotkeyBox.Text = config.PauseHotkey;
        RetentionDaysBox.Text = Inv(config.RetentionMaxAgeDays);
        RetentionFilesBox.Text = Inv(config.RetentionMaxFiles);

        MonitorModeBox.SelectedItem = config.MonitorSelection;
        RefreshMonitors(config.SpecificMonitorIndex);
        MonitorMode_Changed(null, null!);
        RefreshPerformanceHints();
    }

    public void ShowAdvancedTab()
    {
        SettingsTabs.SelectedItem = AdvancedTab;
    }

    /// <summary>Fill the form from a calibration result without saving.</summary>
    public void ApplyCalibrationResult(AppConfig config)
    {
        ApplyConfigToUi(config);
        ShowAdvancedTab();
    }

    private async void OptimizeForThisPc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitGrids();
            var current = BuildConfigFromUi();
            var result = await OptimizeForThisPcRunner.RunAsync(this, current, _monitor, _logger);
            if (result is null)
                return;

            ApplyConfigToUi(result.Config);
            ShowAdvancedTab();
            MessageBox.Show(
                $"Chose {PerformanceProfiles.DisplayName(result.Chosen)}.\n\n{result.Summary}\n\nReview Advanced settings, then click Save.",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("Optimize for this PC failed", ex);
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetPerformance_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyQualityPreset(PerformanceProfileKind.Ultra, "Reset to Ultra defaults (not saved yet).");
            ShowAdvancedTab();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QualityPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;
        if (QualityPresetBox.SelectedValue is not PerformanceProfileKind kind)
            return;
        if (kind == _appliedPerformanceProfile)
            return;

        try
        {
            ApplyQualityPreset(
                kind,
                $"Manual preset: {PerformanceProfiles.DisplayName(kind)} (not saved yet).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "pay_attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Fill Advanced knobs from a named profile without saving.</summary>
    private void ApplyQualityPreset(PerformanceProfileKind kind, string summary)
    {
        CommitGrids();
        var config = BuildConfigFromUi();
        PerformanceProfiles.Apply(config, kind);
        config.LastCalibrationSummary = summary;
        ApplyConfigToUi(config);
    }


    private void RefreshMonitors(int selectedIndex)
    {
        var monitors = ScreenGrabber.GetMonitors()
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
        if (!_loadingUi)
            RefreshPerformanceHints();
    }

    private void PerformanceHintField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loadingUi)
            RefreshPerformanceHints();
    }

    private void PerformanceHintCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingUi)
            RefreshPerformanceHints();
    }

    /// <summary>
    /// Show amber ⚠ glyphs beside form values that are likely costly on this PC.
    /// Hints follow the live form (no Save required); values are never auto-changed.
    /// </summary>
    private void RefreshPerformanceHints()
    {
        var multiMonitor = ScreenGrabber.GetMonitors().Count > 1;
        var monitorAll = MonitorModeBox.SelectedItem is MonitorSelection.All;
        var warnMonitorAll = multiMonitor && monitorAll;
        SetPerfHint(
            MonitorAllPerfHint,
            warnMonitorAll,
            "More than one monitor detected. Limiting capture scope to one monitor is recommended for optimal performance.");
        MonitorAllPerfCaption.Visibility = warnMonitorAll ? Visibility.Visible : Visibility.Collapsed;

        var gray = OcrEnableGrayBox.IsChecked == true;
        var adaptive = OcrEnableAdaptiveBox.IsChecked == true;
        var neon = OcrEnableNeonBox.IsChecked == true;
        var terminal = OcrEnableTerminalBox.IsChecked == true;
        SetPerfHint(OcrGrayPerfHint, gray, "Adds an extra OCR pass per frame (more CPU).");
        SetPerfHint(OcrAdaptivePerfHint, adaptive, "Adds an adaptive-contrast OCR pass per frame (more CPU).");
        SetPerfHint(OcrNeonPerfHint, neon, "Adds a neon high-contrast OCR pass per frame (more CPU).");
        SetPerfHint(
            OcrTerminalPerfHint,
            terminal,
            "Terminal ROI passes crop and re-OCR dark regions — often the costliest optional pass.");

        var extraPassCount = (gray ? 1 : 0) + (adaptive ? 1 : 0) + (neon ? 1 : 0) + (terminal ? 1 : 0);
        MultiPassPerfBanner.Visibility = extraPassCount >= 2 ? Visibility.Visible : Visibility.Collapsed;

        var maxWidth = TryParseUiInt(OcrMaxWidthBox.Text);
        SetPerfHint(
            OcrMaxWidthPerfHint,
            maxWidth is > 1600,
            "OCR max width above 1600px increases CPU and OCR time. Prefer Optimize for this PC or a leaner Quality preset on weaker machines.");

        var absMax = TryParseUiInt(OcrAbsoluteMaxWidthBox.Text);
        SetPerfHint(
            OcrAbsoluteMaxWidthPerfHint,
            absMax is > 1920,
            "Absolute max width above 1920px can make boosted OCR very expensive on multi-monitor captures.");

        var interval = TryParseUiInt(OcrIntervalBox.Text);
        SetPerfHint(
            OcrIntervalPerfHint,
            warnMonitorAll && interval is < 500,
            "A short OCR interval with All monitors is especially heavy. Prefer Primary/Specific, or raise the interval.");

        var fastBudget = TryParseUiInt(OcrCascadeBudgetFastBox.Text);
        var idleBudget = OcrApplyBudgetWhenIdleBox.IsChecked == true;
        var warnIdleTight = idleBudget && fastBudget is <= 600;
        SetPerfHint(
            OcrIdleBudgetPerfHint,
            warnIdleTight,
            "Idle cascade budget with a short Fast budget can abort searches early on large captures. Prefer Primary/Specific, or raise the Fast budget if misses increase.");
        SetPerfHint(
            OcrFastBudgetPerfHint,
            warnIdleTight,
            "Fast cascade budget ≤ 600ms while Apply budget when idle is on — incomplete cascades are common on All-monitor grabs.");
    }

    private static void SetPerfHint(System.Windows.Controls.TextBlock glyph, bool show, string tooltip)
    {
        glyph.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        glyph.ToolTip = show ? tooltip : null;
    }

    private static int? TryParseUiInt(string? text) =>
        int.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private void AddTrigger_Click(object sender, RoutedEventArgs e)
    {
        _triggers.Add(new TriggerConfig
        {
            Id = Guid.NewGuid(),
            Text = "New trigger",
            Match = MatchMode.Contains,
            Action = CaptureAction.Screenshot,
            OcrMode = OcrSpeedMode.Fast,
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
            TriggerFilter = ApplicationTriggerFilter.All,
            OcrAdaptive = OcrPrepOverride.Inherit,
            OcrNeon = OcrPrepOverride.Inherit,
            OcrGray = OcrPrepOverride.Inherit,
            OcrInvert = OcrPrepOverride.Inherit,
            OcrTerminal = OcrPrepOverride.Inherit
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

        var names = ScreenGrabber.GetRunningProcessNames();
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
            _monitor.RestartIfRunning();
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
            _monitor.RestartIfRunning();
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

    private static string Inv(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int RequireInt(System.Windows.Controls.TextBox box, string label)
    {
        if (!int.TryParse(
                box.Text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
            throw new InvalidOperationException($"{label} must be an integer.");
        return value;
    }

    private AppConfig BuildConfigFromUi()
    {
        var ocrInterval = RequireInt(OcrIntervalBox, "OCR interval");
        var debounce = RequireInt(DebounceBox, "Debounce");
        var bufferSeconds = RequireInt(BufferSecondsBox, "Record buffer seconds");
        var fps = RequireInt(RecordFpsBox, "Record FPS");
        var sequenceInterval = RequireInt(SequenceIntervalBox, "Sequence spacing");
        var sequenceBefore = RequireInt(SequenceBeforeBox, "Sequence before seconds");
        var sequenceAfter = RequireInt(SequenceAfterBox, "Sequence after seconds");
        var retentionDays = RequireInt(RetentionDaysBox, "Retention days");
        var retentionFiles = RequireInt(RetentionFilesBox, "Retention max files");
        var ocrMaxWidth = RequireInt(OcrMaxWidthBox, "OCR max width");
        var ocrUpscale = RequireInt(OcrUpscaleBox, "OCR upscale percent");
        var ocrBoost = RequireInt(OcrBoostUpscaleBox, "OCR boost upscale percent");
        var ocrAbsoluteMax = RequireInt(OcrAbsoluteMaxWidthBox, "OCR absolute max width");
        var ocrTerminalRegions = RequireInt(OcrTerminalMaxRegionsBox, "Terminal max regions");
        var ocrMaxWindows = RequireInt(OcrMaxWindowsBox, "OCR max windows");
        var ocrQueueCapacity = RequireInt(OcrQueueCapacityBox, "OCR queue capacity");
        var ocrBudgetFast = RequireInt(OcrCascadeBudgetFastBox, "Fast cascade budget");
        var ocrBudgetThorough = RequireInt(OcrCascadeBudgetThoroughBox, "Thorough cascade budget");
        var jpegQuality = RequireInt(RecordJpegQualityBox, "JPEG quality");

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

        var summary = CalibrationSummaryText.Text?.Trim() ?? string.Empty;
        if (string.Equals(summary, "Not calibrated on this PC yet.", StringComparison.Ordinal))
            summary = string.Empty;

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
            OcrEnableGray = OcrEnableGrayBox.IsChecked == true,
            OcrUpscalePercent = ocrUpscale,
            OcrBoostUpscalePercent = ocrBoost,
            OcrAbsoluteMaxWidth = ocrAbsoluteMax,
            OcrEnableAdaptive = OcrEnableAdaptiveBox.IsChecked == true,
            OcrEnableNeon = OcrEnableNeonBox.IsChecked == true,
            OcrEnableTerminal = OcrEnableTerminalBox.IsChecked == true,
            OcrTerminalMaxRegions = ocrTerminalRegions,
            OcrMaxWindows = ocrMaxWindows,
            OcrQueueCapacity = ocrQueueCapacity,
            OcrCascadeBudgetFastMs = ocrBudgetFast,
            OcrCascadeBudgetThoroughMs = ocrBudgetThorough,
            OcrApplyBudgetWhenIdle = OcrApplyBudgetWhenIdleBox.IsChecked == true,
            UseBackgroundPriority = UseBackgroundPriorityBox.IsChecked == true,
            AppliedPerformanceProfile = _appliedPerformanceProfile,
            LastCalibrationSummary = summary,
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
        FastMatchThresholdLabel.Text =
            $"{((int)Math.Round(FastMatchThresholdSlider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture)}%";
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
        var scroll = SettingsTabs.SelectedItem == AdvancedTab ? AdvancedScrollViewer : SettingsScrollViewer;
        var offset = scroll.VerticalOffset - e.Delta;
        scroll.ScrollToVerticalOffset(offset);
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

    private sealed class QualityPresetOption
    {
        public QualityPresetOption(PerformanceProfileKind kind, string display)
        {
            Kind = kind;
            Display = display;
        }

        public PerformanceProfileKind Kind { get; }
        public string Display { get; }
    }

    private void EditApplicationOcrPrep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApplicationRow row })
            return;

        var dialog = new ApplicationOcrPrepWindow(
            row.ProcessName,
            row.OcrAdaptive,
            row.OcrNeon,
            row.OcrGray,
            row.OcrInvert,
            row.OcrTerminal)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        row.OcrAdaptive = dialog.OcrAdaptive;
        row.OcrNeon = dialog.OcrNeon;
        row.OcrGray = dialog.OcrGray;
        row.OcrInvert = dialog.OcrInvert;
        row.OcrTerminal = dialog.OcrTerminal;
    }

    private sealed class ApplicationRow : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _processName = string.Empty;
        private ApplicationListMode _listMode = ApplicationListMode.Blacklist;
        private int _debounceSeconds = 30;
        private ApplicationTriggerFilter _triggerFilter = ApplicationTriggerFilter.All;
        private OcrPrepOverride _ocrAdaptive = OcrPrepOverride.Inherit;
        private OcrPrepOverride _ocrNeon = OcrPrepOverride.Inherit;
        private OcrPrepOverride _ocrGray = OcrPrepOverride.Inherit;
        private OcrPrepOverride _ocrInvert = OcrPrepOverride.Inherit;
        private OcrPrepOverride _ocrTerminal = OcrPrepOverride.Inherit;

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

        public OcrPrepOverride OcrAdaptive
        {
            get => _ocrAdaptive;
            set { _ocrAdaptive = value; OnPropertyChanged(); OnPropertyChanged(nameof(OcrPrepSummary)); }
        }

        public OcrPrepOverride OcrNeon
        {
            get => _ocrNeon;
            set { _ocrNeon = value; OnPropertyChanged(); OnPropertyChanged(nameof(OcrPrepSummary)); }
        }

        public OcrPrepOverride OcrGray
        {
            get => _ocrGray;
            set { _ocrGray = value; OnPropertyChanged(); OnPropertyChanged(nameof(OcrPrepSummary)); }
        }

        public OcrPrepOverride OcrInvert
        {
            get => _ocrInvert;
            set { _ocrInvert = value; OnPropertyChanged(); OnPropertyChanged(nameof(OcrPrepSummary)); }
        }

        public OcrPrepOverride OcrTerminal
        {
            get => _ocrTerminal;
            set { _ocrTerminal = value; OnPropertyChanged(); OnPropertyChanged(nameof(OcrPrepSummary)); }
        }

        public string OcrPrepSummary => OcrPrepResolver.FormatSummary(ToConfig());

        public event PropertyChangedEventHandler? PropertyChanged;

        public ApplicationConfig ToConfig() => new()
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Enabled = Enabled,
            ProcessName = ProcessName,
            ListMode = ListMode,
            DebounceSeconds = DebounceSeconds,
            TriggerFilter = TriggerFilter,
            TriggerIds = TriggerIds.ToList(),
            OcrAdaptive = OcrAdaptive,
            OcrNeon = OcrNeon,
            OcrGray = OcrGray,
            OcrInvert = OcrInvert,
            OcrTerminal = OcrTerminal
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
                TriggerIds = ids,
                OcrAdaptive = config.OcrAdaptive,
                OcrNeon = config.OcrNeon,
                OcrGray = config.OcrGray,
                OcrInvert = config.OcrInvert,
                OcrTerminal = config.OcrTerminal
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
