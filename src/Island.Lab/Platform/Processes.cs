using System.Runtime.InteropServices;

namespace Island.Lab.Platform;

/// <summary>
/// What another process was started with: when, its command line and its working directory (read from its
/// PEB, 64-bit processes of the same user only). Results are cached per process, so polling is cheap.
/// </summary>
public static class Processes
{
    public sealed record Info(DateTime Created, string? CommandLine, string? Cwd);

    static readonly Dictionary<int, Info?> _cache = new();

    /// <summary>Null when the process is gone or can't be opened (another user's, elevated, protected).</summary>
    public static Info? Peek(int pid)
    {
        lock (_cache)
        {
            // the start time guards against a reused pid
            if (_cache.TryGetValue(pid, out var c) && c != null && Created(pid) == c.Created) return c;
            var info = Read(pid);
            if (_cache.Count > 512) _cache.Clear();
            return _cache[pid] = info;
        }
    }

    /// <summary>Each running process's parent (the parent may have exited since, and its pid been reused).</summary>
    public static Dictionary<int, int> Parents()
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

    public static DateTime? Created(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (h == 0) return null;
        try { return GetProcessTimes(h, out long created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// The process is still running (and, when <paramref name="startFileTime"/> is given, it is the same one:
    /// pids get reused). One cheap system call, where System.Diagnostics.Process lists every process first.
    /// </summary>
    public static bool Alive(int pid, long? startFileTime = null)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (h == 0) return false;
        try
        {
            if (!GetProcessTimes(h, out long created, out long exited, out _, out _)) return false;
            if (exited != 0 && GetExitCodeProcess(h, out uint code) && code != 259 /* STILL_ACTIVE */) return false;
            return startFileTime is not { } ft || Math.Abs(created - ft) <= 20_000_000;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32")] static extern bool GetExitCodeProcess(nint h, out uint code);

    static Info? Read(int pid)
    {
        var h = OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION */ | 0x0010 /* PROCESS_VM_READ */, false, (uint)pid);
        if (h == 0)
        {
            var at = Created(pid);
            return at is { } t ? new Info(t, null, null) : null;
        }
        try
        {
            if (!GetProcessTimes(h, out long created, out _, out _, out _)) return null;
            var info = new Info(DateTime.FromFileTimeUtc(created), null, null);
            // a 32-bit process keeps a different PEB layout: skip it
            if (!Environment.Is64BitProcess || IsWow64Process(h, out bool wow) && wow) return info;
            var pbi = new nint[6];
            if (NtQueryInformationProcess(h, 0 /* ProcessBasicInformation */, pbi, pbi.Length * 8, out _) != 0) return info;
            nint parameters = ReadPtr(h, pbi[1] + 0x20); // PEB.ProcessParameters
            if (parameters == 0) return info;
            // RTL_USER_PROCESS_PARAMETERS: CurrentDirectory.DosPath at 0x38, CommandLine at 0x70
            return info with { Cwd = ReadUnicode(h, parameters + 0x38), CommandLine = ReadUnicode(h, parameters + 0x70) };
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    static nint ReadPtr(nint h, nint at)
    {
        var buf = new byte[8];
        return ReadProcessMemory(h, at, buf, 8, out _) ? (nint)BitConverter.ToInt64(buf) : 0;
    }

    static string? ReadUnicode(nint h, nint at)
    {
        var head = new byte[16]; // UNICODE_STRING: Length, MaximumLength, (pad), Buffer
        if (!ReadProcessMemory(h, at, head, 16, out _)) return null;
        int len = BitConverter.ToUInt16(head, 0);
        nint buffer = (nint)BitConverter.ToInt64(head, 8);
        if (len == 0 || buffer == 0) return null;
        var text = new byte[len];
        return ReadProcessMemory(h, buffer, text, len, out _) ? System.Text.Encoding.Unicode.GetString(text) : null;
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

    [DllImport("kernel32")] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool Process32NextW(nint snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32")] static extern bool GetProcessTimes(nint h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32")] static extern bool IsWow64Process(nint h, out bool wow);
    [DllImport("kernel32")] static extern bool ReadProcessMemory(nint h, nint at, byte[] buffer, nint size, out nint read);
    [DllImport("ntdll")] static extern int NtQueryInformationProcess(nint h, int cls, nint[] info, int size, out int returned);
}
