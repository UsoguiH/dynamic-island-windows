using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Island.Lab.Platform;

/// <summary>
/// The Bloub icon in the taskbar's notification area: click opens the dashboard, right-click shows a menu
/// (open, agents, usage, start with Windows, quit). Survives Explorer restarts (TaskbarCreated).
/// </summary>
public sealed class Tray : IDisposable
{
    public const uint WM_TRAY = 0x8002; // WM_APP + 2
    public enum Command { None, Open = 1, Agents, Usage, Startup, Quit, Preview, Intro }

    readonly nint _hwnd, _icon;
    readonly uint _taskbarCreated;
    bool _added;

    public Tray(nint hwnd)
    {
        _hwnd = hwnd;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "bloub.ico");
        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        _icon = File.Exists(ico) ? LoadImageW(0, ico, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */) : 0;
        // Single-file builds have no Assets folder: use the icon embedded in the exe (the app icon is resource 32512).
        if (_icon == 0) _icon = LoadImageW(GetModuleHandleW(null), 32512, 1 /* IMAGE_ICON */, size, size, 0);
        Add();
    }

    void Add()
    {
        var d = Data();
        d.uFlags = 0x1 | 0x2 | 0x4; // NIF_MESSAGE | NIF_ICON | NIF_TIP
        _added = Shell_NotifyIconW(0 /* NIM_ADD */, ref d);
    }

    NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = "Dynamic Island — click to open, right-click for options",
    };

    /// <summary>Handles window messages for the tray; returns the command the user picked.</summary>
    public Command Handle(uint msg, nint lParam)
    {
        if (msg == _taskbarCreated) { Add(); return Command.None; }
        if (msg != WM_TRAY) return Command.None;
        switch ((uint)(lParam & 0xFFFF))
        {
            case 0x0202: return Command.Open;           // WM_LBUTTONUP
            case 0x0205: case 0x007B: return Menu();    // WM_RBUTTONUP / WM_CONTEXTMENU
        }
        return Command.None;
    }

    Command Menu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, (nuint)Command.Open, "Open dashboard");
        AppendMenuW(menu, 0, (nuint)Command.Agents, "Agents");
        AppendMenuW(menu, 0, (nuint)Command.Usage, "Usage && limits");
        AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenuW(menu, 0, (nuint)Command.Preview, "Play the \"done\" animation");
        AppendMenuW(menu, 0, (nuint)Command.Intro, "Replay the intro");
        AppendMenuW(menu, 0x800, 0, null);
        AppendMenuW(menu, StartsWithWindows ? 0x8u /* MF_CHECKED */ : 0, (nuint)Command.Startup, "Start with Windows");
        AppendMenuW(menu, 0x800, 0, null);
        AppendMenuW(menu, 0, (nuint)Command.Quit, "Quit Dynamic Island");
        GetCursorPos(out var p);
        SetForegroundWindow(_hwnd); // lets the menu close when you click elsewhere
        int cmd = TrackPopupMenu(menu, 0x100 /* TPM_RETURNCMD */ | 0x2 /* TPM_RIGHTBUTTON */, p.X, p.Y, 0, _hwnd, 0);
        PostMessageW(_hwnd, 0, 0, 0);
        DestroyMenu(menu);
        return (Command)cmd;
    }

    // ---- start with Windows: HKCU\...\Run (shows in Task Manager's Startup tab with the Bloub icon)
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "Dynamic Island";

    public static bool StartsWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) is string; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, false);
        }
    }

    public void Dispose()
    {
        if (_added) { var d = Data(); Shell_NotifyIconW(2 /* NIM_DELETE */, ref d); _added = false; }
        if (_icon != 0) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern nint LoadImageW(nint inst, nint id, uint type, int cx, int cy, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("user32")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32")] static extern int GetSystemMetrics(int index);
    [DllImport("user32")] static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32")] static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32")] static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
