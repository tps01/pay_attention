using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PayAttention;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xA11E;

    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? HotkeyPressed;

    public void Register(string hotkey)
    {
        Unregister();

        if (!TryParse(hotkey, out var modifiers, out var key))
            throw new ArgumentException($"Invalid hotkey: {hotkey}");

        var parameters = new HwndSourceParameters("pay_attention_hotkey")
        {
            Width = 0,
            Height = 0,
            ParentWindow = IntPtr.Zero,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        try
        {
            if (!NativeMethods.RegisterHotKey(
                    _source.Handle,
                    HotkeyId,
                    modifiers | NativeMethods.ModNoRepeat,
                    key))
            {
                throw new InvalidOperationException("RegisterHotKey failed. The combination may already be in use.");
            }

            _registered = true;
        }
        catch
        {
            Unregister();
            throw;
        }
    }

    public void Dispose() => Unregister();

    private void Unregister()
    {
        if (_source is not null && _registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "alt":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "shift":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    return false;
            }
        }

        var keyName = parts[^1];
        if (keyName.Length == 1)
        {
            var ch = char.ToUpperInvariant(keyName[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = ch;
                return true;
            }
        }

        if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(keyName[1..], out var fn) && fn is >= 1 and <= 24)
        {
            key = (uint)(0x70 + fn - 1);
            return true;
        }

        return false;
    }

    private static class NativeMethods
    {
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        public const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
