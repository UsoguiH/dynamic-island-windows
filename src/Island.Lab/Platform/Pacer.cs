using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Island.Lab.Platform;

/// <summary>
/// Paces the main loop to the display: sleeps (on a high-resolution timer) until just after the next
/// vblank, waking early for any window message. Presenting to a composition swap chain doesn't block,
/// so without this the loop would spin hundreds of times per vblank.
/// </summary>
sealed unsafe class Pacer : IDisposable
{
    readonly nint _timer;
    long _period = Stopwatch.Frequency / 60, _vblank, _refreshAt;

    public Pacer()
    {
        _timer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_timer == 0) _timer = CreateWaitableTimerExW(0, null, 0, TIMER_ALL_ACCESS);
        // Windows throttles timers of apps it thinks are in the background; the island must wake on time.
        var state = stackalloc uint[] { 1, PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION, 0 };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, state, 12);
    }

    /// <summary>How late (ms) the last wait woke past its vblank (diagnostics).</summary>
    public double Late { get; private set; }

    /// <summary>
    /// Waits until the vblank <paramref name="frames"/> refreshes ahead (1 = the next one), or until a
    /// message arrives. True when the vblank was reached, false when a message woke it first.
    /// </summary>
    public bool Wait(int frames = 1)
    {
        long now = Stopwatch.GetTimestamp();
        Late = 0;
        if (now >= _refreshAt) Refresh(now);
        // aim a sliver past the vblank (landing just before it would wait a whole extra refresh)
        long since = (now - (_vblank + _period / 16)) % _period;
        if (since < 0) since += _period;
        long target = now - since + _period * frames;
        long wait100ns = (target - now) * 10_000_000 / Stopwatch.Frequency;
        if (wait100ns <= 0) return true;
        long due = -wait100ns; // relative
        if (!SetWaitableTimerEx(_timer, &due, 0, 0, 0, 0, 0)) { Thread.Sleep(1); return true; }
        nint h = _timer;
        uint r = MsgWaitForMultipleObjectsEx(1, &h, 1000, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (r == 0) Late = (Stopwatch.GetTimestamp() - target) * 1000.0 / Stopwatch.Frequency;
        return r != 1; // 1 = a message
    }

    void Refresh(long now)
    {
        _refreshAt = now + Stopwatch.Frequency; // re-read the display timing once a second
        var info = stackalloc byte[TimingSize];
        new Span<byte>(info, TimingSize).Clear();
        *(uint*)info = TimingSize;
        if (DwmGetCompositionTimingInfo(0, info) < 0) return;
        long period = *(long*)(info + 12), vblank = *(long*)(info + 28);
        // QPC units are the Stopwatch's on Windows; ignore nonsense (display off, RDP…)
        if (period > Stopwatch.Frequency / 400 && period < Stopwatch.Frequency / 20) { _period = period; _vblank = vblank; }
    }

    public void Dispose() { if (_timer != 0) CloseHandle(_timer); }

    // DWM_TIMING_INFO is byte-packed: cbSize @0, rateRefresh @4, qpcRefreshPeriod @12, rateCompose @20, qpcVBlank @28
    const int TimingSize = 292;
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;
    const uint QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x0004;
    const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1, PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;
    [DllImport("kernel32")] static extern bool SetProcessInformation(nint process, int infoClass, void* info, uint size);
    [DllImport("kernel32")] static extern nint GetCurrentProcess();
    [DllImport("dwmapi")] static extern int DwmGetCompositionTimingInfo(nint hwnd, byte* info);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint CreateWaitableTimerExW(nint attrs, string? name, uint flags, uint access);
    [DllImport("kernel32")] static extern bool SetWaitableTimerEx(nint timer, long* due, int period, nint completion, nint arg, nint wakeContext, uint tolerable);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("user32")] static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint wake, uint flags);
}
