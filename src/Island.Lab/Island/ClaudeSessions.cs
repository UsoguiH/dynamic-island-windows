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
        public long Offset;
        public string Partial = "";
        public bool SkipFirst;
    }
    readonly Dictionary<string, Tail> _tails = new();

    public ClaudeSessions()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ClaudeSessions", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    void Loop()
    {
        while (!_stop)
        {
            try { _snapshot = Read(); }
            catch (Exception ex) { Diag.Log("sessions: " + ex.Message); }
            Thread.Sleep(400);
        }
    }

    List<SessionInfo> Read()
    {
        var list = new List<SessionInfo>();
        if (!Directory.Exists(SessionsDir)) return list;
        var seen = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.json"))
        {
            JsonElement o;
            try
            {
                using var doc = JsonDocument.Parse(ReadShared(file));
                o = doc.RootElement.Clone();
            }
            catch { continue; }

            string? id = Str(o, "sessionId");
            if (id == null || !o.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid)) continue;
            string kind = Str(o, "kind") ?? "interactive";
            if (kind != "interactive") continue;
            if (!Alive(pid, Str(o, "procStart"))) continue;

            string cwd = Str(o, "cwd") ?? "";
            string name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = Str(o, "name") ?? "session";
            string status = Str(o, "status") ?? "idle";
            long since = o.TryGetProperty("statusUpdatedAt", out var su) && su.TryGetInt64(out var ms) ? ms : 0;
            long started = o.TryGetProperty("startedAt", out var sa) && sa.TryGetInt64(out var ms2) ? ms2 : since;

            seen.Add(id);
            var t = Transcript(id);
            if (t.Snap == null || t.Snap.Version != t.Data.Version) t.Snap = t.Data.Clone();
            list.Add(new SessionInfo(id, pid, name, cwd, status, Str(o, "waitingFor"), t.Title, t.Prompt, t.Snap,
                DateTimeOffset.FromUnixTimeMilliseconds(since).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(started).UtcDateTime));
        }
        foreach (var k in _tails.Keys.Where(k => !seen.Contains(k)).ToList()) _tails.Remove(k);
        // Most recently active first.
        list.Sort((a, b) => b.StatusSince.CompareTo(a.StatusSince));
        return list;
    }

    static string? Str(JsonElement o, string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    static bool Alive(int pid, string? procStart)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            // Guard against PID reuse: the registry stores the process start time (FILETIME).
            if (procStart != null && long.TryParse(procStart, out long ft))
            {
                try { if (Math.Abs(p.StartTime.ToFileTimeUtc() - ft) > 20_000_000) return false; } catch { }
            }
            return true;
        }
        catch { return false; }
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
            // last time (the first read takes the last 2 MB).
            using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (t.Offset > len || t.Offset == 0) { t.Offset = Math.Max(0, len - 2 * 1024 * 1024); t.Partial = ""; t.SkipFirst = t.Offset > 0; }
            fs.Seek(t.Offset, SeekOrigin.Begin);
            var buf = new byte[len - t.Offset];
            int read = 0;
            while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            t.Offset += read;
            var text = t.Partial + Encoding.UTF8.GetString(buf, 0, read);
            int lastNl = text.LastIndexOf('\n');
            t.Partial = lastNl >= 0 ? text[(lastNl + 1)..] : text;
            var lines = lastNl >= 0 ? text[..lastNl].Split('\n') : [];
            for (int i = t.SkipFirst ? 1 : 0; i < lines.Length; i++) Parse(lines[i], t, true);
            if (lines.Length > 0) t.SkipFirst = false;

            // The AI title is written early; scan the whole file once if the tail didn't have it.
            if (t.Title == null && !t.TitleScanned)
            {
                t.TitleScanned = true;
                fs.Seek(0, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
                string? l;
                while ((l = sr.ReadLine()) != null)
                    if (l.Contains("\"ai-title\"")) Parse(l, t, false);
            }
        }
        catch (Exception ex) { Diag.Log("transcript: " + ex.Message); }
        return t;
    }

    static void Parse(string line, Tail t, bool messages)
    {
        if (line.Length < 10) return;
        bool title = line.Contains("\"ai-title\""), prompt = line.Contains("\"last-prompt\"");
        bool msg = messages && (line.Contains("\"type\":\"assistant\"") || line.Contains("\"type\":\"user\""));
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
