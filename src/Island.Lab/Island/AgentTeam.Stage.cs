using System.Numerics;
using System.Text.Json.Nodes;
using Island.Lab.Mascot;
using Island.Lab.Motion;
using Island.Lab.Platform;
using Island.Lab.Render;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

// Inside one agent: "On stage" (design/agent-view.html, direction 1). He is the screen.
//
//  ┌ (manager) name · state ─────── model ▬ 31k $0.10 33m  ← ┐
//  │ (o)                   ( his own Bloub, )              (o) │   the round buttons at the sides are only there
//  │ (o)                   (     large      )              (o) │   while your pointer is on the island
//  │              What he is doing now, in bold                │
//  │        the task · ●●●○ 2 of 4 · 3 files · Activity        │
//  │        ╭─ his last words, as a bubble out of him ─╮       │   (or the steps he just took, a question with its
//  │        ╰──────────────────────────────────────────╯       │    answers, Allow / Deny, or a sheet: plan, files, activity)
//  │ ( Message…                                           ➤ )  │
//  └───────────────────────────────────────────────────────────┘
public sealed partial class AgentTeam
{
    public const float StageW = 480, StageH = 478, SheetGrow = 56, StageBallY = 132, StageBallR = 52;
    const float DeckTop = 265, DeckSide = 22;
    const char IcUp = '\uE70E';

    bool _sheet;                       // a sheet (plan, files or activity: Panel says which) is open over the deck
    Agent? _stageOf;
    AgentStatus _stageStatus;
    string _nowText = "", _deckKey = "";
    double _nowAt = -10, _deckAt = -10, _trailAt = -10;
    int _trailN;
    readonly Spring _acts = new(0, new SpringSpec(0.34f, 1f), 0.002f);

    /// <summary>A sheet is showing (the island is a little taller for it). What he needs from you always comes first.</summary>
    bool SheetShown => _sheet && View == TeamView.Focus && Selected is { } a && a.Status != AgentStatus.NeedsYou;

    enum Deck { Ask, Sheet, Trail, Ended, Words }

    Deck DeckOf(Agent a) => a.Status == AgentStatus.NeedsYou ? Deck.Ask : _sheet ? Deck.Sheet
        : a.Status == AgentStatus.Working ? Deck.Trail : a.Status == AgentStatus.Ended ? Deck.Ended : Deck.Words;

    string DeckKey(Agent a) => DeckOf(a) switch
    {
        Deck.Ask => $"ask{a.Ask?.RequestId}{a.Ask?.Tool}{a.AskStep}",
        Deck.Sheet => "sheet" + Panel,
        Deck.Trail => "trail" + a.Data.Chat.Count(c => c.User),
        Deck.Ended => "ended",
        _ => "words" + a.Data.Chat.Count,
    };

    static string NowLine(Agent a) => a.Status switch
    {
        AgentStatus.Working => a.Data.Log.Count > 0 ? a.Data.Log[^1] : "Thinking…",
        AgentStatus.NeedsYou => a.Ask == null ? "Needs you in its terminal" : a.Ask.Tool == "AskUserQuestion" ? "Has a question for you" : "Needs your permission",
        AgentStatus.Done => "Finished",
        AgentStatus.Ended => a.LastError ?? "Stopped",
        _ => "Ready for your next message",
    };

    /// <summary>The steps of the turn he is on (everything since your last message), oldest first.</summary>
    static List<FeedItem> TurnSteps(Agent a)
    {
        var feed = a.Data.Feed; var steps = new List<FeedItem>();
        for (int i = feed.Count - 1; i >= 0 && feed[i].Kind != FeedKind.You; i--) if (feed[i].Kind == FeedKind.Step) steps.Insert(0, feed[i]);
        return steps;
    }

    void OpenSheet(TeamPanel p)
    {
        if (_sheet && Panel == p) { _sheet = false; return; }
        _sheet = true;
        Panel = p; Scroll.Snap(0); PanelT.Snap(0); PanelT.To(1);
    }

    /// <summary>What changes on the stage from one tick to the next (kept out of the drawing, which runs twice a frame).</summary>
    void StepStage(IslandModel m, float dt, bool widget)
    {
        var a = Selected;
        bool on = widget && View == TeamView.Focus && a != null;
        _acts.To(on && m.Hovered ? 1 : 0); _acts.Step(dt);
        if (!on || a == null) { _stageOf = null; return; }
        if (_sheet && (Panel == TeamPanel.Plan && a.Data.Todos.Count == 0 || Panel == TeamPanel.Files && a.Data.Changes.Count == 0)) _sheet = false;
        if (a != _stageOf)
        {
            _stageOf = a; _stageStatus = a.Status;
            _nowText = NowLine(a); _nowAt = _now - 1;
            _deckKey = DeckKey(a); _deckAt = _now - 0.1;
            _trailN = TurnSteps(a).Count; _trailAt = _now - 1;
            Scroll.Snap(0);
        }
        if (a.Status != _stageStatus)
        {
            // he needs you, or he has just finished: whatever sheet was open gives way to it
            if (a.Status is AgentStatus.NeedsYou or AgentStatus.Done or AgentStatus.Ended) _sheet = false;
            _stageStatus = a.Status;
        }
        string now = NowLine(a);
        if (now != _nowText)
        {
            _nowText = now; _nowAt = _now;
            if (a.Status == AgentStatus.Working && m.Focused != Message) { a.LookAt = new Vector2(a.X, a.Y + 120); a.LookUntil = _now + 0.7; }   // (he glances at what he has just started)
        }
        string key = DeckKey(a);
        if (key != _deckKey) { _deckKey = key; _deckAt = _now; Scroll.Snap(0); }
        int n = TurnSteps(a).Count;
        if (n != _trailN) { _trailAt = n > _trailN ? _now : _now - 1; _trailN = n; }
    }

    static float Out3(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);
    static float Out5(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 5);

    static uint StageStatusColor(Agent a) => a.Status switch
    {
        AgentStatus.Working => 0x4DA3FF, AgentStatus.NeedsYou => 0xFF9F0A, AgentStatus.Done => 0x30D158, AgentStatus.Ended => 0xFF6259, _ => 0x8E8E93,
    };

    void StageBody(Canvas c, IslandModel m, Agent a, float x0, float y0, float w, float h, float vf)
    {
        double t = m.Clock; float cx = x0 + w / 2;
        var ag = Rgba(a.Color);
        Color4 agd = Mix(ag, 0x0B0B0D, 0.26f), agl = Mix(ag, 0xFFFFFF, 0.62f);
        float Pop(int i) => vf * Math.Clamp((ViewTime - 0.04f - i * 0.04f) / 0.25f, 0, 1);

        // ---- the island's own corner: Bloub the manager, the agent's name and state, its numbers, the way back
        float p0 = Pop(0);
        float statsW = Stats(c, a, x0 + w - 58, y0 + 15, p0);
        string st = a.StatusText;
        float stW = c.Measure(st, 12.5f, FontWeight.SemiBold), nameMax = MathF.Max(60, w - 48 - 58 - statsW - 16 - stW - 8);
        c.Text(a.Name, x0 + 48, y0 + 11, 14.5f, WhiteA(0.95f * vf), FontWeight.Bold, 0, nameMax);
        float nw = MathF.Min(c.Measure(a.Name, 14.5f, FontWeight.Bold), nameMax);
        c.Text(st, x0 + 48 + nw + 8, y0 + 13.5f, 12.5f, WithA(Rgba(StageStatusColor(a)), vf), FontWeight.SemiBold);

        // ---- him (drawn by DrawBalls on top of all this); a click pokes him
        m.Hit("ag:poke", cx - StageBallR - 4, y0 + StageBallY - StageBallR - 4, StageBallR * 2 + 8, StageBallR * 2 + 8);

        // ---- what he is doing now, in one bold line
        float p1 = Pop(1), nk = Out3((float)(_now - _nowAt) / 0.45f);
        var nowCol = a.Status switch { AgentStatus.NeedsYou => Rgba(0xFFB340), AgentStatus.Done => Rgba(0x3EE06A), AgentStatus.Ended => Rgba(0xFF6E66), _ => WhiteA(0.96f) };
        c.Text(c.Fit(_nowText, 19, FontWeight.Bold, w - 56), cx, y0 + 198 + (1 - nk) * 9, 19, WithA(nowCol, nowCol.A * p1 * nk), FontWeight.Bold, 0.5f);

        SubRow(c, m, a, x0, y0 + 231, w, p1);

        // ---- the deck: his last words, the steps he just took, what he needs from you, or a sheet
        float msgY = y0 + h - 64, dx = x0 + DeckSide, dw = w - DeckSide * 2, dTop = y0 + DeckTop, dBot = msgY - 8;
        float p2 = Pop(2), since = (float)(_now - _deckAt);
        switch (DeckOf(a))
        {
            case Deck.Ask: AskBubble(c, m, a, dx, dTop + 9, dw, cx, agd, since, p2); break;
            case Deck.Sheet: Sheet(c, m, a, dx, dTop + 2, dw, dBot - dTop - 2, since, p2); break;
            case Deck.Trail: Trail(c, m, a, dx, dTop, dw, cx, agl, p2); break;
            case Deck.Ended: EndedBubble(c, m, a, dx, dTop + 9, dw, cx, agd, since, p2); break;
            default: Words(c, m, a, dx, dTop + 9, dw, dBot - dTop - 9, cx, agd, since, p2); break;
        }

        // ---- the message box
        Field(c, m, Message, "input:msg", x0 + 20, msgY, w - 40, 44, Pop(3), true);

        SideActions(c, m, a, x0, y0, w, vf);
    }

    /// <summary>Model, context, cost and age, right-aligned at <paramref name="right"/>. Returns how wide they are.</summary>
    float Stats(Canvas c, Agent a, float right, float y, float k)
    {
        var col = WhiteA(0.36f * k);
        float x = right;
        void Word(string s) { float tw = c.Measure(s, 11); x -= tw; c.Text(s, x, y, 11, col); x -= 9; }
        var age = DateTime.UtcNow - a.StartedAt;
        Word(age.TotalHours >= 1 ? $"{(int)age.TotalHours}h {age.Minutes}m" : $"{Math.Max(1, (int)age.TotalMinutes)}m");
        if (a.ForkOf != null) Word("side question");
        if (a.Data.Cost > 0) Word($"${a.Data.Cost:0.00}");
        if (a.Data.Context > 0)
        {
            Word($"{a.Data.Context / 1000.0:0}k");
            x += 9 - 5;
            float full = a.Data.Context > 190_000 ? 1_000_000f : 200_000f, fr = Math.Clamp(a.Data.Context / full, 0.04f, 1);
            x -= 24;
            c.Round(x, y + 5.5f, 24, 4, 2, WhiteA(0.14f * k));
            c.Round(x, y + 5.5f, MathF.Max(4, 24 * fr), 4, 2, WhiteA(0.62f * k));
            x -= 9;
        }
        if (a.Data.Model is { } model) Word(model.Replace("claude-", ""));
        return right - (x + 9);
    }

    /// <summary>Under the bold line: the task, the plan as dots, the files it changed, and the way into its activity.</summary>
    void SubRow(Canvas c, IslandModel m, Agent a, float x0, float y, float w, float k)
    {
        double t = m.Clock; float gap = 10;
        var todos = a.Data.Todos; int files = a.Data.Changes.Count;
        bool dots = todos.Count is > 0 and <= 8;
        string planL = todos.Count > 0 ? $"{a.Data.TodosDone} of {todos.Count}" : "", filesL = files == 1 ? "1 file" : $"{files} files";
        float planW = todos.Count > 0 ? 8 + (dots ? todos.Count * 12 - 4 : 40) + 6 + c.Measure(planL, 11.5f, FontWeight.SemiBold) + 8 : 0;
        float filesW = files > 0 ? 8 + 14 + 5 + c.Measure(filesL, 11.5f, FontWeight.SemiBold) + 8 : 0;
        float actW = a.Data.Feed.Count > 0 ? 8 + c.Measure("Activity", 11.5f, FontWeight.SemiBold) + 8 : 0;
        float chips = planW + filesW + actW + gap * ((planW > 0 ? 1 : 0) + (filesW > 0 ? 1 : 0) + (actW > 0 ? 1 : 0));
        string task = a.Title ?? a.Prompt ?? (a.Island ? "" : "No task yet");
        float taskW = MathF.Min(c.Measure(task, 12.5f), MathF.Max(60, MathF.Min(210, w - 48 - chips)));
        float x = x0 + w / 2 - (taskW + chips) / 2;
        c.Text(task, x, y + 2, 12.5f, WhiteA(0.46f * k), FontWeight.Regular, 0, taskW);
        x += taskW + gap;

        bool Chip(string id, float cw, bool on)
        {
            bool hov = m.Hit(id, x, y, cw, 22);
            if (hov || on) c.Round(x, y, cw, 22, 11, WhiteA(0.1f * k));
            return hov || on;
        }
        if (planW > 0)
        {
            bool lit = Chip("atab:plan", planW, _sheet && Panel == TeamPanel.Plan);
            float px = x + 8;
            if (dots)
                foreach (var td in todos)
                {
                    bool done = td.Status == "completed", cur = td.Status == "in_progress";
                    float r = cur ? 4 * (1.12f + 0.2f * (float)Math.Sin(t * 5.7f)) : 4;
                    c.Circle(px + 4, y + 11, r, done ? WithA(Rgba(0x30D158), k) : cur ? WithA(Rgba(0x4DA3FF), k) : WhiteA(0.2f * k));
                    px += 12;
                }
            else
            {
                c.Round(px, y + 9, 40, 4, 2, WhiteA(0.16f * k));
                c.Round(px, y + 9, MathF.Max(4, 40f * a.Data.TodosDone / todos.Count), 4, 2, WithA(Rgba(0x30D158), k));
                px += 44;
            }
            c.Text(planL, px + 2, y + 3, 11.5f, WhiteA((lit ? 1 : 0.5f) * k), FontWeight.SemiBold);
            x += planW + gap;
        }
        if (filesW > 0)
        {
            bool lit = Chip("atab:files", filesW, _sheet && Panel == TeamPanel.Files);
            c.Icon(IcChanges, x + 8 + 7, y + 11, 11, WhiteA((lit ? 1 : 0.5f) * k));
            c.Text(filesL, x + 8 + 14 + 5, y + 3, 11.5f, WhiteA((lit ? 1 : 0.5f) * k), FontWeight.SemiBold);
            x += filesW + gap;
        }
        if (actW > 0)
        {
            bool lit = Chip("atab:activity", actW, _sheet && Panel == TeamPanel.Activity);
            c.Text("Activity", x + 8, y + 3, 11.5f, WhiteA((lit ? 1 : 0.5f) * k), FontWeight.SemiBold);
        }
    }

    /// <summary>A bubble that comes out of him: it grows from where he stands, and settles under him.</summary>
    Matrix3x2 BubblePop(Canvas c, float cx, float top, float since, out float k, float secs = 0.42f)
    {
        float e = Out5(since / secs);
        k = Out3(since / (secs * 0.6f));
        var keep = c.Transform;
        if (e < 0.999f) c.Transform = Matrix3x2.CreateScale(0.3f + 0.7f * e, 0.3f + 0.7f * e, new Vector2(cx, top - 80)) * Matrix3x2.CreateTranslation(0, -26 * (1 - e)) * keep;
        return keep;
    }

    static void Tail(Canvas c, float cx, float top, Color4 col) =>
        c.Polygon(col, new Vector2(cx - 11, top + 1.5f), new Vector2(cx, top - 8.5f), new Vector2(cx + 11, top + 1.5f));

    /// <summary>His last words. A long answer scrolls inside the bubble; what came before it is one click away.</summary>
    void Words(Canvas c, IslandModel m, Agent a, float x, float top, float w, float room, float cx, Color4 agd, float since, float k0)
    {
        if (a.Data.LastReply is not { Length: > 0 } reply)
        {
            _scrollMax = 0;
            c.Text(a.Island ? "Starting up…" : "Nothing yet. Its steps and its answers show up here", cx, top + 34, 12.5f, WhiteA(0.38f * k0), FontWeight.Regular, 0.5f, w);
            return;
        }
        string text = Plain(reply.Length > 1400 ? reply[..1400] + "…" : reply);
        float maxT = w - 36, tw = c.WrappedWidth(text, 13.5f, maxT), th = c.WrappedHeight(text, 13.5f, maxT);
        float viewH = MathF.Min(th, MathF.Min(104, room - 30)), bw = Math.Clamp(tw + 38, 150, w), bh = viewH + 26, bx = cx - bw / 2;
        _scrollMax = MathF.Max(0, th - viewH);
        var keep = BubblePop(c, cx, top, since, out float pk);
        float k = k0 * pk;
        bool settled = since > 0.3f;
        bool hov = settled && m.Hit("act:copy", bx, top, bw, bh);
        Tail(c, cx, top, WithA(agd, k));
        c.Round(bx, top, bw, bh, MathF.Min(24, bh / 2), WithA(agd, k));
        c.PushClip(bx + 4, top + 13, bw - 8, viewH);
        c.Wrapped(text, bx + 18, top + 13 - Scroll, maxT, 13.5f, WhiteA(0.95f * k));
        c.PopClip();
        if (_scrollMax > 0)
        {
            float bar = MathF.Max(14, viewH * viewH / th), pos = Math.Clamp(Scroll / _scrollMax, 0, 1);
            c.Round(bx + bw - 8, top + 13 + (viewH - bar) * pos, 3, bar, 1.5f, WhiteA(0.3f * k));
        }
        c.Transform = keep;
        if (hov || m.HoverHit == "atab:activity:earlier")
        {
            // what came before, and a way to take his words with you
            float ew = c.Measure("Earlier", 10.5f, FontWeight.SemiBold) + 28, ex = bx + bw - 10 - ew, ey = top - 11;
            bool eh = m.Hit("atab:activity:earlier", ex, ey, ew, 22);
            c.Round(ex, ey, ew, 22, 11, Rgba(eh ? 0x343438u : 0x232326u));
            c.Icon(IcUp, ex + 12, ey + 11, 8, WhiteA(0.8f));
            c.Text("Earlier", ex + 20, ey + 4, 10.5f, WhiteA(0.85f), FontWeight.SemiBold);
            if (hov && !eh) c.Text("Click to copy", bx + 14, top - 15, 10.5f, WhiteA(0.4f), FontWeight.SemiBold);
        }
    }

    /// <summary>While he works: the steps he has just taken fall away under him, and your message waits below.</summary>
    void Trail(Canvas c, IslandModel m, Agent a, float x, float top, float w, float cx, Color4 agl, float k)
    {
        _scrollMax = 0;
        var steps = TurnSteps(a);
        int shown = Math.Min(3, Math.Max(0, steps.Count - 1)), more = steps.Count - 1 - shown;
        float sl = Out3((float)(_now - _trailAt) / 0.45f), pitch = 24;
        bool hov = steps.Count > 1 && m.Hit("atab:activity:trail", x, top, w, 4 + shown * pitch + (more > 0 ? 20 : 0));
        ReadOnlySpan<float> tone = [0.5f, 0.34f, 0.2f, 0];
        for (int i = 0; i < shown; i++)
        {
            var s = steps[steps.Count - 2 - i];
            // (a new step has just begun: the one before it drops in from him, the others move down one place)
            float y = top + 4 + i * pitch - (1 - sl) * pitch, al = (tone[i] + (tone[Math.Max(0, i - 1)] - tone[i]) * (1 - sl)) * (i == 0 ? sl : 1) * (hov ? 1.5f : 1);
            float tw = MathF.Min(c.Measure(s.Text, 12.5f), w - 30), rx = cx - (tw + 22) / 2;
            var ic = StepIcon(s.Tool);
            if (ic == IcDot) c.Circle(rx + 7, y + 9, 2.4f, WhiteA(al * k)); else c.Icon(ic, rx + 7, y + 9, 11, WhiteA(al * k));
            c.Text(s.Text, rx + 22, y, 12.5f, WhiteA(al * k), FontWeight.Regular, 0, tw);
        }
        if (more > 0) c.Text(more == 1 ? "+ 1 earlier step" : $"+ {more} earlier steps", cx, top + 4 + shown * pitch + 1, 11, WhiteA((hov ? 0.5f : 0.26f) * k), FontWeight.Regular, 0.5f);

        // your message to it, at the foot of the stage
        string? you = null;
        for (int i = a.Data.Feed.Count - 1; i >= 0; i--) if (a.Data.Feed[i].Kind == FeedKind.You) { you = a.Data.Feed[i].Text; break; }
        if (you == null) return;
        you = you.Replace('\n', ' ');
        float lw = c.Measure("You", 12.5f, FontWeight.SemiBold), yw = MathF.Min(c.Measure(you, 12.5f), w - 28 - lw - 7), pw = lw + 7 + yw + 28, px = cx - pw / 2, py = top + 100;
        float e = Out5((float)(_now - _deckAt) / 0.5f);
        py += (1 - e) * 14;
        c.Round(px, py, pw, 33, 16.5f, WithA(Rgba(0x151517), k * e));
        c.Text("You", px + 14, py + 8, 12.5f, WithA(agl, k * e), FontWeight.SemiBold);
        c.Text(you, px + 14 + lw + 7, py + 8, 12.5f, WhiteA(0.86f * k * e), FontWeight.Regular, 0, yw);
    }

    /// <summary>What he needs from you: a question with its answers, a permission, or a trip to its terminal.</summary>
    void AskBubble(Canvas c, IslandModel m, Agent a, float x, float top, float w, float cx, Color4 agd, float since, float k0)
    {
        _scrollMax = 0;
        var keep = BubblePop(c, cx, top, since, out float pk);
        float k = k0 * pk, ix = x + 18, iw = w - 36;
        bool live = since > 0.3f;
        string head, body; bool code = false;
        var qs = a.Ask is { Tool: "AskUserQuestion" } ask ? Questions(ask) : [];
        if (qs.Length > 0)
        {
            head = qs.Length > 1 ? $"Question {Math.Min(a.AskStep, qs.Length - 1) + 1} of {qs.Length}" : "A question for you";
            body = qs[Math.Min(a.AskStep, qs.Length - 1)].Q;
        }
        else if (a.Ask != null)
        {
            head = $"{a.Ask.Tool} · needs your permission";
            code = a.Ask.Summary.StartsWith("run: ", StringComparison.Ordinal);
            body = code ? a.Ask.Summary[5..] : "Wants to " + a.Ask.Summary;
        }
        else
        {
            head = "Answer it where it runs";
            body = a.WaitingFor is { Length: > 0 } wf && wf != "dialog open" ? char.ToUpperInvariant(wf[0]) + wf[1..] : "It is waiting for your answer";
        }
        float bodyH = code ? 30 : MathF.Min(c.WrappedHeight(body, 15.5f, iw, FontWeight.Bold), 42);
        float bh = 13 + 16 + 4 + bodyH + 11 + 35 + 14;
        Tail(c, cx, top, WithA(agd, k));
        c.Round(x, top, w, bh, 24, WithA(agd, k));
        c.Text(head, ix, top + 12, 11, WhiteA(0.55f * k), FontWeight.SemiBold, 0, iw);
        float by = top + 13 + 16 + 4;
        if (code)
        {
            string cmd = c.Fit(body, 13.5f, FontWeight.SemiBold, iw - 20);
            float cw = c.Measure(cmd, 13.5f, FontWeight.SemiBold) + 20;
            c.Round(ix, by, cw, 28, 9, new Color4(0, 0, 0, 0.34f * k));
            c.Text(cmd, ix + 10, by + 4.5f, 13.5f, WhiteA(0.96f * k), FontWeight.SemiBold);
        }
        else
        {
            c.PushClip(ix, by, iw, bodyH);
            c.Wrapped(body, ix, by, iw, 15.5f, WhiteA(k), FontWeight.Bold);
            c.PopClip();
        }
        float oy = by + bodyH + 11;
        // the answers arrive one after the other
        float In(int i) => Out3((since - 0.12f - i * 0.055f) / 0.3f);
        bool Button(string id, string label, float bx, float bw, int i, int look)   // look 0: an answer, 1: quiet, 2: the one to press
        {
            float e = In(i);
            bool hov = live && e > 0.6f && m.Hit(id, bx, oy, bw, 35);
            bool white = look == 2 || hov && look == 0;
            float yy = oy + (1 - e) * 8;
            c.Round(bx, yy, bw, 35, 17.5f, white ? WhiteA((look == 2 && !hov ? 0.94f : 1) * k * e) : WhiteA((look == 1 ? (hov ? 0.16f : 0.07f) : 0.15f) * k * e));
            c.Text(label, bx + bw / 2, yy + 8, 13, white ? new Color4(0.07f, 0.07f, 0.07f, k * e) : WhiteA((look == 1 ? 0.62f : 1) * k * e), FontWeight.SemiBold, 0.5f, bw - 14);
            return hov;
        }
        if (qs.Length > 0)
        {
            var opts = qs[Math.Min(a.AskStep, qs.Length - 1)].Options;
            int n = Math.Min(opts.Length, 4);
            float skipW = 62, gap = 6, avail = iw - skipW - gap * n, sum = 0;
            Span<float> ws = stackalloc float[n];
            for (int i = 0; i < n; i++) { ws[i] = c.Measure(opts[i], 13, FontWeight.SemiBold) + 32; sum += ws[i]; }
            if (sum > avail) for (int i = 0; i < n; i++) ws[i] = avail / n;
            float ox = ix;
            for (int i = 0; i < n; i++) { Button($"ans:{i}", opts[i], ox, ws[i], i, 0); ox += ws[i] + gap; }
            Button("ans:skip", "Skip", ix + iw - skipW, skipW, n, 1);
        }
        else if (a.Ask != null)
        {
            Button("perm:deny", "Deny", ix, 84, 0, 1);
            Button("perm:allow", "Allow", ix + 90, 96, 1, 2);
        }
        else if (!a.Island) Button("act:window", "Open terminal", ix, 136, 0, 2);
        c.Transform = keep;
    }

    void EndedBubble(Canvas c, IslandModel m, Agent a, float x, float top, float w, float cx, Color4 agd, float since, float k0)
    {
        _scrollMax = 0;
        var keep = BubblePop(c, cx, top, since, out float pk);
        float k = k0 * pk, bw = 104, tw = w - 36 - bw - 14;
        const string kept = "What it did so far is kept. Restart, and it carries on from there.";
        float th = c.WrappedHeight(kept, 13.5f, tw), bh = MathF.Max(th, 35) + 28;
        Tail(c, cx, top, WithA(agd, k));
        c.Round(x, top, w, bh, MathF.Min(24, bh / 2), WithA(agd, k));
        c.Wrapped(kept, x + 18, top + (bh - th) / 2, tw, 13.5f, WhiteA(0.95f * k));
        float bx = x + w - 18 - bw, by = top + (bh - 35) / 2;
        bool hov = since > 0.3f && m.Hit("act:restart", bx, by, bw, 35);
        c.Round(bx, by, bw, 35, 17.5f, WhiteA((hov ? 1 : 0.94f) * k));
        var ink = new Color4(0.07f, 0.07f, 0.07f, k);
        c.Icon(IcRestart, bx + 20, by + 17.5f, 11, ink);
        c.Text("Restart", bx + 33, by + 8, 13, ink, FontWeight.SemiBold);
        c.Transform = keep;
    }

    /// <summary>The plan, the files it changed, or everything it did and said: a sheet over the deck (the island grows a little for it).</summary>
    void Sheet(Canvas c, IslandModel m, Agent a, float x, float top, float w, float h, float since, float k0)
    {
        float e = Out5(since / 0.4f), k = k0 * Out3(since / 0.25f);
        top += (1 - e) * 16;
        c.Round(x, top, w, h, 24, WithA(Rgba(0x121214), k));
        // which sheet, as three words: the one that is open is the bright one
        float tx = x + 18;
        void Tab(string id, string label, TeamPanel p)
        {
            bool on = Panel == p;
            float tw = c.Measure(label, 11.5f, FontWeight.SemiBold);
            bool hov = !on && m.Hit(id, tx - 5, top + 6, tw + 10, 24);
            c.Text(label, tx, top + 11, 11.5f, WhiteA((on ? 0.95f : hov ? 0.7f : 0.38f) * k), FontWeight.SemiBold);
            tx += tw + 14;
        }
        Tab("atab:activity", "Activity", TeamPanel.Activity);
        if (a.Data.Todos.Count > 0) Tab("atab:plan", $"Plan · {a.Data.TodosDone} of {a.Data.Todos.Count}", TeamPanel.Plan);
        if (a.Data.Changes.Count > 0) Tab("atab:files", a.Data.Changes.Count == 1 ? "1 changed file" : $"{a.Data.Changes.Count} changed files", TeamPanel.Files);
        float qx = x + w - 24, qy = top + 19;
        bool ch = m.Hit("sheet:close", qx - 13, qy - 13, 26, 26);
        c.Circle(qx, qy, 10, WhiteA((ch ? 0.2f : 0.1f) * k));
        c.Icon(IcClose, qx, qy, 7.5f, WhiteA(0.85f * k));

        float ly = top + 36, lh = h - 36 - 12, kk = k * Math.Clamp(PanelT, 0, 1);
        c.PushClip(x + 2, ly, w - 4, lh);
        float content = Panel switch
        {
            TeamPanel.Plan => PlanPanel(c, a, x + 18, ly + 2, w - 36, lh - 2, kk) + 4,
            TeamPanel.Files => FilesList(c, m, a, x + 12, ly, w - 24, lh, kk),
            _ => ActivityFeed(c, m, a, x + 18, ly, w - 36, lh, kk),
        };
        c.PopClip();
        _scrollMax = MathF.Max(0, content - lh);
        if (_scrollMax > 0)
        {
            float bar = MathF.Max(18, lh * lh / content), pos = Math.Clamp(Scroll / _scrollMax, 0, 1);
            if (Panel == TeamPanel.Activity) pos = 1 - pos;
            c.Round(x + w - 8, ly + (lh - bar) * pos, 3, bar, 1.5f, WhiteA(0.22f * kk));
        }
    }

    /// <summary>The files it changed, each with the icon Windows gives it; a click opens it in your editor.</summary>
    float FilesList(Canvas c, IslandModel m, Agent a, float x, float top, float w, float h, float k)
    {
        var ch = a.Data.Changes;
        float y = top - Scroll, rowH = 34;
        for (int i = 0; i < ch.Count; i++, y += rowH)
        {
            if (y + rowH < top || y > top + h) continue;
            var f = ch[i];
            bool visible = y >= top - 4 && y + rowH <= top + h + 4;
            bool hov = visible && m.Hit($"file:{i}", x, y, w, rowH - 2);
            if (hov) c.Round(x, y, w, rowH - 2, 11, WhiteA(0.08f * k));
            string name = Path.GetFileName(f.Path), dir = Path.GetDirectoryName(f.Path) ?? "";
            if (a.Cwd.Length > 0 && dir.StartsWith(a.Cwd, StringComparison.OrdinalIgnoreCase)) dir = "." + dir[a.Cwd.Length..];
            string full = Path.IsPathRooted(f.Path) ? f.Path : Path.Combine(a.Cwd, f.Path);
            string ext = Path.GetExtension(name);
            // (its real icon when the file is there to ask Windows about; otherwise the icon Windows gives that kind of file)
            if (!(File.Exists(full) && FileIcon(c, Images.Icon + full, x + 8, y + 7, 18, k)))
                FileIcon(c, Images.Kind + "x" + (ext.Length > 1 ? ext : ".txt"), x + 8, y + 7, 18, k);
            float nw = MathF.Min(c.Measure(name, 12.5f, FontWeight.SemiBold), w * 0.5f);
            string edits = f.Edits == 1 ? "1 edit" : $"{f.Edits} edits";
            float ew = c.Measure(edits, 11, FontWeight.SemiBold);
            c.Text(name, x + 34, y + 7, 12.5f, WhiteA(0.94f * k), FontWeight.SemiBold, 0, nw);
            c.Text(dir, x + 34 + nw + 9, y + 8.5f, 11, WhiteA(0.38f * k), FontWeight.Regular, 0, MathF.Max(0, w - 34 - nw - 9 - ew - 22));
            c.Text(edits, x + w - 10, y + 8.5f, 11, WhiteA((hov ? 0.8f : 0.45f) * k), FontWeight.SemiBold, 1);
        }
        return ch.Count * rowH;
    }

    static bool FileIcon(Canvas c, string url, float x, float y, float s, float a)
    {
        if (Images.Get(url) is not { } pic || pic.W < 1 || pic.H < 1) return false;
        float k = MathF.Min(s / pic.W, s / pic.H), w = pic.W * k, h = pic.H * k;
        return c.Image(url, x + (s - w) / 2, y + (s - h) / 2, w, h, 0, a);
    }

    /// <summary>The few things you can do with it, as quiet round buttons beside him: there only while your pointer is on the island.</summary>
    void SideActions(Canvas c, IslandModel m, Agent a, float x0, float y0, float w, float vf)
    {
        float ak = Math.Clamp(_acts, 0, 1) * vf;
        if (ak < 0.01f) return;
        (string id, char icon, string label, uint? tint) first = !a.Island ? ("act:window", IcWindow, "Go to terminal", (uint?)0x0A84FF)
            : a.Status is AgentStatus.Working or AgentStatus.NeedsYou ? ("act:stop", IcStop, "Stop", (uint?)0xFF453A)
            : a.Status == AgentStatus.Ended ? ("act:restart", IcRestart, "Restart", (uint?)0x30D158)
            : ("act:terminal", IcTerminal, "Open in terminal", (uint?)null);
        Span<(string id, char icon, string label, uint? tint)> acts =
            [first, ("act:editor", IcCode, Shell.EditorName, null), ("act:folder", IcFolder, "Folder", null), ("act:close", IcClose, "Close", null)];
        int n = a.Island ? 4 : 3;
        for (int i = 0; i < n; i++)
        {
            var (id, icon, label, tint) = acts[i];
            bool left = i < 2; int row = left ? i : i - 2;
            float e = Out3(Math.Clamp(ak * 1.2f - row * 0.2f, 0, 1));   // (the second one a beat after the first)
            float bx = (left ? x0 + 40 - (1 - e) * 12 : x0 + w - 40 + (1 - e) * 12), by = y0 + 108 + row * 44, r = 18 * (0.8f + 0.2f * e);
            bool hov = e > 0.6f && m.Hit(id, bx - 19, by - 19, 38, 38);
            var bg = tint is { } tc ? WithA(Mix(Rgba(tc), 0x0C0C0E, hov ? 0.55f : 0.3f), e) : WhiteA((hov ? 0.18f : 0.08f) * e);
            c.Circle(bx, by, r, bg);
            c.Icon(icon, bx, by, 12.5f * (0.8f + 0.2f * e), tint is { } ti && !hov ? WithA(Mix(Rgba(ti), 0xFFFFFF, 0.35f), e) : WhiteA((hov ? 1 : 0.72f) * e));
            if (!hov) continue;
            float lw = c.Measure(label, 11.5f, FontWeight.SemiBold) + 20, lx = left ? bx + 25 : bx - 25 - lw;
            c.Round(lx, by - 12, lw, 24, 12, Rgba(0x1E1E21));
            c.Text(label, lx + 10, by - 8, 11.5f, WhiteA(1), FontWeight.SemiBold);
        }
    }

    /// <summary>
    /// Dev (showcase only): puts a pretend island agent on stage in one of its states (working, q, perm, done, ended, idle),
    /// so every screen of the console can be filmed without Claude Code. Script: "pose q".
    /// </summary>
    public void DemoPose(IslandModel m, string what)
    {
        if (!Showcase.On) return;
        var a = Selected is { Island: true, Gone: false } s && View == TeamView.Focus ? s : Live.FirstOrDefault(x => x.Island);
        if (a == null)
        {
            a = Launch(m, @"C:\dev\chrome-status", "Chrome working status");
            var d = a.Data;
            d.Model = "claude-fable-5-2"; d.Context = 334_000; d.Cost = 1.84;
            d.Chat.Clear(); d.Feed.Clear(); d.Log.Clear();
            d.AddChat(true, "Show Chrome's working status in the island");
            d.AddLog("Looking for the Chrome extension", "Glob"); d.AddLog("Reading extension/manifest.json", "Read"); d.AddLog("Editing extension/background.js", "Edit");
            d.AddChat(false, "I found where Chrome reports its state: the extension knows exactly when a tab is loading. I would read it from there rather than guess from the tab's title. Shall I go on?");
            d.AddChat(true, "Continue, and read it from the extension");
            d.AddLog("Reading extension/background.js", "Read"); d.AddLog("Searching “tabStatus”", "Grep"); d.AddLog("Editing src/chrome/status.ts", "Edit"); d.AddLog("Editing src/island/StatusPill.tsx", "Edit");
            d.Todos = [new("Find where Chrome reports its state", "completed", null), new("Read the state from the extension", "completed", null), new("Show it in the island", "in_progress", "Showing it in the island"), new("Build and test", "pending", null)];
            d.Changes.Add(new FileChange(@"extension\background.js", 1)); d.Changes.Add(new FileChange(@"src\chrome\status.ts", 3)); d.Changes.Add(new FileChange(@"src\island\StatusPill.tsx", 2)); d.Changes.Add(new FileChange(@"src\island\island.css", 1));
            d.Version++;
        }
        if (Selected != a || View != TeamView.Focus) Focus(a);
        switch (what)
        {
            case "working": a.Ask = null; a.LastError = null; if (a.Status != AgentStatus.Working) Transition(m, a, AgentStatus.Working); a.Data.AddLog(a.Data.Log.Count % 2 == 0 ? "Running npm test" : "Editing src/island/island.css", a.Data.Log.Count % 2 == 0 ? "Bash" : "Edit"); break;
            case "q":
                a.AskStep = 0; a.AskAnswers = new JsonObject();
                a.Ask = new PermissionAsk("demo", "AskUserQuestion", "ask you something", JsonNode.Parse(
                    """{"questions":[{"question":"Show the status as a dot or as a word?","options":[{"label":"A dot"},{"label":"A word"},{"label":"Both"}]},{"question":"Where should it sit in the island?","options":[{"label":"Beside the time"},{"label":"Under Bloub"}]}]}"""));
                Transition(m, a, AgentStatus.NeedsYou); break;
            case "perm":
                a.Ask = new PermissionAsk("demo", "Bash", "run: npm test", JsonNode.Parse("""{"command":"npm test"}"""));
                Transition(m, a, AgentStatus.NeedsYou); break;
            case "done":
                a.Ask = null; a.LastError = null;
                a.Data.Todos = a.Data.Todos.Select(td => td with { Status = "completed" }).ToList();
                a.Data.AddChat(false, "Done. The island now shows Chrome's status: a dot while a page loads, and the tab's name once it is ready. I changed 4 files, and the tests pass.");
                Transition(m, a, AgentStatus.Done); break;
            case "ended": a.Ask = null; a.LastError = "It stopped before it finished"; Transition(m, a, AgentStatus.Ended); break;
            case "idle": a.Ask = null; a.LastError = null; a.Status = AgentStatus.Idle; break;
        }
    }
}
