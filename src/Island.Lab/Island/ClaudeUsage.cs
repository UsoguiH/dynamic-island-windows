using System.Runtime.InteropServices;
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
/// <para>
/// Everything here stays off the island's thread and off the garbage collector's back: the files are read as
/// bytes through one reused buffer, only the lines that carry usage are parsed, and the stats are computed
/// here too (the island just reads the latest <see cref="Stats"/>). Transcripts run to gigabytes; reading
/// them as strings allocated hundreds of megabytes a second and made the island stutter.
/// </para>
/// </summary>
public sealed class ClaudeUsage : IDisposable
{
    sealed record Ev(DateTime Time, long Tokens, long Output, string? Model, string Project, string Session, string[] Edited, int Commands, ulong Id);

    readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<ulong> _seenIds = new();
    readonly List<Ev> _events = new();
    readonly Dictionary<string, string> _names = new();
    volatile bool _stop;
    volatile UsageStats _stats = new();
    long _asked;
    bool _dirty = true;
    DateTime _computedAt;

    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public ClaudeUsage()
    {
        _onLine = Line;
        _stopped = () => _stop;
        if (Showcase.On) return;
        new Thread(Loop) { IsBackground = true, Name = "ClaudeUsage", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Latest stats (kept fresh in the background; fresher while something is asking for them).</summary>
    public UsageStats Stats
    {
        get
        {
            if (Showcase.On) return Showcase.Usage();
            Volatile.Write(ref _asked, Environment.TickCount64);
            return _stats;
        }
    }

    void Loop()
    {
        bool scanned = false;
        while (!_stop)
        {
            try
            {
                Scan();
                scanned = true;
                // (time alone changes them too: a new day, a window running out)
                if (_dirty || (DateTime.Now - _computedAt).TotalSeconds >= 60)
                {
                    _stats = Compute(scanned);
                    _dirty = false; _computedAt = DateTime.Now;
                }
            }
            catch (Exception ex) { Diag.Log("usage scan: " + ex.Message); }
            // look again soon while the Usage tab is on screen, rarely otherwise
            bool watched = Environment.TickCount64 - Volatile.Read(ref _asked) < 4000;
            for (int i = 0, n = watched ? 4 : 20; i < n && !_stop; i++)
            {
                Thread.Sleep(1000);
                if (!watched && Environment.TickCount64 - Volatile.Read(ref _asked) < 1500) break; // the tab was just opened
            }
        }
    }

    void Scan()
    {
        if (!Directory.Exists(Root)) return;
        var since = DateTime.Now.AddDays(-8);
        // (the directory listing already carries each file's size and date: no extra call per file)
        foreach (var fi in new DirectoryInfo(Root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            if (_stop) return;
            long len; DateTime written;
            try { len = fi.Length; written = fi.LastWriteTime; } catch { continue; }
            if (written < since) continue;
            _offsets.TryGetValue(fi.FullName, out long offset);
            if (len < offset) offset = 0;
            if (len == offset) continue;
            try { _offsets[fi.FullName] = ReadFrom(fi.FullName, offset); }
            catch (IOException) { } // being written right now: next time
            catch (UnauthorizedAccessException) { }
        }
        int before = _events.Count;
        for (int i = _events.Count - 1; i >= 0; i--)
            if (_events[i].Time < since) { _seenIds.Remove(_events[i].Id); _events.RemoveAt(i); }
        if (_events.Count != before) _dirty = true;
    }

    // one buffer for every file
    byte[] _buf = new byte[1 << 18];
    readonly Action<ReadOnlyMemory<byte>> _onLine;
    readonly Func<bool> _stopped;

    static ReadOnlySpan<byte> AssistantTag => "\"type\":\"assistant\""u8;
    static ReadOnlySpan<byte> UsageTag => "\"usage\""u8;

    /// <summary>Reads the lines added after <paramref name="offset"/>; returns where the next read starts (after the last whole line).</summary>
    long ReadFrom(string path, long offset) =>
        // (a line longer than the buffer is kept only if it can be an assistant message: screenshots are skipped unread)
        Jsonl.Read(path, offset, ref _buf, AssistantTag, _onLine, _stopped);

    /// <summary>One transcript line: adds an event when it carries usage or tool calls.</summary>
    void Line(ReadOnlyMemory<byte> line)
    {
        // cheap filters before parsing: only assistant messages carry usage and tool calls
        var s = line.Span;
        if (s.Length < 50 || s.IndexOf(AssistantTag) < 0 || s.IndexOf(UsageTag) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (!o.TryGetProperty("message", out var msg) || !o.TryGetProperty("timestamp", out var ts)) return;
            if (!ts.TryGetDateTime(out var time)) return;
            time = time.ToLocalTime();
            msg.TryGetProperty("model", out var mv);
            string? model = mv.ValueKind == JsonValueKind.String ? Name(mv) : null;
            if (model != null && model.StartsWith('<')) return; // synthetic messages

            ulong id = msg.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? Hash(JsonMarshal.GetRawUtf8Value(idv)) : 0;
            long tokens = 0, output = 0;
            bool first = id == 0 || _seenIds.Add(id);
            if (first && msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                long T(string p) => u.TryGetProperty(p, out var v) && v.TryGetInt64(out var x) ? x : 0;
                output = T("output_tokens");
                tokens = T("input_tokens") + T("cache_creation_input_tokens") + output; // new tokens (cache reads excluded)
            }
            List<string>? edited = null;
            int commands = 0;
            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var c in content.EnumerateArray())
                {
                    if (!c.TryGetProperty("type", out var ct) || ct.ValueKind != JsonValueKind.String || !ct.ValueEquals("tool_use"u8)) continue;
                    if (!c.TryGetProperty("name", out var nv) || nv.ValueKind != JsonValueKind.String) continue;
                    if (nv.ValueEquals("Bash"u8) || nv.ValueEquals("PowerShell"u8)) { commands++; continue; }
                    if (!(nv.ValueEquals("Edit"u8) || nv.ValueEquals("MultiEdit"u8) || nv.ValueEquals("Write"u8) || nv.ValueEquals("NotebookEdit"u8))) continue;
                    if (c.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                        && (input.TryGetProperty("file_path", out var fp) || input.TryGetProperty("notebook_path", out fp)) && fp.ValueKind == JsonValueKind.String)
                        (edited ??= new()).Add(Name(fp));
                }
            if (!first && edited == null && commands == 0) return;
            string cwd = o.TryGetProperty("cwd", out var cv) && cv.ValueKind == JsonValueKind.String ? Name(cv) : "";
            string session = o.TryGetProperty("sessionId", out var sv) && sv.ValueKind == JsonValueKind.String ? Name(sv) : "";
            _events.Add(new Ev(time, tokens, output, first ? model : null, ProjectName(cwd), session, edited?.ToArray() ?? [], commands, id));
            _dirty = true;
        }
        catch { }
    }

    /// <summary>A string that repeats line after line (a model, a folder, a session id): one copy of each is kept.</summary>
    string Name(JsonElement e)
    {
        string s = e.GetString() ?? "";
        if (_names.TryGetValue(s, out var kept)) return kept;
        if (_names.Count > 20000) _names.Clear();
        return _names[s] = s;
    }

    static ulong Hash(ReadOnlySpan<byte> s)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in s) h = (h ^ b) * 1099511628211;
        return h == 0 ? 1 : h;
    }

    readonly Dictionary<string, string> _projects = new();

    string ProjectName(string cwd)
    {
        if (_projects.TryGetValue(cwd, out var known)) return known;
        var dir = cwd.TrimEnd('\\', '/');
        string leaf = "other";
        if (dir.Length > 0)
        {
            leaf = Path.GetFileName(dir);
            // a session started in a project's code folder still counts for the project (…\DynmicIsland\src\Island.Lab → DynmicIsland)
            while (leaf is "src" or "Island.Lab" && Path.GetDirectoryName(dir) is { Length: > 3 } parent) { dir = parent; leaf = Path.GetFileName(dir); }
        }
        return _projects[cwd] = leaf;
    }

    UsageStats Compute(bool scanned)
    {
        var evs = _events;
        evs.Sort(static (a, b) => a.Time.CompareTo(b.Time)); // in place: files arrive one after another, not in time order
        var s = new UsageStats { Ready = scanned };
        var now = DateTime.Now;
        var today = now.Date;

        // 5-hour windows: a window starts at the hour of the first message after the previous one ended
        DateTime? ws = null;
        foreach (var e in evs)
            if (e.Tokens > 0 && (ws == null || e.Time >= ws.Value.AddHours(5))) ws = e.Time.Date.AddHours(e.Time.Hour);
        bool inWindow = ws != null && now < ws.Value.AddHours(5);
        if (inWindow) { s.WindowStart = ws; s.WindowEnd = ws!.Value.AddHours(5); }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>();
        var proj = new Dictionary<string, long>();
        var models = new Dictionary<string, long>();
        DateTime? prev = null;
        foreach (var e in evs)
        {
            if (inWindow && e.Time >= ws) { s.WindowTokens += e.Tokens; if (e.Tokens > 0) s.WindowRequests++; }
            int d = 6 - (int)(today - e.Time.Date).TotalDays;
            if (d is >= 0 and < 7) s.Days[d] += e.Tokens;
            if (e.Time < today) continue;

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
        for (int d = 6; d >= 0 && s.Days[d] > 0; d--) s.Streak++;
        return s;
    }

    readonly Dictionary<string, string> _short = new();

    string ShortModel(string m)
    {
        if (_short.TryGetValue(m, out var known)) return known;
        string name = m.Replace("claude-", "");
        int dash = name.IndexOf('-');
        return _short[m] = dash > 0 ? char.ToUpperInvariant(name[0]) + name[1..dash] : name; // "opus-5-5" → "Opus"
    }

    public void Dispose() => _stop = true;
}
