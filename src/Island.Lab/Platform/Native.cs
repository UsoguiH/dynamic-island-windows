using System.Runtime.InteropServices;

namespace Island.Lab.Platform;

internal static unsafe partial class Native
{
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int GWL_EXSTYLE = -20;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint LWA_ALPHA = 0x2;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_APP_SHOW = 0x8001; // WM_APP + 1: "open the dashboard" (sent by a second launch)
    public const uint WM_CHAR = 0x0102;
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_DPICHANGED = 0x02E0;
    public const int MA_NOACTIVATE = 3;
    public const uint PM_REMOVE = 0x1;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
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
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(int exStyle, string className, string title, int style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32")] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32")] public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern nint GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? cls, string? title);
    [DllImport("user32")] public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] public static extern short GetKeyState(int vk);
    [DllImport("user32")] public static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    [DllImport("user32")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] public static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);
    [DllImport("user32")] public static extern nint GetShellWindow();
    [DllImport("user32")] public static extern nint GetDesktopWindow();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, char* buf, int max);
    [DllImport("user32")] public static extern nint LoadCursorW(nint inst, nint id);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("shell32")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("winmm")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("user32")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] public static extern nint SetCursor(nint cursor);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint hwnd);

    // AppBar: reserves a strip of the screen so maximized windows don't cover it.
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    public const uint ABE_TOP = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [DllImport("shell32")] public static extern nuint SHAppBarMessage(uint msg, ref APPBARDATA data);

    public static string ClassName(nint hwnd)
    {
        char* buf = stackalloc char[128];
        int n = GetClassNameW(hwnd, buf, 128);
        return new string(buf, 0, n);
    }
}
