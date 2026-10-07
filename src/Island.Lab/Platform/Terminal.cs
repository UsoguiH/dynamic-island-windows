using System.Runtime.InteropServices;

namespace Island.Lab.Platform;

/// <summary>
/// Brings the terminal that hosts a Claude Code session to the front. Walks up the process tree
/// (claude → shell → OpenConsole/ptyhost → Windows Terminal / VS Code / conhost…) until a process owns
/// a visible top-level window; falls back to the console window attached to the process.
/// </summary>
public static class Terminal
{
    public static bool Focus(int pid)
    {
        try
        {
            var parents = ParentMap();
            int p = pid;
            var chain = new List<int>();
            for (int depth = 0; depth < 8 && p > 4; depth++)
            {
                chain.Add(p);
                var hwnd = TopWindowOf(p);
                if (hwnd != 0) return Activate(hwnd);
                if (!parents.TryGetValue(p, out p)) break;
            }
            // Multi-process hosts (Electron terminals like ClawDeck, VS Code): the window belongs to a sibling
            // process of the same app — match by executable path.
            foreach (int a in chain.Skip(1))
            {
                var exe = ExePath(a);
                if (exe == null || exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
                    || exe.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var hwnd = TopWindowWhere(wp => string.Equals(ExePath((int)wp), exe, StringComparison.OrdinalIgnoreCase));
                if (hwnd != 0) return Activate(hwnd);
            }
            // Classic console (conhost): the window belongs to conhost, not to the parent chain.
            FreeConsole();
            if (AttachConsole((uint)pid))
            {
                var hwnd = GetConsoleWindow();
                FreeConsole();
                if (hwnd != 0) return Activate(hwnd);
            }
        }
        catch (Exception ex) { Diag.Log("terminal focus: " + ex.Message); }
        return false;
    }

    static bool Activate(nint hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        // Windows only lets the foreground process hand over focus; a synthetic Alt press unlocks it.
        keybd_event(0x12, 0, 0, 0);
        keybd_event(0x12, 0, 2, 0);
        bool ok = SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        return ok || GetForegroundWindow() == hwnd;
    }

    static nint TopWindowOf(int pid) => TopWindowWhere(wp => wp == pid);

    static readonly Dictionary<int, string?> _exe = new();
    static string? ExePath(int pid)
    {
        if (_exe.TryGetValue(pid, out var e)) return e;
        e = null;
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (h != 0)
        {
            var sb = new System.Text.StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref n)) e = sb.ToString();
            CloseHandle(h);
        }
        if (_exe.Count > 512) _exe.Clear();
        _exe[pid] = e;
        return e;
    }

    static nint TopWindowWhere(Func<uint, bool> match)
    {
        nint found = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wp);
            if (match(wp) && IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == 0 && GetWindowTextLengthW(h) > 0)
            {
                found = h;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
                do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("kernel32")] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(nint h, uint flags, System.Text.StringBuilder name, ref uint size);
    [DllImport("kernel32")] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32")] static extern bool FreeConsole();
    [DllImport("kernel32")] static extern nint GetConsoleWindow();
    [DllImport("user32")] static extern bool EnumWindows(EnumProc proc, nint lParam);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32")] static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32")] static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32")] static extern nint GetForegroundWindow();
    [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
}
