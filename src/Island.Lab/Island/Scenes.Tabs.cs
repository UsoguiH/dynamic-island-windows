using System.Numerics;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Island.Lab.Platform;
using Island.Lab.Render;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

/// <summary>The dashboard's header (your tabs, your team, customize) and the panels of the newer tabs.</summary>
public static partial class Scenes
{
    const float TabsLeft = 78, TabY = 18, TabH = 28, IconTabW = 34, TabGap = 4;

    /// <summary>Width of the pencil / "Done" control at the header's right edge.</summary>
    static float EditW(IslandModel m) => 32 + 40 * Math.Clamp(m.Tabs.EditT.Value, 0, 1);

    /// <summary>Where the team of mini Bloubs docks in the dashboard header (relative to the island's left edge).</summary>
    public static (float Left, float Right, int Shown) TeamDock(IslandModel m, float w)
    {
        int n = Math.Min(m.Team.Count, 5);
        float right = w - 22 - EditW(m) - 10;
        return (right - (n > 0 ? 18 + n * 16 : 82), right, n);
    }

    // ================================================================ header

    static void Header(Canvas c, IslandModel m, float x0, float y0, float w)
    {
        var tb = m.Tabs;
        float edit = Math.Clamp(tb.EditT.Value, 0, 1);
        if (m.Hit("bloub", x0 + 18, y0 + 12, 44, 40)) c.Circle(x0 + 40, y0 + 32, 21, WhiteA(0.08f));

        // ---- right: customize (pencil → Done)
        float ew = EditW(m), ex = x0 + w - 22 - ew;
        bool eh = m.Hit("tabs:edit", ex, y0 + 16, ew, 32);
        if (!tb.Discovered && edit < 0.01f)
        {
            float pulse = (m.Clock * 0.8f) % 1;
            c.StrokeCircle(ex + 16, y0 + 32, 16 + pulse * 9, 1.5f, WithA(Rgba(0x0A84FF), 0.6f * (1 - pulse)));
        }
        c.Round(ex, y0 + 16, ew, 32, 16, edit > 0.5f ? Rgba(0x0A84FF, eh ? 1 : 0.9f) : WhiteA(eh ? 0.18f : 0.1f));
        c.Icon('', ex + 16, y0 + 32, 13, WhiteA(1 - edit));
        if (edit > 0.05f) c.Text("Done", ex + ew / 2, y0 + 23, 13, WhiteA(edit), FontWeight.SemiBold, 0.5f);
        if (eh && edit < 0.5f) Tooltip(c, "Customize tabs", ex + ew / 2, y0 + 54);

        // ---- right: your team (the mini Bloubs are drawn on top by the team itself)
        var (dl, dr, shown) = TeamDock(m, w);
        bool th = m.Hit("tabs:team", x0 + dl, y0 + 16, dr - dl, 32);
        c.Round(x0 + dl, y0 + 16, dr - dl, 32, 16, WhiteA(th ? 0.16f : 0.08f));
        if (m.Team.AnyNeeds)
        {
            float p = 0.5f + 0.5f * MathF.Sin(m.Clock * 5);
            c.StrokeRound(x0 + dl + 0.5f, y0 + 16.5f, dr - dl - 1, 31, 15.5f, 1.4f, WithA(Rgba(Orange), 0.45f + 0.45f * p));
        }
        if (shown == 0)
        {
            c.Icon('', x0 + dl + 20, y0 + 32, 12, WhiteA(0.55f));
            c.Text("Agents", x0 + dl + 33, y0 + 23, 12.5f, WhiteA(0.55f), FontWeight.SemiBold);
        }
        if (th) Tooltip(c, m.Team.Count == 0 ? "Your Claude agents" : m.Team.Summary, x0 + (dl + dr) / 2, y0 + 54);

        // ---- your tabs
        float avail = dl - 12 - TabsLeft;
        var ids = tb.Enabled;
        float FullW(string id) => c.Measure(TabBar.Def(id).Name, 13, FontWeight.SemiBold) + 24;
        float sumFull = ids.Sum(FullW) + TabGap * (ids.Count - 1);
        bool compact = sumFull > avail;
        var targetX = new Dictionary<string, float>();
        var targetW = new Dictionary<string, float>();
        float tx = TabsLeft;
        foreach (var id in ids)
        {
            float tw = compact && id != tb.Selected ? IconTabW : FullW(id);
            targetX[id] = tx; targetW[id] = tw;
            tx += tw + TabGap;
        }
        // dragging (customize mode): the grabbed tab follows the cursor, the others make room
        tb.Drag(m.Cursor.X - x0, id => targetX.TryGetValue(id, out var x) ? x + targetW[id] / 2 : 0);

        foreach (var id in ids)
        {
            var s = tb[id];
            if (!s.Placed) { s.X.Snap(targetX[id]); s.W.Snap(targetW[id] * 0.4f); s.Placed = true; }
            if (tb.Dragging != id) s.X.To(targetX[id]);
            s.W.To(targetW[id]);
        }

        // selection pill
        if (ids.Contains(tb.Selected))
        {
            var sel = tb[tb.Selected];
            float px = tb.Dragging == tb.Selected ? sel.X.Value : targetX[tb.Selected];
            if (!m.TabPillReady) { m.TabPillX.Snap(px); m.TabPillW.Snap(targetW[tb.Selected]); m.TabPillReady = true; }
            m.TabPillX.To(px); m.TabPillW.To(targetW[tb.Selected]);
            c.Round(x0 + m.TabPillX.Value, y0 + TabY, m.TabPillW.Value, TabH, TabH / 2, WhiteA(0.14f * (1 - 0.3f * edit)));
        }

        int i = 0;
        foreach (var def in TabBar.Catalogue)
        {
            var s = tb[def.Id];
            float a = Math.Clamp(s.A.Value, 0, 1);
            if (a < 0.01f) continue;
            bool on = ids.Contains(def.Id);
            float x = x0 + s.X.Value, tw = MathF.Max(0, s.W.Value) * (on ? 1 : a);
            float wob = edit * MathF.Sin(m.Clock * 9 + i++ * 1.7f) * 0.9f;
            float y = y0 + TabY + wob;
            bool selected = def.Id == tb.Selected;
            bool hover = on && m.Hit("tab:" + def.Id, x, y0 + TabY, tw, TabH);
            if (edit > 0.01f)
            {
                c.Round(x, y, tw, TabH, TabH / 2, WhiteA((tb.Dragging == def.Id ? 0.2f : 0.06f) * edit * a));
                c.DashedRound(x + 0.5f, y + 0.5f, tw - 1, TabH - 1, TabH / 2, 1, WhiteA(0.28f * edit * a));
            }
            float full = FullW(def.Id);
            float label = Math.Clamp((tw - IconTabW) / MathF.Max(1, full - IconTabW), 0, 1);
            var ink = selected ? White : WhiteA(hover ? 0.75f : 0.42f);
            c.Ctx.PushAxisAlignedClip(new Rect(x, y0, MathF.Max(0, tw), 60), Vortice.Direct2D1.AntialiasMode.Aliased);
            if (label > 0.01f) c.Text(def.Name, x + 12, y + 4, 13, WithA(ink, ink.A * label * a), FontWeight.SemiBold);
            if (label < 0.99f) c.Icon(def.Icon, x + tw / 2, y + TabH / 2, 13, WithA(selected ? Rgba(def.Color) : ink, ink.A * (1 - label) * a));
            c.Ctx.PopAxisAlignedClip();
            if (hover && label < 0.5f && tb.Dragging == null) Tooltip(c, def.Name, x + tw / 2, y0 + 54);
        }
    }

    static void Tooltip(Canvas c, string text, float cx, float y)
    {
        float tw = c.Measure(text, 11.5f, FontWeight.SemiBold) + 18;
        c.Round(cx - tw / 2, y, tw, 22, 11, Rgba(0x3A3A3C, 0.96f));
        c.Text(text, cx, y + 3.5f, 11.5f, White, FontWeight.SemiBold, 0.5f);
    }

    // ================================================================ customize: the tab library

    // Bloub's orb in the library, relative to the island's top-left (the Face anchor flies there).
    const float LibCol = 196;
    public const float LibOrbX = 16 + LibCol / 2, LibOrbY = 62 + 128, LibOrbR = 50;

    static Color4 Mix(uint a, uint b, float t)
    {
        Color4 x = Rgba(a), y = Rgba(b);
        return new(x.R + (y.R - x.R) * t, x.G + (y.G - x.G) * t, x.B + (y.B - x.B) * t, 1);
    }

    static Color4 Lerp(Color4 a, Color4 b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    static float Smooth(float a, float b, float u) { float t = Math.Clamp((u - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>
    /// The tab library: Bloub in his orb on the left (with his moons, the status line and Done), your tabs as
    /// Quick Settings tiles on the right. Tapping a tile pins it (the colour spreads from your finger, Bloub
    /// winks) or unpins it (Bloub shrugs: "oh well").
    /// </summary>
    static void Library(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var tb = m.Tabs;
        float edit = Math.Clamp(tb.EditT.Value, 0, 1);

        // ---- left: caption, the orb's glow and floor, status, Done
        float cx = x + LibCol / 2, cy = y + 128;
        string cap = "Tab library", cnt = $"{tb.Enabled.Count} / {TabBar.MaxTabs}";
        float capW = c.Measure(cap, 13, FontWeight.SemiBold), cntW = c.Measure(cnt, 11, FontWeight.SemiBold) + 16;
        float gx0 = cx - (capW + 8 + cntW) / 2;
        c.Text(cap, gx0, y + 12, 13, WhiteA(0.62f), FontWeight.SemiBold);
        c.Round(gx0 + capW + 8, y + 13, cntW, 19, 9.5f, WhiteA(0.1f));
        c.Text(cnt, gx0 + capW + 8 + cntW / 2, y + 14.5f, 11, WhiteA(0.62f), FontWeight.SemiBold, 0.5f);

        c.Glow(cx, cy + 4, 96, 86, tb.GlowColor, 0.26f * edit);
        c.Glow(cx, cy + LibOrbR + 10, 68, 9, tb.GlowColor, 0.6f * edit);
        m.Hit("bloub", cx - LibOrbR, cy - LibOrbR, LibOrbR * 2, LibOrbR * 2);

        float st = Math.Clamp(tb.StatusT.Value, 0, 1);
        var ink = tb.StatusWarn ? Rgba(Orange, st) : WhiteA(0.55f * st);
        float sw = c.Measure(tb.Status, 12.5f, FontWeight.SemiBold) + 20, sy = y + 206 + (1 - st) * 5;
        c.Icon(tb.StatusIcon, cx - sw / 2 + 7, sy + 9, 12, ink);
        c.Text(tb.Status, cx - sw / 2 + 20, sy, 12.5f, ink, FontWeight.SemiBold);

        bool dh = m.Hit("tabs:edit", cx - 42, y + 242, 84, 32);
        c.Round(cx - 42, y + 242, 84, 32, 16, Rgba(dh ? 0x3D9BFFu : 0x0A84FFu));
        c.Text("Done", cx, y + 249, 13, White, FontWeight.SemiBold, 0.5f);

        // ---- right: the tiles, 3 per row
        const int cols = 3;
        int n = TabBar.Catalogue.Length, rows = (n + cols - 1) / cols;
        float gap = 8, left = x + LibCol + 16, gw = w - LibCol - 16;
        float tw = (gw - gap * (cols - 1)) / cols, th = MathF.Min(54, (h - gap * (rows - 1)) / rows);
        float top = y + (h - (rows * th + gap * (rows - 1))) / 2;
        bool full = tb.Enabled.Count >= TabBar.MaxTabs;
        var old = c.Ctx.Transform;
        for (int i = 0; i < n; i++)
        {
            var def = TabBar.Catalogue[i];
            var s = tb[def.Id];
            float on = Math.Clamp(s.On.Value, 0, 1), fill = Math.Clamp(s.Fill.Value, 0, 1);
            int order = tb.Enabled.IndexOf(def.Id);
            bool pinned = order >= 0;
            float tx = left + (i % cols) * (tw + gap) + s.Shake.Value * 0.02f, ty = top + (i / cols) * (th + gap);
            // tiles cascade in when the library opens
            float enter = Math.Clamp((tb.EditT.Value - i * 0.02f) / 0.6f, 0, 1);
            float pop = s.Pop.Value * (0.9f + 0.1f * enter);
            c.Ctx.Transform = Matrix3x2.CreateScale(pop, new Vector2(tx + tw / 2, ty + th / 2)) * old;
            bool hover = m.Hit("tabs:toggle:" + def.Id, tx, ty, tw, th);
            float alpha = enter * (full && !pinned ? 0.42f : 1);
            float r = th / 2 + (16 - th / 2) * on;      // a pill when off, rounder-square when on

            Color4 bg = Mix(def.Color, 0x2A292F, 0.93f), t80 = Mix(def.Color, 0xFFFFFF, 0.45f), t20 = Mix(def.Color, 0x000000, 0.7f);
            c.Round(tx, ty, tw, th, r, WithA(bg, alpha));
            if (hover) c.Round(tx, ty, tw, th, r, WhiteA(0.05f * alpha));
            if (fill > 0.001f)
            {
                if (fill > 0.999f) c.Round(tx, ty, tw, th, r, WithA(t80, alpha));
                else
                {
                    // the colour spreads out from where you clicked, clipped to the tile
                    var at = s.FillAt is { } f ? new Vector2(Math.Clamp(f.X, tx, tx + tw), Math.Clamp(f.Y, ty, ty + th)) : new Vector2(tx + tw / 2, ty + th / 2);
                    float far = MathF.Sqrt(MathF.Pow(MathF.Max(at.X - tx, tx + tw - at.X), 2) + MathF.Pow(MathF.Max(at.Y - ty, ty + th - at.Y), 2));
                    using var clip = c.Factory.CreateRoundedRectangleGeometry(new Vortice.Direct2D1.RoundedRectangle(new System.Drawing.RectangleF(tx, ty, tw, th), r, r));
                    c.Ctx.PushLayer(new Vortice.Direct2D1.LayerParameters1
                    {
                        ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
                        GeometricMask = clip,
                        MaskAntialiasMode = Vortice.Direct2D1.AntialiasMode.PerPrimitive,
                        MaskTransform = Matrix3x2.Identity,
                        Opacity = 1,
                    }, null!);
                    c.Circle(at.X, at.Y, far * fill, WithA(t80, alpha));
                    c.Ctx.PopLayer();
                }
            }
            var title = Lerp(Rgba(0xE6E0E9), t20, fill);
            var sub = Lerp(Rgba(0xCAC4D0), t20, fill);
            var icon = Lerp(t80, t20, fill);
            c.Icon(def.Icon, tx + 27, ty + th / 2, 16, WithA(icon, alpha));
            c.Text(def.Name, tx + 48, ty + th / 2 - 17, 13.5f, WithA(title, alpha), FontWeight.SemiBold, 0, tw - 58);
            c.Text(pinned ? $"Pinned · {order + 1}" : "Off", tx + 48, ty + th / 2 + 1, 11.5f, WithA(sub, alpha * 0.9f), FontWeight.Regular, 0, tw - 58);
        }
        c.Ctx.Transform = old;
    }

    /// <summary>
    /// Bloub's two moons in the library, drawn around him in absolute coordinates (the renderer calls this once
    /// before Bloub for the far side of the orbit, once after him for the near side). In a shrug they leave the
    /// orbit and become his shoulders: out to the sides, up, down, up a little, back.
    /// </summary>
    public static void LibraryMoons(Canvas c, IslandModel m, bool front, float opacity)
    {
        var tb = m.Tabs;
        float edit = Math.Clamp(tb.EditT.Value, 0, 1) * opacity;
        if (m.Mode != Mode.Full || edit < 0.01f) return;
        float k = 11f * m.Face.Size.Value / 56f;
        float ox = m.Face.X, oy = m.Face.Y;
        float su = (float)(m.Clock - tb.ShrugAt) / 0.95f;
        float mb = su is >= 0 and < 1 ? Smooth(0, 0.16f, su) * (1 - Smooth(0.78f, 1, su)) : 0;
        float Hump(float a, float b) => MathF.Sin(MathF.PI * Math.Clamp((su - a) / (b - a), 0, 1));
        float lift = Hump(0.14f, 0.46f) + 0.6f * Hump(0.48f, 0.76f);
        for (int i = 0; i < 2; i++)
        {
            float a = tb.MoonAngle + i * MathF.PI;
            bool near = MathF.Sin(a) > 0;
            float x = ox + MathF.Cos(a) * 88 * k, y = oy + (6 + MathF.Sin(a) * 32 - MathF.Cos(a) * 10) * k, sc = near ? 1 : 0.78f;
            if (mb > 0)
            {
                // which moon takes which side is decided at the start of the shrug, so they never cross
                float side = MathF.Cos(tb.ShrugAngle + i * MathF.PI) < 0 ? -1 : 1;
                x += (ox + side * 74 * k - x) * mb;
                y += (oy + (22 - 30 * lift) * k - y) * mb;
                sc += (1 - sc) * mb;
                near = near || mb > 0.5f;
            }
            if (near != front) continue;
            float r = 7.5f * k * sc, dim = near ? 1 : 0.75f;
            c.Circle(x, y, r, WithA(new Color4(0.55f * dim, 0.6f * dim, 0.67f * dim, 1), edit));
            c.Circle(x - r * 0.12f, y - r * 0.12f, r * 0.84f, WithA(new Color4(0.83f * dim, 0.86f * dim, 0.9f * dim, 1), edit));
            c.Circle(x - r * 0.3f, y - r * 0.32f, r * 0.42f, WithA(new Color4(dim, dim, dim, 1), 0.9f * edit));
        }
    }

    // ================================================================ panels

    static void Empty(Canvas c, float x, float y, float w, float h, char icon, string title, string sub)
    {
        c.Icon(icon, x + w / 2, y + h / 2 - 34, 26, WhiteA(0.3f));
        c.Text(title, x + w / 2, y + h / 2 - 6, 15, White, FontWeight.SemiBold, 0.5f);
        c.Text(sub, x + w / 2, y + h / 2 + 18, 12.5f, Tertiary, FontWeight.Regular, 0.5f);
    }

    static string Since(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return d.TotalDays < 7 ? $"{(int)d.TotalDays}d" : t.ToString("MMM d");
    }

    static string Bytes(double b) => b >= 1e9 ? $"{b / 1e9:0.0} GB" : b >= 1e6 ? $"{b / 1e6:0.0} MB" : b >= 1e3 ? $"{b / 1e3:0} KB" : $"{b:0} B";

    // ---------------------------------------------------------------- Projects

    static void ProjectsPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var list = m.Tabs.Projects;
        if (list.Count == 0) { Empty(c, x, y, w, h, '', "No recent projects", "Projects you open with Claude Code show up here"); return; }
        var live = m.Team.Live.ToList();
        float rowH = MathF.Min(54, h / list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            string path = list[i], name = Path.GetFileName(path.TrimEnd('\\', '/'));
            float ry = y + i * rowH, bh = rowH - 8;
            var agent = live.FirstOrDefault(a => string.Equals(a.Cwd.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            bool hov = m.Hit("proj:open:" + i, x, ry, w - 300, bh);
            c.Round(x, ry, w, bh, MathF.Min(16, bh / 2), WhiteA(hov ? 0.1f : 0.06f));
            uint col = agent?.Color ?? ProjectColors[(int)((uint)name.GetHashCode() % ProjectColors.Length)];
            c.Round(x + 12, ry + bh / 2 - 14, 28, 28, 9, Rgba(col, 0.22f));
            c.Icon('', x + 26, ry + bh / 2, 13, Rgba(col));
            c.Text(name, x + 52, ry + bh / 2 - 17, 14, White, FontWeight.SemiBold, 0, 240);
            string sub = path.Length > 46 ? "…" + path[^45..] : path;
            c.Text(sub, x + 52, ry + bh / 2 + 1, 11.5f, Tertiary, FontWeight.Regular, 0, w - 380);
            if (agent != null)
            {
                float nx = x + 60 + MathF.Min(240, c.Measure(name, 14, FontWeight.SemiBold));
                c.Circle(nx, ry + bh / 2 - 8, 3.5f, Rgba(agent.StatusColor));
                c.Text(agent.StatusText, nx + 8, ry + bh / 2 - 16, 11.5f, Rgba(agent.StatusColor), FontWeight.SemiBold);
            }
            float bx = x + w - 12;
            bx -= 104; PillButton(c, m, "proj:agent:" + i, "+ New agent", bx, ry + bh / 2 - 14, 100, 28, Blue);
            bx -= 84; PillButton(c, m, "proj:editor:" + i, Shell.EditorName == "Explorer" ? "Open" : Shell.EditorName, bx, ry + bh / 2 - 14, 80, 28, null);
            bx -= 72; PillButton(c, m, "proj:folder:" + i, "Folder", bx, ry + bh / 2 - 14, 68, 28, null);
        }
    }

    // ---------------------------------------------------------------- Music

    static void MediaPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        float cw = 404;
        Card(c, m, x, y, cw, h, "Now Playing", "Sample · SMTC soon");
        Art(c, m, x + 20, y + 46, 112, 22);
        c.Text(m.Song.Title, x + 150, y + 56, 20, White, FontWeight.SemiBold, 0, cw - 170);
        c.Text(m.Song.Artist, x + 150, y + 86, 14, Secondary);
        Waveform(c, m, x + 150 + 5 * 3.4f + 4 * 3f, y + 132, 26, 3.4f, 3f);
        Scrubber(c, m, x + 20, y + 196, cw - 40, true);
        Controls(c, m, x + cw / 2, y + 250, 1.05f);

        float ux = x + cw + 16, uw = w - cw - 16;
        Card(c, m, ux, y, uw, h, "Up next");
        for (int i = 1; i < IslandModel.Songs.Length; i++)
        {
            var song = IslandModel.Songs[(m.SongIndex + i) % IslandModel.Songs.Length];
            float ry = y + 44 + (i - 1) * 56;
            bool hov = m.Hit("song:" + ((m.SongIndex + i) % IslandModel.Songs.Length), ux + 8, ry, uw - 16, 50);
            if (hov) c.Round(ux + 8, ry, uw - 16, 50, 14, WhiteA(0.07f));
            using (var g = c.Gradient(new(ux + 18, ry + 7), new(ux + 54, ry + 43), Rgba(song.C0), Rgba(song.C1)))
                c.Ctx.FillRoundedRectangle(new Vortice.Direct2D1.RoundedRectangle(new System.Drawing.RectangleF(ux + 18, ry + 7, 36, 36), 9, 9), g);
            c.Text(song.Title, ux + 66, ry + 7, 13.5f, White, FontWeight.SemiBold, 0, uw - 130);
            c.Text(song.Artist, ux + 66, ry + 26, 11.5f, Tertiary, FontWeight.Regular, 0, uw - 130);
            c.Text(Time(song.Length), ux + uw - 20, ry + 16, 11.5f, Tertiary, FontWeight.SemiBold, 1f);
        }
    }

    // ---------------------------------------------------------------- Focus

    static readonly int[] FocusPresets = [15, 25, 50, 90];

    static void FocusPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        Card(c, m, x, y, w, h, "Focus", m.FocusPaused ? "Paused" : "In session");
        float cx = x + 150, cy = y + h / 2 + 10, r = 92;
        float left = MathF.Max(0, m.FocusLength - m.FocusElapsed), p = left / m.FocusLength;
        var green = Rgba(Green);
        c.Ring(cx, cy, r, 10, p, WithA(green, 0.16f), m.FocusPaused ? WhiteA(0.45f) : green);
        string digits = $"{(int)left / 60:00}:{(int)left % 60:00}";
        c.Text(digits, cx, cy - 26, 40, White, FontWeight.SemiBold, 0.5f);
        c.Text(m.FocusPaused ? "paused" : "remaining", cx, cy + 22, 12.5f, Tertiary, FontWeight.SemiBold, 0.5f);

        float tx = x + 300;
        c.Text("Deep work", tx, y + 56, 22, White, FontWeight.SemiBold);
        c.Text("Notifications wait until you're done. Bloub keeps an eye on your agents.", tx, y + 90, 12.5f, Secondary, FontWeight.Regular, 0, w - 320);
        c.Text("LENGTH", tx, y + 132, 11, Tertiary, FontWeight.SemiBold);
        float chx = tx;
        foreach (int min in FocusPresets)
        {
            string label = $"{min} min";
            Chip(c, m, "focus:len:" + min, label, chx, y + 150, MathF.Abs(m.FocusLength - min * 60) < 1);
            chx += ChipW(c, label);
        }
        PillButton(c, m, "focus", m.FocusPaused ? "Resume" : "Pause", tx, y + 206, 120, 38, m.FocusPaused ? Green : null);
        PillButton(c, m, "focus:reset", "Restart", tx + 130, y + 206, 100, 38, null);
        // sessions today
        c.Text("TODAY", tx, y + 270, 11, Tertiary, FontWeight.SemiBold);
        for (int i = 0; i < 4; i++)
            c.Circle(tx + 64 + i * 20, y + 278, 6, i < 2 ? green : i == 2 ? WithA(green, 0.45f) : WhiteA(0.14f));
    }

    // ---------------------------------------------------------------- Tasks

    static void TasksPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var tb = m.Tabs;
        AgentTeam.Field(c, m, tb.TaskField, "tf:task", x, y, w - 120, 40, 1, false);
        int done = tb.Tasks.Count(t => t.Done);
        PillButton(c, m, "tasks:clear", done > 0 ? $"Clear {done} done" : "All clear", x + w - 110, y + 4, 110, 32, null);
        if (tb.Tasks.Count == 0) { Empty(c, x, y + 40, w, h - 40, '', "Nothing to do", "Type a task above and press Enter"); return; }
        float rowH = 40;
        int max = (int)((h - 54) / rowH);
        for (int i = 0; i < Math.Min(max, tb.Tasks.Count); i++)
        {
            var t = tb.Tasks[i];
            float ry = y + 54 + i * rowH;
            bool hov = m.Hit("tasks:toggle:" + i, x, ry, w, rowH - 6);
            c.Round(x, ry, w, rowH - 6, 13, WhiteA(hov ? 0.09f : 0.05f));
            Check(c, x + 20, ry + (rowH - 6) / 2, t.Done, hov);
            c.Text(t.Text, x + 38, ry + 7, 13.5f, t.Done ? Tertiary : White, t.Done ? FontWeight.Regular : FontWeight.SemiBold, 0, w - 60);
            if (t.Done) c.Line(x + 38, ry + 17, x + 38 + MathF.Min(w - 60, c.Measure(t.Text, 13.5f)), ry + 17, 1.2f, Tertiary);
        }
        if (tb.Tasks.Count > max) c.Text($"+ {tb.Tasks.Count - max} more", x + w / 2, y + h - 16, 11.5f, Tertiary, FontWeight.SemiBold, 0.5f);
    }

    // ---------------------------------------------------------------- Calendar

    static void CalendarPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1).AddMonths(m.Tabs.MonthOffset);
        float cw = 420;
        Card(c, m, x, y, cw, h, month.ToString("MMMM yyyy"));
        PillButton(c, m, "cal:prev", "‹", x + cw - 132, y + 10, 30, 26, null);
        PillButton(c, m, "cal:today", "Today", x + cw - 98, y + 10, 52, 26, m.Tabs.MonthOffset == 0 ? null : Blue);
        PillButton(c, m, "cal:next", "›", x + cw - 42, y + 10, 30, 26, null);

        float gx = x + 16, gw = cw - 32, colW = gw / 7, gy = y + 48;
        string[] days = ["M", "T", "W", "T", "F", "S", "S"];
        for (int d = 0; d < 7; d++) c.Text(days[d], gx + d * colW + colW / 2, gy, 11, d >= 5 ? Tertiary : Secondary, FontWeight.SemiBold, 0.5f);
        int lead = ((int)month.DayOfWeek + 6) % 7, count = DateTime.DaysInMonth(month.Year, month.Month);
        int rows = (lead + count + 6) / 7;
        float rowH = MathF.Min(40, (h - 76) / rows);
        for (int day = 1; day <= count; day++)
        {
            int cell = lead + day - 1;
            float ccx = gx + (cell % 7) * colW + colW / 2, ccy = gy + 30 + (cell / 7) * rowH + rowH / 2;
            var date = month.AddDays(day - 1);
            bool isToday = date == today;
            if (isToday) c.Circle(ccx, ccy, 15, Rgba(Red));
            c.Text(day.ToString(), ccx, ccy - 9, 13, isToday ? White : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Tertiary : WhiteA(0.85f),
                isToday ? FontWeight.Bold : FontWeight.SemiBold, 0.5f);
        }

        float rx = x + cw + 16, rw = w - cw - 16;
        Card(c, m, rx, y, rw, h, "Today");
        c.Text(today.ToString("dddd").ToUpperInvariant(), rx + 18, y + 48, 12, Rgba(Red), FontWeight.Bold);
        c.Text(today.Day.ToString(), rx + 16, y + 62, 64, White, FontWeight.SemiBold);
        c.Text(today.ToString("MMMM yyyy"), rx + 18, y + 162, 14, Secondary, FontWeight.SemiBold);
        int doy = today.DayOfYear, total = DateTime.IsLeapYear(today.Year) ? 366 : 365;
        int week = System.Globalization.ISOWeek.GetWeekOfYear(today);
        c.Text($"Week {week}  ·  day {doy} of {total}", rx + 18, y + 204, 12, Tertiary, FontWeight.SemiBold);
        c.Round(rx + 18, y + 226, rw - 36, 6, 3, WhiteA(0.1f));
        c.Round(rx + 18, y + 226, (rw - 36) * doy / total, 6, 3, Rgba(Red));
        float dayP = (float)(DateTime.Now - today).TotalHours / 24;
        c.Text($"{dayP * 100:0}% of today is gone", rx + 18, y + 248, 12, Tertiary, FontWeight.SemiBold);
        c.Round(rx + 18, y + 270, rw - 36, 6, 3, WhiteA(0.1f));
        c.Round(rx + 18, y + 270, (rw - 36) * dayP, 6, 3, Rgba(Orange));
    }

    // ---------------------------------------------------------------- Timer

    static readonly int[] CountdownPresets = [1, 5, 10, 25];

    static void TimerPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var tb = m.Tabs;
        float cw = (w - 16) / 2;
        // stopwatch
        Card(c, m, x, y, cw, h, "Stopwatch", tb.Laps.Count > 0 ? $"{tb.Laps.Count} laps" : null);
        var sw = TimeSpan.FromSeconds(tb.SwElapsed);
        c.Text($"{(int)sw.TotalMinutes:00}:{sw.Seconds:00}", x + cw / 2 - 18, y + 52, 46, White, FontWeight.SemiBold, 0.5f);
        c.Text($".{sw.Milliseconds / 10:00}", x + cw / 2 + c.Measure("00:00", 46, FontWeight.SemiBold) / 2 - 16, y + 70, 26, Secondary, FontWeight.SemiBold);
        PillButton(c, m, "sw:toggle", tb.SwRunning ? "Stop" : tb.SwElapsed > 0 ? "Resume" : "Start", x + 20, y + 132, cw / 2 - 26, 38, tb.SwRunning ? Red : Green);
        PillButton(c, m, tb.SwRunning ? "sw:lap" : "sw:reset", tb.SwRunning ? "Lap" : "Reset", x + cw / 2 + 6, y + 132, cw / 2 - 26, 38, null);
        for (int i = 0; i < Math.Min(4, tb.Laps.Count); i++)
        {
            int k = tb.Laps.Count - 1 - i;
            var lap = TimeSpan.FromSeconds(tb.Laps[k] - (k > 0 ? tb.Laps[k - 1] : 0));
            float ly = y + 186 + i * 30;
            c.Text($"Lap {k + 1}", x + 22, ly, 13, Secondary, FontWeight.SemiBold);
            c.Text($"{(int)lap.TotalMinutes:00}:{lap.Seconds:00}.{lap.Milliseconds / 10:00}", x + cw - 22, ly, 13, White, FontWeight.SemiBold, 1f);
        }

        // countdown
        float bx = x + cw + 16;
        float flash = Math.Clamp(tb.CdFlash.Value, 0, 1);
        Card(c, m, bx, y, cw, h, "Countdown", tb.CdRunning ? "running" : tb.CdLeft <= 0 ? "done" : null);
        if (flash > 0.01f) c.Round(bx, y, cw, h, 22, WithA(Rgba(Orange), 0.25f * flash));
        float rcx = bx + 82, rcy = y + 112, rr = 54;
        float p = tb.CdLength > 0 ? (float)(tb.CdLeft / tb.CdLength) : 0;
        c.Ring(rcx, rcy, rr, 8, p, WithA(Rgba(Orange), 0.18f), Rgba(Orange));
        var left = TimeSpan.FromSeconds(Math.Ceiling(tb.CdLeft));
        c.Text($"{(int)left.TotalMinutes}:{left.Seconds:00}", rcx, rcy - 15, 24, White, FontWeight.SemiBold, 0.5f);
        float chx = bx + 156, chy = y + 64;
        foreach (int min in CountdownPresets)
        {
            string label = $"{min} min";
            Chip(c, m, "cd:len:" + min, label, chx, chy, MathF.Abs((float)tb.CdLength - min * 60) < 1);
            chy += 36;
            if (chy > y + 64 + 36) { chy = y + 64; chx += ChipW(c, "25 min") + 2; }
        }
        PillButton(c, m, "cd:toggle", tb.CdRunning ? "Pause" : tb.CdLeft <= 0 || tb.CdLeft >= tb.CdLength ? "Start" : "Resume",
            bx + 20, y + 196, cw / 2 - 26, 38, tb.CdRunning ? null : Orange);
        PillButton(c, m, "cd:reset", "Reset", bx + cw / 2 + 6, y + 196, cw / 2 - 26, 38, null);
        c.Text("You'll get a banner (and a happy Bloub) when it ends.", bx + 20, y + 252, 12, Tertiary, FontWeight.Regular, 0, cw - 40);
    }

    // ---------------------------------------------------------------- Notes

    static void NotesPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var tb = m.Tabs;
        AgentTeam.Field(c, m, tb.NoteField, "tf:note", x, y, w, 40, 1, false);
        if (tb.Notes.Count == 0) { Empty(c, x, y + 40, w, h - 40, '', "No notes yet", "Jot something down — it's saved on this PC"); return; }
        float gap = 12, cw = (w - gap) / 2, ch = (h - 54 - gap) / 2;
        for (int i = 0; i < Math.Min(4, tb.Notes.Count); i++)
        {
            var n = tb.Notes[i];
            float nx = x + (i % 2) * (cw + gap), ny = y + 54 + (i / 2) * (ch + gap);
            bool hov = m.Hit("note:copy:" + i, nx, ny, cw - 40, ch);
            c.Round(nx, ny, cw, ch, 18, Rgba(0xFFD60A, hov ? 0.16f : 0.1f));
            c.Ctx.PushAxisAlignedClip(new Rect(nx, ny + 10, cw - 36, ch - 44), Vortice.Direct2D1.AntialiasMode.Aliased);
            c.Wrapped(n.Text, nx + 16, ny + 14, cw - 52, 13, WhiteA(0.92f), FontWeight.SemiBold);
            c.Ctx.PopAxisAlignedClip();
            c.Text(hov ? "Click to copy" : Since(n.At), nx + 16, ny + ch - 26, 11.5f, hov ? Rgba(0xFFD60A) : Tertiary, FontWeight.SemiBold);
            bool dh = m.Hit("note:del:" + i, nx + cw - 36, ny + 8, 28, 28);
            if (hov || dh)
            {
                c.Circle(nx + cw - 22, ny + 22, 13, WhiteA(dh ? 0.2f : 0.08f));
                c.Icon('', nx + cw - 22, ny + 22, 11, dh ? Rgba(Red) : WhiteA(0.6f));
            }
        }
    }

    // ---------------------------------------------------------------- Downloads

    static (char icon, uint col) FileKind(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".crdownload" or ".part" or ".partial") ext = Path.GetExtension(Path.GetFileNameWithoutExtension(name)).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".fig" => ('', 0xFF375F),
            ".mp4" or ".mov" or ".mkv" or ".webm" => ('', 0xBF5AF2),
            ".mp3" or ".wav" or ".flac" => ('', 0xFF9F0A),
            ".zip" or ".7z" or ".rar" or ".gz" or ".tar" => ('', 0xFFD60A),
            ".exe" or ".msi" or ".msix" => ('', 0x0A84FF),
            ".pdf" => ('', 0xFF453A),
            _ => ('', 0x8E8E93),
        };
    }

    static void DownloadsPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var list = m.Tabs.Downloads;
        if (list.Count == 0) { Empty(c, x, y, w, h, '', "Downloads is empty", "New files land here as soon as they arrive"); return; }
        float rowH = MathF.Min(54, h / list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            float ry = y + i * rowH, bh = rowH - 8;
            bool hov = m.Hit("dl:open:" + i, x, ry, w - 96, bh);
            c.Round(x, ry, w, bh, MathF.Min(16, bh / 2), WhiteA(hov ? 0.1f : 0.06f));
            var (icon, col) = FileKind(f.Name);
            c.Round(x + 12, ry + bh / 2 - 14, 28, 28, 9, Rgba(col, 0.22f));
            c.Icon(icon, x + 26, ry + bh / 2, 13, Rgba(col));
            c.Text(f.Name, x + 52, ry + bh / 2 - 17, 13.5f, White, FontWeight.SemiBold, 0, w - 260);
            if (f.Partial)
            {
                float t = (m.Clock * 0.6f) % 1.4f - 0.2f, bw = w - 300;
                c.Round(x + 52, ry + bh / 2 + 6, bw, 4, 2, WhiteA(0.1f));
                c.Ctx.PushAxisAlignedClip(new Rect(x + 52, ry, bw, bh), Vortice.Direct2D1.AntialiasMode.Aliased);
                c.Round(x + 52 + bw * t, ry + bh / 2 + 6, bw * 0.3f, 4, 2, Rgba(Blue));
                c.Ctx.PopAxisAlignedClip();
                c.Text("downloading…", x + w - 108, ry + bh / 2 - 9, 12, Rgba(Blue), FontWeight.SemiBold, 1f);
            }
            else
            {
                c.Text($"{Bytes(f.Size)}  ·  {Since(f.At)}", x + 52, ry + bh / 2 + 1, 11.5f, Tertiary);
                c.Text(hov ? "Open" : "", x + w - 108, ry + bh / 2 - 9, 12, Rgba(Blue), FontWeight.SemiBold, 1f);
            }
            PillButton(c, m, "dl:show:" + i, "Show", x + w - 84, ry + bh / 2 - 14, 72, 28, null);
        }
    }

    // ---------------------------------------------------------------- Clocks

    static readonly (string City, string Zone)[] Zones =
    [
        ("San Francisco", "Pacific Standard Time"),
        ("New York", "Eastern Standard Time"),
        ("London", "GMT Standard Time"),
        ("Tokyo", "Tokyo Standard Time"),
    ];

    static void ClocksPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var nowUtc = DateTime.UtcNow;
        var list = new List<(string city, DateTime t, bool local)> { ("Here", DateTime.Now, true) };
        foreach (var (city, zone) in Zones)
        {
            try { list.Add((city, TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.FindSystemTimeZoneById(zone)), false)); }
            catch { }
        }
        float gap = 12, cw = (w - gap * (list.Count - 1)) / list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            var (city, t, local) = list[i];
            float cx0 = x + i * (cw + gap);
            bool day = t.Hour is >= 7 and < 19;
            c.Round(cx0, y, cw, h, 22, local ? WhiteA(0.1f) : WhiteA(0.06f));
            float r = MathF.Min(cw / 2 - 18, 52), ccx = cx0 + cw / 2, ccy = y + 26 + r;
            c.Circle(ccx, ccy, r, day ? Rgba(0xF2F2F7) : Rgba(0x1C1C1E));
            c.StrokeCircle(ccx, ccy, r, 1, WhiteA(day ? 0 : 0.14f));
            var ink = day ? Rgba(0x1C1C1E) : White;
            for (int k = 0; k < 12; k++)
            {
                float a = k * MathF.Tau / 12, l = k % 3 == 0 ? 7 : 4;
                var d = new Vector2(MathF.Sin(a), -MathF.Cos(a));
                c.Line(ccx + d.X * (r - 5), ccy + d.Y * (r - 5), ccx + d.X * (r - 5 - l), ccy + d.Y * (r - 5 - l), k % 3 == 0 ? 2 : 1.2f, WithA(ink, 0.55f));
            }
            float sec = t.Second + t.Millisecond / 1000f, min = t.Minute + sec / 60, hr = t.Hour % 12 + min / 60;
            void Hand(float turns, float len, float width, Color4 col)
            {
                float a = turns * MathF.Tau;
                c.Line(ccx, ccy, ccx + MathF.Sin(a) * len, ccy - MathF.Cos(a) * len, width, col);
            }
            Hand(hr / 12, r * 0.5f, 3.4f, ink);
            Hand(min / 60, r * 0.74f, 2.4f, ink);
            Hand(sec / 60, r * 0.8f, 1.2f, Rgba(Orange));
            c.Circle(ccx, ccy, 3, Rgba(Orange));

            c.Text(city, ccx, ccy + r + 16, 13.5f, White, FontWeight.SemiBold, 0.5f, cw - 12);
            c.Text(t.ToString("HH:mm"), ccx, ccy + r + 36, 20, local ? White : WhiteA(0.85f), FontWeight.SemiBold, 0.5f);
            string rel;
            if (local) rel = t.ToString("ddd d MMM");
            else
            {
                double diff = Math.Round((t - DateTime.Now).TotalHours * 2) / 2;
                string dayWord = t.Date > DateTime.Today ? "Tomorrow" : t.Date < DateTime.Today ? "Yesterday" : "Today";
                rel = $"{dayWord}, {(diff >= 0 ? "+" : "")}{diff:0.#}h";
            }
            c.Text(rel, ccx, ccy + r + 66, 11.5f, Tertiary, FontWeight.SemiBold, 0.5f, cw - 12);
        }
    }

    // ---------------------------------------------------------------- Network

    static void NetworkPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var tb = m.Tabs;
        float cw = (w - 16) / 2, ch = h - 74;
        void Graph(float gx, string title, float[] data, uint col, long total)
        {
            Card(c, m, gx, y, cw, ch, title, $"{Bytes(total)} this session");
            float now = data[^1];
            c.Text(Bytes(now) + "/s", gx + 16, y + 40, 26, White, FontWeight.SemiBold);
            float max = MathF.Max(1, data.Max());
            float bx = gx + 16, bw = cw - 32, bTop = y + 92, bH = ch - 92 - 16, slot = bw / data.Length;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i] / max, bh = MathF.Max(2, bH * v);
                c.Round(bx + i * slot, bTop + bH - bh, MathF.Max(1, slot - 1.5f), bh, 1, i == data.Length - 1 ? Rgba(col) : Rgba(col, 0.35f + 0.4f * i / data.Length));
            }
        }
        Graph(x, "Download", tb.Down, Blue, tb.NetDownTotal);
        Graph(x + cw + 16, "Upload", tb.Up, Green, tb.NetUpTotal);

        float iy = y + ch + 14;
        c.Round(x, iy, w, h - ch - 14, 18, WhiteA(0.06f));
        c.Icon(tb.NetKind.StartsWith("Wireless") ? '' : '', x + 26, iy + (h - ch - 14) / 2, 15, Rgba(Green));
        c.Text(tb.NetName.Length > 0 ? tb.NetName : "No connection", x + 48, iy + 9, 13.5f, White, FontWeight.SemiBold, 0, 260);
        c.Text(tb.NetKind, x + 48, iy + 29, 11.5f, Tertiary, FontWeight.Regular, 0, 300);
        if (tb.NetIp.Length > 0) c.Text("IPv4  " + tb.NetIp, x + w - 18, iy + 18, 12.5f, Secondary, FontWeight.SemiBold, 1f);
    }

    // ---------------------------------------------------------------- Battery

    static void BatteryPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var p = m.Tabs.Power;
        if (!p.HasBattery)
        {
            Empty(c, x, y, w, h - 40, '', "No battery here", "This PC runs on wall power");
            c.Text($"Power mode · {p.Mode}", x + w / 2, y + h - 52, 12.5f, Secondary, FontWeight.SemiBold, 0.5f);
            return;
        }
        uint col = p.Charging || p.PluggedIn ? Green : p.Percent < 0.1f ? Red : p.Percent < 0.2f ? Orange : 0x66D4CF;
        string state = p.Charging ? "Charging" : p.PluggedIn ? "Plugged in" : "On battery";

        // the charge, as a ring
        float cw = 300;
        Card(c, m, x, y, cw, h, "Battery", state);
        float rcx = x + cw / 2, rcy = y + h / 2 + 12, rr = 92;
        float breathe = p.Charging ? 0.5f + 0.5f * MathF.Sin(m.Clock * 2.4f) : 0;
        c.Glow(rcx, rcy, rr + 26, rr + 26, col, 0.08f + 0.1f * breathe);
        c.Ring(rcx, rcy, rr, 12, p.Percent, WithA(Rgba(col), 0.16f), Rgba(col));
        c.Text($"{p.Percent * 100:0}%", rcx, rcy - 30, 44, White, FontWeight.SemiBold, 0.5f);
        if (p.Charging) c.Icon('', rcx, rcy + 30, 16, Rgba(col));
        else c.Text(state.ToLowerInvariant(), rcx, rcy + 22, 12.5f, Tertiary, FontWeight.SemiBold, 0.5f);

        // details and this session's charge
        float dx = x + cw + 16, dw = w - cw - 16;
        Card(c, m, dx, y, dw, h, "Power");
        string left = p.Charging ? "Charging" + (p.Percent >= 0.99f ? " · almost full" : "")
            : p.PluggedIn ? "Fully charged"
            : p.SecondsLeft > 0 ? $"About {TabBar.Duration(p.SecondsLeft)} left" : "Estimating time left…";
        (string k, string v, uint? dot)[] rows =
        [
            ("Status", left, col),
            ("Power mode", p.Mode, null),
            ("Battery saver", p.Saver ? "On" : "Off", p.Saver ? Orange : null),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            float ry = y + 44 + i * 36;
            c.Text(rows[i].k, dx + 16, ry, 12.5f, Tertiary, FontWeight.SemiBold);
            float vx = dx + dw - 16;
            c.Text(rows[i].v, vx, ry, 13, White, FontWeight.SemiBold, 1f);
            if (rows[i].dot is { } d) c.Circle(vx - c.Measure(rows[i].v, 13, FontWeight.SemiBold) - 10, ry + 10, 3.5f, Rgba(d));
        }
        float gy = y + 160, gh = h - 160 - 18, gx = dx + 16, gw = dw - 32;
        c.Text("THIS SESSION", gx, gy, 11, Tertiary, FontWeight.SemiBold);
        var log = m.Tabs.ChargeLog;
        int first = Array.FindIndex(log, v => !float.IsNaN(v));
        float bTop = gy + 22, bH = gh - 22, slot = gw / log.Length;
        c.Round(gx, bTop + bH, gw, 1, 0.5f, WhiteA(0.12f));
        if (first < 0 || first >= log.Length - 1) { c.Text("Charge over time shows up here", gx + gw / 2, bTop + bH / 2 - 8, 12, Tertiary, FontWeight.SemiBold, 0.5f); return; }
        for (int i = first; i < log.Length; i++)
        {
            float bh = MathF.Max(2, bH * log[i]);
            c.Round(gx + i * slot, bTop + bH - bh, MathF.Max(1, slot - 1), bh, 1, Rgba(col, i == log.Length - 1 ? 1 : 0.3f + 0.45f * (i - first) / MathF.Max(1, log.Length - first)));
        }
    }

    // ---------------------------------------------------------------- Dev servers

    static uint ServerColor(TabBar.ServerItem s) => s.Port switch
    {
        5432 or 3306 or 27017 => 0x64D2FF,
        6379 => Red,
        _ => s.Process.ToLowerInvariant() switch
        {
            "node" or "bun" or "deno" => Green,
            "python" or "python3" or "pythonw" or "uvicorn" => 0xFFD60A,
            "dotnet" => 0x5E5CE6,
            "java" or "javaw" => Orange,
            _ => 0xFF6482,
        },
    };

    static void ServersPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var tb = m.Tabs;
        var list = tb.Servers;
        c.Text("Listening on this PC", x + 4, y + 2, 13, Secondary, FontWeight.SemiBold);
        if (!tb.ServersLoaded) { Empty(c, x, y, w, h, '', "Looking for servers…", "Checking what's listening on localhost"); return; }
        if (list.Count == 0) { Empty(c, x, y, w, h, '', "No dev servers running", "Start one (npm run dev, uvicorn, dotnet run…) and it shows up here"); return; }
        float rowH = MathF.Min(54, (h - 28) / list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            float ry = y + 28 + i * rowH, bh = rowH - 8;
            bool hov = s.Web && m.Hit("srv:open:" + i, x, ry, w - 190, bh);
            c.Round(x, ry, w, bh, MathF.Min(16, bh / 2), WhiteA(hov ? 0.1f : 0.06f));
            uint col = ServerColor(s);
            c.Round(x + 12, ry + bh / 2 - 14, 28, 28, 9, Rgba(col, 0.22f));
            // a live dot: these are running right now
            float pulse = 0.55f + 0.45f * MathF.Sin(m.Clock * 3 + i);
            c.Circle(x + 26, ry + bh / 2, 4.5f, Rgba(col, pulse));
            c.Text($"localhost:{s.Port}", x + 52, ry + bh / 2 - 17, 14, White, FontWeight.SemiBold);
            string up = s.Started is { } at ? "  ·  up " + Since(at).Replace("now", "just now") : "";
            string kind = string.Equals(s.Kind, s.Process, StringComparison.OrdinalIgnoreCase) ? s.Process : $"{s.Kind}  ·  {s.Process}";
            c.Text(kind + up, x + 52, ry + bh / 2 + 1, 11.5f, Tertiary, FontWeight.Regular, 0, w - 260);
            bool confirm = tb.ConfirmStop.Pid == s.Pid && m.Clock < tb.ConfirmStop.Until && s.Pid != 0;
            PillButton(c, m, "srv:stop:" + i, confirm ? "Sure?" : "Stop", x + w - 84, ry + bh / 2 - 14, 72, 28, confirm ? Red : null);
            if (s.Web) PillButton(c, m, "srv:open:" + i, "Open", x + w - 164, ry + bh / 2 - 14, 72, 28, Blue);
        }
    }
}
