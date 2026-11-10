using System.Collections.Concurrent;

namespace Island.Lab;

/// <summary>
/// Tiny diagnostics log (%TEMP%\islandlab.log). Lines are stamped where they are logged and written by a
/// background thread: opening a file can take tens of milliseconds (antivirus, a busy disk), and the island
/// logs from the thread that draws it — every mode change used to risk a dropped frame.
/// </summary>
public static class Diag
{
    static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "islandlab.log");
    static readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>());
    static int _count, _started;

    public static void Log(string s)
    {
        if (Interlocked.Increment(ref _count) > 500) return; // don't flood
        Write(s);
    }

    /// <summary>Logs without the flood limit (the developer switches: ISLAND_PERF, ISLAND_PROF…).</summary>
    public static void Write(string s)
    {
        _lines.Add($"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}");
        if (Interlocked.Exchange(ref _started, 1) == 0)
            new Thread(Pump) { IsBackground = true, Name = "Log", Priority = ThreadPriority.BelowNormal }.Start();
    }

    static void Pump()
    {
        var batch = new System.Text.StringBuilder();
        foreach (var first in _lines.GetConsumingEnumerable())
        {
            batch.Clear().Append(first);
            while (_lines.TryTake(out var more)) batch.Append(more);
            try { File.AppendAllText(Path, batch.ToString()); } catch { }
        }
    }
}

/// <summary>
/// Saves small files (settings) off the drawing thread. Writes to the same path keep their order and only
/// the newest text is written; <see cref="Flush"/> waits for them (on exit).
/// </summary>
public static class Disk
{
    static readonly Dictionary<string, string> _pending = new();
    static readonly object _lock = new(), _io = new();
    static bool _running;

    public static void Write(string path, string text)
    {
        lock (_lock)
        {
            _pending[path] = text;
            if (_running) return;
            _running = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static _ => Drain(), null);
    }

    static void Drain()
    {
        lock (_io)
            while (true)
            {
                string path, text;
                lock (_lock)
                {
                    if (_pending.Count == 0) { _running = false; return; }
                    var first = _pending.First();
                    (path, text) = (first.Key, first.Value);
                    _pending.Remove(path);
                }
                try { File.WriteAllText(path, text); } catch (Exception ex) { Diag.Log("save: " + ex.Message); }
            }
    }

    /// <summary>Returns once everything asked for so far is on disk.</summary>
    public static void Flush()
    {
        for (int i = 0; i < 200; i++)
        {
            lock (_lock) if (!_running) break;
            Thread.Sleep(5);
        }
        lock (_io) { }
    }
}
