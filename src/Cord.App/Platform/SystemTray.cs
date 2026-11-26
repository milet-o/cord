using System.Runtime.InteropServices;

namespace Cord.App.Platform;

/// <summary>
/// Lazy Windows system-tray icon via Shell_NotifyIconW: the Win32 icon and message-only window
/// are created only while the Cord window is minimized to the tray (0% GPU mode), avoiding any
/// cross-process Shell_NotifyIconW RPC or message-loop work during normal desktop rendering.
/// </summary>
internal sealed unsafe partial class SystemTray : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 1;
    private const uint WmCommand = 0x0111;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightAlign = 0x0008;
    private const uint TpmBottomAlign = 0x0020;

    private const uint CmdShow = 1001;
    private const uint CmdStopWeb = 1002;
    private const uint CmdExit = 1003;

    private nint _hwnd;
    private nint _hIcon;
    private WndProcDelegate? _wndProc;
    private ushort _classAtom;
    private bool _added;
    private bool _disposed;

    public event Action? ShowRequested;

    public event Action? StopWebShareRequested;

    public event Action? ExitRequested;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public bool IsVisible => _added;

    public void ShowIcon()
    {
        if (!OperatingSystem.IsWindows() || _disposed || _added)
            return;

        if (!EnsureWindowCreated())
            return;

        var data = new NotifyIconDataW
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconDataW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _hIcon,
        };
        CopyTip("Cord — Web Share em segundo plano", data.szTip);
        _added = Shell_NotifyIconW(NimAdd, &data);
    }

    public void HideIcon()
    {
        if (!OperatingSystem.IsWindows() || !_added)
            return;

        var data = new NotifyIconDataW
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconDataW>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIconW(NimDelete, &data);
        _added = false;
    }

    public void PumpMessages()
    {
        if (!_added || _disposed || _hwnd == 0)
            return;

        while (PeekMessageW(out var msg, _hwnd, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!OperatingSystem.IsWindows())
            return;

        HideIcon();

        if (_hIcon != 0)
        {
            DestroyIcon(_hIcon);
            _hIcon = 0;
        }

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_classAtom != 0)
        {
            _ = UnregisterClassW((char*)_classAtom, GetModuleHandleW(null));
            _classAtom = 0;
        }
    }

    private bool EnsureWindowCreated()
    {
        if (_hwnd != 0)
            return true;

        _wndProc ??= WindowProc;
        var className = "CordTrayWnd_" + Environment.ProcessId;
        var hInstance = GetModuleHandleW(null);
        var wc = new WndClassW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = Marshal.StringToHGlobalUni(className),
        };
        try
        {
            _classAtom = RegisterClassW(&wc);
            if (_classAtom == 0)
                return false;

            _hwnd = CreateWindowExW(0, className, "CordTray", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, hInstance, 0);
            if (_hwnd == 0)
                return false;

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                nint large = 0, small = 0;
                if (ExtractIconExW(exePath, 0, &large, &small, 1) > 0)
                {
                    _hIcon = small != 0 ? small : large;
                    if (large != 0 && large != _hIcon)
                        DestroyIcon(large);
                }
            }

            if (_hIcon == 0)
                _hIcon = LoadIconW(0, 32512 /* IDI_APPLICATION */);

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(wc.lpszClassName);
        }
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            var evt = (uint)lParam & 0xFFFF;
            if (evt is WmLButtonDblClk or WmLButtonUp)
            {
                ShowRequested?.Invoke();
                return 0;
            }

            if (evt == WmRButtonUp)
            {
                ShowContextMenu(hwnd);
                return 0;
            }
        }
        else if (msg == WmCommand)
        {
            var cmd = (uint)wParam & 0xFFFF;
            switch (cmd)
            {
                case CmdShow:
                    ShowRequested?.Invoke();
                    return 0;
                case CmdStopWeb:
                    StopWebShareRequested?.Invoke();
                    return 0;
                case CmdExit:
                    ExitRequested?.Invoke();
                    return 0;
            }
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void ShowContextMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            return;
        try
        {
            AppendMenuW(menu, MfString, CmdShow, "Abrir Cord");
            AppendMenuW(menu, MfString, CmdStopWeb, "Parar Web Share");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CmdExit, "Sair");

            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd);
            TrackPopupMenu(menu, TpmRightAlign | TpmBottomAlign, pt.X, pt.Y, 0, hwnd, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void CopyTip(string tip, char* dest)
    {
        var len = Math.Min(tip.Length, 127);
        for (var i = 0; i < len; i++)
            dest[i] = tip[i];
        dest[len] = '\0';
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgStruct
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public PointStruct pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconDataW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort RegisterClassW(WndClassW* lpWndClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int UnregisterClassW(char* lpClassName, nint hInstance);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MsgStruct lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(MsgStruct* lpMsg);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DispatchMessageW(MsgStruct* lpMsg);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LoadIconW(nint hInstance, nint lpIconName);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ExtractIconExW(string lpszFile, int nIconIndex, nint* phiconLarge, nint* phiconSmall, uint nIcons);

    [LibraryImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint dwMessage, NotifyIconDataW* lpData);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint hMenu, uint uFlags, nuint uIdNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out PointStruct lpPoint);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);
}
