using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LiteMixer;

/// <summary>Minimal Shell_NotifyIcon wrapper so the app does not need to load WinForms.</summary>
internal sealed class TrayIcon : IDisposable
{
    const int WmTray = 0x8001;
    const int WmInput = 0x00FF;
    const int WmMouseMove = 0x0200;
    const int WmLButtonUp = 0x0202;
    const int WmRButtonUp = 0x0205;
    const int WmMButtonUp = 0x0208;
    const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    const int NifMessage = 1, NifIcon = 2, NifTip = 4;
    const ushort RiMouseWheel = 0x0400;

    readonly HwndSource _source;
    readonly IntPtr _normalIcon;
    readonly IntPtr _mutedIcon;
    readonly int _taskbarCreated;
    readonly DispatcherTimer _hoverTimer;
    IntPtr _icon;
    string _tip;
    bool _wheelHooked;

    public event Action? Clicked;
    public event Action? MiddleClicked;
    public event Action? MenuRequested;
    /// <summary>Mouse wheel over the tray icon: +1 per notch up, -1 per notch down.</summary>
    public event Action<int>? Scrolled;

    public TrayIcon(string tip)
    {
        _tip = tip;
        _taskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");
        // A hidden top-level window (not message-only) so it also hears TaskbarCreated after Explorer restarts.
        _source = new HwndSource(new HwndSourceParameters("LiteMixerTray") { WindowStyle = 0, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
        _normalIcon = LoadIcon();
        _mutedIcon = Icons.CreateMutedTrayIcon();
        _icon = _normalIcon;

        // Tray icons get no wheel messages, so raw mouse input is listened to only while the cursor is over the icon.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += (_, _) =>
        {
            if (!CursorOverIcon()) SetWheelHook(false);
        };
        Add();
    }

    public void ShowMenu(ContextMenu menu)
    {
        SetForegroundWindow(_source.Handle);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public void SetState(bool micMuted, string tip)
    {
        var icon = micMuted && _mutedIcon != IntPtr.Zero ? _mutedIcon : _normalIcon;
        if (icon == _icon && tip == _tip) return;
        _icon = icon;
        _tip = tip;
        var data = Data();
        data.uFlags = NifIcon | NifTip;
        Shell_NotifyIcon(NimModify, ref data);
    }

    void Add()
    {
        var data = Data();
        data.uFlags = NifMessage | NifIcon | NifTip;
        Shell_NotifyIcon(NimAdd, ref data);
    }

    NotifyIconData Data() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _source.Handle,
        uID = 1,
        uCallbackMessage = WmTray,
        hIcon = _icon,
        szTip = _tip.Length > 127 ? _tip[..127] : _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTray)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp: Clicked?.Invoke(); break;
                case WmMButtonUp: MiddleClicked?.Invoke(); break;
                case WmRButtonUp: MenuRequested?.Invoke(); break;
                case WmMouseMove: SetWheelHook(true); break;
            }
            handled = true;
        }
        else if (msg == WmInput)
        {
            int notches = ReadWheel(lParam);
            if (notches != 0 && CursorOverIcon()) Scrolled?.Invoke(notches);
        }
        else if (msg == _taskbarCreated)
        {
            Add();
        }
        return IntPtr.Zero;
    }

    void SetWheelHook(bool on)
    {
        if (on == _wheelHooked) return;
        var device = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x02, // mouse
            Flags = on ? 0x100u /* RIDEV_INPUTSINK */ : 0x1u /* RIDEV_REMOVE */,
            Target = on ? _source.Handle : IntPtr.Zero,
        };
        if (RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            _wheelHooked = on;
            _hoverTimer.IsEnabled = on;
        }
    }

    static int ReadWheel(IntPtr lParam)
    {
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        GetRawInputData(lParam, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 256) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize) != size) return 0;
            if (Marshal.ReadInt32(buffer) != 0 /* RIM_TYPEMOUSE */) return 0;
            // RAWMOUSE follows the header: usFlags (2), padding (2), usButtonFlags (2), usButtonData (2).
            ushort flags = (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 4);
            if ((flags & RiMouseWheel) == 0) return 0;
            short delta = Marshal.ReadInt16(buffer, (int)headerSize + 6);
            return delta > 0 ? 1 : delta < 0 ? -1 : 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    bool CursorOverIcon()
    {
        var id = new NotifyIconIdentifier { cbSize = Marshal.SizeOf<NotifyIconIdentifier>(), hWnd = _source.Handle, uID = 1 };
        if (Shell_NotifyIconGetRect(ref id, out var rect) != 0 || !GetCursorPos(out var point)) return false;
        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    static IntPtr LoadIcon()
    {
        var small = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
            return small[0];
        return LoadIconW(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
    }

    public void Dispose()
    {
        SetWheelHook(false);
        var data = Data();
        Shell_NotifyIcon(NimDelete, ref data);
        _source.Dispose();
        Icons.DestroyIcon(_normalIcon);
        if (_mutedIcon != IntPtr.Zero) Icons.DestroyIcon(_mutedIcon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NotifyIconIdentifier
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect location);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
}
