using System.Numerics;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Island.Lab.Render;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

/// <summary>
/// Giving Bloub a file: the island while something hangs over it (he is the box with its slot open, and says what
/// he sees), the row once he has it (what it is, and the two or three useful things to do with it), the count
/// beside him in the pill, and the Pocket tab that lists everything he holds.
/// </summary>
public static partial class Scenes
{
    public const float PocketBadgeW = 28;

    /// <summary>A spring from 0 to 1 that goes a little past and settles.</summary>
    static float PopIn(float u)
    {
        u = Math.Clamp(u, 0, 1);
        return 1 + 2.70158f * MathF.Pow(u - 1, 3) + 1.70158f * MathF.Pow(u - 1, 2);
    }

    /// <summary>A small white Bloub (the mark of "give it to Claude").</summary>
    static void MiniBloub(Canvas c, float cx, float cy, float r, float a)
    {
        c.Circle(cx, cy, r, Rgba(0xF2F2F7, a));
        float ew = r * 0.27f, eh = r * 0.62f;
        c.Round(cx - r * 0.42f, cy - eh / 2, ew, eh, ew / 2, new Color4(0, 0, 0, a));
        c.Round(cx + r * 0.15f, cy - eh / 2, ew, eh, ew / 2, new Color4(0, 0, 0, a));
    }

    static string DropTitle(IReadOnlyList<PocketItem> items) =>
        items.Count == 1 ? items[0].Name : items.All(i => i.Kind == PocketKind.Folder) ? $"{items.Count} folders" : $"{items.Count} files";

    // ================================================================ something hangs over the island
    //
    // A tall card: Bloub big in the middle (the box, slot open), a light behind him that breathes, small arrows
    // falling into his slot, and under him what you are holding. When you let go the card grows a little and the
    // things to do with the file appear under its name: he never moves, so the sheet has one place to go.

    /// <summary>Where he sits in the card (from its top), and how big he is.</summary>
    public const float DropFaceY = 96, DropFaceR = 44;

    /// <summary>The file's tile and name, centred as one group. Returns nothing; the name is cut to fit.</summary>
    static void DropName(Canvas c, IReadOnlyList<PocketItem> items, float cx, float y, float maxW, float a)
    {
        const float Size = 17, Tile = 24;
        string name = c.Fit(DropTitle(items), Size, FontWeight.SemiBold, maxW - Tile - 10);
        float nw = c.Measure(name, Size, FontWeight.SemiBold), x = cx - (Tile + 10 + nw) / 2;
        PocketTile(c, items[0], x, y, Tile, a);
        if (items.Count > 1)
        {
            string n = items.Count.ToString();
            float bw = MathF.Max(16, c.Measure(n, 10, FontWeight.Bold) + 8);
            c.Round(x + Tile - bw / 2 - 1, y - 6, bw, 16, 8, Rgba(Blue, a));
            c.Text(n, x + Tile - 1, y - 5.5f, 10, WhiteA(a), FontWeight.Bold, 0.5f);
        }
        c.Text(name, x + Tile + 10, y + 0.5f, Size, WhiteA(a), FontWeight.SemiBold);
    }

    static void DropCatch(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        var items = m.Hovering;
        if (items.Count == 0) return;
        float t = m.ModeTime, cx = x0 + w / 2, by = y0 + DropFaceY;
        float g = Math.Clamp(t / 0.45f, 0, 1);

        // the light behind him breathes while he waits
        c.Glow(cx, by + 8, 180, 116, 0x4FA8E8, (0.40f + 0.10f * (float)Math.Sin(m.Clock * 3.4f)) * g);

        // "in here": small arrows fall towards his slot, one after the other
        for (int i = 0; i < 3; i++)
        {
            float ph = (float)((m.Clock * 0.85f + i / 3f) % 1), y = y0 + 10 + ph * 34;
            float a = MathF.Sin(ph * MathF.PI) * 0.8f * g, s = 6.5f;
            if (a < 0.02f) continue;
            c.Line(cx - s, y - s * 0.55f, cx, y + s * 0.45f, 2.3f, WhiteA(a));
            c.Line(cx, y + s * 0.45f, cx + s, y - s * 0.55f, 2.3f, WhiteA(a));
        }

        // under him: what you are holding, and what happens when you let go
        float e = 1 - MathF.Pow(1 - Math.Clamp((t - 0.14f) / 0.4f, 0, 1), 3);
        if (e > 0.01f) DropName(c, items, cx, y0 + 174 + (1 - e) * 10, w - 70, e);
        string hint = items[0].Kind switch
        {
            PocketKind.Text => "Let go and I'll save it to your Notes",
            PocketKind.Link => "Let go and I'll keep the link",
            _ => items.Count > 1 ? "Let go and I'll hold them" : "Let go and I'll hold it",
        };
        float e2 = Math.Clamp((t - 0.26f) / 0.4f, 0, 1);
        if (e2 > 0.01f) c.ShimmerText(hint, cx, y0 + 205 + (1 - e2) * 8, 13, e2, (float)((m.Clock * 0.55f) % 1.5f) / 1.5f, FontWeight.Regular, 0.5f);
    }

    // ================================================================ he has it: what it is, what you can do with it

    static void DropRow(Canvas c, IslandModel m, float x0, float y0, float w, float h)
    {
        var items = m.Dropped;
        if (items.Count == 0) return;
        // (the buttons wait until he has swallowed it)
        float a = m.Film.Now is OrbFilm.Clip.File ? m.Film.TextIn : 1;
        float since = (float)(m.Clock - m.DroppedShownAt), cx = x0 + w / 2, by = y0 + DropFaceY;
        var first = items[0];

        // the light behind him: blue while the sheet goes in, green once he has it
        c.Glow(cx, by + 8, 180, 116, 0x4FA8E8, 0.4f * (1 - a));
        c.Glow(cx, by + 8, 180, 116, 0x30D158, 0.3f * a);

        // ---- its tile and name (where they were while you held it), then what it is
        DropName(c, items, cx, y0 + 174, w - 70, 1);
        bool kept = items.Any(i => !i.Demo && i.Kind != PocketKind.Text);
        string meta = items.Count > 1 ? c.Fit(string.Join(", ", items.Select(i => i.Name)), 12.5f, FontWeight.Regular, w - 80)
                    : first.Meta + (first.Kind == PocketKind.Text ? " · saved to Notes" : kept ? " · in your Pocket" : "");
        c.Text(meta, cx, y0 + 203, 12.5f, WhiteA(0.46f), FontWeight.Regular, 0.5f);

        // ---- top right: forget it (he keeps everything unless told otherwise)
        if (kept && m.RowNote == null && a > 0.01f)
        {
            float fx = x0 + w - 38, fy = y0 + 36;
            bool fh = m.Hit("dz:forget", fx - 15, fy - 15, 30, 30);
            c.Circle(fx, fy, 13, WhiteA((fh ? 0.2f : 0.09f) * a));
            c.Icon('', fx, fy, 8.5f, fh ? Rgba(Red, a) : WhiteA(0.6f * a));
            if (fh) c.Text("Don't keep it", fx - 20, fy - 8.5f, 11.5f, WhiteA(0.7f * a), FontWeight.SemiBold, 1f);
        }

        // ---- the things to do with it (or, once you did one, the word that it is done)
        if (a < 0.01f) return;
        if (m.RowNote is { } note)
        {
            float cy = y0 + 240;
            float u = Math.Clamp((float)(m.Clock - note.At) / 0.3f, 0, 1), pop = PopIn(u);
            float tw = c.Measure(note.Text, 13.5f, FontWeight.SemiBold), nx = cx - (28 + tw) / 2, ny = cy + 15;
            c.Circle(nx + 10, ny, 10 * (0.6f + 0.4f * pop), Rgba(Green, a));
            c.Line(nx + 5.6f, ny, nx + 8.8f, ny + 3.2f, 2, WhiteA(a * u));
            c.Line(nx + 8.8f, ny + 3.2f, nx + 14.4f, ny - 3.2f, 2, WhiteA(a * u));
            c.Text(note.Text, nx + 28, cy + 5 + (1 - u) * 5, 13.5f, WhiteA(0.92f * a * u), FontWeight.SemiBold);
            return;
        }
        // Claude first, then the everyday things, and "More": the same file, big, in the Pocket
        var acts = new List<PocketAction>();
        if (Pocket.Claude(items) is { Length: > 0 } ai) acts.Add(ai[0] with { Label = ai[0].Id == "ask" ? "Give to agent" : ai[0].Label });
        foreach (var act in Pocket.Actions(items)) if (acts.Count < (kept ? 3 : 4)) acts.Add(act);
        if (kept) acts.Add(new PocketAction("more", "More", '\uE712', 0xAEAEB2));
        const float TW = 100, TH = 56, Gap = 8;
        float x = cx - (acts.Count * TW + (acts.Count - 1) * Gap) / 2;
        for (int i = 0; i < acts.Count; i++)
            ActionTile(c, m, acts[i].Id == "more" ? "dz:more" : "dz:a:" + acts[i].Id, acts[i], x + i * (TW + Gap), y0 + 226, TW, TH, since - 0.06f - i * 0.07f, a);
    }

    // ================================================================ the pill: how many things he holds

    /// <summary>A small sheet and a count, right after Bloub. Returns how far the pill's text moves over for it.</summary>
    public static float PocketBadge(Canvas c, IslandModel m, float x0, float y0, float h)
    {
        float b = Math.Clamp(m.Pocket.Badge.Value, 0, 1);
        if (b < 0.01f) return 0;
        int n = m.Pocket.Count;
        float x = x0 + 39, cy = y0 + h / 2;
        var was = c.Transform;
        c.Transform = Matrix3x2.CreateScale(m.Pocket.BadgePop.Value * (0.5f + 0.5f * b), new Vector2(x + 9, cy)) * was;
        // the sheet: a page with its corner folded
        var paper = WhiteA(0.92f * b);
        float sx = x, sy = cy - 5.5f;
        c.Polygon(paper, new(sx, sy), new(sx + 5.4f, sy), new(sx + 8.6f, sy + 3.2f), new(sx + 8.6f, sy + 11), new(sx, sy + 11));
        c.Polygon(WhiteA(0.45f * b), new(sx + 5.4f, sy), new(sx + 8.6f, sy + 3.2f), new(sx + 5.4f, sy + 3.2f));
        c.Text(n.ToString(), sx + 12, cy - 8.5f, 11.5f, WhiteA(0.92f * b), FontWeight.SemiBold);
        c.Transform = was;
        return (PocketBadgeW + (n > 9 ? 6 : 0)) * b;
    }
}
