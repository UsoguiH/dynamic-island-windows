using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Island.Lab.Island;

/// <summary>One running Claude Code session, as last read from disk.</summary>
public sealed record SessionInfo(
    string Id, int Pid, string Name, string Cwd,
    string Status,          // "busy" | "idle" | "waiting"
    string? WaitingFor,     // e.g. "dialog open", "input needed"
    string? Title,          // the session's AI title ("Desktop dynamic island with mascot")
    string? Prompt,         // last prompt you sent
    AgentData Data,         // live log, chat, plan (todos), changed files, model, context — a snapshot
    DateTime StatusSince,
    DateTime StartedAt);

/// <summary>
/// Reads the live Claude Code sessions without any configuration:
/// <list type="bullet">
/// <item><c>~/.claude/sessions/&lt;pid&gt;.json</c> — Claude Code's own registry of running sessions
///   (cwd, name, status busy/idle/waiting, waitingFor);</item>
/// <item><c>~/.claude/projects/*/&lt;sessionId&gt;.jsonl</c> — the transcript, tailed for the AI title, your
///   last prompt and every tool call (turned into a readable live log).</item>
/// </list>
/// Runs on a background thread; the island reads <see cref="Snapshot"/> each frame.
/// </summary>
public sealed class ClaudeSessions : IDisposable
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    static readonly string SessionsDir = Path.Combine(Root, "sessions");
    static readonly string ProjectsDir = Path.Combine(Root, "projects");

    volatile IReadOnlyList<SessionInfo> _snapshot = [];
    public IReadOnlyList<SessionInfo> Snapshot => _snapshot;

    readonly Thread _thread;
    volatile bool _stop;

    sealed class Tail
    {
        public string? Path;
        public DateTime NextSearch;
        public long Length = -1;
        public string? Title, Prompt;
        public bool TitleScanned;
        public AgentData Data = new();
        public AgentData? Snap;
        public long Offset = -1;
    }
    readonly Dictionary<string, Tail> _tails = new();

    /// <summary>A session file as last parsed (it is only read again when Claude Code rewrites it).</summary>
    sealed record Entry(long Length, DateTime Written, string? Id, int Pid, long? ProcStart, bool Shown, string Cwd, string Name, string Status,
        string? WaitingFor, long Since, long Started);
    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    // one buffer for every transcript
    byte[] _buf = new byte[1 << 18];

    public ClaudeSessions()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ClaudeSessions", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Loop()
    {
        while (!_stop)
        {
            try { _snapshot = Showcase.On ? Showcase.Sessions() : Read(); }
            catch (Exception ex) { Diag.Log("sessions: " + ex.Message); }
            Thread.Sleep(400);
        }
    }

    readonly HashSet<string> _seen = new(), _files = new(StringComparer.OrdinalIgnoreCase);

    List<SessionInfo> Read()
    {
        var list = new List<SessionInfo>();
        if (!Directory.Exists(SessionsDir)) return list;
        _seen.Clear(); _files.Clear();
        // (the directory listing carries each file's size and date, so an unchanged session costs no read)
        foreach (var fi in new DirectoryInfo(SessionsDir).EnumerateFiles("*.json"))
        {
            long len; DateTime written;
            try { len = fi.Length; written = fi.LastWriteTimeUtc; } catch { continue; }
            _files.Add(fi.FullName);
            if (!_entries.TryGetValue(fi.FullName, out var e) || e.Length != len || e.Written != written)
            {
                if (Parse(fi.FullName, len, written) is not { } parsed) continue; // being rewritten right now: next time
                _entries[fi.FullName] = e = parsed;
            }
            if (e.Id == null || !e.Shown) continue;
            // (the registry stores the process start time, FILETIME: a reused pid is not the session)
            if (!Platform.Processes.Alive(e.Pid, e.ProcStart)) continue;

            _seen.Add(e.Id);
            var t = Transcript(e.Id);
            if (t.Snap == null || t.Snap.Version != t.Data.Version) t.Snap = t.Data.Clone();
            list.Add(new SessionInfo(e.Id, e.Pid, e.Name, e.Cwd, e.Status, e.WaitingFor, t.Title, t.Prompt, t.Snap,
                DateTimeOffset.FromUnixTimeMilliseconds(e.Since).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(e.Started).UtcDateTime));
        }
        if (_tails.Count > _seen.Count) foreach (var k in _tails.Keys.Where(k => !_seen.Contains(k)).ToList()) _tails.Remove(k);
        if (_entries.Count > _files.Count) foreach (var k in _entries.Keys.Where(k => !_files.Contains(k)).ToList()) _entries.Remove(k);
        // Most recently active first.
        list.Sort((a, b) => b.StatusSince.CompareTo(a.StatusSince));
        return list;
    }

    /// <summary>Reads one session file. Null when it cannot be read right now.</summary>
    static Entry? Parse(string file, long len, DateTime written)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file));
            var o = doc.RootElement;
            string? id = Str(o, "sessionId");
            if (id == null || !o.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid))
                return new Entry(len, written, null, 0, null, false, "", "", "", null, 0, 0);
            // Sessions you sit in front of: a terminal, an editor. Headless runs ("claude -p": the limits check and
            // the tab builder of the island, scripts, the agents it launches itself) register as interactive too,
            // with an SDK entry point; they would pop in and out of the team as one-second Bloubs.
            bool shown = (Str(o, "kind") ?? "interactive") == "interactive"
                         && !(Str(o, "entrypoint") ?? "cli").StartsWith("sdk", StringComparison.OrdinalIgnoreCase);
            string cwd = Str(o, "cwd") ?? "";
            string name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = Str(o, "name") ?? "session";
            long since = o.TryGetProperty("statusUpdatedAt", out var su) && su.TryGetInt64(out var ms) ? ms : 0;
            long started = o.TryGetProperty("startedAt", out var sa) && sa.TryGetInt64(out var ms2) ? ms2 : since;
            return new Entry(len, written, id, pid, long.TryParse(Str(o, "procStart"), out long ft) ? ft : null, shown, cwd, name,
                Str(o, "status") ?? "idle", Str(o, "waitingFor"), since, started);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    static string? Str(JsonElement o, string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[fs.Length];
        int read = 0;
        while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
        return read == buf.Length ? buf : buf[..read];
    }

    // ---------------------------------------------------------------- transcript tail

    Tail Transcript(string id)
    {
        if (!_tails.TryGetValue(id, out var t)) _tails[id] = t = new Tail();
        if (t.Path == null)
        {
            if (DateTime.UtcNow < t.NextSearch) return t;
            t.NextSearch = DateTime.UtcNow.AddSeconds(5);
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
                {
                    var p = Path.Combine(dir, id + ".jsonl");
                    if (File.Exists(p)) { t.Path = p; break; }
                }
            }
            catch { }
            if (t.Path == null) return t;
        }

        long len;
        try { len = new FileInfo(t.Path).Length; } catch { return t; }
        if (len == t.Length) return t;
        t.Length = len;

        try
        {
            // Transcripts are append-only and can be huge (screenshots): read only what was added since
            // last time (the first read takes the last 2 MB), as bytes, through one reused buffer.
            bool midLine = false;
            if (t.Offset < 0 || t.Offset > len) { t.Offset = Math.Max(0, len - 2 * 1024 * 1024); midLine = t.Offset > 0; }
            t.Offset = Jsonl.Read(t.Path, t.Offset, ref _buf, default, line => Parse(line, t, true), () => _stop, midLine);

            // The AI title is written early; look through the whole file once if the tail did not have it
            // (lines too long to be a title are skipped unread).
            if (t.Title == null && !t.TitleScanned)
            {
                t.TitleScanned = true;
                Jsonl.Read(t.Path, 0, ref _buf, TitleTag, line => { if (line.Span.IndexOf(TitleTag) >= 0) Parse(line, t, false); }, () => _stop);
            }
        }
        catch (Exception ex) { Diag.Log("transcript: " + ex.Message); }
        return t;
    }

    static ReadOnlySpan<byte> TitleTag => "\"ai-title\""u8;

    static void Parse(ReadOnlyMemory<byte> line, Tail t, bool messages)
    {
        var s = line.Span;
        if (s.Length < 10) return;
        bool title = s.IndexOf(TitleTag) >= 0, prompt = s.IndexOf("\"last-prompt\""u8) >= 0;
        bool msg = messages && (s.IndexOf("\"type\":\"assistant\""u8) >= 0 || s.IndexOf("\"type\":\"user\""u8) >= 0);
        if (!title && !prompt && !msg) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            string? type = Str(o, "type");
            if (type == "ai-title") { t.Title = Str(o, "aiTitle") ?? t.Title; return; }
            if (type == "last-prompt") { t.Prompt = Str(o, "lastPrompt") ?? t.Prompt; return; }
            if (messages) t.Data.Apply(o);
        }
        catch { }
    }

    /// <summary>Turns a tool call into a short human line: "Editing Renderer.cs", "Running npm test"…</summary>
    public static string Describe(string tool, JsonElement input)
    {
        string? S(string p) => input.ValueKind == JsonValueKind.Object ? Str(input, p) : null;
        string File(string p) => Path.GetFileName((S(p) ?? "").Replace('/', '\\'));
        string Clip(string s, int n = 44)
        {
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > n ? s[..(n - 1)] + "…" : s;
        }
        switch (tool)
        {
            case "Edit": case "MultiEdit": return "Editing " + File("file_path");
            case "Write": return "Writing " + File("file_path");
            case "Read": return "Reading " + File("file_path");
            case "NotebookEdit": return "Editing " + File("notebook_path");
            case "Bash": case "PowerShell":
                return S("description") is { Length: > 0 } d ? Clip(d) : "Running " + Clip(S("command") ?? "", 36);
            case "Grep": return "Searching “" + Clip(S("pattern") ?? "", 30) + "”";
            case "Glob": return "Finding " + Clip(S("pattern") ?? "", 34);
            case "WebFetch": return "Reading " + (Uri.TryCreate(S("url"), UriKind.Absolute, out var u) ? u.Host : "a web page");
            case "WebSearch": return "Searching the web: " + Clip(S("query") ?? "", 28);
            case "Agent": case "Task": return "Agent: " + Clip(S("description") ?? "working", 36);
            case "TodoWrite": return "Updating the plan";
            case "Skill": return "Using " + (S("skill") ?? "a skill");
            case "AskUserQuestion": return "Asking you a question";
            case "Artifact": return "Publishing a page";
        }
        if (tool.StartsWith("mcp__"))
        {
            var p = tool.Split("__");
            return p.Length >= 3 ? $"{p[1]} · {p[2]}" : tool;
        }
        return Clip(tool);
    }

    public void Dispose() => _stop = true;
}
