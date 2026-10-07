using System.Text;
using System.Text.Json;

namespace Island.Lab.Island;

/// <summary>What the Usage tab shows: your Claude Code activity, computed from local transcripts.</summary>
public sealed class UsageStats
{
    public bool Ready;
    // current 5-hour limit window (Claude's usage limits reset 5 hours after a window starts)
    public DateTime? WindowStart, WindowEnd;
    public long WindowTokens;
    public int WindowRequests;
    // today
    public long TodayTokens, TodayOutput;
    public int TodayRequests, TodayFiles, TodayCommands, TodaySessions;
    public TimeSpan TodayActive;
    public readonly long[] Hours = new long[24];
    public List<(string Project, long Tokens)> Projects = new();
    public List<(string Model, long Tokens)> Models = new();
    // last 7 days (index 6 = today)
    public readonly long[] Days = new long[7];
    public int Streak;
}

/// <summary>
/// Reads token usage and tool calls from Claude Code transcripts (<c>~/.claude/projects/**/*.jsonl</c>) on a
/// background thread: a first pass over the last week, then only the bytes appended since.
/// One assistant message can span several transcript lines (one per content block) that repeat the same
/// usage, so usage counts once per message id while tool calls count on every line.
/// </summary>
public sealed class ClaudeUsage : IDisposable
{
    sealed record Ev(DateTime Time, long Tokens, long Output, string? Model, string Project, string Session, string[] Edited, int Commands);

    sealed class FileState { public long Offset; public string Partial = ""; }

    readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _seenIds = new();
    readonly List<Ev> _events = new();
    readonly object _lock = new();
    volatile bool _stop;
    UsageStats _stats = new();
    DateTime _statsAt;

    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public ClaudeUsage()
    {
        new Thread(Loop) { IsBackground = true, Name = "ClaudeUsage", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Latest stats (recomputed at most every 2 s).</summary>
    public UsageStats Stats
    {
        get
        {
            if ((DateTime.Now - _statsAt).TotalSeconds > 2) { _stats = Compute(); _statsAt = DateTime.Now; }
            return _stats;
        }
    }

    bool _scanned;

    void Loop()
    {
        while (!_stop)
        {
            try { Scan(); _scanned = true; } catch (Exception ex) { Diag.Log("usage scan: " + ex.Message); }
            for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(1000);
        }
    }

    void Scan()
    {
        if (!Directory.Exists(Root)) return;
        var since = DateTime.Now.AddDays(-8);
        foreach (var path in Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.AllDirectories))
        {
            if (_stop) return;
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            if (fi.LastWriteTime < since) continue;
            if (!_files.TryGetValue(path, out var st)) _files[path] = st = new FileState();
            if (fi.Length < st.Offset) { st.Offset = 0; st.Partial = ""; }
            if (fi.Length == st.Offset) continue;
            ReadFrom(path, st);
        }
        lock (_lock) _events.RemoveAll(e => e.Time < since);
    }

    void ReadFrom(string path, FileState st)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(st.Offset, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        var sb = new StringBuilder(st.Partial);
        int n;
        var batch = new List<Ev>();
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            st.Offset += n;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            string text = sb.ToString();
            int start = 0, nl;
            while ((nl = text.IndexOf('\n', start)) >= 0)
            {
                Line(text.AsSpan(start, nl - start), batch);
                start = nl + 1;
            }
            sb.Clear();
            sb.Append(text, start, text.Length - start);
        }
        st.Partial = sb.ToString();
        if (batch.Count > 0) lock (_lock) _events.AddRange(batch);
    }

    void Line(ReadOnlySpan<char> line, List<Ev> batch)
    {
        // cheap filters before parsing: only assistant messages carry usage and tool calls
        if (line.Length < 50 || !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) || !line.Contains("\"usage\"", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(line.ToString());
            var o = doc.RootElement;
            if (!o.TryGetProperty("message", out var msg) || !o.TryGetProperty("timestamp", out var ts)) return;
            if (!DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var time)) return;
            time = time.ToLocalTime();
            string? id = msg.TryGetProperty("id", out var idv) ? idv.GetString() : null;
            string? model = msg.TryGetProperty("model", out var mv) ? mv.GetString() : null;
            if (model != null && model.StartsWith('<')) return; // synthetic messages
            string cwd = o.TryGetProperty("cwd", out var cv) ? cv.GetString() ?? "" : "";
            string session = o.TryGetProperty("sessionId", out var sv) ? sv.GetString() ?? "" : "";

            long tokens = 0, output = 0;
            bool first = id == null || _seenIds.Add(id);
            if (first && msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                long T(string p) => u.TryGetProperty(p, out var v) && v.TryGetInt64(out var x) ? x : 0;
                output = T("output_tokens");
                tokens = T("input_tokens") + T("cache_creation_input_tokens") + output; // new tokens (cache reads excluded)
            }
            var edited = new List<string>();
            int commands = 0;
            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var c in content.EnumerateArray())
                {
                    if (!c.TryGetProperty("type", out var ct) || ct.GetString() != "tool_use") continue;
                    string name = c.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
                    var input = c.TryGetProperty("input", out var iv) ? iv : default;
                    if (name is "Edit" or "MultiEdit" or "Write" or "NotebookEdit" && input.ValueKind == JsonValueKind.Object
                        && (input.TryGetProperty("file_path", out var fp) || input.TryGetProperty("notebook_path", out fp)) && fp.GetString() is { } f)
                        edited.Add(f);
                    else if (name is "Bash" or "PowerShell") commands++;
                }
            if (!first && edited.Count == 0 && commands == 0) return;
            batch.Add(new Ev(time, tokens, output, first ? model : null, ProjectName(cwd), session, edited.ToArray(), commands));
        }
        catch { }
    }

    static string ProjectName(string cwd)
    {
        var dir = cwd.TrimEnd('\\', '/');
        if (dir.Length == 0) return "other";
        var leaf = Path.GetFileName(dir);
        // a session started in a project's code folder still counts for the project (…\DynmicIsland\src\Island.Lab → DynmicIsland)
        while (leaf is "src" or "Island.Lab" && Path.GetDirectoryName(dir) is { Length: > 3 } parent) { dir = parent; leaf = Path.GetFileName(dir); }
        return leaf;
    }

    UsageStats Compute()
    {
        List<Ev> evs;
        lock (_lock) evs = _events.OrderBy(e => e.Time).ToList();
        var s = new UsageStats { Ready = _scanned };
        var now = DateTime.Now;
        var today = now.Date;

        // 5-hour windows: a window starts at the hour of the first message after the previous one ended
        DateTime? ws = null;
        foreach (var e in evs.Where(e => e.Tokens > 0))
            if (ws == null || e.Time >= ws.Value.AddHours(5)) ws = e.Time.Date.AddHours(e.Time.Hour);
        if (ws != null && now < ws.Value.AddHours(5))
        {
            s.WindowStart = ws; s.WindowEnd = ws.Value.AddHours(5);
            foreach (var e in evs.Where(e => e.Time >= ws)) { s.WindowTokens += e.Tokens; if (e.Tokens > 0) s.WindowRequests++; }
        }

        var todays = evs.Where(e => e.Time >= today).ToList();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>();
        var proj = new Dictionary<string, long>();
        var models = new Dictionary<string, long>();
        DateTime? prev = null;
        foreach (var e in todays)
        {
            s.TodayTokens += e.Tokens; s.TodayOutput += e.Output;
            if (e.Tokens > 0) s.TodayRequests++;
            s.Hours[e.Time.Hour] += e.Tokens;
            foreach (var f in e.Edited) files.Add(f);
            s.TodayCommands += e.Commands;
            sessions.Add(e.Session);
            proj[e.Project] = proj.GetValueOrDefault(e.Project) + e.Tokens;
            if (e.Model != null) { var mk = ShortModel(e.Model); models[mk] = models.GetValueOrDefault(mk) + e.Tokens; }
            // active time: gaps of up to 5 minutes between messages count as working
            if (prev != null) { var gap = e.Time - prev.Value; if (gap <= TimeSpan.FromMinutes(5)) s.TodayActive += gap; }
            prev = e.Time;
        }
        s.TodayFiles = files.Count;
        s.TodaySessions = sessions.Count;
        s.Projects = proj.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList();
        s.Models = models.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList();

        foreach (var e in evs)
        {
            int d = 6 - (int)(today - e.Time.Date).TotalDays;
            if (d is >= 0 and < 7) s.Days[d] += e.Tokens;
        }
        for (int d = 6; d >= 0 && s.Days[d] > 0; d--) s.Streak++;
        return s;
    }

    static string ShortModel(string m)
    {
        m = m.Replace("claude-", "");
        int dash = m.IndexOf('-');
        return dash > 0 ? char.ToUpperInvariant(m[0]) + m[1..dash] : m; // "opus-5-5" → "Opus"
    }

    public void Dispose() => _stop = true;
}
