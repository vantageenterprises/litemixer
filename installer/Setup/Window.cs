using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteMixerSetup;

/// <summary>A borderless Win32 window that keeps Windows' own shadow and rounded corners and repaints the Ui each frame.</summary>
internal sealed unsafe partial class Window
{
    const uint WmDestroy = 0x0002, WmSize = 0x0005, WmPaint = 0x000F, WmClose = 0x0010, WmEraseBkgnd = 0x0014,
        WmSetCursor = 0x0020, WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmKeyDown = 0x0100, WmTimer = 0x0113,
        WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmDpiChanged = 0x02E0;
    const int HtClient = 1, HtCaption = 2;
    const uint WsOverlapped = 0, WsCaption = 0xC00000, WsSysMenu = 0x80000, WsMinimizeBox = 0x20000;
    const int VkTab = 0x09, VkReturn = 0x0D, VkEscape = 0x1B, VkSpace = 0x20;

    static Window? _instance;

    readonly Ui _ui;
    readonly Action<Ui.Hit> _clicked;
    readonly Action _tick;
    readonly bool _preview;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    nint _hwnd, _memDc, _bitmap, _oldBitmap;
    int _pixelW, _pixelH;
    float _scale = 1;
    double _last;

    public Window(Ui ui, Action<Ui.Hit> clicked, Action tick, bool preview)
    {
        _ui = ui;
        _clicked = clicked;
        _tick = tick;
        _preview = preview;
        _instance = this;
    }

    public nint Handle => _hwnd;

    public void Run()
    {
        SetProcessDpiAwarenessContext(-4 /* PER_MONITOR_AWARE_V2 */);
        var instance = GetModuleHandleW(null);
        const string className = "LiteMixerSetupWindow";
        fixed (char* name = className)
        {
            var cls = new WndClassEx
            {
                cbSize = (uint)sizeof(WndClassEx),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(0, 32512 /* IDC_ARROW */),
                hIcon = LoadIconW(instance, 32512),
                lpszClassName = name,
            };
            // The SDK embeds the app icon as the first icon resource.
            ExtractIconExW(Environment.ProcessPath ?? "", 0, &cls.hIcon, &cls.hIconSm, 1);
            RegisterClassExW(ref cls);
        }

        // Centered on the primary monitor at that monitor's scale.
        uint dpi = GetDpiForSystem();
        _scale = dpi / 96f;
        int w = (int)(Ui.Width * _scale), h = (int)(Ui.Height * _scale);
        int x = (GetSystemMetrics(0) - w) / 2, y = (GetSystemMetrics(1) - h) / 2;

        // A captioned window with its frame removed in WM_NCCALCSIZE keeps the DWM shadow and rounded corners.
        _hwnd = CreateWindowExW(0, className, "LiteMixer Setup", WsOverlapped | WsCaption | WsSysMenu | WsMinimizeBox,
            x, y, w, h, 0, 0, instance, 0);

        int dark = 1, round = 2;
        DwmSetWindowAttribute(_hwnd, 20 /* DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(_hwnd, 33 /* CORNER_PREFERENCE */, ref round, sizeof(int));
        var margins = new Margins { Left = 0, Right = 0, Top = 1, Bottom = 0 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, 0x0027 /* NOMOVE|NOSIZE|NOZORDER|FRAMECHANGED */);

        ShowWindow(_hwnd, _preview ? 4 /* SHOWNOACTIVATE */ : 5 /* SHOW */);
        if (!_preview) SetForegroundWindow(_hwnd);
        SetTimer(_hwnd, 1, 16, 0);

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    public void Close() => PostMessageW(_hwnd, WmClose, 0, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        var self = _instance;
        return self == null ? DefWindowProcW(hwnd, message, wParam, lParam) : self.OnMessage(hwnd, message, wParam, lParam);
    }

    nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmNcCalcSize when wParam != 0:
                return 0; // The client area covers the whole window, so there is no title bar.

            case WmEraseBkgnd:
                return 1;

            case WmNcHitTest:
            {
                var point = new Point { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                ScreenToClient(hwnd, ref point);
                // Anything that is not a control drags the window.
                return _ui.HitTest(point.X / _scale, point.Y / _scale) == Ui.Hit.None ? HtCaption : HtClient;
            }

            case WmSetCursor:
                if (_ui.Hot is not Ui.Hit.None)
                {
                    SetCursor(LoadCursorW(0, 32649 /* IDC_HAND */));
                    return 1;
                }
                break;

            case WmTimer:
                _tick();
                Frame();
                InvalidateRect(hwnd, 0, 0);
                return 0;

            case WmPaint:
                Paint(hwnd);
                return 0;

            case WmSize:
                ReleaseBuffer();
                return 0;

            case WmDpiChanged:
            {
                _scale = (wParam.ToInt64() & 0xFFFF) / 96f;
                var rect = (Rect*)lParam;
                SetWindowPos(hwnd, 0, rect->Left, rect->Top, rect->Right - rect->Left, rect->Bottom - rect->Top, 0x0014);
                ReleaseBuffer();
                return 0;
            }

            case WmLButtonDown:
                _ui.Pressed = _ui.Hot;
                SetCapture(hwnd);
                return 0;

            case WmLButtonUp:
            {
                ReleaseCapture();
                var pressed = _ui.Pressed;
                _ui.Pressed = Ui.Hit.None;
                if (pressed != Ui.Hit.None && pressed == _ui.Hot) _clicked(pressed);
                return 0;
            }

            case WmKeyDown:
                OnKey((int)wParam);
                return 0;

            case WmClose:
                if (!_ui.CanClose) return 0;
                DestroyWindow(hwnd);
                return 0;

            case WmDestroy:
                KillTimer(hwnd, 1);
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    void OnKey(int key)
    {
        switch (key)
        {
            case VkTab:
            {
                var order = _ui.Focusable().ToList();
                if (order.Count > 0) _ui.Focused = order[(order.IndexOf(_ui.Focused) + 1) % order.Count];
                break;
            }
            case VkReturn:
            case VkSpace:
                _clicked(_ui.Focused != Ui.Hit.None ? _ui.Focused : Ui.Hit.Primary);
                break;
            case VkEscape:
                if (_ui.CanClose) DestroyWindow(_hwnd);
                break;
        }
    }

    void Frame()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _last, 0.1);
        _last = now;

        if (GetCursorPos(out var point) && ScreenToClient(_hwnd, ref point) && WindowFromPoint(point.ToScreen(_hwnd)) == _hwnd)
            _ui.Hot = _ui.HitTest(point.X / _scale, point.Y / _scale);
        else
            _ui.Hot = Ui.Hit.None;
        _ui.Advance(dt);
    }

    void Paint(nint hwnd)
    {
        var hdc = BeginPaint(hwnd, out var paint);
        GetClientRect(hwnd, out var client);
        int w = client.Right - client.Left, h = client.Bottom - client.Top;
        if (w > 0 && h > 0)
        {
            if (_memDc == 0 || w != _pixelW || h != _pixelH)
            {
                ReleaseBuffer();
                _memDc = CreateCompatibleDC(hdc);
                _bitmap = CreateCompatibleBitmap(hdc, w, h);
                _oldBitmap = SelectObject(_memDc, _bitmap);
                _pixelW = w;
                _pixelH = h;
            }
            using (var gfx = Gfx.FromHdc(_memDc, _scale)) _ui.Draw(gfx);
            BitBlt(hdc, 0, 0, w, h, _memDc, 0, 0, 0x00CC0020 /* SRCCOPY */);
        }
        EndPaint(hwnd, ref paint);
    }

    void ReleaseBuffer()
    {
        if (_memDc == 0) return;
        SelectObject(_memDc, _oldBitmap);
        DeleteObject(_bitmap);
        DeleteDC(_memDc);
        _memDc = 0;
    }

    /// <summary>True when the user asked Windows to turn off animations.</summary>
    public static bool ReducedMotion()
    {
        int enabled = 1;
        return SystemParametersInfoW(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, &enabled, 0) && enabled == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WndClassEx
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;

        public readonly Point ToScreen(nint hwnd)
        {
            var copy = this;
            ClientToScreen(hwnd, ref copy);
            return copy;
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Msg { public nint Hwnd; public uint Message; public nint W, L; public uint Time; public Point Pt; }
    [StructLayout(LayoutKind.Sequential)] struct PaintStruct { public nint Hdc; public int Erase; public Rect Paint; public int Restore, Update; public fixed byte Reserved[32]; }

    const string User = "user32.dll", Gdi = "gdi32.dll";
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport(User)] private static partial ushort RegisterClassExW(ref WndClassEx cls);
    [LibraryImport(User, StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport(User)] private static partial nint DefWindowProcW(nint hwnd, uint message, nint w, nint l);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport(User)] private static partial void PostQuitMessage(int code);
    [LibraryImport(User)] private static partial int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(ref Msg msg);
    [LibraryImport(User)] private static partial nint DispatchMessageW(ref Msg msg);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint hwnd, uint message, nint w, nint l);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport(User)] private static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool KillTimer(nint hwnd, nuint id);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool InvalidateRect(nint hwnd, nint rect, int erase);
    [LibraryImport(User)] private static partial nint BeginPaint(nint hwnd, out PaintStruct paint);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndPaint(nint hwnd, ref PaintStruct paint);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint hwnd, out Rect rect);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Point point);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ScreenToClient(nint hwnd, ref Point point);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(nint hwnd, ref Point point);
    [LibraryImport(User)] private static partial nint WindowFromPoint(Point point);
    [LibraryImport(User)] private static partial nint SetCapture(nint hwnd);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReleaseCapture();
    [LibraryImport(User)] private static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport(User)] private static partial nint LoadIconW(nint instance, nint name);
    [LibraryImport(User)] private static partial nint SetCursor(nint cursor);
    [LibraryImport(User)] private static partial int GetSystemMetrics(int index);
    [LibraryImport(User)] private static partial uint GetDpiForSystem();
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetProcessDpiAwarenessContext(nint context);
    [LibraryImport(User)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SystemParametersInfoW(uint action, uint param, void* value, uint flags);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint ExtractIconExW(string file, int index, nint* large, nint* small, uint count);
    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [LibraryImport("dwmapi.dll")] private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [LibraryImport(Gdi)] private static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport(Gdi)] private static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport(Gdi)] private static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport(Gdi)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport(Gdi)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint hdc);
    [LibraryImport(Gdi)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint op);
}
