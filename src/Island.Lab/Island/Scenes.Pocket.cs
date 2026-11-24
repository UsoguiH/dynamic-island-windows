using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Island.Lab.Render;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

/// <summary>
/// The Pocket tab: on the left everything Bloub holds, on the right the one you are pointing at, big: the file
/// itself as Windows shows it (drag it out into any window), what it is, where it lives, one wide button for
/// Claude that splits like a drop into the three things Claude can do with it, and under it the four everyday
/// things: open it, copy it, copy its path, show it in its folder.
/// </summary>
public static partial class Scenes
{
    public const float PocketPitch = 52;
    const float PocketListW = 230, PocketFoot = 32;
    const uint ClaudeCoral = 0xD97757;

    static void PocketPanel(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        var pk = m.Pocket;
        if (pk.Count == 0)
        {
            pk.ScrollMax = 0;
            PocketEmpty(c, m, x, y, w, h);
            return;
        }
        var cur = pk.Current;
        PocketList(c, m, cur, x, y, PocketListW, h - PocketFoot);
        PocketFooter(c, m, x, y + h - 21, PocketListW);
        if (cur != null) PocketDetail(c, m, cur, x + PocketListW + 12, y, w - PocketListW - 12, h, (float)(m.Clock - pk.SelectedAt));
    }

    /// <summary>The foot of the list: how much he holds, and "forget everything".</summary>
    static void PocketFooter(Canvas c, IslandModel m, float x, float fy, float w)
    {
        var pk = m.Pocket;
        float right = x + w;
        if (pk.Count > 1)
        {
            float cw = c.Measure("Clear all", 11.5f, FontWeight.SemiBold) + 22;
            bool ch = m.Hit("pk:clear", right - cw, fy - 5, cw, 26);
            c.Round(right - cw, fy - 5, cw, 26, 13, WhiteA(ch ? 0.18f : 0.08f));
            c.Text("Clear all", right - cw / 2, fy, 11.5f, WhiteA(ch ? 0.95f : 0.6f), FontWeight.SemiBold, 0.5f);
            right -= cw + 6;
        }
        long bytes = 0;
        foreach (var it in pk.Items) if (!it.Leaving && it.OnDisk) bytes += it.Size;
        c.Text((pk.Count == 1 ? "1 thing" : pk.Count + " things") + (bytes > 0 ? " · " + Bytes(bytes) : ""), x + 6, fy, 11.5f, WhiteA(0.36f), FontWeight.Regular, 0, right - x - 12);
    }

    // ================================================================ left: everything he holds

    static void PocketList(Canvas c, IslandModel m, PocketItem? cur, float x, float y, float w, float listH)
    {
        var pk = m.Pocket;
        float bh = PocketPitch - 6;
        float content = pk.Count * PocketPitch - 6;
        pk.ScrollMax = MathF.Max(0, content - listH);
        float top = y - pk.Scroll.Value;

        c.PushClip(x - 2, y, w + 4, listH);
        // the highlight glides to the row whose file is shown on the right
        if (cur != null) c.Round(x, top + pk.SelY.Value, w, bh, 15, WhiteA(0.085f));
        int slot = 0;
        foreach (var it in pk.Items)
        {
            if (!it.Leaving) { it.Show.To(1); float target = slot++ * PocketPitch; if (!it.Placed) { it.Y.Snap(target); it.Placed = true; } else it.Y.To(target); }
            float a = Math.Clamp(it.Show.Value, 0, 1);
            float ry = top + it.Y.Value;
            if (a < 0.01f || ry + bh < y - 4 || ry > y + listH + 4) continue;
            float vy = MathF.Max(ry, y), vh = MathF.Min(ry + bh, y + listH) - vy;
            bool hov = !it.Leaving && vh > 0 && m.Hit("pk:row:" + it.Key, x, vy, w, vh);
            bool sel = it == cur;
            float rx = x + (1 - a) * 14;
            c.Round(rx, ry, w, bh, 15, WhiteA((hov && !sel ? 0.085f : 0.05f) * a));
            PocketTile(c, it, rx + 8, ry + bh / 2 - 15, 30, a * (it.Missing ? 0.45f : 1));
            string ago = Since(it.At);
            float agoW = c.Measure(ago, 11, FontWeight.SemiBold);
            c.Text(ago, rx + w - 12, ry + 8.5f, 11, WhiteA((sel ? 0.5f : 0.34f) * a), FontWeight.SemiBold, 1f);
            c.Text(it.Display, rx + 48, ry + 6, 13, WhiteA((it.Missing ? 0.5f : sel ? 1 : 0.86f) * a), FontWeight.SemiBold, 0, w - 48 - 20 - agoW);
            if (it.Missing) c.Text("Moved or deleted", rx + 48, ry + 24, 11.5f, Rgba(Orange, 0.85f * a));
            else c.Text(it.Meta, rx + 48, ry + 24, 11.5f, WhiteA(0.4f * a), FontWeight.Regular, 0, w - 60);
        }
        c.PopClip();

        // more above or below: the rows fade into the island instead of being cut
        var black = new Color4(0, 0, 0, 1);
        var clear = new Color4(0, 0, 0, 0);
        if (pk.Scroll.Value < pk.ScrollMax - 1)
            c.GradientRound(x - 2, y + listH - 30, w + 4, 30.5f, 0, new(0, y + listH - 30), new(0, y + listH), clear, black);
        if (pk.Scroll.Value > 1)
            c.GradientRound(x - 2, y - 0.5f, w + 4, 22, 0, new(0, y), new(0, y + 22), black, clear);
        if (pk.ScrollMax > 0)
        {
            float frac = listH / content, sh = MathF.Max(22, listH * frac), pos = Math.Clamp(pk.Scroll.Value / pk.ScrollMax, 0, 1);
            c.Round(x + w + 3, y + (listH - sh) * pos, 3, sh, 1.5f, WhiteA(0.2f));
        }
    }

    // ================================================================ right: the one you point at, big

    static void PocketDetail(Canvas c, IslandModel m, PocketItem it, float x, float y, float w, float h, float since)
    {
        var pk = m.Pocket;
        float cx = x + w / 2;
        float u = Math.Clamp(since / 0.34f, 0, 1), pop = PopIn(u), al = MathF.Min(1, u * 3.2f);
        float lift = Math.Clamp(pk.PreHover.Value, 0, 1.2f), lc = MathF.Min(1, lift);
        var (_, col) = it.Look;
        bool can = it.OnDisk && !it.Demo && !it.Missing;
        var claude = it.Missing ? [] : Pocket.Claude([it]);
        var acts = it.Missing ? [] : Pocket.Actions([it]);
        int n = Math.Min(acts.Length, 4);
        float drop = claude.Length > 0 ? 0 : 24;      // (no Claude button: the file is bigger and everything sits lower)
        float sy = y + 60 + drop * 0.5f;

        c.Round(x, y, w, h, 22, WhiteA(0.055f));
        // its colour, as a light behind it (brighter when you reach for it)
        c.PushClip(x, y, w, h);
        c.Glow(cx, sy + 2, 160, 92, col, (it.Missing ? 0.08f : 0.2f + 0.1f * lc) * al);
        c.PopClip();

        // ---- a link, as a code for your phone's camera (in place of everything above the buttons; a long link's
        //      code needs the whole card: every square is 4 px, so a camera reads it from the screen)
        bool wide = false;
        if (pk.QrKey == it.Key && Qr.Encode(it.Path) is { } code)
        {
            int qn = code.GetLength(0);
            float side = (qn + 8) * 4;
            wide = side > 166;
            float qx = cx - side / 2, qy = wide ? y + (h - side - 30) / 2 : y + 14, qu = Math.Clamp((float)(m.Clock - pk.QrAt) / 0.32f, 0, 1);
            bool qh = m.Hit("pk:do:qr", qx, qy, side, side);
            var qwas = c.Transform;
            c.Transform = Matrix3x2.CreateScale(0.8f + 0.2f * PopIn(qu), new Vector2(cx, qy + side / 2)) * qwas;
            c.Round(qx, qy, side, side, 14, WhiteA(MathF.Min(1, qu * 3)));
            float ox = qx + 16, oy = qy + 16;
            if (qu > 0.3f)
                c.Custom(qx, qy, side, side, (ulong)HashCode.Combine(it.Path, qn), (ctx, brush) =>
                {
                    brush.Color = new Color4(0, 0, 0, 1);
                    var smooth = ctx.AntialiasMode;
                    ctx.AntialiasMode = AntialiasMode.Aliased;   // (hard squares: a camera reads edges)
                    for (int r = 0; r < qn; r++)
                        for (int q = 0; q < qn; q++)
                        {
                            if (!code[r, q]) continue;
                            int q0 = q;
                            while (q + 1 < qn && code[r, q + 1]) q++;
                            ctx.FillRectangle(new Rect(ox + q0 * 4, oy + r * 4, (q - q0 + 1) * 4, 4), brush);
                        }
                    ctx.AntialiasMode = smooth;
                });
            c.Transform = qwas;
            c.Text(qh ? "Click to put it away" : wide ? "Point your phone's camera at it · click to put it away" : "Point your phone's camera at it",
                   cx, qy + side + 9, 12.5f, WhiteA(0.62f * qu), FontWeight.Regular, 0.5f);
            goto buttons;
        }

        // ---- the file itself: it floats; under the cursor it comes up to be taken
        m.Hit("pk:pre:" + it.Key, cx - 112, y + 10, 224, 100 + drop);
        float bob = (float)Math.Sin(m.Clock * 1.5f) * 2f * (1 - lc) - 3 * lift;
        var was = c.Transform;
        var mid = new Vector2(cx, sy);
        c.Transform = Matrix3x2.CreateScale((0.86f + 0.14f * pop) * (1 + 0.06f * lift), mid) * Matrix3x2.CreateTranslation(0, bob) * was;
        float aa = al * (it.Missing ? 0.4f : 1);
        if (aa < 0.995f) c.PushLayer(aa);
        PocketArt(c, it, cx, sy, 236, 90 + drop * 0.6f, 86 + drop * 0.5f);
        if (aa < 0.995f) c.PopLayer();
        c.Transform = was;

        // ---- what it is
        float ty = y + 112 + drop;
        c.Text(c.Fit(it.Display, 16.5f, FontWeight.SemiBold, w - 96), cx, ty + (1 - u) * 6, 16.5f, WhiteA((it.Missing ? 0.6f : 1) * al), FontWeight.SemiBold, 0.5f);
        string ago = Since(it.At);
        ago = ago == "now" ? "just now" : ago.Contains(' ') ? ago : ago + " ago";
        if (it.Missing) c.Text("Moved or deleted · " + ago, cx, ty + 26, 12.5f, Rgba(Orange, 0.9f * al), FontWeight.Regular, 0.5f);
        else c.Text(it.Meta + " · " + ago, cx, ty + 26, 12.5f, WhiteA(0.5f * al), FontWeight.Regular, 0.5f);

        // where it lives (click: show it there); while you reach for the file, how to take it
        string where = it.OnDisk ? Path.GetDirectoryName(it.Path.TrimEnd('\\', '/')) ?? "" : it.Path;
        if (lc > 0.5f && can) c.Text("Drag it out into any window · click to open it", cx, ty + 46.5f, 11.5f, WhiteA(0.7f * al), FontWeight.Regular, 0.5f);
        else if (where.Length > 0)
        {
            string shown = FitTail(c, where, 11.5f, w - 90);
            float ww = c.Measure(shown, 11.5f);
            bool wh = !it.Missing && m.Hit($"pk:a:{it.Key}:{(it.OnDisk ? "show" : "open")}", cx - ww / 2 - 8, ty + 45, ww + 16, 20);
            if (wh) c.Round(cx - ww / 2 - 8, ty + 45, ww + 16, 20, 10, WhiteA(0.09f));
            c.Text(shown, cx, ty + 46.5f, 11.5f, WhiteA((wh ? 0.85f : 0.32f) * al), FontWeight.Regular, 0.5f);
        }

        buttons:
        // ---- top right: he stops holding it
        {
            float fx = x + w - 28, fy = y + 28;
            bool fh = m.Hit("pk:x:" + it.Key, fx - 15, fy - 15, 30, 30);
            c.Circle(fx, fy, 13, WhiteA(fh ? 0.2f : 0.08f));
            c.Icon('', fx, fy, 8.5f, fh ? Rgba(Red) : WhiteA(0.55f));
            if (fh) c.Text("Take it out of the Pocket", fx - 20, fy - 8.5f, 11.5f, WhiteA(0.7f), FontWeight.SemiBold, 1f);
        }
        if (wide) return;

        // ---- Claude: one wide button that splits into the three things it can do with this
        const float Gap = 8, TH = 56, BarH = 46;
        float by0 = y + 190 + drop;
        if (claude.Length > 0)
        {
            ClaudeBar(c, m, claude, x + 14, by0, w - 28, BarH, pk.AskT.Value, since - 0.06f);
            by0 += BarH + Gap;
        }
        // ---- and the everyday things
        float tw = (w - 28 - Gap * 3) / 4, rx0 = cx - (n * tw + (n - 1) * Gap) / 2;
        for (int i = 0; i < n; i++)
            ActionTile(c, m, "pk:do:" + acts[i].Id, acts[i], rx0 + i * (tw + Gap), by0, tw, TH, since - 0.12f - i * 0.045f, 1);
        if (claude.Length == 0 && n > 0 && can)
            c.Text("Drag the file out to drop it into any window", cx, y + h - 27, 11.5f, WhiteA(0.28f * al), FontWeight.Regular, 0.5f);
    }

    /// <summary>
    /// The Claude button. Closed: one wide pill. Pressed, it pulls apart like one drop into three (necks of the same
    /// colour stretch between them, thin, and let go), one for each thing Claude can do with the file.
    /// <paramref name="t"/>: 0 closed … 1 apart (a spring: it goes a little past and settles).
    /// </summary>
    static void ClaudeBar(Canvas c, IslandModel m, PocketAction[] opts, float x, float y, float w, float h, float t, float since)
    {
        float u = Math.Clamp(since / 0.4f, 0, 1);
        if (u <= 0) return;
        float a = MathF.Min(1, u * 2.6f);
        var was = c.Transform;
        c.Transform = Matrix3x2.CreateScale(0.9f + 0.1f * PopIn(u), new Vector2(x + w / 2, y + h / 2)) * was;
        if (a < 0.995f) c.PushLayer(a);
        var coral = Rgba(ClaudeCoral);
        int n = opts.Length;
        float open = n > 1 ? Math.Clamp(t, 0, 1.25f) : 0;

        if (open < 0.004f)
        {
            // one button
            string id = n > 1 ? "pk:do:claude" : "pk:do:" + opts[0].Id;
            bool hov = m.Hit(id, x, y, w, h);
            c.Round(x, y, w, h, h / 2, coral);
            if (hov) c.Round(x, y, w, h, h / 2, WhiteA(0.12f));
            string label = n > 1 ? "Give it to Claude" : opts[0].Label;
            float lw = c.Measure(label, 14, FontWeight.SemiBold), gx = x + (w - (26 + lw)) / 2;
            MiniBloub(c, gx + 9, y + h / 2, 9, 1);
            c.Text(label, gx + 26, y + h / 2 - 14 * 0.72f, 14, White, FontWeight.SemiBold);
            // (three small drops: there is more than one thing behind it)
            if (n > 1) for (int i = 0; i < 3; i++) c.Circle(x + w - 36 + i * 8 + (hov ? i * 1.2f : 0), y + h / 2, 2.2f, WhiteA(hov ? 0.95f : 0.6f));
        }
        else
        {
            // they pull further apart than they will rest (the bridge between them stretches), then ease back
            float gap = 8 * open + 11 * MathF.Sin(MathF.PI * MathF.Min(1, open)), pw = (w - gap * (n - 1)) / n;
            float pinch = MathF.Pow(Math.Clamp(open / 0.9f, 0, 1), 1.5f);
            // the necks: full height while the drops still touch, then thinner and thinner until they let go
            if (pinch < 0.995f)
                for (int i = 0; i < n - 1; i++)
                {
                    float right = x + (i + 1) * pw + i * gap;
                    Neck(c, right - h / 2, right + gap + h / 2, y, h, pinch, coral);
                }
            bool live = open > 0.6f;
            if (!live) m.Hit("pk:do:claude", x, y, w, h);
            float closedA = 1 - Math.Clamp(open / 0.3f, 0, 1);
            for (int i = 0; i < n; i++)
            {
                float px = x + i * (pw + gap);
                bool hov = live && m.Hit("pk:do:" + opts[i].Id, px, y, pw, h);
                c.Round(px, y, pw, h, h / 2, coral);
                if (hov) c.Round(px, y, pw, h, h / 2, WhiteA(0.14f));
                float la = Math.Clamp((open - 0.42f - i * 0.05f) / 0.35f, 0, 1);
                if (la < 0.01f) continue;
                string label = opts[i].Label;
                float lw = c.Measure(label, 12.5f, FontWeight.SemiBold), gx = px + (pw - (22 + lw)) / 2, cy = y + h / 2 + (1 - la) * 4;
                if (opts[i].Claude) MiniBloub(c, gx + 7, cy, 7, la);
                else c.Icon(opts[i].Icon, gx + 7, cy, 13, WhiteA(la));
                c.Text(label, gx + 22, cy - 12.5f * 0.72f, 12.5f, WhiteA(la), FontWeight.SemiBold);
            }
            if (closedA > 0.01f)
            {
                const string label = "Give it to Claude";
                float lw = c.Measure(label, 14, FontWeight.SemiBold), gx = x + (w - (26 + lw)) / 2;
                MiniBloub(c, gx + 9, y + h / 2, 9, closedA);
                c.Text(label, gx + 26, y + h / 2 - 14 * 0.72f, 14, WhiteA(closedA), FontWeight.SemiBold);
            }
        }
        if (a < 0.995f) c.PopLayer();
        c.Transform = was;
    }

    /// <summary>What joins two drops as they part: a bridge whose waist thins (pinch 0: as tall as they are; 1: gone).</summary>
    static void Neck(Canvas c, float x0, float x1, float y, float h, float pinch, Color4 col)
    {
        float midX = (x0 + x1) / 2, dip = h * pinch;
        c.Custom(x0, y, x1 - x0, h, (ulong)HashCode.Combine(pinch, col), (ctx, brush) =>
        {
            using var geo = c.Factory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                sink.BeginFigure(new Vector2(x0, y), FigureBegin.Filled);
                sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new Vector2(midX, y + dip), Point2 = new Vector2(x1, y) });
                sink.AddLine(new Vector2(x1, y + h));
                sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = new Vector2(midX, y + h - dip), Point2 = new Vector2(x0, y + h) });
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            brush.Color = col;
            ctx.FillGeometry(geo, brush);
        });
    }

    /// <summary>One everyday thing to do, as a tile: its icon in its own colour, its name under it. Pops in.</summary>
    static void ActionTile(Canvas c, IslandModel m, string id, PocketAction act, float x, float y, float w, float h, float t, float a)
    {
        float u = Math.Clamp(t / 0.4f, 0, 1);
        if (u <= 0) return;
        float al = MathF.Min(1, u * 2.6f) * a;
        bool hov = m.Hit(id, x, y, w, h);
        var was = c.Transform;
        c.Transform = Matrix3x2.CreateScale((0.84f + 0.16f * PopIn(u)) * (hov ? 1.045f : 1), new Vector2(x + w / 2, y + h / 2)) * was;
        c.Round(x, y, w, h, 16, act.Main ? Rgba(act.Color, (hov ? 1 : 0.9f) * al) : hov ? Rgba(act.Color, 0.26f * al) : WhiteA(0.075f * al));
        float ix = x + w / 2, iy = y + 20 - (hov ? 1 : 0);
        if (act.Claude) MiniBloub(c, ix, iy, 8.5f, al);
        else c.Icon(act.Icon, ix, iy, 17, act.Main ? WhiteA(al) : Rgba(act.Color, al));
        c.Text(c.Fit(act.Label, 11.5f, FontWeight.SemiBold, w - 8), ix, y + 33, 11.5f, WhiteA((act.Main || hov ? 1 : 0.8f) * al), FontWeight.SemiBold, 0.5f);
        c.Transform = was;
    }

    static readonly Dictionary<(string, float), string> _tails = new();

    /// <summary>Shortens a path from its start ("…\Mobile app\docs"), so the folder it ends in stays readable.</summary>
    static string FitTail(Canvas c, string text, float size, float maxW)
    {
        if (c.Measure(text, size) <= maxW) return text;
        if (_tails.TryGetValue((text, maxW), out var f)) return f;
        int lo = 1, hi = text.Length;      // (the first character kept)
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (c.Measure("…" + text[mid..], size) <= maxW) hi = mid; else lo = mid + 1;
        }
        f = "…" + text[lo..];
        if (_tails.Count > 400) _tails.Clear();
        return _tails[(text, maxW)] = f;
    }

    // ================================================================ the file as Windows shows it

    /// <summary>
    /// The file itself, centred on (cx, cy): its picture, a film's frame or a document's first page when Windows has
    /// one (up to maxW × maxH), a link's own preview picture or icon, otherwise its real icon (icon × icon).
    /// </summary>
    static void PocketArt(Canvas c, PocketItem it, float cx, float cy, float maxW, float maxH, float icon)
    {
        bool real = !it.Demo && !it.Missing;
        if (it.Kind == PocketKind.Image && real && Picture(c, Images.Big + it.Path, cx, cy, maxW, maxH, 12, null)) return;
        if (it.Kind == PocketKind.Link)
        {
            if (it.ImageUrl is { } img && Picture(c, img, cx, cy, maxW, maxH, 12, null)) return;
            if (it.IconUrl is { } fav && Picture(c, fav, cx, cy, icon * 0.86f, icon * 0.86f, icon * 0.19f, null)) return;
        }
        else if (it.Kind == PocketKind.Text)
        {
            var (g, col) = it.Look;
            c.Round(cx - icon / 2, cy - icon / 2, icon, icon, icon * 0.28f, Rgba(col, 0.24f));
            c.Icon(g, cx, cy, icon * 0.44f, Rgba(col));
            return;
        }
        if (real && it.OnDisk)
        {
            if (it.Kind != PocketKind.Folder && Picture(c, Images.Thumb + it.Path, cx, cy, maxW, maxH, 10, it.Ext)) return;
            if (Picture(c, Images.Icon + it.Path, cx, cy, icon, icon, 0, null)) return;
            if (!Images.Failed(Images.Icon + it.Path)) return;    // (on its way: an empty beat, not a stand-in that flashes)
        }
        string kind = Images.Kind + it.KindName;
        if (Picture(c, kind, cx, cy, icon, icon, 0, null)) return;
        if (!Images.Failed(kind)) return;
        // Windows gave nothing at all: a sheet of paper with its type on it
        var was = c.Transform;
        c.Transform = Matrix3x2.CreateScale(icon / 134f, new Vector2(cx, cy)) * was;
        Sheet(c, it, cx, cy);
        c.Transform = was;
    }

    /// <summary>A picture, whole (never cropped), as large as the box allows. False while it is not there.</summary>
    static bool Picture(Canvas c, string url, float cx, float cy, float maxW, float maxH, float radius, string? tag)
    {
        if (Images.Get(url) is not { } pic || pic.W < 1 || pic.H < 1) return false;
        float k = MathF.Min(MathF.Min(maxW / pic.W, maxH / pic.H), 4);
        float w = pic.W * k, h = pic.H * k, x = cx - w / 2, y = cy - h / 2;
        if (!c.Image(url, x, y, w, h, MathF.Min(radius, MathF.Min(w, h) / 4))) return false;
        if (tag is { Length: > 0 } && w > 60 && h > 40)
        {
            if (tag.Length > 5) tag = tag[..5];
            float tw = c.Measure(tag, 10, FontWeight.Bold) + 14;
            c.Round(x + 7, y + h - 25, tw, 18, 9, new Color4(0, 0, 0, 0.62f));
            c.Text(tag, x + 7 + tw / 2, y + h - 23.5f, 10, White, FontWeight.Bold, 0.5f);
        }
        return true;
    }

    /// <summary>The same, small (a row's tile, the name on the drop card): a picture fills its tile, an icon is shown whole.</summary>
    static void PocketTile(Canvas c, PocketItem it, float x, float y, float s, float a)
    {
        bool real = !it.Demo && !it.Missing;
        if (it.Kind == PocketKind.Image && real && c.Image(Images.Local + it.Path, x, y, s, s, s * 0.28f, a)) return;
        if (it.Kind != PocketKind.Text)
        {
            string? url = it.Kind == PocketKind.Link ? it.IconUrl : real && it.OnDisk ? Images.Icon + it.Path : null;
            if (url != null && Small(c, url, x, y, s, it.Kind == PocketKind.Link ? s * 0.2f : 0, a)) return;
            if (Small(c, Images.Kind + it.KindName, x, y, s, 0, a)) return;
        }
        var (icon, col) = it.Look;
        c.Round(x, y, s, s, s * 0.28f, Rgba(col, 0.24f * a));
        c.Icon(icon, x + s / 2, y + s / 2, s * 0.44f, Rgba(col, a));
    }

    static bool Small(Canvas c, string url, float x, float y, float s, float radius, float a)
    {
        if (Images.Get(url) is not { } pic || pic.W < 1 || pic.H < 1) return false;
        float k = MathF.Min(s / pic.W, s / pic.H), w = pic.W * k, h = pic.H * k;
        return c.Image(url, x + (s - w) / 2, y + (s - h) / 2, w, h, radius, a);
    }

    /// <summary>A sheet of paper with its corner folded: lines of writing, and its type on a band of its colour.</summary>
    static void Sheet(Canvas c, PocketItem it, float cx, float cy)
    {
        const float W = 104, H = 134, F = 27, R = 11;
        float x = cx - W / 2, y = cy - H / 2;
        var (_, col) = it.Look;
        var paper = Rgba(0xF2F2F7);
        c.Round(x, y + F, W, H - F, R, paper);                  // the page, below the fold
        c.Round(x, y, W - F, F + R, R, paper);                  // …and beside it
        c.Round(x + W - F - R, y, R, R, 0, paper);              // (the two corners the fold starts and ends at are square)
        c.Round(x + W - R, y + F, R, R + 1, 0, paper);
        c.Polygon(Rgba(0xC7C7CC), new(x + W - F, y), new(x + W, y + F), new(x + W - F, y + F));   // the folded corner

        string ext = it.Ext.Length > 4 ? it.Ext[..4] : it.Ext;
        bool band = ext.Length > 0;
        var ink = Rgba(0x1C1C1E, 0.15f);
        float[] lens = band ? [0.46f, 0.74f, 0.66f, 0.72f, 0.4f] : [0.46f, 0.74f, 0.66f, 0.72f, 0.58f, 0.7f, 0.64f, 0.34f];
        for (int i = 0; i < lens.Length; i++)
        {
            float ly = y + 20 + i * 12.5f, lw = (W - 28) * lens[i];
            if (ly < y + F + 4) lw = MathF.Min(lw, W - F - 24);
            c.Round(x + 14, ly, lw, 5, 2.5f, ink);
        }
        if (!band) return;
        float by = y + H - 49;
        bool light = 0.299f * ((col >> 16) & 0xFF) + 0.587f * ((col >> 8) & 0xFF) + 0.114f * (col & 0xFF) > 150;
        c.Round(x + 10, by, W - 20, 28, 9, Rgba(col));
        c.Text(ext, cx, by + 14 - 13.5f * 0.72f, 13.5f, light ? Rgba(0x000000, 0.82f) : White, FontWeight.Bold, 0.5f);
    }

    // ================================================================ nothing in it yet

    static readonly PocketItem GhostSheet = new() { Kind = PocketKind.Doc };

    static void PocketEmpty(Canvas c, IslandModel m, float x, float y, float w, float h)
    {
        float cx = x + w / 2, sy = y + h / 2 - 34;
        // "up there": small arrows rise towards Bloub, one after the other
        for (int i = 0; i < 3; i++)
        {
            float ph = (float)((m.Clock * 0.7f + i / 3f) % 1), ay = sy - 58 - ph * 30, s = 6.5f;
            float a = MathF.Sin(ph * MathF.PI) * 0.6f;
            if (a < 0.02f) continue;
            c.Line(cx - s, ay + s * 0.5f, cx, ay - s * 0.5f, 2.3f, WhiteA(a));
            c.Line(cx, ay - s * 0.5f, cx + s, ay + s * 0.5f, 2.3f, WhiteA(a));
        }
        var was = c.Transform;
        var mid = new Vector2(cx, sy);
        c.Transform = Matrix3x2.CreateRotation(-5 * MathF.PI / 180, mid) * Matrix3x2.CreateScale(0.62f, mid)
                      * Matrix3x2.CreateTranslation(0, (float)Math.Sin(m.Clock * 1.5f) * 2.2f) * was;
        c.PushLayer(0.42f);
        Sheet(c, GhostSheet, cx, sy);
        c.PopLayer();
        c.Transform = was;
        c.Text("Nothing in the Pocket yet", cx, y + h / 2 + 22, 15.5f, White, FontWeight.SemiBold, 0.5f);
        c.Text("Drag a file, a folder or a link up to Bloub. He holds it here until you need it", cx, y + h / 2 + 47, 12.5f, Tertiary, FontWeight.Regular, 0.5f);
    }
}
