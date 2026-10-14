using System.Windows;
using PayAttention.Core.Models;
using ComboBox = System.Windows.Controls.ComboBox;

namespace PayAttention;

public partial class ApplicationOcrPrepWindow : Window
{
    public OcrPrepOverride OcrAdaptive { get; private set; }
    public OcrPrepOverride OcrNeon { get; private set; }
    public OcrPrepOverride OcrGray { get; private set; }
    public OcrPrepOverride OcrInvert { get; private set; }
    public OcrPrepOverride OcrTerminal { get; private set; }

    public ApplicationOcrPrepWindow(
        string processName,
        OcrPrepOverride adaptive,
        OcrPrepOverride neon,
        OcrPrepOverride gray,
        OcrPrepOverride invert,
        OcrPrepOverride terminal)
    {
        InitializeComponent();
        SubtitleText.Text = string.IsNullOrWhiteSpace(processName)
            ? "Application OCR prep"
            : $"OCR prep — {processName}";

        Fill(AdaptiveBox, adaptive);
        Fill(NeonBox, neon);
        Fill(GrayBox, gray);
        Fill(InvertBox, invert);
        Fill(TerminalBox, terminal);

        OcrAdaptive = adaptive;
        OcrNeon = neon;
        OcrGray = gray;
        OcrInvert = invert;
        OcrTerminal = terminal;
    }

    private static void Fill(ComboBox box, OcrPrepOverride selected)
    {
        box.ItemsSource = Enum.GetValues(typeof(OcrPrepOverride));
        box.SelectedItem = selected;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        OcrAdaptive = Read(AdaptiveBox);
        OcrNeon = Read(NeonBox);
        OcrGray = Read(GrayBox);
        OcrInvert = Read(InvertBox);
        OcrTerminal = Read(TerminalBox);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static OcrPrepOverride Read(ComboBox box) =>
        box.SelectedItem is OcrPrepOverride v ? v : OcrPrepOverride.Inherit;
}
