using System.Globalization;
using System.Numerics;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Island.Lab.Render;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

/// <summary>
/// The "describe a tab" flow, drawn: the white line Bloub becomes, the living previews under it, the build view
/// (your words as the title, the dot field, the tab turning solid, the bar), and over everything the ✓ stamp,
/// the touchdown ring, the confetti and the chip that flies into your bar. See <see cref="TabMaker"/>.
/// </summary>
public static partial class Scenes
{
    static readonly Color4 Ink = Rgba(0x0B0B10), LineWhite = Rgba(0xF2F2F7), PhGrey = Rgba(0xA2A2AC), WordGrey = Rgba(0x7A7A84), LineChip = Rgba(0xE2E2E7);
    const float PanelX = 20, PanelY = 70, PanelW = 684, PanelH = 314;

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ================================================================ the library steps back, and comes back

    /// <summary>How a library tile looks while the describe flow opens or closes: (opacity, scale).</summary>
    static (float A, float S) LibFade(IslandModel m, int i, float tx, float ty, Vector2 newAt)
    {
        var k = m.Maker;
        if (k.Now == TabMaker.Stage.Line)
        {
            // they fold away, the ones nearest the New tile first
            float d = Vector2.Distance(new(tx, ty), newAt);
            float u = TabMaker.EaseIn(k.Since(k.OpenedAt + d * 0.00012f, 0.26f));
            return (1 - u, 1 - 0.08f * u);
        }
        if (k.Now != TabMaker.Stage.Off) return (0, 1);
        float b = k.Since(k.LibBackAt + 0.09f + i * 0.012f, 0.52f);
        if (b >= 1) return (1, 1);
        float e = TabMaker.Calm(b);
        return (Math.Clamp(e, 0, 1), 0.92f + 0.08f * e);
    }

    /// <summary>The library's caption, status, Done and glow while the flow opens or closes.</summary>
    static float LibSideFade(IslandModel m, int i)
    {
        var k = m.Maker;
        if (k.Now == TabMaker.Stage.Line) return 1 - k.Since(k.OpenedAt, 0.2f);
        if (k.Now != TabMaker.Stage.Off) return 0;
        return Math.Clamp(TabMaker.Calm(k.Since(k.LibBackAt + 0.16f + i * 0.05f, 0.52f)), 0, 1);
    }

    /// <summary>The library's last tile: New tab · Describe it (a dashed outline and Bloub's little face).</summary>
    static void NewTile(Canvas c, IslandModel m, float tx, float ty, float tw, float th, float alpha, bool live)
    {
        bool hover = live && m.Hit("mk:new", tx, ty, tw, th);
        c.Round(tx, ty, tw, th, th / 2, WhiteA((hover ? 0.06f : 0.03f) * alpha));
        c.DashedRound(tx + 0.75f, ty + 0.75f, tw - 1.5f, th - 1.5f, th / 2 - 0.75f, 1.5f, WhiteA((hover ? 0.65f : 0.3f) * alpha));
        float fx = tx + 27, fy = ty + th / 2;
        c.Circle(fx, fy, 10.45f, WithA(LineWhite, alpha));
        c.Round(fx - 4.2f, fy - 3.3f, 2.5f, 5.8f, 1.25f, WithA(Ink, alpha));
        c.Round(fx + 0.8f, fy - 3.6f, 2.5f, 5.8f, 1.25f, WithA(Ink, alpha));
        c.Text("New tab", tx + 48, ty + th / 2 - 17, 13.5f, WhiteA(alpha), FontWeight.SemiBold, 0, tw - 58);
        c.Text("Describe it", tx + 48, ty + th / 2 + 1, 11.5f, WhiteA(0.5f * alpha), FontWeight.Regular, 0, tw - 58);
    }

    // ================================================================ the panel: the describe screen, or the build view

    static void MakerPanel(Canvas c, IslandModel m, float x0, float y0)
    {
        var k = m.Maker;
        TabMaker.Measure ??= (t, s) => c.Measure(t, s, FontWeight.SemiBold);
        switch (k.Now)
        {
            case TabMaker.Stage.Line: Describe(c, m, x0, y0); break;
            case TabMaker.Stage.Build: BuildView(c, m, x0, y0, 1); break;
            case TabMaker.Stage.Adding:
                float u = TabMaker.EaseIn(k.Since(k.AddAt, 0.34f));
                if (u < 1)
                {
                    var old = c.Transform;
                    c.Transform = Matrix3x2.CreateScale(1 - 0.05f * u, new Vector2(x0 + 362, y0 + 200)) * old;
                    BuildView(c, m, x0, y0, 1 - u);
                    c.Transform = old;
                }
                break;
        }
    }

    // ---------------------------------------------------------------- 1 · describe

    static void Describe(Canvas c, IslandModel m, float x0, float y0)
    {
        var k = m.Maker;
        var (l, cy, w, h, _) = k.Line();
        l += x0; cy += y0; // (the line is drawn at the same offset: see TheLine)
        if (k.Armed)
        {
            float gx = l + w - 10 - 44;
            m.Hit("mk:field", l, cy - h / 2, gx - l - 4, h);
            m.Hit("mk:go", gx - 4, cy - 26, 52, 52); // a little bigger than the circle: easy to hit
        }

        // the quiet line under it: a hint, or what Bloub understood
        float vy = y0 + PanelY + 104;
        c.PushClip(x0 + PanelX, vy - 2, PanelW, 26);
        RollLine(c, k, k.Voice, k.VoiceOld, x0 + PanelX + PanelW / 2, vy, 12, 0.56f, 0.34f, Voice);
        c.PopClip();

        // living previews to start from; the one Bloub thinks you mean steps forward
        float ty = y0 + PanelY + 148, gap = 10, tw = (PanelW - gap * 3) / 4, th = 138;
        int pick = k.Reading.Kind is { } kind ? Array.IndexOf(TabMaker.Kinds, kind) : -1;
        for (int i = 0; i < 4; i++)
        {
            float tx = x0 + PanelX + i * (tw + gap);
            float enter = TabMaker.Calm(k.Since(k.OpenedAt + 0.24f + i * 0.05f, 0.64f));
            float p = Math.Clamp(k.Pick[i].Value, 0, 1.1f), hp = Math.Clamp(k.HasPick.Value, 0, 1);
            float fade = 1 - 0.68f * hp * (1 - Math.Clamp(p, 0, 1));
            float sc = 1 - 0.03f * hp * (1 - Math.Clamp(p, 0, 1));
            float y = ty + 16 * (1 - enter) - 6 * p;
            float a = Math.Clamp(enter, 0, 1) * fade;
            if (a < 0.01f) continue;
            bool hover = k.Armed && m.Hit("mk:tile:" + i, tx, ty, tw, th);
            k.Hov[i].To(hover ? 1 : 0);
            float hv = Math.Clamp(k.Hov[i].Value, 0, 1);
            var old = c.Transform;
            c.Transform = Matrix3x2.CreateScale(sc, new Vector2(tx + tw / 2, y + th / 2)) * old;
            c.Round(tx, y, tw, th, 20, WhiteA((0.055f + 0.03f * MathF.Max(hv, Math.Clamp(p, 0, 1))) * a));
            if (p > 0.01f) c.StrokeRound(tx + 0.75f, y + 0.75f, tw - 1.5f, th - 1.5f, 19.25f, 1.5f, WhiteA(Math.Clamp(p, 0, 1) * a));
            c.PushClip(tx, y, tw, th);
            Mini(c, m, i, tx, y, tw, th, a);
            c.PopClip();
            var def = TabMaker.Kinds[i];
            c.Icon(def.Icon, tx + 14 + 8.5f, y + th - 13 - 8, 15, WhiteA(0.82f * a));
            c.Text(def.Name, tx + 14 + 24, y + th - 13 - 16.5f, 12.5f, WhiteA(0.82f * a), FontWeight.SemiBold);
            float ar = MathF.Max(hv, Math.Clamp(p, 0, 1));
            if (ar > 0.01f)
            {
                float s = 0.6f + 0.4f * TabMaker.Calm(ar), acx = tx + tw - 11 - 13, acy = y + th - 9 - 13;
                c.Circle(acx, acy, 13 * s, WhiteA(ar * a));
                c.Icon('', acx, acy, 12 * s, WithA(Rgba(0x000000), ar * a));
            }
            c.Transform = old;
        }
    }

    /// <summary>A line that rolls: the new one rises in, the old one rises away.</summary>
    static void RollLine(Canvas c, TabMaker k, (string Text, float At) now, (string Text, float At) old, float cx, float y, float dist, float inDur, float outDur,
        Action<Canvas, string, float, float, float> draw)
    {
        if (old.Text.Length > 0)
        {
            float u = TabMaker.EaseIn(k.Since(old.At, outDur));
            if (u < 1) draw(c, old.Text, cx, y - dist * u, 1 - u);
        }
        if (now.Text.Length == 0) return;
        float e = TabMaker.Calm(k.Since(now.At, inDur));
        draw(c, now.Text, cx, y + dist * (1 - e), Math.Clamp(e, 0, 1));
    }

    /// <summary>The voice line: plain text, <b>bold</b>, a <k>key</k> chip, and a green ✓ when he understood.</summary>
    static void Voice(Canvas c, string text, float cx, float y, float a)
    {
        if (a < 0.01f) return;
        bool ok = text.StartsWith("<ok>");
        if (ok) text = text[4..];
        var runs = Runs(text);
        float W(string t, int kind) => kind switch { 1 => c.Measure(t, 13.5f, FontWeight.SemiBold), 2 => c.Measure(t, 10, FontWeight.Bold) + 12, _ => c.Measure(t, 13.5f, FontWeight.Medium) };
        float total = runs.Sum(r => W(r.Text, r.Kind)) + (ok ? 23 : 0);
        float x = cx - total / 2;
        if (ok)
        {
            c.Circle(x + 8, y + 11, 8, WithA(Rgba(Green), a));
            c.Icon('', x + 8, y + 11.5f, 9, WithA(Rgba(0x000000), a));
            x += 23;
        }
        foreach (var (t, kind) in runs)
        {
            switch (kind)
            {
                case 1: c.Text(t, x, y + 1.5f, 13.5f, WhiteA(a), FontWeight.SemiBold); break;
                case 2:
                    float kw = W(t, 2) - 6;
                    c.StrokeRound(x + 3, y + 3.5f, kw, 16, 5, 1, WhiteA(0.25f * a));
                    c.Text(t, x + 3 + kw / 2, y + 5.5f, 10, WhiteA(0.75f * a), FontWeight.Bold, 0.5f);
                    break;
                default: c.Text(t, x, y + 1.5f, 13.5f, WhiteA(0.5f * a), FontWeight.Medium); break;
            }
            x += W(t, kind);
        }
    }

    static List<(string Text, int Kind)> Runs(string s)
    {
        var o = new List<(string, int)>();
        int i = 0;
        while (i < s.Length)
        {
            int b = s.IndexOf("<b>", i, StringComparison.Ordinal), kk = s.IndexOf("<k>", i, StringComparison.Ordinal);
            int next = b < 0 ? kk : kk < 0 ? b : Math.Min(b, kk);
            if (next < 0) { o.Add((s[i..], 0)); break; }
            if (next > i) o.Add((s[i..next], 0));
            bool bold = next == b;
            int end = s.IndexOf(bold ? "</b>" : "</k>", next, StringComparison.Ordinal);
            if (end < 0) end = s.Length;
            o.Add((s[(next + 3)..end], bold ? 1 : 2));
            i = Math.Min(s.Length, end + 4);
        }
        return o;
    }

    // ---------------------------------------------------------------- the living previews

    static void Mini(Canvas c, IslandModel m, int i, float tx, float ty, float tw, float th, float a)
    {
        var k = m.Maker; var t = k.Minis;
        float mx = tx + 12, my = ty + 12, mw = tw - 24;
        float In(int j) => Math.Clamp(TabMaker.Calm(k.Since(k.OpenedAt + 0.34f + i * 0.05f + j * 0.05f, 0.52f)), 0, 1);
        switch (i)
        {
            case 0: // matches: three rows, a goal now and then
                for (int j = 0; j < 3; j++)
                {
                    float e = In(j), ra = a * e, rx = mx - 8 * (1 - e), ry = my + j * 28;
                    float g = 1 - TabMaker.EaseOut(k.Since(t.GoalAt[j], 1.4f));
                    if (k.Clock - t.GoalAt[j] > 1.4f) g = 0;
                    c.Round(rx, ry, mw, 24, 8, Lerp(WhiteA(0.06f * ra), Rgba(Green, 0.14f * ra), g));
                    if (g > 0.01f) c.StrokeRound(rx + 0.75f, ry + 0.75f, mw - 1.5f, 22.5f, 7.25f, 1.5f, Rgba(Green, 0.9f * g * ra));
                    float pulse = t.Live[j] ? 0.25f + 0.75f * (0.5f + 0.5f * MathF.Cos(k.Clock * MathF.Tau / 1.6f)) : 1;
                    c.Circle(rx + 9 + 2.5f, ry + 12, 2.5f, t.Live[j] ? Rgba(Green, pulse * ra) : WhiteA(0.28f * ra));
                    string hs = t.Hs[j].ToString(), @as = t.As[j].ToString();
                    float sw = c.Measure(hs, 11, FontWeight.Bold) + c.Measure("–", 11, FontWeight.Medium) + c.Measure(@as, 11, FontWeight.Bold) + 6;
                    float col = (mw - 18 - 5 - 21 - sw) / 2, c2 = rx + 9 + 5 + 7, sx = c2 + col + 7;
                    c.Text(t.Home[j], c2 + col, ry + 4.5f, 10.5f, WhiteA(ra), FontWeight.SemiBold, 1f);
                    c.Text(t.Away[j], sx + sw + 7, ry + 4.5f, 10.5f, WhiteA(ra), FontWeight.SemiBold);
                    // the score: the number that changed rolls up
                    float roll = k.Clock - t.RollAt[j] < 0.7f ? TabMaker.Calm(k.Since(t.RollAt[j], 0.56f)) : 1;
                    float hw = c.Measure(hs, 11, FontWeight.Bold);
                    c.PushClip(sx - 1, ry + 5, sw + 2, 14);
                    void Num(string now, int was, float x, bool rolls)
                    {
                        if (rolls && roll < 1)
                        {
                            c.Text(was.ToString(), x, ry + 3.5f - 14 * roll, 11, WhiteA(ra), FontWeight.Bold);
                            c.Text(now, x, ry + 3.5f + 14 * (1 - roll), 11, WhiteA(ra), FontWeight.Bold);
                        }
                        else c.Text(now, x, ry + 3.5f, 11, WhiteA(ra), FontWeight.Bold);
                    }
                    Num(hs, t.Hs[j] - 1, sx, !t.RollAway[j]);
                    c.Text("–", sx + hw + 3, ry + 3.5f, 11, WhiteA(0.3f * ra), FontWeight.Medium);
                    Num(@as, t.As[j] - 1, sx + hw + 6 + c.Measure("–", 11, FontWeight.Medium), t.RollAway[j]);
                    c.PopClip();
                }
                break;
            case 1: // a price and its sparkline, sliding along
            {
                float e = In(0);
                string p = "$" + t.Price.ToString("N0", CultureInfo.InvariantCulture);
                float f = k.Clock - t.PriceAt < 0.9f ? 1 - TabMaker.EaseOut(k.Since(t.PriceAt, 0.9f)) : 0;
                var col = Lerp(WhiteA(a * e), Rgba(t.PriceUp ? Green : Red, a * e), f);
                c.Text(p, mx - 8 * (1 - e), my - 3, 19, col, FontWeight.Bold);
                c.Text("+2.4%", mx - 8 * (1 - e) + c.Measure(p, 19, FontWeight.Bold) + 6, my + 6, 10, Rgba(Green, a * e), FontWeight.SemiBold);
                float reveal = TabMaker.Bezier(k.Since(k.OpenedAt + 0.36f + 0.05f, 1f), 0.3f, 0, 0.2f, 1);
                float top = my + 80 - 46, slide = (k.Clock / 9f) % 1;
                c.PushClip(tx, top - 2, tw * reveal, 50);
                const int n = 48;
                Vector2 P(int s)
                {
                    float x = tw * s / n, svg = x / (2 * tw) * 280 + slide * 140, u = svg / 140 * MathF.Tau;
                    return new(tx + x, top + 23 - 8 * MathF.Sin(u) - 5 * MathF.Sin(2 * u + 1) - 3 * MathF.Sin(5 * u + 0.5f));
                }
                for (int s = 1; s <= n; s++) { var p0 = P(s - 1); var p1 = P(s); c.Line(p0.X, p0.Y, p1.X, p1.Y, 1.6f, WhiteA(0.85f * a)); }
                c.PopClip();
                break;
            }
            case 2: // weather: the sun, the temperature, five little bars
            {
                float e = In(0), ox = mx - 8 * (1 - e);
                Weather(c, "sun", ox + 12, my + 12, 24);
                c.Text("38°", ox + 31, my - 1, 19, WhiteA(a * e), FontWeight.Bold);
                c.Text("Riyadh", ox + 31 + c.Measure("38°", 19, FontWeight.Bold) + 7, my + 7, 10.5f, WhiteA(0.4f * a * e), FontWeight.Medium);
                int[] temps = [38, 39, 36, 34, 37]; string[] days = ["T", "F", "S", "S", "M"];
                float bw = (mw - 32) / 5, bottom = my + 80 - 14;
                for (int j = 0; j < 5; j++)
                {
                    float grow = TabMaker.Calm(k.Since(k.OpenedAt + 0.38f + i * 0.05f + j * 0.04f, 0.64f));
                    if (k.Clock - t.BarsAt < 1.2f) grow *= 0.35f + 0.65f * TabMaker.Calm(k.Since(t.BarsAt + j * 0.06f, 0.7f));
                    float bh = (temps[j] - 31) * 3.4f * Math.Clamp(grow, 0, 1.1f), bx = mx + j * (bw + 8);
                    if (bh > 0.5f) c.Round(bx, bottom - bh, bw, bh, MathF.Min(4, bh / 2), j == 0 ? WhiteA(a) : WhiteA(0.2f * a));
                    c.Text(days[j], bx + bw / 2, bottom + 2, 8.5f, WhiteA(0.4f * a), FontWeight.SemiBold, 0.5f);
                }
                break;
            }
            case 3: // the top stories, points ticking up
            {
                (uint col, string title)[] rows = [(Green, "Whistle: speech to text"), (Orange, "Deno is joining Cloudflare"), (0x8E8E93, "Show HN: a tiny editor")];
                for (int j = 0; j < 3; j++)
                {
                    float e = In(j), ra = a * e, rx = mx - 8 * (1 - e), ry = my + j * 28;
                    c.Round(rx, ry, mw, 24, 8, WhiteA(0.06f * ra));
                    float pop = j == 2 && k.Clock - t.PointsAt < 0.6f ? 1 + 1.2f * (1 - TabMaker.Calm(k.Since(t.PointsAt, 0.52f))) : 1;
                    c.Circle(rx + 12, ry + 12, 3 * pop, Rgba(rows[j].col, ra));
                    string pts = $"{t.Points[j]} pts";
                    float pw = c.Measure(pts, 9.5f, FontWeight.Medium);
                    c.Text(rows[j].title, rx + 22, ry + 4.5f, 10.5f, WhiteA(ra), FontWeight.SemiBold, 0, mw - 22 - pw - 16);
                    c.Text(pts, rx + mw - 9, ry + 6, 9.5f, WhiteA(0.4f * ra), FontWeight.Medium, 1f);
                }
                break;
            }
        }
    }

    // ---------------------------------------------------------------- 2 · build

    static void BuildView(Canvas c, IslandModel m, float x0, float y0, float alpha)
    {
        var k = m.Maker;
        float ax = x0 + PanelX, ay = y0 + PanelY;

        // your sentence flies up from the line and becomes the title
        float u = k.Since(k.BuildAt, 0.82f), e = TabMaker.Calm(u);
        var to = new Vector2(ax + 56, ay + 1);
        var from = new Vector2(x0, y0) + k.TitleFrom;
        var at = Vector2.Lerp(from, to, e);
        float sc = Lerp(k.TitleFs / 16, 1, e);
        var ink = Lerp(Ink, White, Math.Clamp(u / 0.3f, 0, 1));
        var old = c.Transform;
        c.Transform = Matrix3x2.CreateScale(sc, to) * Matrix3x2.CreateTranslation(at - to) * old;
        c.Text(k.Title, to.X, to.Y, 16, WithA(ink, alpha), FontWeight.SemiBold, 0, (PanelW - 56) / MathF.Max(sc, 1));
        c.Transform = old;

        // the step he's on
        c.PushClip(ax + 56, ay + 22, PanelW - 56, 20);
        if (k.StepOld is { } so && k.Clock - so.At < 0.5f && k.Step is { } sn)
        {
            float o = TabMaker.EaseIn(k.Since(sn.At, 0.38f));
            if (o < 1) StepLine(c, k, so.Step, ax + 56, ay + 24 - 14 * o, (1 - o) * alpha, false);
        }
        if (k.Step is { } s)
        {
            float i = TabMaker.Calm(k.Since(s.At, 0.56f));
            StepLine(c, k, s.Step, ax + 56, ay + 24 + 14 * (1 - i), Math.Clamp(i, 0, 1) * alpha, !k.Done && !k.Failed);
        }
        c.PopClip();

        // the dot field: two soft rings travel out from Bloub while he works
        Dots(c, k, ax, ay + 56, alpha);

        // the tab turns solid where the dots were, and its data decodes in
        if (k.Preview is { } tab && k.SolidAt > 0 && k.Clock >= k.SolidAt)
        {
            float r = TabMaker.Calm(k.Since(k.SolidAt + 0.08f, 0.64f));
            float adj = k.Clock - k.AdjustAt < 0.6f ? 0.4f + 0.6f * TabMaker.Calm(k.Since(k.AdjustAt, 0.52f)) : 1;
            float ca = Math.Clamp(r, 0, 1) * adj * alpha;
            if (ca > 0.01f)
            {
                c.Transform = Matrix3x2.CreateScale(0.97f + 0.03f * Math.Clamp(r, 0, 1.05f), new Vector2(ax + PanelW / 2, ay + 56 + 100)) * old;
                c.PushLayer(ca);
                float dec = MathF.Min(k.Since(k.SolidAt + 0.18f, 0.52f), k.Clock - k.AdjustAt < 0.6f ? k.Since(k.AdjustAt, 0.52f) : 1);
                c.DecodeU = dec; c.DecodeSeed = (int)(k.Clock * 30);
                CustomView(c, m, tab, ax, ay + 56, PanelW, 204);
                c.DecodeU = 1;
                c.PopLayer();
                c.Transform = old;
            }
        }

        // the bar: where it comes from, a tweak or two, and Add to island
        if (k.BarAt > 0 && k.Clock >= k.BarAt) Bar(c, m, ax, ay + PanelH - 40, alpha);
    }

    /// <summary>
    /// The step line. While he works it's alive like Claude's own: the spark turns and breathes, a soft light
    /// sweeps across the words, and the seconds count up.
    /// </summary>
    static void StepLine(Canvas c, TabMaker k, TabBuilder.Step s, float x, float y, float a, bool working)
    {
        if (a < 0.01f) return;
        if (working && !s.Done && !s.Bad)
        {
            float t = k.Clock - k.BuildAt;
            Spark(c, x + 7, y + 8.5f, 7, t, a);
            x += 20;
            float lw = c.Measure(s.Lead, 12, FontWeight.Medium), bw = c.Measure(s.Bold, 12, FontWeight.SemiBold);
            const float band = 46;
            float start = x, end = x + lw + 6 + bw;
            float sweep = start - band + (end - start + band * 2 + 80) * ((t * 0.55f) % 1);   // one pass every ~1.8 s
            float Phase(float x0, float w) => (sweep - x0 + band) / (w + band * 2);
            c.ShimmerText(s.Lead, x, y, 12, 0.9f * a, Phase(x, lw), FontWeight.Medium);
            c.ShimmerText(s.Bold, x + lw + 6, y, 12, a, Phase(x + lw + 6, bw), FontWeight.SemiBold);
            if (t > 3)
            {
                string secs = t < 60 ? $"{(int)t}s" : $"{(int)t / 60}m {(int)t % 60:00}s";
                c.Text(secs, end + 10, y, 12, WhiteA(0.3f * a), FontWeight.Medium);
            }
            return;
        }
        if (s.Done)
        {
            c.Circle(x + 7, y + 8, 7, Rgba(Green, a));
            c.Icon('', x + 7, y + 8.5f, 8, WithA(Rgba(0x000000), a));
            x += 20;
        }
        else if (s.Bad)
        {
            c.Circle(x + 7, y + 8, 7, Rgba(Orange, a));
            c.Text("!", x + 7, y - 0.5f, 11, WithA(Rgba(0x000000), a), FontWeight.Bold, 0.5f);
            x += 20;
        }
        else if (s.Live)
        {
            c.Circle(x + 3, y + 8, 3, Rgba(Green, a));
            x += 12;
        }
        c.Text(s.Lead, x, y, 12, WhiteA(0.45f * a), FontWeight.Medium);
        x += c.Measure(s.Lead, 12, FontWeight.Medium) + 6;
        c.Text(s.Bold, x, y, 12, WhiteA(0.85f * a), FontWeight.SemiBold, 0, 520);
    }

    /// <summary>Claude's spark: rays that turn slowly and breathe one after another.</summary>
    static void Spark(Canvas c, float cx, float cy, float r, float t, float a)
    {
        var col = Rgba(0xD97757, a);
        const int n = 8;
        float rot = t * 0.8f;
        for (int i = 0; i < n; i++)
        {
            float ang = rot + i * MathF.Tau / n;
            float len = r * (0.5f + 0.5f * (0.5f + 0.5f * MathF.Sin(t * 4.4f - i * 0.785f)));
            float dx = MathF.Cos(ang), dy = MathF.Sin(ang);
            c.Line(cx + dx * r * 0.18f, cy + dy * r * 0.18f, cx + dx * len, cy + dy * len, r * 0.27f, col);
        }
    }

    static void Dots(Canvas c, TabMaker k, float x, float y, float alpha)
    {
        if (k.DotsAt < 0) return;
        float t = k.Clock - k.DotsAt;
        float show = Math.Clamp((t - 0.25f) / 0.5f, 0, 1) * MathF.Min(1, t / 0.6f);
        float gone = k.DotsOffAt > 0 ? k.Since(k.DotsOffAt, 0.55f) : 0;
        if (show <= 0 || gone >= 1) return;
        const float sp = 12, ox = 22, oy = -34;
        // one primitive for the whole field: it changes every frame anyway, so hashing each dot would only cost
        ulong key = new FrameHash(81).F(t).A(show).A(gone).A(alpha).V;
        c.Custom(x, y, PanelW, 258, key, (ctx, brush) =>
        {
            for (float dy = sp / 2; dy < 258; dy += sp)
                for (float dx = sp / 2; dx < PanelW; dx += sp)
                {
                    float dist = MathF.Sqrt((dx - ox) * (dx - ox) + (dy - oy) * (dy - oy));
                    float wave = 0;
                    for (int i = 0; i < 2; i++)
                    {
                        float R = ((t + i * 0.9f) % 1.8f) * 520;
                        wave = MathF.Max(wave, MathF.Max(0, 1 - MathF.Abs(dist - R) / 34));
                    }
                    wave *= wave;
                    float a = (0.09f + wave * 0.55f) * show * (1 - gone) * alpha;
                    if (a < 0.01f) continue;
                    float r = 1.05f + wave * 0.9f;
                    brush.Color = WhiteA(MathF.Min(1, a));
                    ctx.FillEllipse(new Vortice.Direct2D1.Ellipse(new Vector2(x + dx, y + dy), r, r), brush);
                }
        });
    }

    static string Every(double s) => s < 60 ? $"{s:0} s" : s < 3600 ? $"{s / 60:0} min" : $"{s / 3600:0.#} h";

    static void Bar(Canvas c, IslandModel m, float x, float y, float alpha)
    {
        var k = m.Maker;
        float e = TabMaker.Calm(k.Since(k.BarAt, 0.64f)), a = Math.Clamp(e, 0, 1) * alpha;
        y += 12 * (1 - e);
        float cy = y + 20;
        float right = x + PanelW;
        if (k.Failed)
        {
            right = BarButton(c, m, right, cy, "Try again", '', "mk:retry", primary: true, a);
            BarButton(c, m, right - 8, cy, "Change it", '', "mk:edit", primary: false, a);
            return;
        }
        if (k.Preview is not { } tab) return;
        right = BarButton(c, m, right, cy, "Add to island", '\0', "mk:add", primary: true, a);
        for (int i = k.Tweaks.Count - 1; i >= 0; i--)
        {
            var tw = k.Tweaks[i];
            float g = tw.GoneAt > 0 ? k.Since(tw.GoneAt, 0.26f) : 0;
            if (g >= 1) continue;
            float bw = BarButtonW(c, tw.Label, '', false);
            var old = c.Transform;
            c.Transform = Matrix3x2.CreateScale(1 - 0.1f * g, new Vector2(right - 8 - bw / 2, cy)) * old;
            right = BarButton(c, m, right - 8, cy, tw.Label, '', "mk:tw:" + i, primary: false, a * (1 - g));
            c.Transform = old;
        }
        string src = $"{tab.Data.Host} · live every {Every(tab.Recipe.Every)}";
        c.Circle(x + 3, cy, 3, Rgba(Green, a));
        c.Text(src, x + 13, cy - 8.5f, 11.5f, WhiteA(0.4f * a), FontWeight.Medium, 0, MathF.Max(40, right - x - 30));
    }

    static float BarButtonW(Canvas c, string label, char icon, bool primary) =>
        primary ? c.Measure(label, 13, FontWeight.SemiBold) + 17 + 15 + (icon == '\0' ? 26 : 25)
                : c.Measure(label, 12.5f, FontWeight.Medium) + 10 + 13 + 23;

    /// <summary>Draws a bar button whose right edge is at <paramref name="right"/>; returns its left edge.</summary>
    static float BarButton(Canvas c, IslandModel m, float right, float cy, string label, char icon, string hit, bool primary, float a)
    {
        float w = BarButtonW(c, label, icon, primary), x = right - w;
        bool hover = a > 0.5f && m.Hit(hit, x, cy - 18, w, 36);
        if (primary)
        {
            c.Round(x, cy - 18, w, 36, 18, WhiteA(a));
            float tx = x + 17;
            if (icon != '\0') { c.Icon(icon, tx + 7, cy, 13, WithA(Rgba(0x000000), a)); tx += 21; }
            c.Text(label, tx, cy - 9, 13, WithA(Rgba(0x000000), a), FontWeight.SemiBold);
            if (icon == '\0')
            {
                float kx = tx + c.Measure(label, 13, FontWeight.SemiBold) + 8;
                c.Round(kx, cy - 9, 18, 18, 5, WithA(Rgba(0x000000), 0.08f * a));
                c.Text("↵", kx + 9, cy - 8, 10.5f, WithA(Rgba(0x000000), a), FontWeight.Bold, 0.5f);
            }
        }
        else
        {
            c.Round(x, cy - 17, w, 34, 17, WhiteA((hover ? 0.12f : 0.06f) * a));
            c.Icon(icon, x + 10 + 8.5f, cy, 14, WhiteA((hover ? 1 : 0.7f) * a));
            c.Text(label, x + 10 + 23, cy - 9, 12.5f, WhiteA((hover ? 1 : 0.7f) * a), FontWeight.Medium);
        }
        return x;
    }

    // ================================================================ over everything: the line, the stamp, the ring, the confetti, the chip

    /// <summary>Drawn after Bloub, clipped to the island.</summary>
    public static void MakerOverlay(Canvas c, IslandModel m)
    {
        var k = m.Maker;
        if (m.Mode != Mode.Full) return;
        float x0 = IslandModel.CX - m.Geometry(Mode.Full).W / 2, y0 = m.Top;
        if (k.LineShown) TheLine(c, m, x0, y0);
        if (k.Badged) Stamp(c, m);
        foreach (var (at, t0, s) in k.Rings)
        {
            float u = TabMaker.Bezier(k.Since(t0, 0.64f), 0.15f, 0.7f, 0.3f, 1), sc = (0.25f + 0.7f * u) * s;
            c.StrokeEllipse(x0 + at.X, y0 + at.Y, 40 * sc, 9 * sc, 1.5f, WhiteA(0.75f * 0.9f * (1 - u)));
        }
        foreach (var (at, deg, r0, s, t0) in k.Zips)
        {
            float u = TabMaker.EaseOut(k.Since(t0, 0.48f));
            if (k.Clock < t0) continue;
            float rr = u < 0.4f ? r0 + 8 * s * u / 0.4f : r0 + 8 * s + 10 * s * (u - 0.4f) / 0.6f;
            float sx = u < 0.4f ? 0.2f + 0.8f * u / 0.4f : 1 - 0.7f * (u - 0.4f) / 0.6f, op = u < 0.4f ? 1 : 1 - (u - 0.4f) / 0.6f;
            float ang = deg * MathF.PI / 180;
            var p = new Vector2(x0, y0) + at + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rr;
            var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 5 * sx;
            c.Line(p.X - d.X, p.Y - d.Y, p.X + d.X, p.Y + d.Y, 2, WhiteA(op));
        }
        foreach (var (from, to, size, col, t0, dur) in k.Confetti)
        {
            float u = TabMaker.Bezier(k.Since(t0, dur), 0.2f, 0.7f, 0.3f, 1);
            Vector2 p; float sc, op;
            var dir = Vector2.Normalize(to - from + new Vector2(0.001f, 0));
            if (u < 0.45f) { float v = u / 0.45f; p = Vector2.Lerp(from, to, v); sc = 0.4f + 0.6f * v; op = 1; }
            else { float v = (u - 0.45f) / 0.55f; p = Vector2.Lerp(to, to + new Vector2(dir.X * 6, 26), v); sc = 1 - 0.4f * v; op = 1 - v; }
            c.Circle(x0 + p.X, y0 + p.Y, size / 2 * sc, Rgba(col, op));
        }
        if (k.Now == TabMaker.Stage.Adding && k.Clock >= k.ChipAt) FlyingChip(c, m, x0, y0);
    }

    /// <summary>The white line: Bloub's own body stretched out, his eyes at its left end, your words, the kind chip and the go button.</summary>
    static void TheLine(Canvas c, IslandModel m, float x0, float y0)
    {
        var k = m.Maker;
        var (l, cy, w, h, shade) = k.Line();
        l += x0; cy += y0;
        float ps = k.Ps.Value;
        var old = c.Transform;
        var pill = Matrix3x2.CreateScale(1 + (1 - ps) * 0.25f, ps, new Vector2(l, cy)) * Matrix3x2.CreateTranslation(k.Sx.Value, 0);
        c.Transform = pill * old;
        float r = MathF.Min(w, h) / 2;
        c.Round(l, cy - h / 2, w, h, r, LineWhite);
        if (shade > 0.01f)
        {
            float d = MathF.Min(w, h);
            c.RadialRound(l, cy - h / 2, w, h, r, new Vector2(l + 0.38f * d, cy - h / 2 + 0.3f * d), 0.75f * d,
                [(0, Rgba(0xFFFFFF)), (0.72f, Rgba(0xE9E9EF)), (1, Rgba(0xBDBDC7))], shade);
        }

        float open = k.Since(k.ArmedAt, 0.25f);
        float tx = l + 69;
        if (k.Armed && open > 0)
        {
            string text = k.Field.Text;
            // the side first: the go button, and the kind chip left of it; your words stop short of them
            float side = k.Since(k.ArmedAt, 0.15f);
            float gx = l + w - 10 - 44, gcx = gx + 22;
            float ct = k.Reading.Kind != null ? Math.Clamp(k.ChipT.Value, 0, 1) : 0;
            float chipFull = k.Reading.Kind is { } kd ? 9 + 17 + 6 + c.Measure(kd.Name, 12.5f, FontWeight.SemiBold) + 12 : 0;
            float chx = gx - 8 - chipFull * ct;
            float right = (ct > 0.01f ? chx : gx) - 12;
            c.PushClip(tx - 2, cy - h / 2, MathF.Max(0, right - tx + 2), h);
            if (text.Length == 0)
            {
                // the grey question / suggestion, rolling one line at a time
                RollLine(c, k, k.Ph, k.PhOld, tx, cy - 23 * 0.7f, 26, 0.52f, 0.24f, (cc, s, x, y, a) =>
                    cc.Text(s, x, y, 23, WithA(PhGrey, a * open), FontWeight.SemiBold));
                k.Pan.Snap(0);
                if (m.Focused == k.Field && ((k.Clock - k.Field.ChangedAt) % 1.06f) < 0.53f) c.Round(tx - 1, cy - 23 * 0.6f, 2, 23 * 1.2f, 1, Ink);
                c.PopClip();
            }
            else
            {
                float fs = MathF.Round(k.Fs.Value * 2) / 2, y = cy - fs * 0.7f;
                // a sentence longer than the line slides left, so the caret is always in view
                float vw = right - tx, total = c.Measure(text, fs, FontWeight.SemiBold);
                float caretX = c.Measure(text[..Math.Min(k.Field.Caret, text.Length)], fs, FontWeight.SemiBold);
                float pan = k.Pan.Target;
                if (total <= vw - 4) pan = 0;
                else
                {
                    if (caretX - pan > vw - 16) pan = caretX - vw + 16;
                    if (caretX - pan < 16) pan = caretX - 16;
                    pan = Math.Clamp(pan, 0, total - vw + 16);
                }
                k.Pan.To(pan);
                float off = MathF.Max(0, k.Pan.Value);
                k.TextX = tx; k.TextFs = fs;
                // the words he understood go dark
                int at = 0; float x = tx - off;
                foreach (var t in k.Reading.Tokens)
                {
                    if (t.At > at) x += c.Measure(text[at..t.At], fs, FontWeight.SemiBold);
                    var col = WordGrey;
                    if (t.Hit) col = Lerp(WordGrey, Ink, k.HitAt.TryGetValue(t.At, out var ha) ? TabMaker.EaseOut(k.Since(ha, 0.48f)) : 1);
                    c.Text(t.Word, x, y, fs, WithA(col, open), FontWeight.SemiBold);
                    x += c.Measure(t.Word, fs, FontWeight.SemiBold);
                    at = t.At + t.Word.Length;
                }
                // the caret
                if (m.Focused == k.Field && (k.Typing || ((k.Clock - k.Field.ChangedAt) % 1.06f) < 0.53f))
                    c.Round(tx - off + caretX, cy - fs * 0.6f, 2, fs * 1.2f, 1, Ink);
                c.PopClip();
                // more words past either edge: they fade into the line
                if (off > 1) c.GradientRound(tx - 2, cy - h / 2 + 6, 30, h - 12, 0, new(tx - 2, 0), new(tx + 28, 0), LineWhite, WithA(LineWhite, 0));
                if (total - off > vw + 1) c.GradientRound(right - 30, cy - h / 2 + 6, 32, h - 12, 0, new(right - 30, 0), new(right + 2, 0), WithA(LineWhite, 0), LineWhite);
            }

            // the side: what kind of tab he thinks it is, and the go button (it grows a little under the mouse)
            float gh = Math.Clamp(k.GoHover.Value, -0.2f, 1.2f), gd = Math.Clamp(k.GoDark.Value, 0, 1);
            c.Circle(gcx, cy, 22 + 2.5f * gh, WithA(Lerp(LineChip, Ink, MathF.Max(gd, 0.35f * gh)), side));
            c.Icon('', gcx + 1.5f * gh, cy, 16, WithA(Lerp(PhGrey, White, MathF.Max(gd, 0.5f * gh)), side));
            if (ct > 0.01f && k.Reading.Kind is { } kind)
            {
                float full = chipFull, cw = full * ct;
                c.PushRoundClip(chx, cy - 16, cw, 32, 16);
                c.Round(chx, cy - 16, full, 32, 16, WithA(LineChip, ct * side));
                c.Icon(kind.Icon, chx + 9 + 8.5f, cy, 14, WithA(Ink, ct * side));
                c.Text(kind.Name, chx + 9 + 17 + 6, cy - 8.5f, 12.5f, WithA(Ink, ct * side), FontWeight.SemiBold);
                c.PopLayer();
            }
        }

        // his eyes: Bloub's own, at the line's left end, looking at you
        var f = m.Bloub.Engine.Sample(m.Clock);
        float er = MathF.Max(1, k.EyeR.Value), es = k.EyeS.Value;
        var ec = new Vector2(l + er + k.EyeDx.Value, cy);
        var group = Matrix3x2.CreateTranslation(-ec) * Matrix3x2.CreateScale(es) * Matrix3x2.CreateRotation(k.Tilt.Value * MathF.PI / 180)
                  * Matrix3x2.CreateTranslation(ec + new Vector2(0, k.Hop.Value));
        foreach (var eye in f.Eyes)
        {
            if (eye.Alpha <= 0.002f) continue;
            c.Transform = eye.Matrix * Matrix3x2.CreateScale(er / 100f) * Matrix3x2.CreateTranslation(ec) * group * pill * old;
            float hw = eye.W / 2, hh = eye.H / 2;
            c.Round(-hw, -hh, eye.W, eye.H, MathF.Min(hw, hh), WithA(Ink, MathF.Min(1, eye.Alpha)));
        }
        c.Transform = old;
    }

    /// <summary>The ✓ badge on his top-left edge, the notch it bites out of him (the island's black), the tick drawing itself.</summary>
    static void Stamp(Canvas c, IslandModel m)
    {
        var k = m.Maker;
        var xf = k.BloubXf(m);
        var ctr = new Vector2(m.Face.X, m.Face.Y + m.BloubLift);
        float r = 11 * m.Face.Size.Value * m.Bloub.Squish.Value;
        var at = ctr + new Vector2(-0.684f, -0.684f) * r;

        // the badge: slams down from big and twisted (scale 2.8 → .8 → 1.1 → 1); lets go: 1 → 1.15 → 0
        float bs, rot, ba;
        float su = k.Since(k.StampAt, 0.46f), sb = TabMaker.Bezier(su, 0.3f, 0, 0.4f, 1);
        if (sb < 0.52f) { float v = sb / 0.52f; bs = Lerp(2.8f, 0.8f, v); rot = Lerp(-80, 8, v); ba = v; }
        else if (sb < 0.76f) { float v = (sb - 0.52f) / 0.24f; bs = Lerp(0.8f, 1.1f, v); rot = Lerp(8, -3, v); ba = 1; }
        else { float v = (sb - 0.76f) / 0.24f; bs = Lerp(1.1f, 1, v); rot = Lerp(-3, 0, v); ba = 1; }
        float ns = k.Clock < k.StampAt + 0.24f ? 0 : NotchScale(TabMaker.EaseOut(k.Since(k.StampAt + 0.24f, 0.42f)));
        if (k.UnstampAt > 0)
        {
            float uu = TabMaker.EaseIn(k.Since(k.UnstampAt, 0.38f));
            bs *= uu < 0.3f ? 1 + 0.15f * uu / 0.3f : 1.15f * (1 - (uu - 0.3f) / 0.7f);
            ns *= 1 - TabMaker.Calm(k.Since(k.UnstampAt + 0.12f, 0.42f));
        }
        var old = c.Transform;
        c.Transform = xf * old;
        if (ns > 0.01f) c.Circle(at.X, at.Y, 0.468f * r * ns, Rgba(0x000000));
        if (bs > 0.01f && ba > 0.01f)
        {
            c.Transform = Matrix3x2.CreateRotation(rot * MathF.PI / 180, at) * Matrix3x2.CreateScale(bs, at) * xf * old;
            float br = 0.347f * r;
            c.Circle(at.X, at.Y, br, Rgba(Green, ba));
            float tick = TabMaker.EaseOut(k.Since(k.StampAt + 0.33f, 0.28f));
            if (tick > 0.01f)
            {
                float q = r / 19;
                Vector2 p0 = at + new Vector2(-2.9f, 0.3f) * q, p1 = at + new Vector2(-0.9f, 2.2f) * q, p2 = at + new Vector2(2.9f, -1.7f) * q;
                float l1 = Vector2.Distance(p0, p1), l2 = Vector2.Distance(p1, p2), d = tick * (l1 + l2);
                var e1 = d <= l1 ? Vector2.Lerp(p0, p1, d / l1) : p1;
                c.Line(p0.X, p0.Y, e1.X, e1.Y, 1.8f * q, WithA(Rgba(0x000000), ba));
                if (d > l1) { var e2 = Vector2.Lerp(p1, p2, (d - l1) / l2); c.Line(p1.X, p1.Y, e2.X, e2.Y, 1.8f * q, WithA(Rgba(0x000000), ba)); }
            }
        }
        c.Transform = old;
    }

    static float NotchScale(float u) => u < 0.4f ? 1.2f * u / 0.4f : 1.2f - 0.2f * (u - 0.4f) / 0.6f;

    /// <summary>The finished tab as a white chip, gliding along an arc into its slot in your bar.</summary>
    static void FlyingChip(Canvas c, IslandModel m, float x0, float y0)
    {
        var k = m.Maker;
        if (k.AddingId is not { } id) return;
        float w = 11 + 17 + 7 + c.Measure(k.ChipName, 13, FontWeight.SemiBold) + 14;
        var from = k.ChipFrom;
        Vector2 at; float sc = 1, a = 1;
        if (k.FlightAt < 0 || k.Clock < k.FlightAt)
        {
            float u = TabMaker.Calm(k.Since(k.ChipAt, 0.56f));
            at = from; sc = 0.3f + 0.7f * u; a = Math.Clamp(u, 0, 1);
        }
        else
        {
            float u = k.Since(k.FlightAt, 0.76f);
            if (u >= 1) return;
            var s = m.Tabs[id];
            var to = new Vector2(s.X.Target + MathF.Max(s.W.Target, 40) / 2, 18 + 14);
            float e = TabMaker.Calm(u);
            at = Vector2.Lerp(from, to, e) - new Vector2(0, MathF.Sin(e * MathF.PI) * 46);
            sc = 1 - 0.04f * MathF.Sin(u * MathF.PI);
        }
        var old = c.Transform;
        var p = new Vector2(x0, y0) + at;
        c.Transform = Matrix3x2.CreateScale(sc, p) * old;
        c.Glow(p.X, p.Y + 10, w * 0.6f, 24, 0x000000, 0.5f * a);
        c.Round(p.X - w / 2, p.Y - 16, w, 32, 16, WhiteA(a));
        c.Icon(k.ChipIcon, p.X - w / 2 + 11 + 8.5f, p.Y, 14, WithA(Rgba(0x000000), a));
        c.Text(k.ChipName, p.X - w / 2 + 11 + 17 + 7, p.Y - 9, 13, WithA(Rgba(0x000000), a), FontWeight.SemiBold);
        c.Transform = old;
    }
}
