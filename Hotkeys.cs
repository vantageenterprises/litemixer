using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace LiteMixer;

/// <summary>System-wide hotkeys through RegisterHotKey, so they work while a game has focus.</summary>
internal sealed class HotkeyManager : IDisposable
{
    const int WmHotkey = 0x0312;
    const uint ModNoRepeat = 0x4000;

    readonly HwndSource _source;
    readonly Dictionary<int, string> _actions = new();
    int _nextId = 1;

    public event Action<string>? Pressed;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("LiteMixerHotkeys") { WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers every bound action and returns the ids whose combos are taken by another app.</summary>
    public List<string> Register(IReadOnlyDictionary<string, string> bindings)
    {
        UnregisterAll();
        var failed = new List<string>();
        foreach (var (action, combo) in bindings)
        {
            if (!TryParse(combo, out var modifiers, out var key)) continue;
            int id = _nextId++;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (RegisterHotKey(_source.Handle, id, ToNative(modifiers) | ModNoRepeat, vk)) _actions[id] = action;
            else failed.Add(action);
        }
        return failed;
    }

    public static bool TryParse(string? combo, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) return false;
                    break;
            }
        }
        return key != Key.None;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    static uint ToNative(ModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) value |= 0x1;
        if (modifiers.HasFlag(ModifierKeys.Control)) value |= 0x2;
        if (modifiers.HasFlag(ModifierKeys.Shift)) value |= 0x4;
        if (modifiers.HasFlag(ModifierKeys.Windows)) value |= 0x8;
        return value;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            Pressed?.Invoke(action);
            handled = true;
        }
        return IntPtr.Zero;
    }

    void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
