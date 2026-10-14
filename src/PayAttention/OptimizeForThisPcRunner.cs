using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PayAttention.Capture;
using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using PixelFormats = System.Windows.Media.PixelFormats;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PayAttention;

/// <summary>
/// Shows plain/neon test windows, times Very Low→Ultra (~10s), and returns a config with the P1dB knee applied.
/// Does not save — the caller fills Settings for the user to review.
/// </summary>
public static class OptimizeForThisPcRunner
{
    public sealed record Result(
        AppConfig Config,
        PerformanceProfileKind Chosen,
        string Summary,
        IReadOnlyList<PerformanceProfiles.ProfileTrialResult> Trials);

    public static async Task<Result?> RunAsync(
        Window owner,
        AppConfig currentConfig,
        CaptureMonitor monitor,
        AppLogger logger,
        CancellationToken cancellationToken = default)
    {
        var benchmark = new PcPerformanceBenchmark();
        if (!benchmark.OcrAvailable)
        {
            MessageBox.Show(
                owner,
                "Windows OCR is not available. Install a language pack with OCR support and try again.",
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var wasRunning = monitor.IsRunning;
        var wasPaused = monitor.IsPaused;
        if (wasRunning && !wasPaused)
            monitor.SetPaused(true);

        Window? plainWindow = null;
        Window? neonWindow = null;
        var progressDialog = CreateProgressDialog(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progressDialog.Closing += (_, e) =>
        {
            if (!linked.IsCancellationRequested)
                linked.Cancel();
        };
        progressDialog.Show();

        try
        {
            plainWindow = CreatePlainWindow();
            neonWindow = CreateNeonWindow();
            plainWindow.Show();
            neonWindow.Show();
            // Let DWM paint before PrintWindow / CopyFromScreen.
            await Task.Delay(250, linked.Token).ConfigureAwait(true);

            using var plainBmp = CaptureWindowBitmap(plainWindow);
            using var neonBmp = CaptureWindowBitmap(neonWindow);
            if (plainBmp is null || neonBmp is null)
            {
                MessageBox.Show(
                    owner,
                    "Could not capture the test windows.",
                    "pay_attention",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return null;
            }

            var progress = new Progress<string>(msg =>
            {
                if (progressDialog.Content is StackPanel panel &&
                    panel.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
                    label.Text = msg;
            });

            var trials = await benchmark
                .RunAsync(plainBmp, neonBmp, progress, linked.Token)
                .ConfigureAwait(true);

            var chosen = PerformanceProfiles.Select(trials, out _);
            var summary = PerformanceProfiles.FormatSummary(chosen, trials);
            var config = CloneConfig(currentConfig);
            PerformanceProfiles.Apply(config, chosen);
            config.LastCalibrationSummary = summary;

            logger.Info($"Optimize for this PC chose {chosen}: {summary}");
            return new Result(config, chosen, summary, trials);
        }
        catch (OperationCanceledException)
        {
            logger.Info("Optimize for this PC cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            logger.Error("Optimize for this PC failed", ex);
            MessageBox.Show(
                owner,
                ex.Message,
                "pay_attention",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return null;
        }
        finally
        {
            try { progressDialog.Close(); } catch { /* ignore */ }
            try { plainWindow?.Close(); } catch { /* ignore */ }
            try { neonWindow?.Close(); } catch { /* ignore */ }

            if (wasRunning && !wasPaused)
                monitor.SetPaused(false);
        }
    }

    private static Window CreateProgressDialog(Window owner)
    {
        var label = new TextBlock
        {
            Text = "Preparing test windows… (about 10 seconds)",
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 90,
            Margin = new Thickness(16, 0, 16, 16),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsCancel = true
        };
        var panel = new StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(cancel);
        var dialog = new Window
        {
            Title = "Optimize for this PC",
            Owner = owner,
            Width = 420,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = panel
        };
        cancel.Click += (_, _) => dialog.Close();
        return dialog;
    }

    private static Window CreatePlainWindow()
    {
        var text = new TextBlock
        {
            Text = PerformanceProfiles.PlainToken,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            Foreground = Brushes.Black,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24)
        };
        return new Window
        {
            Title = "pay_attention calibration — plain",
            Width = 420,
            Height = 160,
            Background = Brushes.White,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 80,
            Top = 80,
            Content = text
        };
    }

    private static Window CreateNeonWindow()
    {
        // Harder than plain: small dim Consolas so lean profiles often miss neon.
        var text = new TextBlock
        {
            Text = PerformanceProfiles.NeonToken,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0xAF, 0x0A)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16)
        };
        return new Window
        {
            Title = "pay_attention calibration — neon",
            Width = 360,
            Height = 120,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 80,
            Top = 280,
            Content = text
        };
    }

    private static Bitmap? CaptureWindowBitmap(Window window)
    {
        window.UpdateLayout();
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
            return null;

        var grabber = new ScreenGrabber();
        using (var frame = grabber.TryCaptureWindow(hwnd))
        {
            if (frame is not null)
                return (Bitmap)frame.Bitmap.Clone();
        }

        // Fallback: render the WPF visual to a bitmap (still exercises OCR prep on this PC).
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(window);
        return RenderTargetToBitmap(rtb);
    }

    private static Bitmap RenderTargetToBitmap(System.Windows.Media.Imaging.RenderTargetBitmap rtb)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var temp = new Bitmap(ms);
        return new Bitmap(temp);
    }

    private static AppConfig CloneConfig(AppConfig source) =>
        System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
            System.Text.Json.JsonSerializer.Serialize(source, JsonDefaults.Options),
            JsonDefaults.Options)!;
}
