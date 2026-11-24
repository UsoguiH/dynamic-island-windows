using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Island.Lab.Mascot;
using Island.Lab.Motion;
using Island.Lab.Platform;
using Island.Lab.Render;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

public enum AgentStatus { Idle, Working, NeedsYou, Done, Ended }
public enum AgentKind { Terminal, Island }
public enum TeamView { Grid, Focus, New }
public enum TeamPanel { Activity, Plan, Files }

/// <summary>One Claude agent, embodied as a coloured Bloub: a session running in your terminal, or one the island launched.</summary>
public sealed class Agent
{
    static int _serial;
    public readonly int Key = ++_serial;
    public string HitId => _hitId ??= $"ag:k{Key}";
    string? _hitId;
    public readonly AgentKind Kind;
    public string SessionId;
    public string Name = "";
    public readonly uint Color;
    public readonly BloubExpressionId Personality;
    public readonly BloubEngine Engine;
    public readonly BloubCursorTracker Tracker = new();

    public int Pid;
    public string Cwd = "";
    public AgentStatus Status;
    public string? WaitingFor, Title, Prompt;
    public DateTime BusySince = DateTime.UtcNow, DoneAt, StartedAt = DateTime.UtcNow;
    public bool Gone;
    public AgentData Data = new();
    public long SeenLogVersion = -1;
    public readonly Spring LogScroll = new(0, new SpringSpec(0.38f, 0.9f), 0.002f);

    // Island agents only
    public AgentRunner? Runner;
    public PermissionAsk? Ask;
    public int AskStep;                   // AskUserQuestion: which question is showing
    public JsonObject AskAnswers = new(); // AskUserQuestion: question text -> chosen label
    public string? Mode;          // permission mode it was launched with (null = your default)
    public string? ForkOf;        // name of the terminal session it was forked from (side questions)
    public string? LastError;

    // Motion: absolute island DIPs, so a Bloub flies continuously between the pill, the grid and its own console.
    public readonly Spring X = new(400, new SpringSpec(0.5f, 0.8f), 0.05f);
    public readonly Spring Y = new(25, new SpringSpec(0.5f, 0.8f), 0.05f);
    public readonly Spring R = new(0, new SpringSpec(0.46f, 0.7f), 0.01f);
    public readonly Spring A = new(0, SpringSpec.Content, 0.002f);
    public readonly Spring Whiten = new(0, new SpringSpec(0.6f, 1f), 0.002f);
    public readonly Spring Hop = new(0, new SpringSpec(0.38f, 0.45f), 0.01f);
    public readonly Spring Halo = new(0, SpringSpec.Content, 0.002f);
    public readonly Spring Grow = new(1, SpringSpec.Bounce, 0.0005f);
    public readonly Spring TintR = new(0.6f, SpringSpec.Gentle, 0.002f), TintG = new(0.6f, SpringSpec.Gentle, 0.002f), TintB = new(0.62f, SpringSpec.Gentle, 0.002f);

    public BloubState State = BloubState.Idle;
    public BloubExpressionId Mood;
    public BloubState? React;
    public BloubExpressionId? ReactMood;
    public double ReactUntil, LookUntil, NextFidget;
    public Vector2 LookAt;
    public double FlashAt = -10;
    public double HopAt = double.MaxValue;

    public Agent(AgentKind kind, string sessionId, uint color, BloubExpressionId personality)
    {
        Kind = kind; SessionId = sessionId; Color = color; Personality = personality;
        Mood = personality;
        Engine = new BloubEngine(100, BloubState.Idle, BloubShapeId.Cercle, personality);
    }

    public Spring[] Springs => _springs ??= [LogScroll, X, Y, R, A, Whiten, Hop, Halo, Grow, TintR, TintG, TintB];
    Spring[]? _springs;

    public bool Island => Kind == AgentKind.Island;

    public uint StatusColor => Status switch
    {
        AgentStatus.Working => 0x0A84FF,
        AgentStatus.NeedsYou => 0xFF9F0A,
        AgentStatus.Done => 0x30D158,
        AgentStatus.Ended => 0xFF453A,
        _ => 0x8E8E93,
    };

    public string StatusText => Status switch
    {
        AgentStatus.Working => "working…",
        AgentStatus.NeedsYou => "needs you",
        AgentStatus.Done => "done",
        AgentStatus.Ended => "stopped",
        _ => "idle",
    };

    /// <summary>One line about what it is doing right now.</summary>
    public string Activity => Status switch
    {
        AgentStatus.NeedsYou when Ask != null => "Wants to " + Ask.Summary,
        AgentStatus.NeedsYou => "Waiting for you" + (WaitingFor is { Length: > 0 } w && w != "dialog open" ? $" · {w}" : ""),
        AgentStatus.Working => Data.Log.Count > 0 ? Data.Log[^1] : "Thinking…",
        AgentStatus.Ended => LastError ?? "The agent stopped",
        _ => Title ?? Prompt ?? "Waiting for a prompt",
    };
}

/// <summary>
/// Your Claude agents as a team of coloured Bloubs (animation replicates agent-widget.mp4).
/// Two kinds live together: sessions running in your terminal (read from Claude Code's session registry
/// and transcripts) and agents the island launches itself (headless Claude, fully controllable).
/// Views: grid → focus (live log, chat, plan, changes, actions, message box); plus "New agent".
/// </summary>
public sealed partial class AgentTeam
{
    public const int MaxShown = 6;
    static readonly uint[] Palette = [0x2FBF98, 0xF0912C, 0xE5374C, 0x7A3BF0, 0x2D8CFF, 0xF0479B, 0xE8B92E];
    static readonly BloubExpressionId[] Personalities =
        [BloubExpressionId.Curieux, BloubExpressionId.Heureux, BloubExpressionId.Mefiant, BloubExpressionId.Timide, BloubExpressionId.Fier, BloubExpressionId.Excite];

    public readonly List<Agent> Agents = new();
    public IEnumerable<Agent> Live => Agents.Where(a => !a.Gone);
    public int Count => Agents.Count(a => !a.Gone);

    public TeamView View { get; private set; } = TeamView.Grid;
    public TeamPanel Panel { get; private set; }
    public Agent? Selected;
    public float ViewTime;
    public readonly Spring ViewFade = new(1, SpringSpec.Content, 0.002f);
    public readonly Spring PanelT = new(0, new SpringSpec(0.42f, 0.86f), 0.002f);
    public readonly Spring Scroll = new(0, new SpringSpec(0.3f, 1f), 0.1f);
    float _scrollMax;

    public readonly TextField Message = new("Message…");
    public readonly TextField Task = new("What should it do?");
    List<string> _recent = new();
    string? _newFolder;
    int _newMode;
    static readonly (string? mode, string label)[] Modes = [(null, "Your default"), ("default", "Ask first"), ("plan", "Read-only"), ("acceptEdits", "Auto edits")];
    /// <summary>Starters under the task box: a short label, and the task it writes.</summary>
    static readonly (string label, string task)[] Templates =
        [("Fix bugs", "Find and fix bugs"), ("Write tests", "Write tests for the main code"), ("Review my changes", "Review my recent changes"),
         ("Explain the code", "Explain this codebase"), ("Make it faster", "Make it faster")];

    // ---- files handed to an agent (dropped on Bloub, then "Ask Claude"; or dropped on the open form or console)

    /// <summary>The files the new agent starts with (their paths go with its first task).</summary>
    public readonly List<string> Attached = new();
    /// <summary>A file hangs over the open form or console: "drop to attach".</summary>
    public bool DropHover;
    float _dropT;

    /// <summary>Starters for a task about a file: a short label, and the task it writes.</summary>
    static (string label, string task)[] FileTemplates(string path) => PocketItem.KindOf(path) switch
    {
        PocketKind.Image => [("Describe it", "Describe what is in this image"), ("Read the text in it", "Read out all the text in this image"), ("Build it", "Recreate what this image shows in code"), ("What's wrong here?", "This is a screenshot of a problem. Work out what is wrong and how to fix it")],
        PocketKind.Folder => Templates,
        PocketKind.Code => [("Explain it", "Explain what this file does"), ("Find bugs", "Find bugs in this file and fix them"), ("Review it", "Review this file and suggest improvements"), ("Write tests", "Write tests for this file")],
        _ => [("Summarize it", "Summarize this file"), ("Key points", "Pull out the key points of this file"), ("Explain it", "Explain this file to me simply")],
    };

    (string label, string task)[] Starters => Attached.Count > 0 ? FileTemplates(Attached[0]) : Templates;

    /// <summary>"Ask Claude" about files: the New agent form opens with them attached, in the project they belong to.</summary>
    public void NewAgentWith(IslandModel m, List<string> paths)
    {
        if (paths.Count == 0) return;
        SetView(TeamView.New);
        Attached.Clear();
        Attached.AddRange(paths.Take(8));
        string folder = Pocket.ProjectFor(paths[0], _recent);
        if (!_recent.Contains(folder, StringComparer.OrdinalIgnoreCase)) _recent.Insert(0, folder);
        _newFolder = folder;
        m.SetMode(Mode.Agents);
        m.FocusField(Task);
    }

    /// <summary>Files from the Pocket, waiting for you to pick who gets them: the team is shown, and a click on an agent hands them over.</summary>
    public readonly List<string> Giving = new();

    /// <summary>"Give to an agent": your agents are shown, you pick one (with nobody there yet, a new one starts with the files).</summary>
    public void GiveTo(IslandModel m, List<string> paths)
    {
        if (paths.Count == 0) return;
        if (!Live.Any()) { NewAgentWith(m, paths); return; }
        Attached.Clear();
        m.OpenTeam();
        m.FocusField(null);
        Giving.Clear();
        Giving.AddRange(paths.Take(8));
        // (they hop, one after the other: "me?")
        int i = 0;
        foreach (var a in Live) a.HopAt = m.Clock + 0.3 + i++ * 0.08;
    }

    /// <summary>Files dropped on the open widget: onto the form they join the new agent's files, onto a console they go into your message.</summary>
    public void Attach(IslandModel m, string[] files)
    {
        if (files.Length == 0) return;
        if (View == TeamView.New)
        {
            foreach (var f in files) if (Attached.Count < 8 && !Attached.Contains(f, StringComparer.OrdinalIgnoreCase)) Attached.Add(f);
            if (_step is >= 1 and <= 3) Go(m, 0);   // (back to the first question: what should it do with them?)
            m.FocusField(Task);
        }
        else if (View == TeamView.Focus && Selected is { } a)
        {
            if (Message.Text.Length > 0 && !Message.Text.EndsWith(' ')) Message.Insert(" ");
            Message.Insert(string.Join(" ", files.Select(f => $"\"{f}\"")) + " ");
            Message.ChangedAt = m.Clock;
            m.FocusField(Message);
            a.Grow.Snap(0.84f); a.Grow.To(1, SpringSpec.Bounce);   // (it gulps)
        }
    }

    readonly Random _rng = new();
    readonly ClaudeSessions _sessions = new();
    double _now;
    bool _greeted, _first = true;

    /// <summary>Left edge (island DIPs) of the team in the pill: hovering right of it opens the team, left of it the dashboard.</summary>
    public float PillTeamLeft = float.MaxValue;

    public int CountOf(AgentStatus s) => Live.Count(a => a.Status == s);
    public bool AnyNeeds => Live.Any(a => a.Status == AgentStatus.NeedsYou);
    public Agent? FirstNeeding => Live.FirstOrDefault(a => a.Status == AgentStatus.NeedsYou);

    public string Summary
    {
        get
        {
            if (Count == 0) return "no agents";
            int n = CountOf(AgentStatus.NeedsYou), w = CountOf(AgentStatus.Working), d = CountOf(AgentStatus.Done);
            if (n > 0) return n == 1 ? "1 needs you" : $"{n} need you";
            if (w > 0) return $"{w} working";
            return d > 0 ? $"{d} done" : Count == 1 ? "1 agent" : $"{Count} agents";
        }
    }

    /// <summary>Widget size for the current view (the island springs between them).</summary>
    public (float W, float H, float R) Size => View switch
    {
        TeamView.Focus => (StageW, StageH + (SheetShown ? SheetGrow : 0), 50),   // (a little taller while a sheet is open)
        TeamView.New => (FormW, FormH, 56),
        _ => (400, 352, 54),
    };

    // ================================================================ real sessions + island agents

    void Sync(IslandModel m)
    {
        var snap = _sessions.Snapshot;
        var ids = _ids; ids.Clear();
        int room = MaxShown;
        foreach (var x in Agents) if (x.Island && !x.Gone) room--;
        int taken = 0;
        for (int si = 0; si < snap.Count; si++)
        {
            var s = snap[si];
            if (taken++ >= room) break;
            ids.Add(s.Id);
            Agent? a = null;
            foreach (var x in Agents) if (!x.Island && !x.Gone && x.SessionId == s.Id) { a = x; break; }
            if (a == null)
            {
                a = NewAgent(AgentKind.Terminal, s.Id);
                if (!_first) Event(a, BloubState.Swirl, BloubExpressionId.Excite, 1.2); // a new session pops in
            }
            a.Pid = s.Pid; a.Cwd = s.Cwd; a.Title = s.Title; a.Prompt = s.Prompt; a.WaitingFor = s.WaitingFor;
            a.Name = s.Name; a.StartedAt = s.StartedAt;
            if (s.Data.LogVersion != a.SeenLogVersion)
            {
                if (a.SeenLogVersion >= 0 && s.Data.LogVersion > a.SeenLogVersion) { a.LogScroll.Snap(1); a.LogScroll.To(0); }
                a.SeenLogVersion = s.Data.LogVersion;
            }
            a.Data = s.Data;

            var next = s.Status switch
            {
                "busy" => AgentStatus.Working,
                "waiting" => AgentStatus.NeedsYou,
                _ => a.Status is AgentStatus.Working or AgentStatus.NeedsYou && !_first ? AgentStatus.Done
                   : a.Status == AgentStatus.Done && (DateTime.UtcNow - a.DoneAt).TotalMinutes < 10 ? AgentStatus.Done
                   : AgentStatus.Idle,
            };
            if (next != a.Status) Transition(m, a, next);
        }
        foreach (var a in Agents) if (!a.Island && !ids.Contains(a.SessionId) && !a.Gone) a.Gone = true;

        for (int i = 0; i < Agents.Count; i++) if (Agents[i].Island && !Agents[i].Gone) Pump(m, Agents[i]);

        for (int i = Agents.Count - 1; i >= 0; i--)
        {
            var a = Agents[i];
            if (a.Gone && a.A.Value < 0.02f && a.R.Value < 0.5f) { a.Runner?.Dispose(); Agents.RemoveAt(i); }
        }
        if (Selected != null && Selected.Gone) { Selected = null; if (View == TeamView.Focus) SetView(TeamView.Grid); }
        _first = false;
    }

    Agent NewAgent(AgentKind kind, string id)
    {
        var a = new Agent(kind, id, PickColor(id), Personalities[(int)(Hash(id) % (uint)Personalities.Length)]);
        a.NextFidget = _now + 3 + _rng.NextDouble() * 4;
        Agents.Add(a);
        return a;
    }

    /// <summary>Applies an island agent's stream-json events (UI thread).</summary>
    void Pump(IslandModel m, Agent a)
    {
        var r = a.Runner;
        if (r == null) return;
        r.Drain(e =>
        {
            string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "system" when e.TryGetProperty("subtype", out var st) && st.GetString() == "init":
                    if (r.SessionId != null) a.SessionId = r.SessionId;
                    if (e.TryGetProperty("model", out var md) && md.ValueKind == JsonValueKind.String) a.Data.Model = md.GetString();
                    break;
                case "assistant" or "user":
                {
                    long v = a.Data.LogVersion;
                    a.Data.Apply(e);
                    if (a.Data.LogVersion != v) { a.LogScroll.Snap(1); a.LogScroll.To(0); }
                    if (a.Status is AgentStatus.Idle or AgentStatus.Done) Transition(m, a, AgentStatus.Working);
                    break;
                }
                case "control_request" when e.TryGetProperty("request", out var req) && req.TryGetProperty("subtype", out var rs) && rs.GetString() == "can_use_tool":
                {
                    string tool = req.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "a tool" : "a tool";
                    var input = req.TryGetProperty("input", out var inp) ? inp : default;
                    string id = e.GetProperty("request_id").GetString() ?? "";
                    if (a.ForkOf != null && tool == "ExitPlanMode")
                    {
                        // A side question is a read-only copy of your session: it may answer, never start changing things.
                        r.Answer(new PermissionAsk(id, tool, "", null), false,
                            "This is a read-only side question asked from the island. Don't make changes; reply with your answer as text.");
                        break;
                    }
                    a.AskStep = 0; a.AskAnswers = new JsonObject();
                    a.Ask = new PermissionAsk(id, tool, AskText(tool, input),
                        input.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(input.GetRawText()));
                    Transition(m, a, AgentStatus.NeedsYou);
                    break;
                }
                case "rate_limit_event":
                    m.Limits.Apply(e);
                    break;
                case "result":
                    if (e.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cost)) a.Data.Cost = cost;
                    if (e.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String) a.Data.AddChat(false, res.GetString() ?? "");
                    if (e.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True) a.LastError = "Finished with an error";
                    a.Ask = null;
                    Transition(m, a, AgentStatus.Done);
                    break;
            }
        });
        if (r.Exited && a.Status != AgentStatus.Ended)
        {
            var err = r.StdErr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            a.LastError = err != null ? "Stopped: " + err : "The agent process ended";
            a.Ask = null;
            Transition(m, a, AgentStatus.Ended);
        }
    }

    static readonly Dictionary<string, string> Verbs = new()
    {
        ["Editing"] = "edit", ["Writing"] = "write", ["Reading"] = "read", ["Running"] = "run", ["Searching"] = "search",
        ["Finding"] = "find", ["Updating"] = "update", ["Using"] = "use", ["Asking"] = "ask", ["Publishing"] = "publish",
    };

    /// <summary>"Wants to …" phrase for a permission request: "write notes.txt", "run: npm test".</summary>
    static string AskText(string tool, JsonElement input)
    {
        if (tool is "Bash" or "PowerShell" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var cmd))
        {
            var c = (cmd.GetString() ?? "").Replace('\n', ' ').Trim();
            return "run: " + (c.Length > 60 ? c[..59] + "…" : c);
        }
        if (tool == "ExitPlanMode") return "start working on its plan";
        var d = ClaudeSessions.Describe(tool, input);
        int sp = d.IndexOf(' ');
        if (sp > 0 && Verbs.TryGetValue(d[..sp], out var v)) return v + d[sp..];
        return $"use {tool}: {Lower(d)}";
    }

    /// <summary>AskUserQuestion input: each question with its option labels.</summary>
    static (string Q, string[] Options)[] Questions(PermissionAsk ask)
    {
        if (ask.Input?["questions"] is not JsonArray arr) return [];
        var list = new List<(string, string[])>();
        foreach (var q in arr)
        {
            var text = q?["question"]?.GetValue<string>();
            var opts = (q?["options"] as JsonArray)?.Select(o => o?["label"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];
            if (text != null && opts.Length > 0) list.Add((text, opts));
        }
        return list.ToArray();
    }

    static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    void Transition(IslandModel m, Agent a, AgentStatus next)
    {
        var prev = a.Status;
        if (!_first) Diag.Log($"agent {a.Name}: {prev} -> {next} (busy {(DateTime.UtcNow - a.BusySince).TotalSeconds:0}s)");
        a.Status = next;
        if (_first) return;
        switch (next)
        {
            case AgentStatus.Working:
                if (prev != AgentStatus.NeedsYou) a.BusySince = DateTime.UtcNow;
                if (prev is AgentStatus.Idle or AgentStatus.Done) Event(a, BloubState.Swirl, BloubExpressionId.Excite, 1.1);
                break;
            case AgentStatus.NeedsYou:
                Event(a, BloubState.Wide, BloubExpressionId.Surpris, 1.6);
                m.Bloub.React(BloubState.Wide, _now, 1.2, BloubExpressionId.Surpris);
                if (!(m.Mode == Mode.Agents && Selected == a && View == TeamView.Focus))
                    m.AgentAlert(a, $"{a.Name} needs you", a.Activity);
                break;
            case AgentStatus.Done:
                a.DoneAt = DateTime.UtcNow;
                Event(a, BloubState.Wink, BloubExpressionId.Hilare, 1.8);
                m.Bloub.React(BloubState.Wink, _now, 1.4, BloubExpressionId.Heureux);
                if ((DateTime.UtcNow - a.BusySince).TotalSeconds > (a.Island ? 3 : 8) && !(m.Mode == Mode.Agents && Selected == a && View == TeamView.Focus))
                    m.Celebrate(a, $"{a.Name} is done", DoneLine(a));
                break;
            case AgentStatus.Ended:
                Event(a, BloubState.Sleep, BloubExpressionId.Somnolent, 2);
                break;
        }
    }

    static string DoneLine(Agent a) =>
        (a.Data.LastReply?.Split('\n').Select(l => l.Trim().Trim('*', '#', ' ')).FirstOrDefault(l => l.Length > 0) ?? a.Title ?? "Finished — ready for you");

    /// <summary>Plays the "done" celebration for the first agent (Ctrl+Alt+D preview).</summary>
    public void CelebrateDemo(IslandModel m)
    {
        var a = Live.FirstOrDefault();
        if (a == null) { m.ShowInfo("No agents yet", "Start claude in a terminal"); return; }
        m.PendingCelebration = (a, $"{a.Name} is done", DoneLine(a));
    }

    /// <summary>As if the first session had just finished, through the same queue a real one goes through (demo word "finish").</summary>
    public void FinishDemo(IslandModel m)
    {
        var a = Live.FirstOrDefault();
        if (a != null) m.Celebrate(a, $"{a.Name} is done", DoneLine(a));
    }

    /// <summary>Plays "done" for the first session (Ctrl+Alt+G, Ctrl+Alt+D, the tray's preview, --done-demo): he goes out to your pointer and says it.</summary>
    public void DoneDemo(IslandModel m)
    {
        var a = Live.FirstOrDefault();
        m.ShowDone(a, a != null ? $"{a.Name} is done" : "Claude is done", a != null ? DoneLine(a) : "Finished, ready for you");
    }

    static uint Hash(string s) { uint h = 2166136261; foreach (char c in s) h = (h ^ c) * 16777619; return h; }

    uint PickColor(string id)
    {
        int start = (int)(Hash(id) % (uint)Palette.Length);
        for (int k = 0; k < Palette.Length; k++)
        {
            uint c = Palette[(start + k) % Palette.Length];
            if (!Live.Any(a => a.Color == c)) return c;
        }
        return Palette[start];
    }

    void Event(Agent a, BloubState s, BloubExpressionId mood, double secs)
    {
        a.React = s; a.ReactMood = mood; a.ReactUntil = _now + secs;
        a.Hop.Velocity = -160;
        a.FlashAt = _now;
        foreach (var o in Agents) if (o != a) { o.LookAt = new Vector2(a.X, a.Y); o.LookUntil = _now + 1.6; }
    }

    // ================================================================ launching island agents

    /// <summary>Starts a new headless agent in <paramref name="folder"/> with a first task.</summary>
    public Agent Launch(IslandModel m, string folder, string task, string? mode = null, string? resume = null, string? forkOf = null, IReadOnlyList<string>? files = null, bool focus = true)
    {
        var a = NewAgent(AgentKind.Island, "island-" + Guid.NewGuid().ToString("N")[..8]);
        a.Cwd = folder;
        a.Name = forkOf != null ? "↳ " + forkOf : Path.GetFileName(folder.TrimEnd('\\', '/'));
        a.Title = task;
        a.Mode = mode;
        a.ForkOf = forkOf;
        a.X.Snap(IslandModel.CX); a.Y.Snap(m.Top + 150);
        if (Showcase.On)
        {
            // (showcase mode starts nothing: the new one only looks busy, so the form can be tried and filmed without Claude Code)
            a.Data.AddChat(true, task);
            a.Data.AddLog("Reading the project", "Read");
            a.Status = AgentStatus.Working;
            a.BusySince = DateTime.UtcNow;
        }
        else try
        {
            a.Runner = new AgentRunner(folder, resume, fork: resume != null, permissionMode: mode);
            // (files you attached go along as their paths: the agent reads them itself)
            string with = files is { Count: > 0 } ? "\n\n" + (files.Count == 1 ? "The file: " : "The files:\n") + string.Join("\n", files) : "";
            a.Runner.Send(task + with);
            a.Data.AddChat(true, files is { Count: > 0 } ? $"{task}  ·  {string.Join(", ", files.Select(f => Path.GetFileName(f.TrimEnd('\\', '/'))))}" : task);
            a.Status = AgentStatus.Working;
            a.BusySince = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            a.LastError = "Couldn't start claude: " + ex.Message;
            a.Status = AgentStatus.Ended;
        }
        Event(a, BloubState.Egg, BloubExpressionId.Excite, 1.3); // hatches
        if (focus) Focus(a);   // (from the form it is sent off first: see AgentTeam.Form)
        return a;
    }

    void Send(IslandModel m, Agent a, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (!a.Island)
        {
            // A terminal session can't be typed into from here: ask a read-only copy of it instead.
            Launch(m, a.Cwd, text, "plan", a.SessionId, a.Name);
            m.ShowToast($"Asking a copy of {a.Name} — the original keeps working");
            return;
        }
        if (a.Runner == null || a.Runner.Exited)
        {
            // Pick the conversation up again in a fresh process.
            a.Runner?.Dispose();
            a.Runner = new AgentRunner(a.Cwd, a.SessionId.StartsWith("island-") ? null : a.SessionId, permissionMode: a.Mode);
            a.LastError = null;
        }
        a.Runner.Send(text);
        a.Data.AddChat(true, text);
        Transition(m, a, AgentStatus.Working);
        a.Grow.Snap(0.8f); a.Grow.To(1, SpringSpec.Bounce); // gulps your message
        Scroll.To(0); _sheet = false;
    }

    // ================================================================ interaction

    public void SetView(TeamView v, Agent? sel = null)
    {
        if (sel != null) Selected = sel;
        if (v == TeamView.New) { _recent = Showcase.On ? Showcase.RecentProjects() : Shell.RecentProjects(); _newFolder ??= _recent.FirstOrDefault(); OpenForm(); }
        if (v == View) return;
        View = v;
        ViewTime = 0;
        ViewFade.Snap(0); ViewFade.To(1);
        Panel = TeamPanel.Activity; PanelT.Snap(1); _sheet = false;
    }

    /// <summary>Opens the widget focused on one agent (e.g. from a "needs you" banner).</summary>
    public void Focus(Agent a)
    {
        if (Selected != a) { Message.Clear(); Panel = TeamPanel.Activity; PanelT.Snap(1); Scroll.Snap(0); _sheet = false; }
        Selected = a; View = TeamView.Focus; ViewTime = 0; ViewFade.Snap(0); ViewFade.To(1);
        if (a.Status == AgentStatus.Done) a.DoneAt = DateTime.UtcNow;
    }

    public void Wheel(float delta)
    {
        if (View != TeamView.Focus) return;
        float d = SheetShown && Panel == TeamPanel.Activity ? delta : -delta; // the feed scrolls up into history; his words, the plan and the files scroll down
        Scroll.To(Math.Clamp(Scroll.Target + d * 0.6f, 0, _scrollMax));
    }

    /// <summary>Enter in a text field.</summary>
    public void Submit(IslandModel m, TextField f)
    {
        if (f == Message && Selected != null) { var t = Message.Text; Message.Clear(); Send(m, Selected, t); }
        else if (f == Task || f == Keys) FormEnter(m);
        else m.Tabs.Submit(m, f);
    }

    /// <summary>"+ New agent" from a project: the form opens with that folder picked.</summary>
    public void NewAgentIn(IslandModel m, string folder)
    {
        SetView(TeamView.New);
        if (!_recent.Contains(folder, StringComparer.OrdinalIgnoreCase)) _recent.Insert(0, folder);
        _newFolder = folder;
        m.SetMode(Mode.Agents);
        m.FocusField(Task);
    }

    Agent? ByKey(string k) => int.TryParse(k.TrimStart('k'), out int key) ? Agents.FirstOrDefault(a => a.Key == key) : null;

    /// <summary>Clicks inside the widget. Returns false when the id isn't ours.</summary>
    public bool Act(IslandModel m, string[] p)
    {
        var sel = Selected;
        switch (p[0])
        {
            case "team":
                switch (p[1])
                {
                    case "back" when View == TeamView.New && _step is >= 1 and <= 3: Go(m, _step - 1); break;   // (one question back)
                    case "back": SetView(TeamView.Grid); m.FocusField(null); Attached.Clear(); break;
                    case "home": Giving.Clear(); m.SetMode(Mode.Full); break;
                    // (with a file waiting to be given: the new agent gets it)
                    case "new" when Giving.Count > 0: { var files = Giving.ToList(); Giving.Clear(); NewAgentWith(m, files); break; }
                    case "new": Attached.Clear(); SetView(TeamView.New); m.FocusField(Task); break;
                }
                return true;
            case "input":
                m.FocusField(p[1] == "task" ? Task : Message);
                return true;
            case "send":
                if (sel != null) { var t = Message.Text; Message.Clear(); Send(m, sel, t); }
                return true;
            case "atab":
                OpenSheet(p[1] switch { "plan" => TeamPanel.Plan, "files" => TeamPanel.Files, _ => TeamPanel.Activity });
                return true;
            case "sheet":
                _sheet = false;
                return true;
            case "copy" when sel != null && int.TryParse(p[1], out int ci) && ci < sel.Data.Feed.Count:
                if (Shell.SetClipboard(m.Hwnd, sel.Data.Feed[ci].Text)) m.ShowToast("Copied");
                return true;
            case "ans" when sel?.Ask != null && (sel.Runner != null || Showcase.On):
            {
                var qs = Questions(sel.Ask);
                if (p[1] == "skip" || qs.Length == 0)
                {
                    sel.Runner?.Answer(sel.Ask, false, "The user skipped this question; continue with your best judgement.");
                    sel.Data.AddLog("Skipped the question");
                }
                else
                {
                    var (q, opts) = qs[Math.Min(sel.AskStep, qs.Length - 1)];
                    sel.AskAnswers[q] = opts[int.Parse(p[1])];
                    sel.Data.AddChat(true, opts[int.Parse(p[1])]);
                    if (++sel.AskStep < qs.Length) return true; // next question
                    var input = sel.Ask.Input?.DeepClone() as JsonObject ?? new JsonObject();
                    input["answers"] = sel.AskAnswers.DeepClone();
                    sel.Runner?.Answer(sel.Ask, true, input: input);
                }
                sel.Ask = null;
                Transition(m, sel, AgentStatus.Working);
                Event(sel, BloubState.Wink, BloubExpressionId.Heureux, 1.2);
                return true;
            }
            case "perm" when sel?.Ask != null && (sel.Runner != null || Showcase.On):
            {
                bool allow = p[1] == "allow";
                sel.Runner?.Answer(sel.Ask, allow);
                if (!allow && sel.Ask.Input?["file_path"]?.GetValue<string>() is { } denied) sel.Data.Unchange(denied);
                sel.Data.AddLog((allow ? "Allowed: " : "Denied: ") + sel.Ask.Summary);
                sel.Ask = null;
                Transition(m, sel, AgentStatus.Working);
                if (allow) Event(sel, BloubState.Wink, BloubExpressionId.Heureux, 1.2);
                else Event(sel, BloubState.Idle, BloubExpressionId.Triste, 1.4);
                return true;
            }
            case "new":
                switch (p[1])
                {
                    case "folder": _newFolder = _recent[int.Parse(p[2])]; Chose(1, 1.0); break;
                    case "mode": _newMode = int.Parse(p[2]); Chose(2, 0.9); break;
                    case "next": FormEnter(m); break;
                    case "step": Go(m, int.Parse(p[2])); break;
                    case "other": Browse(m); break;
                    case "open": if (_sent is { Gone: false } made) Focus(made); break;
                    case "poke": _formHop.Velocity = -150; m.Bloub.Engine.Blink(m.Clock); break;
                    case "tpl": Task.Set(Starters[Math.Clamp(int.Parse(p[2]), 0, Starters.Length - 1)].task); m.FocusField(Task); break;
                    case "detach": if (int.TryParse(p[2], out int di) && di < Attached.Count) Attached.RemoveAt(di); break;
                    case "launch": LaunchFromForm(m); break;
                }
                return true;
            case "file" when sel != null && int.TryParse(p[1], out int fi) && fi < sel.Data.Changes.Count:
                Shell.OpenFile(sel.Data.Changes[fi].Path, sel.Cwd);
                m.ShowToast($"Opening {Path.GetFileName(sel.Data.Changes[fi].Path)} in {Shell.EditorName}");
                return true;
            case "act" when sel != null:
                Action(m, sel, p[1]);
                return true;
            case "ag":
                if (p[1] == "poke" && sel != null)
                {
                    sel.Grow.Snap(0.82f); sel.Grow.To(1, SpringSpec.Bounce);
                    sel.React = BloubState.Wink; sel.ReactMood = BloubExpressionId.Hilare; sel.ReactUntil = _now + 1.2;
                }
                else if (ByKey(p[1] == "goto" && p.Length > 2 ? p[2] : p[1]) is { } t)
                {
                    t.Grow.Snap(0.86f); t.Grow.To(1, SpringSpec.Bounce);
                    if (m.Mode == Mode.Agents) Focus(t); else m.OpenTeam(t); // from the dashboard: open its console
                    if (Giving.Count > 0)
                    {
                        // the file from the Pocket is for this one: it goes into your message to it
                        var files = Giving.ToArray();
                        Giving.Clear();
                        Attach(m, files);
                        m.ShowToast($"Given to {t.Name}: say what to do with it");
                    }
                }
                return true;
        }
        return false;
    }

    void Action(IslandModel m, Agent a, string what)
    {
        switch (what)
        {
            case "copy":
                if (a.Data.LastReply is { } reply && Shell.SetClipboard(m.Hwnd, reply)) m.ShowToast("Copied its last reply");
                else m.ShowToast("Nothing to copy yet");
                break;
            case "folder": Shell.OpenFolder(a.Cwd); m.ShowToast("Opened the folder"); break;
            case "editor": Shell.OpenInEditor(a.Cwd); m.ShowToast($"Opening in {Shell.EditorName}"); break;
            case "window":
            {
                bool ok = Terminal.Focus(a.Pid);
                m.ShowToast(ok ? $"Switched to {a.Name}" : "Couldn't find its window");
                if (ok) m.SetMode(m.Base);
                break;
            }
            case "stop":
                a.Runner?.Interrupt();
                m.ShowToast("Stopping the current step");
                Event(a, BloubState.Idle, BloubExpressionId.Confus, 1.2);
                break;
            case "terminal":
                if (a.SessionId.StartsWith("island-")) { m.ShowToast("It hasn't started a session yet"); break; }
                a.Runner?.Kill();
                Shell.ResumeInTerminal(a.Cwd, a.SessionId);
                m.ShowToast("Continuing in a terminal window");
                a.Gone = true;
                SetView(TeamView.Grid);
                break;
            case "close":
                a.Runner?.Kill();
                a.Gone = true;
                m.ShowToast($"{a.Name} said bye");
                SetView(TeamView.Grid);
                break;
            case "restart":
                Send(m, a, "Continue where you left off.");
                break;
        }
    }

    // ================================================================ layout + per-frame update

    static (Vector2[] off, float r) GridLayout(int n) => n switch
    {
        0 => ([], 0),
        1 => ([new(0, 0)], 58),
        2 => ([new(-86, 0), new(86, 0)], 52),
        3 => ([new(-80, -67), new(80, -67), new(0, 67)], 44),
        4 => ([new(-80, -67), new(80, -67), new(-80, 67), new(80, 67)], 44),
        5 => ([new(-112, -64), new(0, -64), new(112, -64), new(-56, 64), new(56, 64)], 38),
        _ => ([new(-112, -64), new(0, -64), new(112, -64), new(-112, 64), new(0, 64), new(112, 64)], 38),
    };

    public const float FocusR = 60, MiniR = 7.5f;

    /// <summary>Where the focused Bloub stands: large, in the middle of his stage.</summary>
    (Vector2 c, float r) FocusPose(float left, float top, float w) => (new Vector2(IslandModel.CX, top + StageBallY), StageBallR);

    public void Update(IslandModel m, float dt)
    {
        _now = m.Clock;
        ViewTime += dt;
        Sync(m);

        var (w, h, _) = m.Live;
        float cx = IslandModel.CX, left = cx - w / 2, top = m.Top;
        bool widget = m.Mode == Mode.Agents;
        bool pill = m.Mode == Mode.Dormant && (!m.Retracted || m.Peeking) && !m.Welcoming;
        bool dock = m.Mode == Mode.Full && !m.Welcoming; // docked in the dashboard header
        if (!widget && View != TeamView.Grid && m.Layers.Count == 1) { View = TeamView.Grid; ViewFade.Snap(1); Panel = TeamPanel.Activity; PanelT.Snap(1); }
        if (View == TeamView.Focus && Selected == null) View = TeamView.Grid;
        Task.Placeholder = Attached.Count == 0 ? "Fix the failing tests…" :   // (he asks the question himself, beside it: here is an example)
            Attached.Count == 1 ? $"What should it do with {Path.GetFileName(Attached[0].TrimEnd('\\', '/'))}?" : $"What should it do with these {Attached.Count} files?";
        if (!widget) DropHover = false;
        if (Giving.Count > 0 && (!widget || View != TeamView.Grid)) Giving.Clear();
        _dropT += ((DropHover ? 1 : 0) - _dropT) * MathF.Min(1, dt * 14);
        if (MathF.Abs((DropHover ? 1 : 0) - _dropT) < 0.006f) _dropT = DropHover ? 1 : 0;   // (it arrives: no creeping by amounts too small to redraw)
        Message.Placeholder = Selected == null ? "Message…" : Selected.Island ? $"Message {Selected.Name}…" : $"Ask {Selected.Name} a side question…";

        var live = _liveNow; live.Clear();
        foreach (var a in Agents) if (!a.Gone) live.Add(a);
        bool greet = pill && (m.Hovered || m.Peeking) || dock && m.HoverHit == "tabs:team";
        if (greet && !_greeted) for (int i = 0; i < live.Count; i++) live[i].HopAt = _now + 0.05 + i * 0.075;
        _greeted = greet;
        foreach (var a in Agents) if (_now >= a.HopAt) { a.Hop.Velocity = -150; a.HopAt = double.MaxValue; }

        StepForm(m, dt, widget, left, top, cx);
        StepStage(m, dt, widget);

        var (offs, gridR) = GridLayout(live.Count);
        PillTeamLeft = float.MaxValue;
        var (fc, fr) = FocusPose(left, top, w);

        foreach (var a in Agents)
        {
            int i = live.IndexOf(a);
            Vector2 pos; float r, alpha = 1, halo = 0, whiten = 0;
            if (a.Gone || i < 0)
            {
                pos = new(a.X, a.Y); r = 0; alpha = 0;
            }
            else if (widget)
            {
                switch (View)
                {
                    case TeamView.Grid:
                        pos = new Vector2(cx, top + 187) + offs[i]; r = gridR; halo = 1;
                        break;
                    case TeamView.Focus when a == Selected:
                        pos = fc; r = fr;
                        break;
                    case TeamView.Focus:
                        pos = new(Selected!.X, Selected.Y); r = 0; alpha = 0; // absorbed into the selected one
                        break;
                    default: // New
                        if (SentSpot(a, top, cx) is { } spot) { pos = spot; r = 17; }   // (the one just launched, in its row)
                        else { pos = new(left + w - 52, top + 22); r = 0; alpha = 0; } // they all hop into the "+"
                        break;
                }
            }
            else if (pill)
            {
                int shown = Math.Min(live.Count, 5);
                if (i >= shown) { pos = new(a.X, a.Y); r = 0; alpha = 0; }
                else
                {
                    float spread = (m.Peeking ? 0.86f : 1) * (1 + 0.12f * m.HoverGrow.Value);
                    pos = new(left + w - (m.Peeking ? 16 : 20) - (shown - 1 - i) * 19 * spread, top + h / 2);
                    PillTeamLeft = MathF.Max(cx + 10, MathF.Min(PillTeamLeft, pos.X - 9));
                    r = m.Peeking ? 6.2f : MiniR;
                    float sp = a.Status == AgentStatus.Working ? 3.4f : 1.8f;
                    pos.Y += (float)Math.Sin(_now * sp + i * 1.4f) * 1.1f;
                }
            }
            else if (dock)
            {
                var (_, dr, shown) = Scenes.TeamDock(m, w);
                if (i >= shown) { pos = new(a.X, a.Y); r = 0; alpha = 0; }
                else
                {
                    float spread = m.HoverHit == "tabs:team" ? 1.12f : 1;
                    pos = new(left + dr - 17 - (shown - 1 - i) * 16 * spread, top + 32);
                    r = 7f;
                    float sp = a.Status == AgentStatus.Working ? 3.4f : 1.8f;
                    pos.Y += (float)Math.Sin(_now * sp + i * 1.4f) * 1.1f;
                }
            }
            else
            {
                pos = new(m.Face.X, m.Face.Y); r = 0; alpha = 0;
            }

            if (a.A.Value < 0.02f && a.R.Value < 0.5f && alpha > 0 && !widget) { a.X.Snap(pos.X); a.Y.Snap(pos.Y); }
            a.X.To(pos.X); a.Y.To(pos.Y);
            a.R.To(r, r > a.R.Target ? (widget && View == TeamView.Grid ? SpringSpec.Bounce : new SpringSpec(0.46f, 0.72f)) : new SpringSpec(0.38f, 0.9f));
            a.A.To(alpha); a.Halo.To(halo); a.Whiten.To(whiten);

            var tint = Rgba(a.Status switch
            {
                AgentStatus.Working => 0x6FAAE8u,
                AgentStatus.NeedsYou => 0xE9A55Au,
                AgentStatus.Ended => 0xB07070u,
                _ => 0x9A9AA2u,
            });
            a.TintR.To(tint.R); a.TintG.To(tint.G); a.TintB.To(tint.B);

            foreach (var s in a.Springs) s.Step(dt);
            Mood(m, a, widget);
        }
        ViewFade.Step(dt); PanelT.Step(dt); Scroll.Step(dt);
    }

    void Mood(IslandModel m, Agent a, bool widget)
    {
        var e = a.Engine;
        bool reacting = a.React != null && _now < a.ReactUntil;
        if (!reacting) { a.React = null; a.ReactMood = null; }

        bool hovered = m.HoverHit == a.HitId || (a == Selected && m.HoverHit == "ag:poke");
        var (state, mood) = reacting ? (a.React!.Value, a.ReactMood ?? a.Personality)
            : hovered ? (BloubState.Wide, BloubExpressionId.Excite)
            : a.Status switch
            {
                AgentStatus.Working => (BloubState.Idle, BloubExpressionId.Attentif),
                AgentStatus.NeedsYou => (BloubState.Wide, BloubExpressionId.Surpris),
                AgentStatus.Done => (BloubState.Idle, BloubExpressionId.Heureux),
                AgentStatus.Ended => (BloubState.Sleep, BloubExpressionId.Somnolent),
                _ => (BloubState.Idle, a.Personality),
            };
        // On his stage he stays himself when he has stopped: drowsy, eyes half shut (the small sleeping dot is for the grid and the pill).
        if (state == BloubState.Sleep && widget && View == TeamView.Focus && a == Selected) state = BloubState.Idle;
        // Typing to it: it listens.
        if (!reacting && a == Selected && m.Focused == Message && Message.Text.Length > 0) (state, mood) = (BloubState.Idle, BloubExpressionId.Attentif);

        if (!reacting && !hovered && _now >= a.NextFidget && state == BloubState.Idle)
        {
            a.NextFidget = _now + 4 + _rng.NextDouble() * 7;
            int k = _rng.Next(4);
            var others = Live.Where(o => o != a).ToList();
            if (k == 0) { a.React = BloubState.Wink; a.ReactMood = BloubExpressionId.Heureux; a.ReactUntil = _now + 1.2; }
            else if (k == 1 || others.Count == 0) { a.Hop.Velocity = -90; }
            else
            {
                var o = others[_rng.Next(others.Count)];
                a.LookAt = new Vector2(o.X, o.Y); a.LookUntil = _now + 1.3;
            }
        }

        if (state != a.State) { a.State = state; e.SetState(state, _now); }
        if (mood != a.Mood) { a.Mood = mood; e.SetExpression(mood, _now); }

        var c = new Vector2(a.X, a.Y);
        bool focusDone = widget && View == TeamView.Focus && a == Selected && a.Status == AgentStatus.Done && !reacting && m.Focused != Message;
        if (focusDone)
            e.SetLook(new Look(0, 16, 1, 0, 0), _now, 0.5);   // (finished: he looks down at what he said)
        else if (a == Selected && m.Focused == Message && widget)
            a.Tracker.Update(e, _now, new Vector2(0, 200), new Vector2(240, 150)); // looks down at what you type
        else if (_now < a.LookUntil)
            a.Tracker.Update(e, _now, a.LookAt - c, new Vector2(60, 40));
        else
        {
            Vector2? d = Vector2.Distance(m.Cursor, c) < 650 ? m.Cursor - c : null;
            a.Tracker.Update(e, _now, d, new Vector2(240, 150));
        }
    }

    // ================================================================ drawing: the Bloubs

    static Color4 Lerp(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);
    readonly List<Agent> _order = new(), _liveNow = new();
    readonly HashSet<string> _ids = new();

    public void DrawBalls(Canvas c, IslandModel m, BloubRenderer r, float opacity)
    {
        // small ones first, big ones on top (same order as a stable sort by size, without allocating one each pass)
        _order.Clear();
        foreach (var a in Agents)
        {
            int at = _order.Count;
            while (at > 0 && _order[at - 1].R.Value > a.R.Value) at--;
            _order.Insert(at, a);
        }
        foreach (var a in _order)
        {
            float rad = MathF.Max(0, a.R) * a.Grow;
            float op = Math.Clamp(a.A, 0, 1) * opacity;
            if (rad < 0.4f || op < 0.01f) continue;
            var center = new Vector2(a.X, a.Y + a.Hop);
            var col = Rgba(a.Color);

            if (a.Halo > 0.01f)
            {
                c.Circle(center.X, center.Y, rad * 1.3f, WithA(Lerp(col, new Color4(0, 0, 0, 1), 0.72f), a.Halo * op));
                c.StrokeCircle(center.X, center.Y, rad * 1.3f, 1, WithA(col, 0.16f * a.Halo * op));
            }

            float wt = Math.Clamp(a.Whiten, 0, 1);
            var ink = Lerp(col, Rgba(0xF2F2F7), wt);
            var bottom = Lerp(Lerp(col, new Color4(0, 0, 0, 1), 0.45f), new Color4(a.TintR, a.TintG, a.TintB, 1), wt);
            r.ShadeBottom = bottom;
            r.ShadeHighlight = 0.42f + 0.43f * wt;
            c.Bloub(r, a.Engine.Sample(m.Clock), center, rad, ink, op);

            Badge(c, m, a, center, rad, op);
        }
        r.ShadeBottom = null;
        r.ShadeHighlight = 0.85f;
    }

    void Badge(Canvas c, IslandModel m, Agent a, Vector2 p, float rad, float op)
    {
        double t = m.Clock;   // (a double: see IslandModel.Clock)
        if (rad < 20) // small Bloubs (pill, list rows) get small badges
        {
            switch (a.Status)
            {
                case AgentStatus.Working:
                {
                    float ang = (float)(t * 5 % (2 * Math.PI));
                    var col = WithA(Rgba(0x5AA8FF), 0.9f * op);
                    float rr = rad + 2.6f;
                    for (int k = 0; k < 6; k++)
                    {
                        float aa = ang + k * 0.22f;
                        c.Circle(p.X + MathF.Cos(aa) * rr, p.Y + MathF.Sin(aa) * rr, 0.75f + k * 0.12f, WithA(col, col.A * (0.25f + k * 0.15f)));
                    }
                    break;
                }
                case AgentStatus.NeedsYou:
                {
                    float k = (float)((t * 1.3f) % 1);
                    c.StrokeCircle(p.X, p.Y, rad + 1.5f + k * 6, 1.4f, WithA(Rgba(0xFF9F0A), (1 - k) * 0.9f * op));
                    break;
                }
                case AgentStatus.Done:
                    c.Circle(p.X + rad * 0.78f, p.Y - rad * 0.78f, 2.6f, WithA(Rgba(0x000000), op));
                    c.Circle(p.X + rad * 0.78f, p.Y - rad * 0.78f, 1.8f, WithA(Rgba(0x30D158), op));
                    break;
            }
            float flash = 1 - (float)(m.Clock - a.FlashAt) / 0.6f;
            if (flash > 0) c.StrokeCircle(p.X, p.Y, rad + (1 - flash) * 9, 1.6f, WithA(Rgba(a.StatusColor), flash * op));
            return;
        }

        float s = Math.Clamp(rad / 40f, 0.75f, 1.15f); // badges stay readable on small Bloubs
        var bc = new Vector2(p.X - rad * 0.74f, p.Y - rad * 0.74f);
        switch (a.Status)
        {
            case AgentStatus.Working:
            {
                float bw = 34 * s, bh = 22 * s;
                c.Round(bc.X - bw / 2 - 2.5f * s, bc.Y - bh / 2 - 2.5f * s, bw + 5 * s, bh + 5 * s, (bh + 5 * s) / 2, WithA(new Color4(0, 0, 0, 1), op));
                c.Round(bc.X - bw / 2, bc.Y - bh / 2, bw, bh, bh / 2, WithA(Rgba(0x2D8CFF), op));
                for (int k = 0; k < 3; k++)
                {
                    float wave = MathF.Max(0, (float)Math.Sin(t * 7 - k * 0.9f));
                    c.Circle(bc.X + (k - 1) * 8 * s, bc.Y - wave * 2.2f * s, 2.6f * s, WhiteA((0.65f + 0.35f * wave) * op));
                }
                break;
            }
            case AgentStatus.NeedsYou:
            {
                float br = 13 * s * (1 + 0.08f * (float)Math.Sin(t * 8));
                c.Circle(bc.X, bc.Y, br + 2.5f * s, WithA(new Color4(0, 0, 0, 1), op));
                c.Circle(bc.X, bc.Y, br, WithA(Rgba(0xFF9F0A), op));
                c.Text("!", bc.X, bc.Y - 10 * s, 15 * s, WhiteA(op), FontWeight.Bold, 0.5f);
                break;
            }
            case AgentStatus.Done:
                c.Circle(bc.X, bc.Y, 13 * s, WithA(new Color4(0, 0, 0, 1), op));
                c.Circle(bc.X, bc.Y, 10.5f * s, WithA(Rgba(0x30D158), op));
                c.Icon(IcCheck, bc.X, bc.Y, 11 * s, WhiteA(op));
                break;
        }
        // island agents wear a little spark on the other side
        if (a.Island && rad > 14)
        {
            float ss = MathF.Max(s, 0.55f);
            var sp = new Vector2(p.X + rad * 0.76f, p.Y - rad * 0.74f);
            c.Circle(sp.X, sp.Y, 8.5f * ss, WithA(new Color4(0, 0, 0, 1), op));
            c.Text("✦", sp.X, sp.Y - 8 * ss, 11 * ss, WithA(Rgba(0xBF5AF2), op), FontWeight.Bold, 0.5f);
        }
    }

    // ================================================================ drawing: widget content

    const char IcChat = '', IcPlan = '', IcChanges = '', IcCopy = '', IcFolder = '', IcCode = '',
        IcWindow = '', IcStop = '', IcTerminal = '', IcClose = '', IcSend = '', IcAdd = '',
        IcHome = '', IcBack = '', IcRestart = '', IcCheck = '', IcRead = '', IcSearch = '', IcPeople = '', IcDot = '';

    public void DrawScene(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        float vf = Math.Clamp(ViewFade, 0, 1);
        var sel = Selected;
        var live = Live.ToList();

        // Header: Bloub (the manager) at the left, title + status, buttons on the right. (The form has its own: he sits beside the question.)
        if (View != TeamView.New) m.Hit("bloub", x0 + 6, y0 + 4, 40, 40);
        bool giving = View == TeamView.Grid && Giving.Count > 0;
        string title = giving ? "Give it to…" : View switch
        {
            TeamView.Focus when sel != null => sel.Name,
            TeamView.New => "New agent",
            _ => "Claude Code",
        };
        float maxTitle = w - 48 - 150;
        bool plain = View == TeamView.Grid;   // (the form and the stage write their own top line)
        if (plain) c.Text(title, x0 + 48, y0 + 11, 14, WhiteA(0.92f * vf), FontWeight.SemiBold, 0, maxTitle);
        string sub = giving ? Giving.Count == 1 ? Path.GetFileName(Giving[0].TrimEnd('\\', '/')) : $"{Giving.Count} files" : View switch
        {
            TeamView.Focus when sel != null => sel.StatusText,
            TeamView.New => "runs in the background",
            _ => Summary,
        };
        var subCol = giving ? Rgba(0xD97757) : View == TeamView.Focus && sel != null ? Rgba(sel.StatusColor) : View != TeamView.New && AnyNeeds ? Rgba(0xFF9F0A) : WhiteA(0.45f);
        float tw0 = MathF.Min(c.Measure(title, 14, FontWeight.SemiBold), maxTitle);
        if (plain) c.Text(sub, x0 + 48 + tw0 + 8, y0 + 13, 12, WithA(subCol, subCol.A * vf), FontWeight.Regular, 0, w - 48 - tw0 - 130);

        float bx = x0 + w - 30;
        if (View == TeamView.Focus) IconButton(c, m, "team:back", bx, y0 + 22, IcBack);
        else if (View != TeamView.New)
        {
            IconButton(c, m, "team:home", bx, y0 + 22, IcHome);
            IconButton(c, m, "team:new", bx - 36, y0 + 22, IcAdd, Rgba(0xBF5AF2));
        }

        switch (View)
        {
            case TeamView.Grid: Grid(c, m, live, x0, y0, w, h, vf); break;
            case TeamView.New: NewForm(c, m, x0, y0, w, h, vf); break;
            case TeamView.Focus when sel != null: StageBody(c, m, sel, x0, y0, w, h, vf); break;
        }

        // a file hangs over the form or the console: it is for this agent
        if (_dropT > 0.01f && View != TeamView.Grid)
        {
            float k = _dropT, inset = 10 + (1 - k) * 8;
            var accent = Rgba(Accent);
            c.Round(x0 + inset, y0 + 48, w - inset * 2, h - 48 - inset, 30, new Color4(0.07f, 0.06f, 0.09f, 0.9f * k));
            c.DashedRound(x0 + inset + 1, y0 + 49, w - inset * 2 - 2, h - 50 - inset, 29, 1.6f, WithA(accent, 0.8f * k));
            float my = y0 + 48 + (h - 48 - inset) / 2, bob = (float)Math.Sin(m.Clock * 3.2f) * 3;
            c.Circle(x0 + w / 2, my - 26 + bob, 22, WithA(accent, 0.9f * k));
            c.Icon('\uE723', x0 + w / 2, my - 26 + bob, 17, WhiteA(k));
            string to = View == TeamView.New ? "Drop to give it to the new agent" : sel != null ? $"Drop to attach it to your message to {sel.Name}" : "Drop to attach";
            c.Text(to, x0 + w / 2, my + 12, 14, WhiteA(0.95f * k), FontWeight.SemiBold, 0.5f, w - 60);
            c.Text(View == TeamView.New ? "It starts with this file" : "Its path goes into the message box", x0 + w / 2, my + 36, 12, WhiteA(0.5f * k), FontWeight.Regular, 0.5f);
        }
    }

    void Grid(Canvas c, IslandModel m, List<Agent> live, float x0, float y0, float w, float h, float vf)
    {
        if (live.Count == 0)
        {
            c.Text("No agents yet", x0 + w / 2, y0 + 118, 16, WhiteA(0.9f * vf), FontWeight.SemiBold, 0.5f);
            c.Text("Start `claude` in a terminal, or launch one from here", x0 + w / 2, y0 + 144, 12, WhiteA(0.45f * vf), FontWeight.Regular, 0.5f);
            BigButton(c, m, "team:new", "New agent", IcAdd, x0 + w / 2 - 90, y0 + 190, 180, 44, Rgba(0xBF5AF2), vf);
            return;
        }
        foreach (var a in live)
        {
            float rr = MathF.Max(a.R, 1) * 1.3f;
            bool hov = m.Hit($"ag:k{a.Key}", a.X - rr, a.Y - rr, rr * 2, rr * 2);
            string tag = hov ? Giving.Count > 0 ? $"Give it to {a.Name}" : $"{a.Name} · {a.StatusText}" : a.Name;
            float tw = MathF.Min(c.Measure(tag, 11.5f, FontWeight.SemiBold), 200) + 18;
            float ty = a.Y + rr - (hov ? 4 : 8);
            if (hov) c.Round(a.X - tw / 2, ty, tw, 22, 11, WithA(Rgba(0x1C1C1E), 0.96f));
            c.Text(tag, a.X - (tw - 18) / 2, ty + 3.5f, 11.5f, WhiteA((hov ? 0.92f : 0.6f) * vf), FontWeight.SemiBold, 0, 200);
        }
        if (Giving.Count > 0)
            c.Text("Click the agent that should get it  ·  + starts a new one with it", x0 + w / 2, y0 + h - 25, 11.5f, WhiteA(0.5f * vf), FontWeight.Regular, 0.5f, w - 40);
    }

    static char StepIcon(string? tool) => tool switch
    {
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => IcChanges,
        "Read" or "WebFetch" => IcRead,
        "Bash" or "PowerShell" => IcTerminal,
        "Grep" or "Glob" or "WebSearch" => IcSearch,
        "TodoWrite" => IcPlan,
        "Agent" or "Task" => IcPeople,
        _ => IcDot,
    };

    /// <summary>Steps, replies and your messages, newest at the bottom (Scroll = distance from the bottom). Returns content height.</summary>
    float ActivityFeed(Canvas c, IslandModel m, Agent a, float x, float top, float w, float h, float k)
    {
        var feed = a.Data.Feed;
        if (feed.Count == 0)
        {
            c.Text(a.Island ? "Starting up…" : "Nothing yet — its steps and replies show up here", x + w / 2, top + h / 2 - 10, 12.5f, WhiteA(0.4f * k), FontWeight.Regular, 0.5f, w);
            return 0;
        }
        // Long runs of steps fold into "N more steps" so replies stay readable.
        var rows = new List<(FeedItem item, int idx, int folded)>();
        for (int i = 0; i < feed.Count;)
        {
            if (feed[i].Kind != FeedKind.Step) { rows.Add((feed[i], i, 0)); i++; continue; }
            int j = i; while (j < feed.Count && feed[j].Kind == FeedKind.Step) j++;
            int run = j - i, keep = Math.Min(run, 3);
            if (run > keep) rows.Add((feed[i], i, run - keep));
            for (int s = j - keep; s < j; s++) rows.Add((feed[s], s, 0));
            i = j;
        }

        float y = top + h + Scroll, total = 0; double t = m.Clock;
        int lastStep = a.Status == AgentStatus.Working ? feed.FindLastIndex(f => f.Kind == FeedKind.Step) : -1;
        for (int r = rows.Count - 1; r >= 0; r--)
        {
            var (it, idx, folded) = rows[r];
            float rh;
            if (folded > 0)
            {
                rh = 20; y -= rh; total += rh;
                if (y < top + h && y + rh > top) c.Text($"+ {folded} earlier steps", x + 22, y + 2, 11, WhiteA(0.3f * k));
                continue;
            }
            switch (it.Kind)
            {
                case FeedKind.Step:
                {
                    rh = 22; y -= rh; total += rh;
                    if (y >= top + h || y + rh <= top) break;
                    bool live = idx == lastStep;
                    var ic = StepIcon(it.Tool);
                    if (ic == IcDot) c.Circle(x + 8, y + 11, 2.5f, WhiteA((live ? 0.9f : 0.45f) * k));
                    else c.Icon(ic, x + 8, y + 11, 11, WhiteA((live ? 0.9f : 0.45f) * k));
                    if (live) c.ShimmerText(c.Fit(it.Text, 12.5f, FontWeight.SemiBold, w - 30), x + 24, y + 2, 12.5f, k, (float)((t * 0.7f) % 1.4f) / 1.4f, FontWeight.SemiBold);
                    else c.Text(it.Text, x + 24, y + 3, 12, WhiteA(0.55f * k), FontWeight.Regular, 0, w - 30);
                    break;
                }
                case FeedKind.Reply:
                {
                    string text = Plain(it.Text.Length > 900 ? it.Text[..900] + "…" : it.Text);
                    float th = c.WrappedHeight(text, 12.5f, w - 20);
                    rh = th + 18; y -= rh; total += rh;
                    if (y >= top + h || y + rh <= top) break;
                    bool hov = y >= top - 2 && m.Hit($"copy:{idx}", x - 6, y + 4, w + 12, rh - 6);
                    if (hov) c.Round(x - 6, y + 4, w + 12, rh - 6, 10, WhiteA(0.06f * k));
                    c.Round(x, y + 9, 3, th - 2, 1.5f, WithA(Rgba(a.Color), 0.9f * k));
                    c.Wrapped(text, x + 14, y + 8, w - 20, 12.5f, WhiteA(0.9f * k));
                    if (hov) c.Text("Click to copy", x + w, y + 8, 10.5f, WhiteA(0.45f * k), FontWeight.SemiBold, 1);
                    break;
                }
                default: // You
                {
                    string text = it.Text.Length > 600 ? it.Text[..600] + "…" : it.Text;
                    float maxB = w * 0.8f;
                    float bw = MathF.Min(c.WrappedWidth(text, 12.5f, maxB - 24), maxB - 24) + 24;
                    float th = c.WrappedHeight(text, 12.5f, maxB - 24);
                    rh = th + 22; y -= rh; total += rh;
                    if (y >= top + h || y + rh <= top) break;
                    c.Round(x + w - bw, y + 6, bw, th + 14, 13, WithA(Rgba(0x0A84FF), 0.9f * k));
                    c.Wrapped(text, x + w - bw + 12, y + 13, maxB - 24, 12.5f, WhiteA(k));
                    break;
                }
            }
        }
        return total + 6;
    }

    static readonly Dictionary<string, string> _plain = new();

    /// <summary>Markdown → readable plain text for the bubbles (bold/code markers, headings, bullets).</summary>
    static string Plain(string md)
    {
        if (_plain.TryGetValue(md, out var p)) return p;
        var lines = md.Replace("\r", "").Split('\n').Select(l =>
        {
            var t = l.TrimStart();
            if (t.StartsWith('#')) t = t.TrimStart('#').TrimStart();
            else if (t.StartsWith("- ") || t.StartsWith("* ")) t = "• " + t[2..];
            return t.Replace("**", "").Replace("`", "");
        });
        p = string.Join('\n', lines).Trim();
        if (_plain.Count > 400) _plain.Clear();
        return _plain[md] = p;
    }

    float PlanPanel(Canvas c, Agent a, float x, float top, float w, float h, float k)
    {
        var todos = a.Data.Todos;
        if (todos.Count == 0) { c.Text("No plan yet — it shows up when the agent writes a to-do list", x + w / 2, top + h / 2 - 10, 12, WhiteA(0.4f * k), FontWeight.Regular, 0.5f, w); return 0; }
        float y = top + 2 - Scroll; double t = _now;
        foreach (var td in todos)
        {
            bool done = td.Status == "completed", active = td.Status == "in_progress";
            string text = active && td.Active is { Length: > 0 } act ? act : td.Text;
            float th = c.WrappedHeight(text, 12.5f, w - 34);
            if (y + th > top - 30 && y < top + h + 10)
            {
                float iy = y + 9;
                if (done)
                {
                    c.Circle(x + 9, iy, 7.5f, WithA(Rgba(0x30D158), k));
                    c.Icon(IcCheck, x + 9, iy, 9, WhiteA(k));
                }
                else if (active)
                {
                    c.StrokeCircle(x + 9, iy, 7, 1.6f, WithA(Rgba(0x0A84FF), 0.35f * k));
                    float a0 = (float)(t * 6 % (2 * Math.PI));   // (an angle: wrapped before it is narrowed)
                    for (int s = 0; s < 5; s++)
                        c.Circle(x + 9 + MathF.Cos(a0 + s * 0.3f) * 7, iy + MathF.Sin(a0 + s * 0.3f) * 7, 1 + s * 0.25f, WithA(Rgba(0x5AA8FF), k));
                }
                else c.StrokeCircle(x + 9, iy, 7, 1.4f, WhiteA(0.35f * k));
                c.Wrapped(text, x + 26, y, w - 34, 12.5f, done ? WhiteA(0.4f * k) : active ? WhiteA(0.98f * k) : WhiteA(0.75f * k),
                    active ? FontWeight.SemiBold : FontWeight.Regular);
            }
            y += th + 9;
        }
        return y + Scroll - top;
    }

    static readonly uint Accent = 0xBF5AF2;

    // ---------------------------------------------------------------- controls

    void IconButton(Canvas c, IslandModel m, string id, float x, float y, char icon, Color4? tint = null)
    {
        bool hov = m.Hit(id, x - 15, y - 15, 30, 30);
        c.Circle(x, y, 15, tint is { } t ? WithA(t, hov ? 0.55f : 0.32f) : WhiteA(hov ? 0.2f : 0.1f));
        c.Icon(icon, x, y, 13, WhiteA(0.9f));
    }

    void BigButton(Canvas c, IslandModel m, string id, string label, char icon, float x, float y, float w, float h, Color4 fill, float k)
    {
        bool hov = m.Hit(id, x, y, w, h);
        c.Round(x, y, w, h, h / 2, WithA(fill, (hov ? 1 : 0.85f) * k));
        float lw = c.Measure(label, 14, FontWeight.SemiBold);
        c.Icon(icon, x + w / 2 - lw / 2 - 10, y + h / 2, 13, WhiteA(k));
        c.Text(label, x + w / 2 + 10, y + h / 2 - 10, 14, WhiteA(k), FontWeight.SemiBold, 0.5f);
    }

    /// <summary>A one-line text box; Enter submits, Esc leaves. Long text scrolls to keep the caret visible.</summary>
    internal static void Field(Canvas c, IslandModel m, TextField f, string id, float x, float y, float w, float h, float k, bool send)
    {
        bool focused = m.Focused == f;
        bool hov = m.Hit(id, x, y, w - (send ? 44 : 0), h);
        c.Round(x, y, w, h, h / 2, WhiteA((focused ? 0.13f : hov ? 0.1f : 0.075f) * k));
        if (focused) c.StrokeRound(x + 0.5f, y + 0.5f, w - 1, h - 1, h / 2, 1.2f, WithA(Rgba(0x0A84FF), 0.8f * k));
        float tx = x + 18, avail = w - 36 - (send ? 40 : 0), ty = y + h / 2 - 10;
        c.PushClip(x + 10, y, avail + 10, h);
        bool blink = focused && (m.Clock - f.ChangedAt) % 1.06 < 0.6;
        if (f.Text.Length == 0)
        {
            c.Text(f.Placeholder, tx, ty, 14, WhiteA(0.35f * k), FontWeight.Regular, 0, avail);
            if (blink) c.Round(tx, y + 11, 1.6f, h - 22, 0.8f, WithA(Rgba(0x0A84FF), k));
        }
        else
        {
            float caretX = c.Measure(f.Text[..f.Caret], 14);
            float shift = MathF.Max(0, caretX - avail + 4);
            c.Text(f.Text, tx - shift, ty, 14, WhiteA(0.95f * k));
            if (blink) c.Round(tx - shift + caretX, y + 11, 1.6f, h - 22, 0.8f, WithA(Rgba(0x0A84FF), k));
        }
        c.PopClip();

        if (send)
        {
            bool can = f.Text.Trim().Length > 0;
            float sx = x + w - h / 2 - 3, sy = y + h / 2;
            bool sh = m.Hit("send", sx - 17, sy - 17, 34, 34);
            c.Circle(sx, sy, 16, can ? WithA(Rgba(sh ? 0x3D9BFFu : 0x0A84FFu), k) : WhiteA(0.1f * k));
            c.Icon(IcSend, sx, sy, 13, WhiteA((can ? 1 : 0.4f) * k));
        }
    }

    /// <summary>Dormant pill text: a tiny status between Bloub and the team.</summary>
    public void DrawPill(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        var col = AnyNeeds ? Rgba(0xFF9F0A) : WhiteA(0.55f);
        // (what he holds for you: a small sheet and a count, between him and the words)
        float over = Scenes.PocketBadge(c, m, x0, y0, h);
        c.Text(Summary, x0 + 40 + over, y0 + h / 2 - 8.5f, 11.5f, col, FontWeight.SemiBold, 0, Count == 0 ? 160 : 80);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        foreach (var a in Agents) a.Runner?.Dispose();
    }
}
