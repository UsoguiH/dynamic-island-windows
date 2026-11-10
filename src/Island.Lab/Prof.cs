using System.Diagnostics;

namespace Island.Lab;

/// <summary>
/// ISLAND_PROF=1: how much time and memory each part of a frame takes (logged with the ISLAND_PERF line).
/// <code>var p = Prof.Start(); … Prof.Stop("tabs", p);</code>
/// Sections may nest (each counts everything inside it). Off, the calls cost nothing.
/// </summary>
public static class Prof
{
    public static readonly bool On = Environment.GetEnvironmentVariable("ISLAND_PROF") == "1";

    public readonly record struct Mark(long Bytes, long Ticks);

    sealed class Acc { public long Bytes, Ticks, Worst; public int N; }
    static readonly Dictionary<string, Acc> _acc = new();

    public static Mark Start() => On ? new Mark(GC.GetAllocatedBytesForCurrentThread(), Stopwatch.GetTimestamp()) : default;

    public static void Stop(string name, Mark m)
    {
        if (!On) return;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - m.Bytes, ticks = Stopwatch.GetTimestamp() - m.Ticks;
        if (!_acc.TryGetValue(name, out var a)) _acc[name] = a = new Acc();
        a.Bytes += bytes; a.Ticks += ticks; a.N++;
        if (ticks > a.Worst) a.Worst = ticks;
    }

    /// <summary>Logs every section (per frame: bytes allocated, milliseconds, and the slowest single run), then starts over.</summary>
    public static void Report(Action<string> log, int frames)
    {
        if (!On || frames <= 0) return;
        double ms = 1000.0 / Stopwatch.Frequency;
        foreach (var (name, a) in _acc.OrderByDescending(k => k.Value.Bytes))
            log($"   prof {name,-18} {a.Bytes / frames,7} B/frame {a.Ticks * ms / frames,7:0.000} ms/frame  worst {a.Worst * ms,6:0.00} ms  x{(double)a.N / frames:0.0}");
        _acc.Clear();
    }
}
