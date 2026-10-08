using System.Numerics;
using Vortice.DirectWrite;
using Island.Lab.Render;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

/// <summary>Content for each island state, laid out at the state's target size (x0, y0 = top-left).</summary>
public static partial class Scenes
{
    static readonly Vortice.Mathematics.Color4 Secondary = WhiteA(0.6f), Tertiary = WhiteA(0.38f), Fill = WhiteA(0.1f);
    const uint Green = 0x30D158, Orange = 0xFF9F0A, Blue = 0x0A84FF, Red = 0xFF453A;

    public static void Draw(Canvas c, IslandModel m, Mode mode, float x0, float y0, float w, float h)
    {
        switch (mode)
        {
            case Mode.Media: case Mode.Split: MediaCompact(c, m, x0, y0, w, h); break;
            case Mode.Focus: FocusCompact(c, m, x0, y0, w, h); break;
            case Mode.Alert: Alert(c, m, x0, y0, w, h); break;
            case Mode.Expanded: MediaExpanded(c, m, x0, y0, w, h); break;
            case Mode.Full: Full(c, m, x0, y0, w, h); break;
            case Mode.Command: Command(c, m, x0, y0, w, h); break;
            case Mode.Gallery: Gallery(c, m, x0, y0, w, h); break;
            case Mode.Agents: m.Team.DrawScene(c, m, x0, y0, w, h); break;
            case Mode.Dormant: m.Team.DrawPill(c, m, x0, y0, w, h); break;
        }
    }

    static void Art(Canvas c, IslandModel m, float x, float y, float s, float r)
    {
        using var g = c.Gradient(new(x, y), new(x + s, y + s), Rgba(m.Song.C0), Rgba(m.Song.C1));
        c.Ctx.FillRoundedRectangle(new Vortice.Direct2D1.RoundedRectangle(new System.Drawing.RectangleF(x, y, s, s), r, r), g);
        // a soft highlight so it reads as artwork
        c.Circle(x + s * 0.72f, y + s * 0.3f, s * 0.18f, WhiteA(0.22f));
    }

    static void Waveform(Canvas c, IslandModel m, float right, float cy, float maxH, float barW = 3f, float gap = 2.6f)
    {
        var color = Rgba(m.Song.C1);
        float x = right - (barW * 5 + gap * 4);
        for (int i = 0; i < 5; i++)
        {
            float bh = MathF.Max(barW, maxH * m.Bars[i]);
            c.Round(x, cy - bh / 2, barW, bh, barW / 2, color);
            x += barW + gap;
        }
    }

    static string Time(float s) => $"{(int)s / 60}:{(int)s % 60:00}";

    static void MediaCompact(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        Art(c, m, x0 + 42, y0 + 7, 22, 6);
        Waveform(c, m, x0 + w - 15, y0 + h / 2, 16);
    }

    public static void BubbleContent(Canvas c, IslandModel m, float cx, float cy, float r, float opacity)
    {
        float p = (m.FocusElapsed % 60) / 60f;
        c.Ring(cx, cy, r * 0.52f, 2.6f, p, WithA(Rgba(Orange), 0.25f * opacity), WithA(Rgba(Orange), opacity));
    }

    static void FocusCompact(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        c.Text("Focus", x0 + 42, y0 + 8.5f, 13.5f, Rgba(Green), FontWeight.SemiBold);
        m.FocusDigits.Draw(c, x0 + w - 86, y0 + 7.5f, 15, White, FontWeight.SemiBold);
        float p = 1 - m.FocusElapsed / m.FocusLength;
        c.Ring(x0 + w - 21, y0 + h / 2, 8, 2.6f, p, WithA(Rgba(Green), 0.25f), Rgba(Green));
    }

    static void Alert(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        switch (m.Alert)
        {
            case AlertKind.Welcome:
                c.Text("Hi, I'm Bloub — your Island", x0 + 80, y0 + 17, 15, White, FontWeight.SemiBold);
                c.Text("Hover me for your dashboard · hover the team for your Claude sessions", x0 + 80, y0 + 41, 12.5f, Secondary, maxWidth: w - 100);
                break;
            case AlertKind.Info:
                c.Text(m.InfoTitle, x0 + 80, y0 + 17, 15, White, FontWeight.SemiBold);
                c.Text(m.InfoText, x0 + 80, y0 + 41, 12.5f, Secondary);
                break;
            case AlertKind.Agent when m.AlertAgent is { } ag:
            {
                c.Text(m.InfoTitle, x0 + 80, y0 + 17, 15, White, FontWeight.SemiBold, 0, w - 200);
                c.Text(m.InfoText, x0 + 80, y0 + 41, 12.5f, Secondary, maxWidth: w - 200);
                // the agent's colour + its status, on the right
                var sc = Rgba(ag.StatusColor);
                float pw = c.Measure(ag.StatusText, 12.5f, FontWeight.SemiBold) + 34;
                c.Round(x0 + w - pw - 18, y0 + h / 2 - 15, pw, 30, 15, WithA(sc, 0.2f));
                float pulse = ag.Status == AgentStatus.NeedsYou ? 0.6f + 0.4f * MathF.Sin(m.Clock * 6) : 1;
                c.Circle(x0 + w - pw - 2, y0 + h / 2, 5, WithA(Rgba(ag.Color), pulse));
                c.Text(ag.StatusText, x0 + w - 30, y0 + h / 2 - 9, 12.5f, sc, FontWeight.SemiBold, 1);
                break;
            }
            case AlertKind.Allowed:
                c.Text("Allowed", x0 + 80, y0 + 17, 15, White, FontWeight.SemiBold);
                c.Text("Claude continues: npm run build", x0 + 80, y0 + 41, 13, Secondary);
                c.Circle(x0 + w - 36, y0 + h / 2, 13, Rgba(Green));
                c.Line(x0 + w - 42, y0 + h / 2, x0 + w - 37.5f, y0 + h / 2 + 4.5f, 2.6f, White);
                c.Line(x0 + w - 37.5f, y0 + h / 2 + 4.5f, x0 + w - 30, y0 + h / 2 - 4, 2.6f, White);
                break;
            default:
                c.Text("Claude Code", x0 + 80, y0 + 17, 15, White, FontWeight.SemiBold);
                c.Text("island-app · wants to run", x0 + 168, y0 + 19, 12, Tertiary);
                c.Round(x0 + 80, y0 + 42, 140, 22, 7, Fill);
                c.Text("npm run build", x0 + 88, y0 + 44, 12.5f, Rgba(Orange), FontWeight.SemiBold);
                c.Round(x0 + w - 172, y0 + 24, 76, 32, 16, WhiteA(0.14f));
                c.Text("Deny", x0 + w - 134, y0 + 30, 13.5f, White, FontWeight.SemiBold, 0.5f);
                c.Round(x0 + w - 90, y0 + 24, 76, 32, 16, Rgba(Blue));
                c.Text("Allow", x0 + w - 52, y0 + 30, 13.5f, White, FontWeight.SemiBold, 0.5f);
                break;
        }
    }

    static void Controls(Canvas c, IslandModel m, float cx, float cy, float scale)
    {
        float hit = 40 * scale;
        (string id, float dx)[] buttons = [("prev", -64), ("play", 0), ("next", 64)];
        foreach (var (id, dx) in buttons)
        {
            float bx = cx + dx * scale;
            if (m.Hit(id, bx - hit / 2, cy - hit / 2, hit, hit)) c.Circle(bx, cy, hit / 2, WhiteA(0.12f));
        }
        c.SkipGlyph(cx - 64 * scale, cy, 20 * scale, White, false);
        if (m.Playing) c.PauseGlyph(cx, cy, 24 * scale, White);
        else c.PlayGlyph(cx + 2, cy, 24 * scale, White);
        c.SkipGlyph(cx + 64 * scale, cy, 20 * scale, White, true);
    }

    static void Scrubber(Canvas c, IslandModel m, float x, float y, float w, bool labels)
    {
        float p = m.TrackPos / m.Song.Length;
        float tx = labels ? x + 40 : x, tw = labels ? w - 80 : w;
        c.Round(tx, y, tw, 4.5f, 2.25f, WhiteA(0.18f));
        c.Round(tx, y, MathF.Max(4.5f, tw * p), 4.5f, 2.25f, White);
        if (!labels) return;
        c.Text(Time(m.TrackPos), x, y - 7.5f, 11.5f, Secondary, FontWeight.SemiBold);
        c.Text("-" + Time(m.Song.Length - m.TrackPos), x + w, y - 7.5f, 11.5f, Secondary, FontWeight.SemiBold, 1f);
    }

    static void MediaExpanded(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        m.Hit("expand", x0 + 16, y0 + 16, 260, 76);
        m.Hit("bloub", x0 + w - 64, y0 + 12, 48, 52);
        Art(c, m, x0 + 22, y0 + 22, 64, 14);
        c.Text(m.Song.Title, x0 + 102, y0 + 26, 17, White, FontWeight.SemiBold);
        c.Text(m.Song.Artist, x0 + 102, y0 + 50, 13.5f, Secondary);
        Waveform(c, m, x0 + 130 + c.Measure(m.Song.Artist, 13.5f), y0 + 60, 12, 2.4f, 2f);
        Scrubber(c, m, x0 + 22, y0 + 112, w - 44, true);
        Controls(c, m, x0 + w / 2, y0 + 160, 1f);
    }

    /// <summary>A card; returns true when hovered (when it is clickable).</summary>
    static bool Card(Canvas c, IslandModel m, float x, float y, float w, float h, string title, string? meta = null, string? hit = null)
    {
        bool hover = hit != null && m.Hit(hit, x, y, w, h);
        c.Round(x, y, w, h, 22, WhiteA(hover ? 0.115f : 0.075f));
        c.Text(title, x + 16, y + 13, 13, WhiteA(0.92f), FontWeight.SemiBold);
        if (meta != null) c.Text(meta, x + w - 16, y + 14, 11.5f, Tertiary, FontWeight.Regular, 1f);
        return hover;
    }

    static void PillButton(Canvas c, IslandModel m, string id, string label, float x, float y, float w, float h, uint? fill)
    {
        bool hover = m.Hit(id, x, y, w, h);
        var col = fill is { } f ? Rgba(f, hover ? 1f : 0.88f) : WhiteA(hover ? 0.22f : 0.14f);
        c.Round(x, y, w, h, h / 2, col);
        c.Text(label, x + w / 2, y + h / 2 - 9, 12.5f, White, FontWeight.SemiBold, 0.5f);
    }

    static void Check(Canvas c, float cx, float cy, bool done, bool hover)
    {
        if (done)
        {
            c.Circle(cx, cy, 8, Rgba(Green));
            c.Line(cx - 3.5f, cy, cx - 1, cy + 2.5f, 2, White);
            c.Line(cx - 1, cy + 2.5f, cx + 3.5f, cy - 2.5f, 2, White);
        }
        else c.StrokeCircle(cx, cy, 7.5f, 1.6f, WhiteA(hover ? 0.75f : 0.4f));
    }

    static void Full(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        Header(c, m, x0, y0, w);

        // Panel cross-fade + small rise when switching tabs.
        float f = Math.Clamp(m.TabFade.Value, 0, 1);
        float py = y0 + 62 + (1 - f) * 10;
        c.Ctx.PushLayer(new Vortice.Direct2D1.LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
            MaskTransform = Matrix3x2.Identity,
            Opacity = f,
        }, null);
        float pw = w - 32, ph = h - 62 - 16, px = x0 + 16;
        if (m.Tabs.Editing) Library(c, m, px, py, pw, ph);
        else switch (m.Tabs.Selected)
        {
            case "overview": Overview(c, m, px, py, pw, ph); break;
            case "agents": Agents(c, m, px, py, pw, ph); break;
            case "usage": Usage(c, m, px, py, pw, ph); break;
            case "clipboard": Clipboard(c, m, px, py, pw, ph); break;
            case "system": SystemPanel(c, m, px, py, pw, ph); break;
            case "projects": ProjectsPanel(c, m, px, py, pw, ph); break;
            case "media": MediaPanel(c, m, px, py, pw, ph); break;
            case "focus": FocusPanel(c, m, px, py, pw, ph); break;
            case "tasks": TasksPanel(c, m, px, py, pw, ph); break;
            case "calendar": CalendarPanel(c, m, px, py, pw, ph); break;
            case "timer": TimerPanel(c, m, px, py, pw, ph); break;
            case "notes": NotesPanel(c, m, px, py, pw, ph); break;
            case "downloads": DownloadsPanel(c, m, px, py, pw, ph); break;
            case "clocks": ClocksPanel(c, m, px, py, pw, ph); break;
            case "network": NetworkPanel(c, m, px, py, pw, ph); break;
            case "battery": BatteryPanel(c, m, px, py, pw, ph); break;
            case "servers": ServersPanel(c, m, px, py, pw, ph); break;
        }
        c.Ctx.PopLayer();

        // Toast
        float age = m.Clock - m.ToastTime;
        if (age < 1.8f)
        {
            float a = Math.Clamp(MathF.Min(age / 0.15f, (1.8f - age) / 0.3f), 0, 1);
            float tw2 = c.Measure(m.Toast, 13, FontWeight.SemiBold) + 32;
            float ty = y0 + h - 52 + (1 - a) * 8;
            c.Round(x0 + w / 2 - tw2 / 2, ty, tw2, 32, 16, Canvas.WithA(Rgba(0x2C2C2E), a));
            c.Text(m.Toast, x0 + w / 2, ty + 7, 13, WhiteA(a), FontWeight.SemiBold, 0.5f);
        }
    }

    static void Overview(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        float pad = 16, cw = (w - pad) / 2, ch = 166;

        // Claude Code: your real sessions (click → the team)
        var team = m.Team.Live.Take(3).ToList();
        var lim = m.Limits;
        string meta = lim.Known ? $"5h {lim.FiveHour * 100:0}% · week {lim.Week * 100:0}%"
                    : m.Team.Count == 0 ? "no sessions" : m.Team.Count == 1 ? "1 session" : $"{m.Team.Count} sessions";
        Card(c, m, x, y, cw, ch, "Claude Code", meta, "agent");
        if (team.Count == 0)
        {
            c.Text("Nothing running", x + 16, y + 52, 13, White, FontWeight.SemiBold);
            c.Text("Start claude in a terminal — it shows up here live", x + 16, y + 74, 12, Tertiary, maxWidth: cw - 32);
        }
        for (int i = 0; i < team.Count; i++)
        {
            var ag = team[i];
            float ry = y + 44 + i * 30;
            float pulse = ag.Status == AgentStatus.Working ? 0.55f + 0.45f * MathF.Sin(m.Clock * 5 + i) : 1f;
            c.Circle(x + 22, ry + 8, 4.5f, Canvas.WithA(Rgba(ag.Color), pulse));
            c.Text(ag.Name, x + 34, ry, 13, White, FontWeight.SemiBold, 0, cw * 0.45f);
            float nameW = MathF.Min(c.Measure(ag.Name, 13, FontWeight.SemiBold), cw * 0.45f);
            string state = ag.Status == AgentStatus.Working ? ag.Activity : ag.StatusText;
            c.Text(state, x + cw - 16, ry + 1, 12, ag.Status == AgentStatus.NeedsYou ? Rgba(Orange) : ag.Status == AgentStatus.Done ? Rgba(Green) : Secondary,
                FontWeight.Regular, 1f, cw - 34 - nameW - 30);
        }
        c.Text(m.Team.Summary, x + 16, y + 138, 11.5f, m.Team.AnyNeeds ? Rgba(Orange) : Tertiary, FontWeight.SemiBold);
        c.Text("Open team ›", x + cw - 16, y + 138, 11.5f, Secondary, FontWeight.SemiBold, 1f);

        // Now playing
        float bx = x + cw + pad;
        Card(c, m, bx, y, cw, ch, "Now Playing", "Spotify");
        Art(c, m, bx + 16, y + 42, 54, 12);
        c.Text(m.Song.Title, bx + 82, y + 46, 15, White, FontWeight.SemiBold);
        c.Text(m.Song.Artist, bx + 82, y + 68, 12.5f, Secondary);
        Waveform(c, m, bx + cw - 18, y + 58, 14, 2.4f, 2f);
        Scrubber(c, m, bx + 16, y + 112, cw - 32, false);
        Controls(c, m, bx + cw / 2, y + 140, 0.75f);

        // Second row
        float y2 = y + ch + pad, h2 = h - ch - pad, w3 = (w - pad * 2) / 3;
        Card(c, m, x, y2, w3, h2, "Focus", m.FocusPaused ? "Paused" : "2 of 4", "focus");
        float p = 1 - m.FocusElapsed / m.FocusLength;
        var ring = m.FocusPaused ? WhiteA(0.45f) : Rgba(Green);
        c.Ring(x + 60, y2 + 84, 34, 5, p, WithA(Rgba(Green), 0.2f), ring);
        m.FocusDigits.Draw(c, x + 39, y2 + 73, 16, White, FontWeight.SemiBold);
        c.Text("Deep work", x + 112, y2 + 64, 13, White, FontWeight.SemiBold);
        c.Text(m.FocusPaused ? "Click to resume" : "Click to pause", x + 112, y2 + 84, 11.5f, Tertiary);

        float dx = x + w3 + pad;
        var tasks = m.Tabs.Tasks;
        int open = tasks.Count(t => !t.Done);
        Card(c, m, dx, y2, w3, h2, "Today", tasks.Count == 0 ? null : $"{tasks.Count - open}/{tasks.Count}", tasks.Count == 0 ? "tab:tasks" : null);
        if (tasks.Count == 0) c.Text("No tasks — add some in Tasks", dx + 16, y2 + 50, 12, Tertiary, FontWeight.Regular, 0, w3 - 32);
        for (int i = 0; i < Math.Min(3, tasks.Count); i++)
        {
            float ty = y2 + 44 + i * 27;
            bool hover = m.Hit("tasks:toggle:" + i, dx + 8, ty - 4, w3 - 16, 26);
            if (hover) c.Round(dx + 8, ty - 4, w3 - 16, 26, 9, WhiteA(0.07f));
            Check(c, dx + 24, ty + 9, tasks[i].Done, hover);
            c.Text(tasks[i].Text, dx + 40, ty, 13, tasks[i].Done ? Tertiary : White, FontWeight.Regular, 0, w3 - 56);
        }

        float sx = dx + w3 + pad;
        Card(c, m, sx, y2, w3, h2, "System", "RTX 2070 S", "tab:system");
        var stats = Stats(m);
        for (int i = 0; i < 3; i++)
        {
            float sy = y2 + 46 + i * 26;
            c.Text(stats[i].k, sx + 16, sy - 6, 11.5f, Tertiary, FontWeight.SemiBold);
            c.Round(sx + 52, sy, w3 - 140, 5, 2.5f, WhiteA(0.14f));
            c.Round(sx + 52, sy, (w3 - 140) * stats[i].v, 5, 2.5f, Rgba(stats[i].col));
            c.Text(stats[i].label, sx + w3 - 16, sy - 6, 11.5f, Secondary, FontWeight.Regular, 1f);
        }
    }

    static (string k, float v, string label, uint col)[] Stats(IslandModel m) =>
    [
        ("CPU", 0.23f + 0.04f * MathF.Sin(m.Clock * 1.3f), "23%", Blue),
        ("GPU", 0.41f + 0.05f * MathF.Sin(m.Clock * 0.9f), "41% · 62°", Green),
        ("RAM", 0.58f, "18.6 GB", Orange),
        ("VRAM", 0.47f + 0.02f * MathF.Sin(m.Clock * 0.5f), "3.8 / 8 GB", 0x5E5CE6),
        ("Disk", 0.71f, "C: 142 GB free", 0x64D2FF),
    ];

    static void Agents(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var team = m.Team.Live.ToList();
        c.Text("Claude Code sessions on this PC", x + 4, y + 2, 13, Secondary, FontWeight.SemiBold);
        if (team.Count == 0)
        {
            c.Text("No sessions running — start claude in a terminal", x + w / 2, y + h / 2 - 10, 14, Secondary, FontWeight.SemiBold, 0.5f);
            return;
        }
        float rowH = MathF.Min(62, (h - 30) / team.Count);
        for (int i = 0; i < team.Count; i++)
        {
            var ag = team[i];
            float ry = y + 28 + i * rowH, bh = rowH - 8;
            bool needs = ag.Status == AgentStatus.NeedsYou;
            bool hov = m.Hit($"ag:k{ag.Key}", x, ry, w - 110, bh);
            c.Round(x, ry, w, bh, MathF.Min(18, bh / 2), WhiteA(needs ? 0.11f : hov ? 0.09f : 0.06f));
            float pulse = ag.Status == AgentStatus.Working ? 0.55f + 0.45f * MathF.Sin(m.Clock * 5 + i) : 1f;
            c.Circle(x + 22, ry + bh / 2, 6, Canvas.WithA(Rgba(ag.Color), pulse));
            c.Text(ag.Name, x + 38, ry + bh / 2 - 18, 14, White, FontWeight.SemiBold, 0, 220);
            c.Text(ag.Activity, x + 38, ry + bh / 2 + 2, 12, needs ? Rgba(Orange) : Secondary, maxWidth: w - 330);
            c.Text(ag.StatusText, x + w - 120, ry + bh / 2 - 9, 12, Rgba(ag.StatusColor), FontWeight.SemiBold, 1f);
            PillButton(c, m, $"ag:goto:k{ag.Key}", "Open", x + w - 96, ry + bh / 2 - 15, 80, 30, needs ? Blue : null);
        }
    }

    // ---------------------------------------------------------------- Usage: your Claude Code activity

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalSeconds < 60 ? "updated now" : d.TotalMinutes < 60 ? $"updated {(int)d.TotalMinutes}m ago" : $"updated {(int)d.TotalHours}h ago";
    }

    static uint LimitColor(float used) => used >= 0.9f ? Red : used >= 0.75f ? Orange : Blue;

    /// <summary>One limit: a ring with the % used, its name and when it resets.</summary>
    static void LimitRing(Canvas c, float x, float y, float w, string name, float? used, DateTime? resets, bool weekly, float grow)
    {
        float r = 30, cx = x + r + 4, cy = y + r + 6;
        if (used is { } u)
        {
            c.Ring(cx, cy, r, 7, u * grow, WhiteA(0.1f), Rgba(LimitColor(u)));
            c.Text($"{u * 100:0}%", cx, cy - 10, 15, White, FontWeight.SemiBold, 0.5f);
        }
        else
        {
            c.Ring(cx, cy, r, 7, 0, WhiteA(0.1f), WhiteA(0.3f));
            c.Text("—", cx, cy - 10, 15, Tertiary, FontWeight.SemiBold, 0.5f);
        }
        float tx = x + r * 2 + 18, tw = w - r * 2 - 18;
        c.Text(name, tx, y + 8, 13.5f, White, FontWeight.SemiBold, 0, tw);
        if (resets is { } t)
        {
            var left = t - DateTime.Now;
            string when = weekly && left.TotalHours >= 24 ? t.ToString("ddd HH:mm") : t.ToString("HH:mm");
            string inS = left.TotalHours >= 24 ? $"in {(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"in {(int)left.TotalHours}h {left.Minutes:00}m" : $"in {Math.Max(0, left.Minutes)}m";
            c.Icon('', tx + 5, y + 36, 10, Secondary); // ↻ resets
            c.Text(when, tx + 14, y + 28, 11.5f, Secondary, FontWeight.SemiBold, 0, tw - 14);
            c.Text(inS, tx, y + 44, 11.5f, Tertiary, FontWeight.Regular, 0, tw);
        }
        else c.Text(used == null ? "checking…" : "", tx, y + 28, 11.5f, Tertiary);
        if (used is { } u2)
            c.Text(u2 >= 0.9f ? "Almost out" : u2 >= 0.75f ? "Getting close" : $"{(1 - u2) * 100:0}% left", x + 4, y + r * 2 + 22, 11.5f,
                u2 >= 0.75f ? Rgba(LimitColor(u2)) : Tertiary, FontWeight.SemiBold);
    }

    static string Tok(long n) => n >= 1_000_000 ? $"{n / 1e6:0.0}M" : n >= 1000 ? $"{n / 1e3:0}k" : n.ToString();
    static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(0, t.Minutes)}m";
    static readonly uint[] ProjectColors = [0x0A84FF, 0xBF5AF2, 0x30D158, 0xFF9F0A, 0xFF375F, 0x64D2FF];

    static void Usage(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var s = m.Usage.Stats;
        float g = Math.Clamp(m.TabFade.Value, 0, 1), grow = 1 - (1 - g) * (1 - g); // bars and ring grow in
        if (!s.Ready)
        {
            c.Text("Reading your Claude Code history…", x + w / 2, y + h / 2 - 10, 14, Secondary, FontWeight.SemiBold, 0.5f);
            return;
        }
        float pad = 16, h1 = 150, aw = 336, bw = w - aw - pad;

        // ---- your real plan limits (5-hour + weekly), as /usage shows them
        var lim = m.Limits;
        string upd = lim.Probing && !lim.Known ? "checking…" : lim.Known ? Ago(lim.UpdatedAt) : lim.Error ?? "checking…";
        Card(c, m, x, y, aw, h1, "Plan limits", upd, "limits");
        LimitRing(c, x + 16, y + 44, aw / 2 - 16, "5-hour", lim.FiveHour, lim.FiveResets, false, grow);
        LimitRing(c, x + aw / 2 + 4, y + 44, aw / 2 - 16, "Weekly", lim.Week, lim.WeekResets, true, grow);

        // ---- today
        float bx = x + aw + pad;
        Card(c, m, bx, y, bw, h1, "Today", s.Streak > 1 ? $"{s.Streak}-day streak" : s.TodaySessions == 1 ? "1 session" : $"{s.TodaySessions} sessions");
        (string v, string k)[] tiles =
        [
            (Tok(s.TodayTokens), "tokens"),
            (Dur(s.TodayActive), "active"),
            (s.TodayFiles.ToString(), s.TodayFiles == 1 ? "file edited" : "files edited"),
            (s.TodayCommands.ToString(), s.TodayCommands == 1 ? "command" : "commands"),
        ];
        float tw = (bw - 32) / tiles.Length;
        for (int i = 0; i < tiles.Length; i++)
        {
            c.Text(tiles[i].v, bx + 16 + i * tw, y + 40, 18, White, FontWeight.SemiBold, 0, tw - 4);
            c.Text(tiles[i].k, bx + 16 + i * tw, y + 64, 11, Tertiary, FontWeight.SemiBold, 0, tw - 4);
        }
        // hour by hour
        float cx0 = bx + 16, cw = bw - 32, cy = y + 92, chH = 34, slot = cw / 24;
        long max = Math.Max(1, s.Hours.Max());
        int nowH = DateTime.Now.Hour;
        for (int hr = 0; hr < 24; hr++)
        {
            float v = s.Hours[hr] / (float)max, bh = MathF.Max(2, chH * v * grow);
            var col = hr == nowH ? Rgba(Blue) : hr < nowH ? WhiteA(s.Hours[hr] > 0 ? 0.42f : 0.1f) : WhiteA(0.06f);
            c.Round(cx0 + hr * slot + 1, cy + chH - bh, slot - 3, bh, MathF.Min(2.5f, (slot - 3) / 2), col);
        }
        foreach (int hr in new[] { 0, 6, 12, 18 })
            c.Text(hr == 0 ? "12am" : hr == 12 ? "12pm" : hr < 12 ? $"{hr}am" : $"{hr - 12}pm", cx0 + hr * slot, cy + chH + 3, 9.5f, Tertiary);

        // ---- projects today
        float y2 = y + h1 + pad, h2 = h - h1 - pad, cw2 = (w - pad) / 2;
        Card(c, m, x, y2, cw2, h2, "Projects", "today");
        if (s.Projects.Count == 0) c.Text("No Claude activity yet today", x + 16, y2 + 50, 12.5f, Tertiary);
        long pmax = s.Projects.Count > 0 ? s.Projects[0].Tokens : 1;
        var live = m.Team.Live.ToList();
        for (int i = 0; i < Math.Min(4, s.Projects.Count); i++)
        {
            var (name, tokens) = s.Projects[i];
            float py = y2 + 44 + i * 28;
            var agent = live.FirstOrDefault(a => a.Cwd.Contains(name, StringComparison.OrdinalIgnoreCase));
            uint col = agent?.Color ?? ProjectColors[i % ProjectColors.Length];
            c.Circle(x + 22, py + 8, 4.5f, Rgba(col));
            c.Text(name, x + 34, py, 12.5f, White, FontWeight.SemiBold, 0, 120);
            float barX = x + 162, barW = cw2 - 162 - 62;
            c.Round(barX, py + 6, barW, 5, 2.5f, WhiteA(0.1f));
            c.Round(barX, py + 6, MathF.Max(5, barW * (tokens / (float)pmax) * grow), 5, 2.5f, Rgba(col));
            c.Text(Tok(tokens), x + cw2 - 16, py, 12, Secondary, FontWeight.SemiBold, 1f);
        }

        // ---- this week
        float dx = x + cw2 + pad;
        Card(c, m, dx, y2, cw2, h2, "This week", $"{Tok(s.Days.Sum())} tokens");
        long dmax = Math.Max(1, s.Days.Max());
        float dslot = (cw2 - 32) / 7, dTop = y2 + 44, dH = 62;
        for (int d = 0; d < 7; d++)
        {
            var day = DateTime.Now.Date.AddDays(d - 6);
            float v = s.Days[d] / (float)dmax, bh = MathF.Max(3, dH * v * grow);
            float bxd = dx + 16 + d * dslot + dslot * 0.2f, bwd = dslot * 0.6f;
            c.Round(bxd, dTop + dH - bh, bwd, bh, MathF.Min(5, bwd / 2), d == 6 ? Rgba(Blue) : WhiteA(s.Days[d] > 0 ? 0.32f : 0.08f));
            c.Text(day.ToString("ddd")[..1], bxd + bwd / 2, dTop + dH + 4, 10.5f, d == 6 ? White : Tertiary, FontWeight.SemiBold, 0.5f);
        }
        long total = s.Models.Sum(mm => mm.Tokens);
        if (total > 0)
            c.Text(string.Join("  ·  ", s.Models.Take(3).Select(mm => $"{mm.Model} {mm.Tokens * 100 / total}%")), dx + 16, y2 + h2 - 26, 11.5f, Secondary, FontWeight.SemiBold, 0, cw2 - 32);
    }

    static void Clipboard(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        (string kind, string text, string ago, uint col)[] clips =
        [
            ("Aa", "dotnet build -c Release -nologo", "now", Blue),
            ("#", "#FF5FA2", "2m", 0xFF5FA2),
            ("↗", "github.com/jeremy-prt/bloub", "8m", Green),
            ("Aa", "The island folds into a line over Chrome tabs…", "21m", Blue),
            ("▣", "Screenshot 14.02.png  ·  1920×1080", "34m", Orange),
        ];
        for (int i = 0; i < clips.Length; i++)
        {
            float ry = y + i * 64;
            bool hover = m.Hit("clip:" + i, x, ry, w, 56);
            c.Round(x, ry, w, 56, 18, WhiteA(hover ? 0.12f : 0.06f));
            if (clips[i].kind == "#") c.Round(x + 14, ry + 12, 32, 32, 9, Rgba(clips[i].col));
            else
            {
                c.Round(x + 14, ry + 12, 32, 32, 9, Rgba(clips[i].col, 0.22f));
                c.Text(clips[i].kind, x + 30, ry + 17, 14, Rgba(clips[i].col), FontWeight.Bold, 0.5f);
            }
            c.Text(clips[i].text, x + 60, ry + 17, 14, White, FontWeight.SemiBold);
            c.Text(hover ? "Click to copy" : clips[i].ago, x + w - 16, ry + 19, 12, hover ? Rgba(Blue) : Tertiary, FontWeight.SemiBold, 1f);
        }
    }

    static void SystemPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var stats = Stats(m);
        for (int i = 0; i < stats.Length; i++)
        {
            float ry = y + i * 52;
            c.Round(x, ry, w, 44, 16, WhiteA(0.06f));
            c.Text(stats[i].k, x + 18, ry + 12, 13, White, FontWeight.SemiBold);
            c.Round(x + 90, ry + 19, w - 300, 6, 3, WhiteA(0.14f));
            c.Round(x + 90, ry + 19, (w - 300) * stats[i].v, 6, 3, Rgba(stats[i].col));
            c.Text(stats[i].label, x + w - 18, ry + 12, 13, Secondary, FontWeight.SemiBold, 1f);
        }
        float ny = y + stats.Length * 52;
        c.Text("↓ 12.4 MB/s   ↑ 1.1 MB/s   ·   ping 18 ms", x + 18, ny + 6, 13, Secondary, FontWeight.SemiBold);
    }

    static void Chip(Canvas c, IslandModel m, string id, string label, float x, float y, bool active = false)
    {
        float w = c.Measure(label, 12.5f, FontWeight.SemiBold) + 26;
        bool hover = m.Hit(id, x, y, w, 30);
        c.Round(x, y, w, 30, 15, active ? Rgba(0xBF5AF2, hover ? 1f : 0.85f) : WhiteA(hover ? 0.2f : 0.11f));
        c.Text(label, x + 13, y + 6, 12.5f, White, FontWeight.SemiBold);
    }

    static float ChipW(Canvas c, string label) => c.Measure(label, 12.5f, FontWeight.SemiBold) + 26 + 8;

    /// <summary>Bloub gallery: every animation state, mood and body shape of the mascot.</summary>
    static void Gallery(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        var b = m.Bloub;
        m.Hit("bloub", x0 + 50, y0 + 40, 140, 144);
        var (_, name, use) = BloubHost.Catalogue[b.GalleryIndex];
        float tx = x0 + 236;
        c.Text($"BLOUB · STATE {b.GalleryIndex + 1} / {BloubHost.Catalogue.Length}", tx, y0 + 26, 11.5f, Tertiary, FontWeight.SemiBold);
        c.Text(name, tx, y0 + 44, 26, White, FontWeight.SemiBold);
        c.Text(use, tx, y0 + 82, 13, Secondary);

        // State dots — click one to jump to it.
        for (int i = 0; i < BloubHost.Catalogue.Length; i++)
        {
            float dx = tx + i * 18;
            bool hover = m.Hit($"g:pick:{i}", dx - 5, y0 + 110, 16, 18);
            bool cur = i == b.GalleryIndex;
            c.Round(dx, y0 + 116, cur ? 14 : 6, 6, 3, cur ? Rgba(0xBF5AF2) : WhiteA(hover ? 0.6f : 0.25f));
        }

        float cy = y0 + 148, cx = tx;
        Chip(c, m, "g:prev", "‹ Prev", cx, cy); cx += ChipW(c, "‹ Prev");
        Chip(c, m, "g:next", "Next ›", cx, cy); cx += ChipW(c, "Next ›");
        string auto = b.GalleryAuto ? "Auto • on" : "Auto • off";
        Chip(c, m, "g:auto", auto, cx, cy, b.GalleryAuto);
        cy += 38; cx = tx;
        string mood = "Mood: " + BloubHost.Moods[b.MoodIndex].Name;
        Chip(c, m, "g:mood", mood, cx, cy); cx += ChipW(c, mood);
        Chip(c, m, "g:shape", "Shape: " + BloubHost.Shapes[b.ShapeIndex].Name, cx, cy);
    }

    static void Command(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        int n = m.CommandChars;
        string typed = IslandModel.CommandText[..n];
        float tx = x0 + 58;
        if (n == 0) c.Text("Search, run, or ask Claude…", tx, y0 + 15, 18, Tertiary);
        else c.Text(typed, tx, y0 + 15, 18, White);
        float caretX = tx + (n == 0 ? 0 : c.Measure(typed, 18)) + 1.5f;
        if ((m.ModeTime * 1.9f) % 1 < 0.6f || n < IslandModel.CommandText.Length && n > 0)
            c.Round(caretX, y0 + 17, 2, 23, 1, Rgba(Blue));
        c.Text("Alt+Space", x0 + w - 22, y0 + 19, 12, Tertiary, FontWeight.SemiBold, 1f);

        if (n < 4) return;
        (string title, string sub, uint col)[] rows =
        [
            ("Visual Studio Code", "Application", 0x0A84FF),
            ("Coding workspace", "Layout · VS Code + Terminal + Browser", 0x5E5CE6),
            ("Ask Claude: “" + typed + "”", "AI", 0xFF9F0A),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            float ry = y0 + 58 + i * 46;
            if (i == 0) c.Round(x0 + 8, ry, w - 16, 42, 14, WhiteA(0.1f));
            c.Round(x0 + 20, ry + 7, 28, 28, 8, Rgba(rows[i].col));
            c.Text(rows[i].title, x0 + 60, ry + 4, 14, White, FontWeight.SemiBold);
            c.Text(rows[i].sub, x0 + 60, ry + 22, 11.5f, Tertiary);
            if (i == 0) c.Text("↵", x0 + w - 26, ry + 10, 15, Tertiary, FontWeight.SemiBold, 1f);
        }
    }
}
