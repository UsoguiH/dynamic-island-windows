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
public enum TeamView { Grid, Focus, List, New }
public enum TeamPanel { Activity, Plan, Files }

/// <summary>One Claude agent, embodied as a coloured Bloub: a session running in your terminal, or one the island launched.</summary>
public sealed class Agent
{
    static int _serial;
    public readonly int Key = ++_serial;
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

    // Motion: absolute island DIPs, so a Bloub flies continuously between the pill, the grid and the list.
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
/// Views: grid → focus (live log, chat, plan, changes, actions, message box) → list; plus "New agent".
/// </summary>
public sealed class AgentTeam
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
    public readonly TextField Task = new("Describe the task…");
    List<string> _recent = new();
    string? _newFolder;
    int _newMode;
    static readonly (string? mode, string label)[] Modes = [(null, "Your default"), ("default", "Ask first"), ("plan", "Read-only"), ("acceptEdits", "Auto edits")];
    static readonly string[] Templates =
        ["Find and fix bugs", "Write tests for the main code", "Review my recent changes", "Explain this codebase", "Improve the README", "Make it faster"];

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
        TeamView.Focus => (480, 440, 50),
        TeamView.New => (500, 440, 56),
        _ => (400, 352, 54),
    };

    // ================================================================ real sessions + island agents

    void Sync(IslandModel m)
    {
        var snap = _sessions.Snapshot;
        var ids = new HashSet<string>();
        int room = MaxShown - Agents.Count(a => a.Island && !a.Gone);
        foreach (var s in snap.Take(Math.Max(0, room)))
        {
            ids.Add(s.Id);
            var a = Agents.FirstOrDefault(x => !x.Island && x.SessionId == s.Id && !x.Gone);
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

        foreach (var a in Agents.Where(a => a.Island && !a.Gone)) Pump(m, a);

        foreach (var a in Agents.Where(a => a.Gone && a.A.Value < 0.02f && a.R.Value < 0.5f).ToList()) { a.Runner?.Dispose(); Agents.Remove(a); }
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
    public Agent Launch(IslandModel m, string folder, string task, string? mode = null, string? resume = null, string? forkOf = null)
    {
        var a = NewAgent(AgentKind.Island, "island-" + Guid.NewGuid().ToString("N")[..8]);
        a.Cwd = folder;
        a.Name = forkOf != null ? "↳ " + forkOf : Path.GetFileName(folder.TrimEnd('\\', '/'));
        a.Title = task;
        a.Mode = mode;
        a.ForkOf = forkOf;
        a.X.Snap(IslandModel.CX); a.Y.Snap(m.Top + 150);
        try
        {
            a.Runner = new AgentRunner(folder, resume, fork: resume != null, permissionMode: mode);
            a.Runner.Send(task);
            a.Data.AddChat(true, task);
            a.Status = AgentStatus.Working;
            a.BusySince = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            a.LastError = "Couldn't start claude: " + ex.Message;
            a.Status = AgentStatus.Ended;
        }
        Event(a, BloubState.Egg, BloubExpressionId.Excite, 1.3); // hatches
        Focus(a);
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
        Scroll.To(0);
    }

    // ================================================================ interaction

    public void SetView(TeamView v, Agent? sel = null)
    {
        if (sel != null) Selected = sel;
        if (v == TeamView.New) { _recent = Shell.RecentProjects(); _newFolder ??= _recent.FirstOrDefault(); }
        if (v == View) return;
        View = v;
        ViewTime = 0;
        ViewFade.Snap(0); ViewFade.To(1);
        Panel = TeamPanel.Activity; PanelT.Snap(1);
    }

    /// <summary>Opens the widget focused on one agent (e.g. from a "needs you" banner).</summary>
    public void Focus(Agent a)
    {
        if (Selected != a) { Message.Clear(); Panel = TeamPanel.Activity; PanelT.Snap(1); Scroll.Snap(0); }
        Selected = a; View = TeamView.Focus; ViewTime = 0; ViewFade.Snap(0); ViewFade.To(1);
        if (a.Status == AgentStatus.Done) a.DoneAt = DateTime.UtcNow;
    }

    void SetTab(TeamPanel p)
    {
        if (Panel == p) return;
        Panel = p;
        Scroll.Snap(0);
        PanelT.Snap(0); PanelT.To(1);
    }

    public void Wheel(float delta)
    {
        if (View != TeamView.Focus) return;
        float d = Panel == TeamPanel.Activity ? delta : -delta; // the feed scrolls up into history
        Scroll.To(Math.Clamp(Scroll.Target + d * 0.6f, 0, _scrollMax));
    }

    /// <summary>Enter in a text field.</summary>
    public void Submit(IslandModel m, TextField f)
    {
        if (f == Message && Selected != null) { var t = Message.Text; Message.Clear(); Send(m, Selected, t); }
        else if (f == Task) LaunchFromForm(m);
    }

    void LaunchFromForm(IslandModel m)
    {
        if (_newFolder == null) { m.ShowToast("Pick a folder first"); return; }
        if (Task.Text.Trim().Length == 0) { m.FocusField(Task); m.ShowToast("Tell it what to do"); return; }
        if (Count >= MaxShown) { m.ShowToast($"The team is full ({MaxShown}) — close an agent first"); return; }
        var task = Task.Text.Trim();
        Task.Clear();
        m.FocusField(null);
        Launch(m, _newFolder, task, Modes[_newMode].mode);
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
                    case "list": SetView(TeamView.List); break;
                    case "grid": case "back": SetView(TeamView.Grid); m.FocusField(null); break;
                    case "home": m.SetMode(Mode.Full); break;
                    case "new": SetView(TeamView.New); m.FocusField(Task); break;
                }
                return true;
            case "input":
                m.FocusField(p[1] == "task" ? Task : Message);
                return true;
            case "send":
                if (sel != null) { var t = Message.Text; Message.Clear(); Send(m, sel, t); }
                return true;
            case "atab":
                SetTab(p[1] switch { "plan" => TeamPanel.Plan, "files" => TeamPanel.Files, _ => TeamPanel.Activity });
                return true;
            case "copy" when sel != null && int.TryParse(p[1], out int ci) && ci < sel.Data.Feed.Count:
                if (Shell.SetClipboard(m.Hwnd, sel.Data.Feed[ci].Text)) m.ShowToast("Copied");
                return true;
            case "ans" when sel?.Runner != null && sel.Ask != null:
            {
                var qs = Questions(sel.Ask);
                if (p[1] == "skip" || qs.Length == 0)
                {
                    sel.Runner.Answer(sel.Ask, false, "The user skipped this question; continue with your best judgement.");
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
                    sel.Runner.Answer(sel.Ask, true, input: input);
                }
                sel.Ask = null;
                Transition(m, sel, AgentStatus.Working);
                Event(sel, BloubState.Wink, BloubExpressionId.Heureux, 1.2);
                return true;
            }
            case "perm" when sel?.Runner != null && sel.Ask != null:
            {
                bool allow = p[1] == "allow";
                sel.Runner.Answer(sel.Ask, allow);
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
                    case "folder": _newFolder = _recent[int.Parse(p[2])]; break;
                    case "mode": _newMode = int.Parse(p[2]); break;
                    case "tpl": Task.Set(Templates[int.Parse(p[2])]); m.FocusField(Task); break;
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

    public const float FocusR = 60, ListR = 15, MiniR = 7.5f;

    /// <summary>Where the focused Bloub sits: big in the middle, or small in the corner while a panel is open.</summary>
    (Vector2 c, float r) FocusPose(float left, float top, float w) => (new Vector2(left + HeroBallX, top + HeroBallY), HeroBallR);

    public void Update(IslandModel m, float dt)
    {
        _now = m.Clock;
        ViewTime += dt;
        Sync(m);

        var (w, h, _) = m.Live;
        float cx = IslandModel.CX, left = cx - w / 2, top = m.Top;
        bool widget = m.Mode == Mode.Agents;
        bool pill = m.Mode == Mode.Dormant && (!m.Retracted || m.Peeking) && !m.Welcoming;
        if (!widget && View != TeamView.Grid && m.Layers.Count == 1) { View = TeamView.Grid; ViewFade.Snap(1); Panel = TeamPanel.Activity; PanelT.Snap(1); }
        if (View == TeamView.Focus && Selected == null) View = TeamView.Grid;
        Message.Placeholder = Selected == null ? "Message…" : Selected.Island ? $"Message {Selected.Name}…" : $"Ask {Selected.Name} a side question…";

        var live = Live.ToList();
        bool greet = pill && (m.Hovered || m.Peeking);
        if (greet && !_greeted) for (int i = 0; i < live.Count; i++) live[i].HopAt = _now + 0.05 + i * 0.075;
        _greeted = greet;
        foreach (var a in Agents) if (_now >= a.HopAt) { a.Hop.Velocity = -150; a.HopAt = double.MaxValue; }

        var (offs, gridR) = GridLayout(live.Count);
        float rowH = live.Count <= 4 ? 62 : 46;
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
                    case TeamView.New:
                        pos = new(left + w - 52, top + 22); r = 0; alpha = 0; // they all hop into the "+"
                        break;
                    default: // List
                    {
                        var row = new Vector2(left + 20 + 27, top + 56 + rowH / 2 + i * rowH);
                        bool shown = a == Selected || ViewTime > 0.08f + 0.07f * i;
                        if (!shown || a.R.Value < 0.6f && a != Selected) { a.X.Snap(row.X); a.Y.Snap(row.Y); }
                        pos = row; r = shown ? (rowH > 50 ? ListR : 13) : 0;
                        break;
                    }
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
                    pos.Y += MathF.Sin((float)_now * sp + i * 1.4f) * 1.1f;
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

        bool hovered = m.HoverHit == $"ag:k{a.Key}" || (a == Selected && m.HoverHit == "ag:poke");
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
            e.SetLook(new Look(18, 15, 1, 0, 0), _now, 0.5);
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

    public void DrawBalls(Canvas c, IslandModel m, BloubRenderer r, float opacity)
    {
        var ctx = c.Ctx;
        foreach (var a in Agents.OrderBy(a => a.R.Value))
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
            r.Draw(ctx, c.Factory, a.Engine.Sample(m.Clock), center, rad, ink, op);

            Badge(c, m, a, center, rad, op);
        }
        r.ShadeBottom = null;
        r.ShadeHighlight = 0.85f;
    }

    void Badge(Canvas c, IslandModel m, Agent a, Vector2 p, float rad, float op)
    {
        float t = m.Clock;
        if (rad < 20) // small Bloubs (pill, list rows) get small badges
        {
            switch (a.Status)
            {
                case AgentStatus.Working:
                {
                    float ang = t * 5f;
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
                    float k = (t * 1.3f) % 1;
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
                    float wave = MathF.Max(0, MathF.Sin(t * 7 - k * 0.9f));
                    c.Circle(bc.X + (k - 1) * 8 * s, bc.Y - wave * 2.2f * s, 2.6f * s, WhiteA((0.65f + 0.35f * wave) * op));
                }
                break;
            }
            case AgentStatus.NeedsYou:
            {
                float br = 13 * s * (1 + 0.08f * MathF.Sin(t * 8));
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
        IcList = '', IcGrid = '', IcHome = '', IcBack = '', IcRestart = '', IcCheck = '', IcRead = '', IcSearch = '', IcPeople = '', IcDot = '';

    public void DrawScene(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        float vf = Math.Clamp(ViewFade, 0, 1);
        var sel = Selected;
        var live = Live.ToList();

        // Header: Bloub (the manager) at the left, title + status, buttons on the right.
        m.Hit("bloub", x0 + 6, y0 + 4, 40, 40);
        string title = View switch
        {
            TeamView.Focus when sel != null => sel.Name,
            TeamView.List => "All agents",
            TeamView.New => "New agent",
            _ => "Claude Code",
        };
        float maxTitle = w - 48 - 150;
        c.Text(title, x0 + 48, y0 + 11, 14, WhiteA(0.92f * vf), FontWeight.SemiBold, 0, maxTitle);
        string sub = View switch
        {
            TeamView.Focus when sel != null => sel.StatusText,
            TeamView.New => "runs in the background",
            _ => Summary,
        };
        var subCol = View == TeamView.Focus && sel != null ? Rgba(sel.StatusColor) : View != TeamView.New && AnyNeeds ? Rgba(0xFF9F0A) : WhiteA(0.45f);
        float tw0 = MathF.Min(c.Measure(title, 14, FontWeight.SemiBold), maxTitle);
        c.Text(sub, x0 + 48 + tw0 + 8, y0 + 13, 12, WithA(subCol, subCol.A * vf), FontWeight.Regular, 0, w - 48 - tw0 - 130);

        float bx = x0 + w - 30;
        if (View is TeamView.Focus or TeamView.New) IconButton(c, m, "team:back", bx, y0 + 22, IcBack);
        else
        {
            IconButton(c, m, "team:home", bx, y0 + 22, IcHome);
            if (live.Count > 0) IconButton(c, m, View == TeamView.Grid ? "team:list" : "team:grid", bx - 36, y0 + 22, View == TeamView.Grid ? IcList : IcGrid);
            IconButton(c, m, "team:new", bx - (live.Count > 0 ? 72 : 36), y0 + 22, IcAdd, Rgba(0xBF5AF2));
        }

        switch (View)
        {
            case TeamView.Grid: Grid(c, m, live, x0, y0, w, h, vf); break;
            case TeamView.List: List(c, m, live, x0, y0, w, h); break;
            case TeamView.New: NewForm(c, m, x0, y0, w, h, vf); break;
            case TeamView.Focus when sel != null: FocusBody(c, m, sel, x0, y0, w, h, vf); break;
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
            string tag = hov ? $"{a.Name} · {a.StatusText}" : a.Name;
            float tw = MathF.Min(c.Measure(tag, 11.5f, FontWeight.SemiBold), 200) + 18;
            float ty = a.Y + rr - (hov ? 4 : 8);
            if (hov) c.Round(a.X - tw / 2, ty, tw, 22, 11, WithA(Rgba(0x1C1C1E), 0.96f));
            c.Text(tag, a.X - (tw - 18) / 2, ty + 3.5f, 11.5f, WhiteA((hov ? 0.92f : 0.6f) * vf), FontWeight.SemiBold, 0, 200);
        }
    }

    void List(Canvas c, IslandModel m, List<Agent> live, float x0, float y0, float w, float h)
    {
        float rowH = live.Count <= 4 ? 62 : 46, pillH = rowH - 12;
        for (int i = 0; i < live.Count; i++)
        {
            var a = live[i];
            float k = Math.Clamp((ViewTime - 0.06f - 0.07f * i) / 0.22f, 0, 1);
            float ry = y0 + 56 + rowH / 2 + i * rowH, slide = (1 - k) * 14;
            bool hov = m.Hit($"ag:k{a.Key}", x0 + 20, ry - pillH / 2, w - 40, pillH);
            c.Round(x0 + 20 + slide, ry - pillH / 2, w - 40, pillH, pillH / 2, WhiteA((hov ? 0.13f : 0.075f) * k));
            if (rowH > 50)
            {
                c.Text(a.Name, x0 + 72 + slide, ry - 10, 14, WhiteA(0.92f * k), FontWeight.SemiBold, 0, w - 200);
                c.Text(a.Activity, x0 + 72 + slide, ry + 9, 11, WhiteA(0.45f * k), maxWidth: w - 190);
            }
            else c.Text(a.Name, x0 + 72 + slide, ry - 9, 13.5f, WhiteA(0.92f * k), FontWeight.SemiBold, 0, w - 200);
            c.Text(a.StatusText, x0 + w - 38 + slide, ry - 8, 12, WithA(Rgba(a.StatusColor), k), FontWeight.SemiBold, 1);
        }
    }

    // ---------------------------------------------------------------- focus: one simple card per agent
    //
    //  ┌ header ─ name · status ───────────────── ← ┐
    //  │ (ball)  What it's doing right now           │
    //  │         the task                            │
    //  │         ▬▬▬▬▭▭ 3 of 7 · opus · 29k · $0.12  │
    //  │ [Activity] [Plan 3/7] [Files 2]             │
    //  │ ┌ feed: steps, replies, your messages ────┐ │
    //  │ │ … needs-you card sits at the bottom …   │ │
    //  │ └─────────────────────────────────────────┘ │
    //  │ ( Message…                            ➤ )   │
    //  │ [Go to terminal] [Cursor] [Folder]          │
    //  └─────────────────────────────────────────────┘

    const float HeroBallX = 46, HeroBallY = 90, HeroBallR = 25;

    void FocusBody(Canvas c, IslandModel m, Agent a, float x0, float y0, float w, float h, float vf)
    {
        float t = m.Clock;
        if (Panel == TeamPanel.Plan && a.Data.Todos.Count == 0 || Panel == TeamPanel.Files && a.Data.Changes.Count == 0) Panel = TeamPanel.Activity;
        m.Hit("ag:poke", x0 + HeroBallX - 30, y0 + HeroBallY - 30, 60, 60);

        // soft light in the agent's colour from below
        float gl = Math.Clamp(ViewTime / 0.5f, 0, 1);
        c.Glow(x0 + w / 2, y0 + h + 30, w * 0.6f, 110, a.Status is AgentStatus.Idle ? a.Color : a.StatusColor, 0.35f * gl);

        float Pop(int i) => vf * Math.Clamp((ViewTime - 0.04f - i * 0.04f) / 0.25f, 0, 1);

        // ---- hero: what it's doing, the task, progress + stats
        float tx = x0 + 86, tw = w - 86 - 22, p0 = Pop(0);
        string now = a.Status switch
        {
            AgentStatus.Working => a.Data.Log.Count > 0 ? a.Data.Log[^1] : "Thinking…",
            AgentStatus.NeedsYou => a.Ask != null ? "Needs you — wants to " + a.Ask.Summary : "Needs you in its terminal",
            AgentStatus.Done => "Finished",
            AgentStatus.Ended => a.LastError ?? "Stopped",
            _ => "Ready for your next message",
        };
        if (a.Status == AgentStatus.Working) c.ShimmerText(c.Fit(now, 15, FontWeight.SemiBold, tw), tx, y0 + 58, 15, p0, (t * 0.7f) % 1.4f / 1.4f, FontWeight.SemiBold);
        else
        {
            var col = a.Status is AgentStatus.NeedsYou or AgentStatus.Ended ? Rgba(a.StatusColor) : a.Status == AgentStatus.Done ? Rgba(0x30D158) : WhiteA(0.92f);
            c.Text(now, tx, y0 + 58, 15, WithA(col, col.A * p0), FontWeight.SemiBold, 0, tw);
        }
        string task = a.Title ?? a.Prompt ?? (a.Island ? "" : "No task yet");
        c.Text(task, tx, y0 + 80, 12.5f, WhiteA(0.5f * p0), FontWeight.Regular, 0, tw);

        float sx = tx;
        var todos = a.Data.Todos;
        if (todos.Count > 0)
        {
            float bw = 70, pr = a.Data.TodosDone / (float)todos.Count;
            c.Round(sx, y0 + 106, bw, 5, 2.5f, WhiteA(0.14f * p0));
            c.Round(sx, y0 + 106, MathF.Max(5, bw * pr), 5, 2.5f, WithA(Rgba(0x30D158), p0));
            string steps = $"{a.Data.TodosDone} of {todos.Count}";
            c.Text(steps, sx + bw + 8, y0 + 100, 11.5f, WhiteA(0.6f * p0), FontWeight.SemiBold);
            sx += bw + 8 + c.Measure(steps, 11.5f, FontWeight.SemiBold) + 12;
        }
        var bits = new List<string>();
        if (a.Data.Model is { } model) bits.Add(model.Replace("claude-", ""));
        if (a.Data.Context > 0) bits.Add($"{a.Data.Context / 1000.0:0}k context");
        if (a.Data.Cost > 0) bits.Add($"${a.Data.Cost:0.00}");
        var age = DateTime.UtcNow - a.StartedAt;
        bits.Add(age.TotalHours >= 1 ? $"{(int)age.TotalHours}h {age.Minutes}m" : $"{Math.Max(1, (int)age.TotalMinutes)}m");
        if (a.ForkOf != null) bits.Add("side question");
        c.Text(string.Join(" · ", bits), sx, y0 + 100, 11.5f, WhiteA(0.35f * p0), FontWeight.Regular, 0, x0 + w - 22 - sx);

        // ---- tabs
        float bx = x0 + 18, by = y0 + 128, p1 = Pop(1);
        TabPill(c, m, "atab:activity", "Activity", ref bx, by, Panel == TeamPanel.Activity, p1);
        if (todos.Count > 0) TabPill(c, m, "atab:plan", $"Plan {a.Data.TodosDone}/{todos.Count}", ref bx, by, Panel == TeamPanel.Plan, p1);
        if (a.Data.Changes.Count > 0) TabPill(c, m, "atab:files", $"Files {a.Data.Changes.Count}", ref bx, by, Panel == TeamPanel.Files, p1);

        // ---- the feed box (needs-you card at its bottom)
        float fx = x0 + 16, fy = y0 + 162, fw = w - 32, fh = 168, p2 = Pop(2);
        c.Round(fx, fy, fw, fh, 18, WhiteA(0.05f * p2));
        float askH = a.Status == AgentStatus.NeedsYou ? 84 : 0;
        float bodyTop = fy + 8, bodyH = fh - 16 - askH;
        float k = p2 * Math.Clamp(PanelT, 0, 1);
        c.Ctx.PushAxisAlignedClip(new Rect(fx + 2, bodyTop, fw - 4, bodyH), Vortice.Direct2D1.AntialiasMode.Aliased);
        float content = Panel switch
        {
            TeamPanel.Plan => PlanPanel(c, a, fx + 14, bodyTop + 4, fw - 28, bodyH - 4, k) + 4,
            TeamPanel.Files => ChangesPanel(c, m, a, fx + 12, bodyTop, fw - 24, bodyH, k),
            _ => ActivityFeed(c, m, a, fx + 14, bodyTop, fw - 28, bodyH, k),
        };
        c.Ctx.PopAxisAlignedClip();
        _scrollMax = MathF.Max(0, content - bodyH);
        if (_scrollMax > 0)
        {
            float frac = bodyH / content, bh = MathF.Max(18, bodyH * frac);
            float pos = Math.Clamp(Scroll / _scrollMax, 0, 1);
            if (Panel == TeamPanel.Activity) pos = 1 - pos;
            c.Round(fx + fw - 7, bodyTop + (bodyH - bh) * pos, 3, bh, 1.5f, WhiteA(0.22f * k));
        }
        if (askH > 0) AskCard(c, m, a, fx + 8, fy + fh - askH - 6, fw - 16, askH - 2, p2);

        // ---- message box
        Field(c, m, Message, "input:msg", x0 + 16, y0 + 340, w - 32, 42, Pop(3), true);

        // ---- a few actions
        float ax = x0 + 16, ay = y0 + 392, p4 = Pop(4);
        if (!a.Island) ActionPill(c, m, "act:window", IcWindow, "Go to terminal", ref ax, ay, 0x0A84FF, p4);
        else if (a.Status is AgentStatus.Working or AgentStatus.NeedsYou) ActionPill(c, m, "act:stop", IcStop, "Stop", ref ax, ay, 0xFF453A, p4);
        else if (a.Status == AgentStatus.Ended) ActionPill(c, m, "act:restart", IcRestart, "Restart", ref ax, ay, 0x30D158, p4);
        else ActionPill(c, m, "act:terminal", IcTerminal, "Open in terminal", ref ax, ay, null, p4);
        ActionPill(c, m, "act:editor", IcCode, Shell.EditorName, ref ax, ay, null, p4);
        ActionPill(c, m, "act:folder", IcFolder, "Folder", ref ax, ay, null, p4);
        if (a.Island) ActionPill(c, m, "act:close", IcClose, "Close", ref ax, ay, null, p4);
    }

    void TabPill(Canvas c, IslandModel m, string id, string label, ref float x, float y, bool on, float k)
    {
        float w = c.Measure(label, 12, FontWeight.SemiBold) + 24;
        bool hov = m.Hit(id, x, y, w, 26);
        c.Round(x, y, w, 26, 13, on ? WhiteA(0.9f * k) : WhiteA((hov ? 0.14f : 0.07f) * k));
        c.Text(label, x + 12, y + 5, 12, on ? WithA(new Color4(0, 0, 0, 1), k) : WhiteA((hov ? 0.9f : 0.6f) * k), FontWeight.SemiBold);
        x += w + 6;
    }

    void ActionPill(Canvas c, IslandModel m, string id, char icon, string label, ref float x, float y, uint? accent, float k)
    {
        float w = c.Measure(label, 12, FontWeight.SemiBold) + 44;
        bool hov = m.Hit(id, x, y, w, 30);
        var bg = accent is { } ac ? WithA(Rgba(ac), (hov ? 0.5f : 0.3f) * k) : WhiteA((hov ? 0.15f : 0.075f) * k);
        c.Round(x, y, w, 30, 15, bg);
        c.Icon(icon, x + 17, y + 15, 12, WhiteA((hov ? 1 : 0.8f) * k));
        c.Text(label, x + 31, y + 6.5f, 12, WhiteA((hov ? 1 : 0.8f) * k), FontWeight.SemiBold);
        x += w + 6;
    }

    /// <summary>The one thing the agent needs from you: a question, a permission, or a nudge to its terminal.</summary>
    void AskCard(Canvas c, IslandModel m, Agent a, float x, float y, float w, float h, float k)
    {
        c.Round(x, y, w, h, 14, WithA(Rgba(0xFF9F0A), 0.13f * k));
        c.StrokeRound(x + 0.5f, y + 0.5f, w - 1, h - 1, 14, 1, WithA(Rgba(0xFF9F0A), 0.45f * k));
        float by = y + h - 40;
        if (a.Ask is { Tool: "AskUserQuestion" } ask && Questions(ask) is { Length: > 0 } qs)
        {
            var (q, opts) = qs[Math.Min(a.AskStep, qs.Length - 1)];
            string head = qs.Length > 1 ? $"{a.AskStep + 1}/{qs.Length}  " : "";
            c.Text(head + q, x + 14, y + 9, 13, WhiteA(0.95f * k), FontWeight.SemiBold, 0, w - 28);
            int n = Math.Min(opts.Length, 4);
            float skipW = 58, gap = 6, ow = (w - 20 - skipW - gap * n) / Math.Max(1, n);
            for (int i = 0; i < n; i++)
            {
                float ox = x + 10 + i * (ow + gap);
                bool hov = m.Hit($"ans:{i}", ox, by, ow, 32);
                c.Round(ox, by, ow, 32, 16, WithA(Rgba(hov ? 0x3D9BFFu : 0x0A84FFu), (hov ? 1 : 0.85f) * k));
                c.Text(opts[i], ox + ow / 2, by + 7.5f, 12, WhiteA(k), FontWeight.SemiBold, 0.5f, ow - 14);
            }
            float sx = x + w - 10 - skipW;
            bool hs = m.Hit("ans:skip", sx, by, skipW, 32);
            c.Round(sx, by, skipW, 32, 16, WhiteA((hs ? 0.24f : 0.13f) * k));
            c.Text("Skip", sx + skipW / 2, by + 7.5f, 12, WhiteA(0.85f * k), FontWeight.SemiBold, 0.5f);
        }
        else if (a.Ask != null)
        {
            c.Text("Wants to " + a.Ask.Summary, x + 14, y + 9, 13, WhiteA(0.95f * k), FontWeight.SemiBold, 0, w - 28);
            c.Text(a.Ask.Tool, x + 14, y + 28, 11, WithA(Rgba(0xFF9F0A), 0.85f * k), FontWeight.SemiBold);
            float pw = 96;
            bool hd = m.Hit("perm:deny", x + w - 10 - pw * 2 - 6, by, pw, 32);
            bool ha = m.Hit("perm:allow", x + w - 10 - pw, by, pw, 32);
            c.Round(x + w - 10 - pw * 2 - 6, by, pw, 32, 16, WhiteA((hd ? 0.24f : 0.13f) * k));
            c.Text("Deny", x + w - 10 - pw * 1.5f - 6, by + 7.5f, 13, WhiteA(0.92f * k), FontWeight.SemiBold, 0.5f);
            c.Round(x + w - 10 - pw, by, pw, 32, 16, WithA(Rgba(ha ? 0x3D9BFFu : 0x0A84FFu), k));
            c.Text("Allow", x + w - 10 - pw / 2, by + 7.5f, 13, WhiteA(k), FontWeight.SemiBold, 0.5f);
        }
        else
        {
            string what = a.WaitingFor is { Length: > 0 } wf && wf != "dialog open" ? wf : "It's waiting for your answer";
            c.Text(what, x + 14, y + 9, 13, WhiteA(0.95f * k), FontWeight.SemiBold, 0, w - 28);
            c.Text("Answer it where it runs", x + 14, y + 28, 11, WhiteA(0.5f * k));
            float pw = 150;
            bool hov = m.Hit("act:window", x + w - 10 - pw, by, pw, 32);
            c.Round(x + w - 10 - pw, by, pw, 32, 16, WithA(Rgba(hov ? 0x3D9BFFu : 0x0A84FFu), k));
            c.Icon(IcWindow, x + w - 10 - pw + 20, by + 16, 12, WhiteA(k));
            c.Text("Open terminal", x + w - 10 - pw / 2 + 10, by + 7.5f, 13, WhiteA(k), FontWeight.SemiBold, 0.5f);
        }
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

        float y = top + h + Scroll, total = 0, t = m.Clock;
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
                    if (live) c.ShimmerText(c.Fit(it.Text, 12.5f, FontWeight.SemiBold, w - 30), x + 24, y + 2, 12.5f, k, (t * 0.7f) % 1.4f / 1.4f, FontWeight.SemiBold);
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
        float y = top + 2 - Scroll, t = (float)_now;
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
                    float a0 = t * 6;
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

    float ChangesPanel(Canvas c, IslandModel m, Agent a, float x, float top, float w, float h, float k)
    {
        var ch = a.Data.Changes;
        if (ch.Count == 0) { c.Text("No files changed yet", x + w / 2, top + h / 2 - 10, 12.5f, WhiteA(0.4f * k), FontWeight.Regular, 0.5f); return 0; }
        float y = top - Scroll, rowH = 36;
        for (int i = 0; i < ch.Count; i++, y += rowH)
        {
            if (y + rowH < top || y > top + h) continue;
            var f = ch[i];
            bool visible = y >= top - 4 && y + rowH <= top + h + 4;
            bool hov = visible && m.Hit($"file:{i}", x - 4, y, w + 8, rowH - 3);
            if (hov) c.Round(x - 4, y, w + 8, rowH - 3, 10, WhiteA(0.09f * k));
            string name = Path.GetFileName(f.Path);
            string dir = Path.GetDirectoryName(f.Path) ?? "";
            if (a.Cwd.Length > 0 && dir.StartsWith(a.Cwd, StringComparison.OrdinalIgnoreCase)) dir = "." + dir[a.Cwd.Length..];
            c.Icon(IcChanges, x + 9, y + rowH / 2 - 1, 12, WithA(Rgba(0x0A84FF), 0.9f * k));
            c.Text(name, x + 26, y + 3, 13, WhiteA(0.92f * k), FontWeight.SemiBold, 0, w * 0.5f);
            c.Text(dir, x + 26, y + 19, 10.5f, WhiteA(0.38f * k), FontWeight.Regular, 0, w - 110);
            c.Text(f.Edits == 1 ? "1 edit" : $"{f.Edits} edits", x + w - 4, y + 9, 11, WhiteA((hov ? 0.8f : 0.4f) * k), FontWeight.SemiBold, 1);
        }
        return ch.Count * rowH;
    }

    // ---------------------------------------------------------------- new agent form

    void NewForm(Canvas c, IslandModel m, float x0, float y0, float w, float h, float vf)
    {
        float x = x0 + 18, iw = w - 36;
        float Pop(int i) => Math.Clamp((ViewTime - i * 0.05f) / 0.25f, 0, 1);

        // where
        float p0 = Pop(0);
        c.Text("Where should it work?", x, y0 + 54 + (1 - p0) * 6, 12.5f, WhiteA(0.55f * vf * p0), FontWeight.SemiBold);
        float fx = x, fy = y0 + 76;
        if (_recent.Count == 0) c.Text("No recent projects found in ~/.claude/history.jsonl", x, fy + 6, 12, WhiteA(0.4f * vf));
        for (int i = 0; i < _recent.Count; i++)
        {
            string name = Path.GetFileName(_recent[i].TrimEnd('\\'));
            float cw = MathF.Min(c.Measure(name, 12.5f, FontWeight.SemiBold), 170) + 36;
            if (fx + cw > x + iw) { fx = x; fy += 36; }
            if (fy > y0 + 116) break;
            bool on = string.Equals(_newFolder, _recent[i], StringComparison.OrdinalIgnoreCase);
            bool hov = m.Hit($"new:folder:{i}", fx, fy, cw, 30);
            float pk = Pop(1 + i);
            c.Round(fx, fy + (1 - pk) * 6, cw, 30, 15, on ? WithA(Rgba(0xBF5AF2), 0.85f * vf * pk) : WhiteA((hov ? 0.17f : 0.09f) * vf * pk));
            c.Icon(IcFolder, fx + 15, fy + 15 + (1 - pk) * 6, 11, WhiteA((on ? 1 : 0.6f) * vf * pk));
            c.Text(name, fx + 27, fy + 6 + (1 - pk) * 6, 12.5f, WhiteA((on ? 1 : 0.8f) * vf * pk), FontWeight.SemiBold, 0, 170);
            fx += cw + 8;
        }

        // what
        c.Text("What should it do?", x, y0 + 156, 12.5f, WhiteA(0.55f * vf * Pop(3)), FontWeight.SemiBold);
        Field(c, m, Task, "input:task", x, y0 + 176, iw, 44, vf * Pop(3), false);
        float tx = x, tyy = y0 + 230;
        for (int i = 0; i < Templates.Length; i++)
        {
            float cw = c.Measure(Templates[i], 11.5f, FontWeight.SemiBold) + 20;
            if (tx + cw > x + iw) { tx = x; tyy += 30; }
            if (tyy > y0 + 262) break;
            bool hov = m.Hit($"new:tpl:{i}", tx, tyy, cw, 24);
            float pk = Pop(4 + i);
            c.Round(tx, tyy, cw, 24, 12, WhiteA((hov ? 0.17f : 0.07f) * vf * pk));
            c.Text(Templates[i], tx + 10, tyy + 4, 11.5f, WhiteA((hov ? 0.95f : 0.6f) * vf * pk), FontWeight.SemiBold);
            tx += cw + 6;
        }

        // permissions
        c.Text("Permissions", x, y0 + 300, 12.5f, WhiteA(0.55f * vf * Pop(6)), FontWeight.SemiBold);
        float sw = iw / Modes.Length;
        c.Round(x, y0 + 320, iw, 32, 16, WhiteA(0.07f * vf));
        for (int i = 0; i < Modes.Length; i++)
        {
            bool on = i == _newMode;
            bool hov = m.Hit($"new:mode:{i}", x + i * sw, y0 + 320, sw, 32);
            if (on) c.Round(x + i * sw + 2, y0 + 322, sw - 4, 28, 14, WhiteA(0.2f * vf));
            else if (hov) c.Round(x + i * sw + 2, y0 + 322, sw - 4, 28, 14, WhiteA(0.08f * vf));
            c.Text(Modes[i].label, x + i * sw + sw / 2, y0 + 327, 12, WhiteA((on ? 1 : 0.6f) * vf), FontWeight.SemiBold, 0.5f);
        }
        c.Text(_newMode switch
        {
            1 => "It asks you here before every edit or command",
            2 => "It can read and plan, but won't change files or run commands",
            3 => "File edits are accepted automatically; anything else asks you here",
            _ => "Same rules as your terminal; anything it needs to ask, it asks you here",
        }, x + iw / 2, y0 + 358, 11, WhiteA(0.38f * vf), FontWeight.Regular, 0.5f, iw);

        // launch
        bool ready = _newFolder != null && Task.Text.Trim().Length > 0;
        BigButton(c, m, "new:launch", "Launch agent", IcAdd, x, y0 + 384, iw, 42, Rgba(ready ? 0xBF5AF2u : 0x48484Au), vf * Pop(7));
    }

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
    void Field(Canvas c, IslandModel m, TextField f, string id, float x, float y, float w, float h, float k, bool send)
    {
        bool focused = m.Focused == f;
        bool hov = m.Hit(id, x, y, w - (send ? 44 : 0), h);
        c.Round(x, y, w, h, h / 2, WhiteA((focused ? 0.13f : hov ? 0.1f : 0.075f) * k));
        if (focused) c.StrokeRound(x + 0.5f, y + 0.5f, w - 1, h - 1, h / 2, 1.2f, WithA(Rgba(0x0A84FF), 0.8f * k));
        float tx = x + 18, avail = w - 36 - (send ? 40 : 0), ty = y + h / 2 - 10;
        c.Ctx.PushAxisAlignedClip(new Rect(x + 10, y, avail + 10, h), Vortice.Direct2D1.AntialiasMode.Aliased);
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
        c.Ctx.PopAxisAlignedClip();

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
        c.Text(Summary, x0 + 40, y0 + h / 2 - 8.5f, 11.5f, col, FontWeight.SemiBold, 0, Count == 0 ? 160 : 80);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        foreach (var a in Agents) a.Runner?.Dispose();
    }
}
