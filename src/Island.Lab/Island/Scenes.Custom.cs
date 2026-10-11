using System.Globalization;
using System.Numerics;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Island.Lab.Render;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

/// <summary>
/// The widget kit for the tabs you describe: a recipe picks one of these layouts and the island draws it
/// natively — score rows grouped by league, a big number over its chart, day cards, or a plain list.
/// Monochrome; colour only comes from the data (a live match, a rise or fall, the sun).
/// </summary>
public static partial class Scenes
{
    static void CustomPanel(Canvas c, IslandModel m, string id, float x, float y, float w, float h)
    {
        var tab = CustomTabs.Get(id);
        if (tab == null) { Empty(c, x, y, w, h, '', "This tab is gone", "Its recipe file was removed"); return; }
        var r = tab.Recipe;
        var d = tab.Data;

        // ---- header: its icon, its title, where the data comes from and how fresh it is
        c.Round(x, y + 2, 36, 36, 12, WhiteA(0.08f));
        c.Icon(r.Icon, x + 18, y + 20, 16, Rgba(r.Color));
        c.Text(r.Title, x + 48, y + 2, 16, White, FontWeight.SemiBold, 0, w - 60);
        bool ok = d.State == CustomData.Kind.Ready;
        string fresh = !ok ? (d.State == CustomData.Kind.Loading ? "connecting…" : "offline") : (DateTime.Now - d.At).TotalSeconds < 45 ? "updated just now" : $"updated {Since(d.At)} ago";
        float sx = x + 48;
        if (ok)
        {
            c.Circle(sx + 3, y + 30, 3, Rgba(Green, 0.55f + 0.45f * MathF.Abs(MathF.Sin(m.Clock * 2.2f))));
            sx += 12;
        }
        c.Text($"{d.Host} · {fresh}", sx, y + 22, 11.5f, Tertiary, FontWeight.Regular, 0, w - 80);

        CustomView(c, m, tab, x, y + 56, w, h - 56);
    }

    /// <summary>A custom tab's body (no header): loading, failed, empty, or its view.</summary>
    static void CustomView(Canvas c, IslandModel m, CustomTab tab, float x, float by, float w, float bh)
    {
        var r = tab.Recipe;
        var d = tab.Data;
        string fresh = d.State != CustomData.Kind.Ready ? "" : (DateTime.Now - d.At).TotalSeconds < 45 ? "just now" : $"{Since(d.At)} ago";
        if (d.State == CustomData.Kind.Loading) { Loading(c, m, x, by, w, bh, d.Host); return; }
        if (d.State == CustomData.Kind.Failed) { Empty(c, x, by, w, bh - 20, '', "Couldn't load this tab", d.Message); return; }
        if (d.Rows.Count == 0 && r.View != "price") { Empty(c, x, by, w, bh - 20, r.Icon, r.Empty, $"Checked {d.Host} {fresh}"); return; }

        switch (r.View)
        {
            case "scores": ScoreRows(c, m, tab, x, by, w, bh); break;
            case "price": PriceChart(c, r, d, x, by, w, bh); break;
            case "days": DayCards(c, d, x, by, w, bh); break;
            case "schedule": ScheduleView(c, m, tab, x, by, w, bh); break;
            case "stats": StatsView(c, r, d, x, by, w, bh); break;
            case "posters": PosterShelf(c, m, tab, x, by, w, bh); break;
            default: ListRows(c, m, tab, x, by, w, bh); break;
        }
    }

    /// <summary>The island's own tabs react to the mouse; the maker's preview of a new one doesn't.</summary>
    static bool Live(CustomTab tab) => CustomTabs.All.Contains(tab);

    /// <summary>A tint the data gives ("#f19350"), or the tab's colour.</summary>
    static uint Tint(Dictionary<string, string> row, Recipe r) =>
        row.GetValueOrDefault("tint") is { Length: 7 } t && t[0] == '#' && uint.TryParse(t[1..], NumberStyles.HexNumber, null, out uint rgb) ? rgb : r.Color;

    /// <summary>Text broken into at most two lines at a word, the second shortened to fit.</summary>
    static (string One, string? Two) TwoLines(Canvas c, string text, float size, FontWeight weight, float w)
    {
        if (c.Measure(text, size, weight) <= w) return (text, null);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string line = "";
        int i = 0;
        for (; i < words.Length; i++)
        {
            string next = line.Length == 0 ? words[i] : line + " " + words[i];
            if (c.Measure(next, size, weight) > w) break;
            line = next;
        }
        if (i == 0) return (text, null); // one long word: shortened on one line
        return (line, string.Join(' ', words[i..]));
    }

    // ---------------------------------------------------------------- posters: a shelf of pictures

    /// <summary>
    /// A shelf of tall cards, each a picture (a poster, a cover, a photo) with its words over a shade at the
    /// bottom: a badge top-left ("EP 7"), a value top-right ("84%"), then title, detail and note. The wheel
    /// slides the shelf; a card eases up under the mouse and opens its link. Fields: image, title, detail,
    /// note, badge, value, tint, link.
    /// </summary>
    static void PosterShelf(Canvas c, IslandModel m, CustomTab tab, float x, float y, float w, float h)
    {
        var r = tab.Recipe;
        var rows = tab.Data.Rows;
        bool live = Live(tab);
        const float gap = 12, rad = 16;
        // tall cards for posters and covers; wide pictures (game art, thumbnails) get landscape cards with the words under them
        float aspect = 0;
        foreach (var row in rows)
            if (row.GetValueOrDefault("image") is { Length: > 0 } u && Images.Get(u) is { } p) { aspect = (float)p.W / p.H; break; }
        bool wide = r.Opt("shape") switch { "wide" => true, "tall" => false, _ => aspect > 1.2f };
        if (wide) { WideShelf(c, m, tab, x, y, w, h, aspect > 1.2f ? aspect : 16 / 9f); return; }
        float ch = h - 6, cw = MathF.Round(ch * 0.66f);
        float total = rows.Count * (cw + gap) - gap;
        if (live) tab.ScrollMax = MathF.Max(0, total - w);
        float scroll = live ? Math.Clamp(tab.Scroll.Value, 0, tab.ScrollMax) : 0;

        c.PushClip(x - 4, y - 6, w + 8, h + 8);
        for (int i = 0; i < rows.Count; i++)
        {
            float cx = x + i * (cw + gap) - scroll;
            if (cx + cw < x - 4 || cx > x + w + 4) continue;
            var row = rows[i];
            string F(string k) => row.GetValueOrDefault(k, "");
            float vl = MathF.Max(cx, x), vr = MathF.Min(cx + cw, x + w);
            bool over = live && vr - vl > 20 && m.Hit($"cx:{tab.Id}:{i}", vl, y, vr - vl, ch);
            float lift = live ? tab.Hover(i, over) : 0;
            float cy = y - 4 * lift;
            uint tint = Tint(row, r);

            // the picture, over its colour while it loads
            c.GradientRound(cx, cy, cw, ch, rad, new(cx, cy), new(cx, cy + ch), Rgba(tint, 0.42f), Rgba(tint, 0.12f));
            bool pic = F("image").Length > 0 && c.Image(F("image"), cx, cy, cw, ch, rad);
            if (!pic) c.Icon(r.Icon, cx + cw / 2, cy + ch * 0.36f, 22, WhiteA(0.4f));

            // the words, over a shade that rises from the bottom
            float sh = ch * 0.56f;
            c.GradientRound(cx, cy + ch - sh, cw, sh, rad, new(0, cy + ch - sh), new(0, cy + ch), new Color4(0, 0, 0, 0), new Color4(0, 0, 0, 0.9f));
            if (lift > 0.01f) c.Round(cx, cy, cw, ch, rad, WhiteA(0.07f * lift));
            float tx = cx + 12, tw = cw - 24, by = cy + ch - 12;
            if (F("note").Length > 0) { by -= 16; c.Text(F("note"), tx, by, 11, NoteColor(F("note")) is var nc && nc == Tertiary ? WhiteA(0.6f) : nc, FontWeight.Medium, 0, tw); }
            if (F("detail").Length > 0) { by -= 17; c.Text(F("detail"), tx, by, 11.5f, WhiteA(0.78f), FontWeight.Medium, 0, tw); }
            var (one, two) = TwoLines(c, F("title"), 14, FontWeight.SemiBold, tw);
            if (two != null) { by -= 19; c.Text(two, tx, by, 14, White, FontWeight.SemiBold, 0, tw); }
            by -= 19;
            c.Text(one, tx, by, 14, White, FontWeight.SemiBold, 0, tw);

            // badge and value, as small dark chips on the picture
            if (F("badge").Length > 0)
            {
                float bw = MathF.Min(c.Measure(F("badge"), 10.5f, FontWeight.Bold) + 16, cw - 16);
                c.Round(cx + 8, cy + 8, bw, 21, 10.5f, new Color4(0, 0, 0, 0.62f));
                c.Text(F("badge"), cx + 8 + bw / 2, cy + 10.5f, 10.5f, White, FontWeight.Bold, 0.5f, bw - 12);
            }
            if (F("value").Length > 0)
            {
                float vw = MathF.Min(c.Measure(F("value"), 10.5f, FontWeight.Bold) + 16, cw * 0.5f);
                c.Round(cx + cw - 8 - vw, cy + 8, vw, 21, 10.5f, new Color4(0, 0, 0, 0.62f));
                c.Text(F("value"), cx + cw - 8 - vw / 2, cy + 10.5f, 10.5f, White, FontWeight.Bold, 0.5f, vw - 12);
            }
        }
        c.PopClip();

        // more on either side: the shelf fades into the island
        float more = total - w - scroll;
        if (scroll > 1) c.GradientRound(x - 4, y - 6, 44, h + 8, 0, new(x + 40, 0), new(x - 4, 0), new Color4(0, 0, 0, 0), new Color4(0, 0, 0, MathF.Min(1, scroll / 40)));
        if (more > 1) c.GradientRound(x + w - 40, y - 6, 44, h + 8, 0, new(x + w - 40, 0), new(x + w + 4, 0), new Color4(0, 0, 0, 0), new Color4(0, 0, 0, MathF.Min(1, more / 40)));
    }

    /// <summary>Three dots breathing while the first data is on its way.</summary>
    static void Loading(Canvas c, IslandModel m, float x, float y, float w, float h, string host)
    {
        float cx = x + w / 2, cy = y + h / 2 - 20;
        for (int i = 0; i < 3; i++)
        {
            float k = 0.5f + 0.5f * MathF.Sin(m.Clock * 5 - i * 0.7f);
            c.Circle(cx - 16 + i * 16, cy, 3.5f + k * 1.5f, WhiteA(0.25f + 0.6f * k));
        }
        c.Text($"Fetching from {host}", cx, cy + 20, 12.5f, Tertiary, FontWeight.Regular, 0.5f);
    }

    /// <summary>The shelf for wide pictures: a card with the picture on top and the words under it.</summary>
    static void WideShelf(Canvas c, IslandModel m, CustomTab tab, float x, float y, float w, float h, float aspect)
    {
        var r = tab.Recipe;
        var rows = tab.Data.Rows;
        bool live = Live(tab);
        const float gap = 12, pad = 6;
        // the picture as big as the words under it allow (about two cards across)
        float ch = h - 6, cw = MathF.Round(Math.Clamp((ch - pad - 112) * aspect + pad * 2, 200, w / 1.9f));
        float ih = MathF.Min((cw - pad * 2) / aspect, ch - pad - 112);
        float total = rows.Count * (cw + gap) - gap;
        if (live) tab.ScrollMax = MathF.Max(0, total - w);
        float scroll = live ? Math.Clamp(tab.Scroll.Value, 0, tab.ScrollMax) : 0;

        c.PushClip(x - 4, y - 6, w + 8, h + 8);
        for (int i = 0; i < rows.Count; i++)
        {
            float cx = x + i * (cw + gap) - scroll;
            if (cx + cw < x - 4 || cx > x + w + 4) continue;
            var row = rows[i];
            string F(string k) => row.GetValueOrDefault(k, "");
            float vl = MathF.Max(cx, x), vr = MathF.Min(cx + cw, x + w);
            bool over = live && vr - vl > 20 && m.Hit($"cx:{tab.Id}:{i}", vl, y, vr - vl, ch);
            float lift = live ? tab.Hover(i, over) : 0;
            float cy = y - 4 * lift;
            uint tint = Tint(row, r);

            c.Round(cx, cy, cw, ch, 18, WhiteA(0.06f + 0.04f * lift));
            float ix = cx + pad, iy = cy + pad, iw = cw - pad * 2;
            c.GradientRound(ix, iy, iw, ih, 13, new(ix, iy), new(ix, iy + ih), Rgba(tint, 0.4f), Rgba(tint, 0.12f));
            if (F("image").Length == 0 || !c.Image(F("image"), ix, iy, iw, ih, 13)) c.Icon(r.Icon, ix + iw / 2, iy + ih / 2, 20, WhiteA(0.4f));
            if (F("badge").Length > 0)
            {
                float bw = MathF.Min(c.Measure(F("badge"), 10.5f, FontWeight.Bold) + 16, iw - 16);
                c.Round(ix + 7, iy + 7, bw, 21, 10.5f, new Color4(0, 0, 0, 0.62f));
                c.Text(F("badge"), ix + 7 + bw / 2, iy + 9.5f, 10.5f, White, FontWeight.Bold, 0.5f, bw - 12);
            }

            // under the picture: the title (two lines at most); at the bottom, detail · note and the value
            float tx = cx + 14, tw = cw - 28, ty = iy + ih + 12;
            var (one, two) = TwoLines(c, F("title"), 14.5f, FontWeight.SemiBold, tw);
            c.Text(one, tx, ty, 14.5f, White, FontWeight.SemiBold, 0, tw);
            if (two != null) c.Text(two, tx, ty + 20, 14.5f, White, FontWeight.SemiBold, 0, tw);
            string value = F("value");
            float vw = value.Length > 0 ? MathF.Min(c.Measure(value, 17, FontWeight.Bold), tw * 0.45f) : 0;
            float by = cy + ch - 34;
            if (vw > 0) c.Text(value, cx + cw - 14, by - 2, 17, White, FontWeight.Bold, 1f, tw * 0.45f);
            string sub = string.Join(" · ", new[] { F("detail"), F("note") }.Where(s => s.Length > 0));
            if (sub.Length > 0) c.Text(sub, tx, by + 3, 11.5f, NoteColor(F("detail")) is var dc && dc != Tertiary ? dc : Tertiary, FontWeight.Medium, 0, tw - (vw > 0 ? vw + 10 : 0));
        }
        c.PopClip();
        float more = total - w - scroll;
        if (scroll > 1) c.GradientRound(x - 4, y - 6, 44, h + 8, 0, new(x + 40, 0), new(x - 4, 0), new Color4(0, 0, 0, 0), new Color4(0, 0, 0, MathF.Min(1, scroll / 40)));
        if (more > 1) c.GradientRound(x + w - 40, y - 6, 44, h + 8, 0, new(x + w - 40, 0), new(x + w + 4, 0), new Color4(0, 0, 0, 0), new Color4(0, 0, 0, MathF.Min(1, more / 40)));
    }

    // ---------------------------------------------------------------- scores: rows by league

    static void ScoreRows(Canvas c, IslandModel m, CustomTab tab, float x, float y, float w, float h)
    {
        var rows = tab.Data.Rows;
        const float rowH = 34, gap = 5, labelH = 20;
        float bottom = y + h - 4, cy = y;
        string? group = null;
        int shown = 0;
        c.PushClip(x, y, w, h);
        foreach (var row in rows)
        {
            string g = row.GetValueOrDefault("group", "");
            float need = (g != group ? labelH : 0) + rowH;
            if (cy + need > bottom) break;
            if (g != group)
            {
                group = g;
                c.Text(g.ToUpperInvariant(), x + 2, cy + 3, 10, Tertiary, FontWeight.Bold, 0, w);
                cy += labelH;
            }
            ScoreRow(c, m, tab, row, x, cy, w, rowH);
            cy += rowH + gap;
            shown++;
        }
        c.PopClip();
        if (shown < rows.Count) c.Text($"+{rows.Count - shown} more", x + w - 2, y + h - 14, 11, Tertiary, FontWeight.SemiBold, 1f);
    }

    static void ScoreRow(Canvas c, IslandModel m, CustomTab tab, Dictionary<string, string> row, float x, float y, float w, float h)
    {
        string state = row.GetValueOrDefault("state", ""), home = row.GetValueOrDefault("home", ""), away = row.GetValueOrDefault("away", "");
        float flash = tab.FlashOf(home + "|" + away);   // a goal: the row lights up green, then fades
        c.Round(x, y, w, h, 12, WhiteA(0.06f));
        if (flash > 0.01f)
        {
            c.Round(x, y, w, h, 12, Rgba(Green, 0.16f * flash));
            c.StrokeRound(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, 11.25f, 1.5f, Rgba(Green, 0.9f * flash));
        }

        // status chip: live minute, full time, or kick-off time
        float chipX = x + 8, chipY = y + 7, chipH = 20;
        if (state == "live")
        {
            string clock = row.GetValueOrDefault("clock", "LIVE");
            float cw = c.Measure(clock, 10.5f, FontWeight.SemiBold) + 24;
            c.Round(chipX, chipY, cw, chipH, 10, Rgba(Green, 0.16f));
            c.Circle(chipX + 10, chipY + 10, 2.5f, Rgba(Green, 0.4f + 0.6f * MathF.Abs(MathF.Sin(m.Clock * 2.4f))));
            c.Text(clock, chipX + 17, chipY + 2.5f, 10.5f, White, FontWeight.SemiBold);
        }
        else
        {
            string label = state == "done" ? "FT" : row.GetValueOrDefault("time", "");
            float cw = c.Measure(label, 10.5f, FontWeight.SemiBold) + 16;
            c.StrokeRound(chipX + 0.5f, chipY + 0.5f, cw - 1, chipH - 1, 9.5f, 1, WhiteA(0.14f));
            c.Text(label, chipX + cw / 2, chipY + 2.5f, 10.5f, WhiteA(0.55f), FontWeight.SemiBold, 0.5f);
        }

        // home · score · away, around the middle of the row
        float mid = x + 70 + (w - 70) / 2, side = (w - 70) / 2 - 50;
        string hl = row.GetValueOrDefault("homeLogo", ""), al = row.GetValueOrDefault("awayLogo", "");
        float logo = hl.Length > 0 || al.Length > 0 ? 28 : 0;
        if (hl.Length > 0) c.Image(hl, mid - 42 - 20, y + h / 2 - 10, 20, 20, 4);
        if (al.Length > 0) c.Image(al, mid + 42, y + h / 2 - 10, 20, 20, 4);
        c.Text(home, mid - 42 - logo, y + 9, 13, White, FontWeight.SemiBold, 1f, side - logo);
        c.Text(away, mid + 42 + logo, y + 9, 13, White, FontWeight.SemiBold, 0, side - logo);
        if (state == "soon") c.Text("vs", mid, y + 10, 12, Tertiary, FontWeight.SemiBold, 0.5f);
        else
        {
            string hs = row.GetValueOrDefault("homeScore", ""), @as = row.GetValueOrDefault("awayScore", "");
            c.Text(hs, mid - 9, y + 7, 15, White, FontWeight.Bold, 1f);
            c.Text("–", mid, y + 7, 15, WhiteA(0.3f), FontWeight.Regular, 0.5f);
            c.Text(@as, mid + 9, y + 7, 15, White, FontWeight.Bold, 0f);
        }
    }

    // ---------------------------------------------------------------- price: a big number over its chart

    static void PriceChart(Canvas c, Recipe r, CustomData d, float x, float y, float w, float h)
    {
        string prefix = r.Opt("prefix"), suffix = r.Opt("suffix");
        int dec = int.TryParse(r.Opt("decimals", "0"), out int dd) ? dd : 0;
        string big = d.Value is { } v ? prefix + v.ToString("N" + dec, CultureInfo.InvariantCulture) + suffix : "—";
        c.Text(big, x, y - 4, 40, White, FontWeight.SemiBold);
        if (d.Change is { } ch)
        {
            float bw = c.Measure(big, 40, FontWeight.SemiBold);
            string span = r.Opt("span");
            c.Text($"{(ch >= 0 ? "+" : "−")}{Math.Abs(ch):0.0}%{(span.Length > 0 ? " · " + span : "")}", x + bw + 14, y + 22, 13, Rgba(ch >= 0 ? Green : Red), FontWeight.SemiBold);
        }

        var s = d.Series;
        if (s.Length < 2) return;
        // at most ~120 points, evenly picked
        int n = Math.Min(s.Length, 120);
        var pts = new float[n];
        for (int i = 0; i < n; i++) pts[i] = s[(int)MathF.Round(i * (s.Length - 1) / (float)(n - 1))];
        float lo = pts.Min(), hi = pts.Max(), span2 = MathF.Max(hi - lo, 1e-6f);
        float gx = x + 2, gw = w - 4, gy = y + 66, gh = h - 66 - 26;
        Vector2 P(int i) => new(gx + gw * i / (n - 1), gy + gh - (pts[i] - lo) / span2 * gh);
        // faint guides
        for (int k = 0; k < 3; k++) c.Line(gx, gy + gh * k / 2f, gx + gw, gy + gh * k / 2f, 1, WhiteA(0.05f));
        for (int i = 1; i < n; i++) { var a = P(i - 1); var b = P(i); c.Line(a.X, a.Y, b.X, b.Y, 2, WhiteA(0.92f)); }
        var last = P(n - 1);
        c.Circle(last.X, last.Y, 5.5f, Rgba(r.Color, 0.3f));
        c.Circle(last.X, last.Y, 3, White);
        string from = r.Opt("span").Length > 0 ? r.Opt("span") + " ago" : "";
        if (from.Length > 0) c.Text(from, gx, gy + gh + 8, 10.5f, Tertiary);
        c.Text("now", gx + gw, gy + gh + 8, 10.5f, Tertiary, FontWeight.Regular, 1f);
    }

    // ---------------------------------------------------------------- days: one card per day

    static void DayCards(Canvas c, CustomData d, float x, float y, float w, float h)
    {
        int n = Math.Min(d.Rows.Count, 7);
        const float gap = 8;
        float cw = (w - gap * (n - 1)) / n, ch = MathF.Min(176, h - 8);
        for (int i = 0; i < n; i++)
        {
            var row = d.Rows[i];
            float cx = x + i * (cw + gap);
            string day = row.GetValueOrDefault("day", "");
            bool today = day == "Today";
            c.Round(cx, y, cw, ch, 18, WhiteA(today ? 0.1f : 0.06f));
            c.Text(day, cx + cw / 2, y + 16, 11.5f, today ? White : Secondary, FontWeight.SemiBold, 0.5f);
            Weather(c, row.GetValueOrDefault("icon", "cloud"), cx + cw / 2, y + ch / 2 - 4, 36);
            c.Text(row.GetValueOrDefault("high", ""), cx + cw / 2, y + ch - 52, 20, White, FontWeight.SemiBold, 0.5f);
            c.Text(row.GetValueOrDefault("low", ""), cx + cw / 2, y + ch - 26, 12, Tertiary, FontWeight.SemiBold, 0.5f);
        }
    }

    /// <summary>Weather, drawn with shapes: sun, partly cloudy, cloud, fog, rain, snow, storm.</summary>
    static void Weather(Canvas c, string kind, float cx, float cy, float s)
    {
        var sun = Rgba(0xFFD60A);
        void Sun(float x, float y, float r)
        {
            c.Circle(x, y, r * 0.42f, sun);
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4;
                c.Line(x + MathF.Cos(a) * r * 0.62f, y + MathF.Sin(a) * r * 0.62f, x + MathF.Cos(a) * r * 0.88f, y + MathF.Sin(a) * r * 0.88f, r * 0.11f, sun);
            }
        }
        void Cloud(float x, float y, float k, Color4 col)
        {
            c.Circle(x - k * 0.26f, y + k * 0.04f, k * 0.24f, col);
            c.Circle(x + k * 0.02f, y - k * 0.1f, k * 0.31f, col);
            c.Circle(x + k * 0.3f, y + k * 0.07f, k * 0.21f, col);
            c.Round(x - k * 0.5f, y + k * 0.02f, k * 1.0f, k * 0.27f, k * 0.135f, col);
        }
        var cloud = WhiteA(0.88f);
        switch (kind)
        {
            case "sun": Sun(cx, cy, s * 0.5f); break;
            case "partly": Sun(cx + s * 0.16f, cy - s * 0.16f, s * 0.36f); Cloud(cx - s * 0.06f, cy + s * 0.1f, s * 0.78f, cloud); break;
            case "fog":
                for (int i = 0; i < 3; i++) c.Line(cx - s * 0.36f + i * 3, cy - s * 0.18f + i * s * 0.18f, cx + s * 0.36f - i * 3, cy - s * 0.18f + i * s * 0.18f, 2.5f, WhiteA(0.75f));
                break;
            case "rain":
                Cloud(cx, cy - s * 0.12f, s * 0.86f, cloud);
                for (int i = 0; i < 3; i++) { float rx = cx - s * 0.2f + i * s * 0.2f; c.Line(rx, cy + s * 0.22f, rx - 3, cy + s * 0.4f, 2.2f, Rgba(Blue)); }
                break;
            case "snow":
                Cloud(cx, cy - s * 0.12f, s * 0.86f, cloud);
                for (int i = 0; i < 3; i++) c.Circle(cx - s * 0.2f + i * s * 0.2f, cy + s * 0.32f + (i % 2) * 4, 2.4f, White);
                break;
            case "storm":
                Cloud(cx, cy - s * 0.14f, s * 0.86f, WhiteA(0.7f));
                c.Polygon(sun, new(cx + 2, cy + s * 0.08f), new(cx - s * 0.12f, cy + s * 0.3f), new(cx, cy + s * 0.3f), new(cx - s * 0.06f, cy + s * 0.48f), new(cx + s * 0.14f, cy + s * 0.22f), new(cx + 1, cy + s * 0.22f));
                break;
            default: Cloud(cx, cy, s * 0.9f, cloud); break;
        }
    }

    // ---------------------------------------------------------------- list: one row per thing

    /// <summary>
    /// Rows: a picture, dot or rank on the left, the title with a detail line under it, a value on the right
    /// with a note under it (green or red when it starts with + or −). The wheel scrolls them; a row with a
    /// link opens it. Fields: title, detail, value, note, dot, rank, image, link.
    /// </summary>
    static void ListRows(Canvas c, IslandModel m, CustomTab tab, float x, float y, float w, float h)
    {
        var r = tab.Recipe;
        var d = tab.Data;
        bool live = Live(tab);
        string F(Dictionary<string, string> row, string k) => row.GetValueOrDefault(k, "");
        bool two = d.Rows.Any(row => F(row, "detail").Length > 0);
        bool pics = d.Rows.Any(row => F(row, "image").Length > 0);
        float rowH = pics ? 58 : two ? 48 : 40, gap = 6;
        float total = d.Rows.Count * (rowH + gap) - gap;
        if (live) tab.ScrollMax = MathF.Max(0, total - h);
        float scroll = live ? Math.Clamp(tab.Scroll.Value, 0, tab.ScrollMax) : 0;
        bool ranked = r.Opt("numbered") is "true" or "True" || d.Rows.Any(row => F(row, "rank").Length > 0);
        c.PushClip(x, y, w, h);
        for (int i = 0; i < d.Rows.Count; i++)
        {
            var row = d.Rows[i];
            float ry = y + i * (rowH + gap) - scroll, cy = ry + rowH / 2;
            if (ry + rowH < y || ry > y + h) continue;
            float vt = MathF.Max(ry, y), vb = MathF.Min(ry + rowH, y + h);
            bool over = live && F(row, "link").Length > 0 && vb - vt > 12 && m.Hit($"cx:{tab.Id}:{i}", x, vt, w, vb - vt);
            float lift = live ? tab.Hover(i, over) : 0;
            c.Round(x, ry, w, rowH, 14, WhiteA(0.06f + 0.05f * lift));
            float tx = x + 16;
            if (ranked)
            {
                string rank = F(row, "rank") is { Length: > 0 } rk ? rk : (i + 1).ToString();
                c.Text(rank, x + 24, cy - 9, 13, i == 0 ? Rgba(r.Color) : Tertiary, FontWeight.Bold, 0.5f);
                tx = x + 42;
            }
            if (pics)
            {
                // the picture, over its colour while it loads (or when there is none)
                float s = rowH - 14, px = ranked ? tx : x + 8;
                c.Round(px, ry + 7, s, s, 11, Rgba(Tint(row, r), 0.25f));
                if (F(row, "image").Length == 0 || !c.Image(F(row, "image"), px, ry + 7, s, s, 11)) c.Icon(r.Icon, px + s / 2, cy, 13, WhiteA(0.4f));
                tx = px + s + 12;
            }
            else if (!ranked && F(row, "dot") is { Length: > 0 } dot)
            {
                c.Circle(x + 20, cy, 4.5f, DotColor(dot));
                tx = x + 36;
            }
            // the right side: value, and the note under it
            string value = F(row, "value"), note = F(row, "note") is { Length: > 0 } n ? n : F(row, "subtitle");
            float vw = MathF.Max(value.Length > 0 ? c.Measure(value, 14, FontWeight.SemiBold) : 0, note.Length > 0 ? c.Measure(note, 11.5f) : 0);
            vw = MathF.Min(vw, w * 0.4f);
            bool stacked = value.Length > 0 && note.Length > 0;
            if (value.Length > 0) c.Text(value, x + w - 16, stacked ? cy - 17 : cy - 10, 14, White, FontWeight.SemiBold, 1f, w * 0.4f);
            if (note.Length > 0) c.Text(note, x + w - 16, stacked ? cy + 1 : cy - 8.5f, 11.5f, NoteColor(note), FontWeight.Regular, 1f, w * 0.4f);
            float tw = x + w - 16 - (vw > 0 ? vw + 16 : 0) - tx;
            string detail = F(row, "detail");
            if (detail.Length > 0)
            {
                c.Text(F(row, "title"), tx, cy - 18, 13.5f, White, FontWeight.SemiBold, 0, tw);
                c.Text(detail, tx, cy + 1, 11.5f, Tertiary, FontWeight.Regular, 0, tw);
            }
            else c.Text(F(row, "title"), tx, cy - 10, 13.5f, White, FontWeight.SemiBold, 0, tw);
        }
        c.PopClip();
        // more above or below: the rows fade into the island
        float more = total - h - scroll;
        if (scroll > 1) c.GradientRound(x, y, w, 28, 0, new(0, y), new(0, y + 28), new Color4(0, 0, 0, MathF.Min(1, scroll / 28)), new Color4(0, 0, 0, 0));
        if (more > 1) c.GradientRound(x, y + h - 28, w, 28, 0, new(0, y + h - 28), new(0, y + h), new Color4(0, 0, 0, 0), new Color4(0, 0, 0, MathF.Min(1, more / 28)));
    }

    static Color4 NoteColor(string note) => note.TrimStart() switch
    {
        var t when t.StartsWith('+') || t.StartsWith('▲') => Rgba(Green),
        var t when t.StartsWith('-') || t.StartsWith('−') || t.StartsWith('▼') => Rgba(Red),
        _ => Tertiary,
    };

    // ---------------------------------------------------------------- schedule: the times of a day, the next one counting down

    /// <summary>
    /// Times of day (prayer times, departures, today's events). On the left the next one, big, with a live
    /// countdown and how far along you are since the last one; on the right the whole day, past ones dimmed,
    /// the next one lit in the tab's colour. Fields: name, time, detail.
    /// </summary>
    static void ScheduleView(Canvas c, IslandModel m, CustomTab tab, float x, float y, float w, float h)
    {
        var r = tab.Recipe;
        var d = tab.Data;
        var items = d.Rows.Select(row => (row, at: CustomTabs.ParseClock(row.GetValueOrDefault("time", ""))))
                          .Where(t => t.at != null).Select(t => (t.row, at: t.at!.Value)).ToList();
        if (items.Count == 0) { ListRows(c, m, tab, x, y, w, h); return; }
        var now = DateTime.Now;
        int next = items.FindIndex(t => t.at > now);
        bool tomorrow = next < 0;
        if (tomorrow) next = 0;
        var nextAt = tomorrow ? items[0].at.AddDays(1) : items[next].at;
        var prevAt = next > 0 ? items[next - 1].at : tomorrow ? items[^1].at : items[^1].at.AddDays(-1);
        string Name(int i) => items[i].row.GetValueOrDefault("name") is { Length: > 0 } n ? n : items[i].row.GetValueOrDefault("title", "");
        string Time(int i) => items[i].at.ToString("HH:mm", CultureInfo.InvariantCulture);
        var accent = Rgba(r.Color);
        const float gap = 6;

        // ---- the next one
        float cw = MathF.Min(272, w * 0.42f), ch = h - 6;
        c.Round(x, y, cw, ch, 22, Rgba(r.Color, 0.12f));
        c.Text(tomorrow ? "TOMORROW" : "NEXT", x + 20, y + 18, 11, accent, FontWeight.Bold);
        c.Text(Name(next), x + 20, y + 34, 28, White, FontWeight.SemiBold, 0, cw - 40);
        string sub = items[next].row.GetValueOrDefault("detail") is { Length: > 0 } det ? $"{Time(next)} · {det}" : Time(next);
        c.Text(sub, x + 20, y + 74, 13, Secondary, FontWeight.Medium, 0, cw - 40);
        var left = nextAt - now;
        string cd = left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes}:{left.Seconds:00}";
        c.Text("in", x + 20, y + ch - 98, 12, Tertiary, FontWeight.Medium);
        c.Text(cd, x + 18, y + ch - 84, 36, White, FontWeight.SemiBold);
        // how far along since the last one
        float span = (float)(nextAt - prevAt).TotalSeconds, done = span > 0 ? Math.Clamp((float)(now - prevAt).TotalSeconds / span, 0, 1) : 0;
        float bx = x + 20, bw = cw - 40, by = y + ch - 30;
        c.Round(bx, by, bw, 6, 3, WhiteA(0.1f));
        if (done > 0.01f) c.Round(bx, by, MathF.Max(6, bw * done), 6, 3, accent);
        int prev = next > 0 ? next - 1 : items.Count - 1;
        c.Text(Name(prev), bx, by + 11, 10.5f, Tertiary, FontWeight.Medium);
        c.Text(Name(next), bx + bw, by + 11, 10.5f, Tertiary, FontWeight.Medium, 1f);

        // ---- the whole day
        float lx = x + cw + 12, lw = w - cw - 12;
        int n = items.Count;
        float rowH = MathF.Min(42, (ch - gap * (n - 1)) / n);
        float top = y + MathF.Max(0, (ch - (rowH * n + gap * (n - 1))) / 2);
        for (int i = 0; i < n; i++)
        {
            float ry = top + i * (rowH + gap), cy = ry + rowH / 2;
            bool isNext = i == next, past = !tomorrow && i < next;
            if (isNext)
            {
                c.Round(lx, ry, lw, rowH, 13, Rgba(r.Color, 0.18f));
                c.Round(lx + 6, cy - rowH * 0.25f, 3, rowH * 0.5f, 1.5f, accent);
            }
            else c.Round(lx, ry, lw, rowH, 13, WhiteA(past ? 0.03f : 0.06f));
            float a = past ? 0.32f : 1;
            float size = rowH < 34 ? 12.5f : 13.5f;
            c.Text(Name(i), lx + 18, cy - size * 0.72f, size, WhiteA((isNext ? 1 : 0.85f) * a), FontWeight.SemiBold, 0, lw * 0.5f);
            if (items[i].row.GetValueOrDefault("detail") is { Length: > 0 } dt && rowH >= 30)
                c.Text(dt, lx + lw * 0.5f, cy - 8, 11, WhiteA(0.4f * a), FontWeight.Regular, 0.5f, lw * 0.3f);
            c.Text(Time(i), lx + lw - 16, cy - (size + 0.5f) * 0.72f, size + 0.5f, isNext ? White : WhiteA(0.6f * a), FontWeight.SemiBold, 1f);
        }
    }

    // ---------------------------------------------------------------- stats: a few big numbers

    /// <summary>A few big numbers side by side (up to 4 a row, 2 rows). Fields: label, value, note, dot.</summary>
    static void StatsView(Canvas c, Recipe r, CustomData d, float x, float y, float w, float h)
    {
        int n = Math.Min(d.Rows.Count, 8);
        if (n == 0) return;
        int rows = n <= 4 ? 1 : 2, cols = (n + rows - 1) / rows;
        const float gap = 8;
        float tw = (w - gap * (cols - 1)) / cols, th = rows == 1 ? MathF.Min(156, h - 8) : (h - 8 - gap) / 2;
        float big = rows == 1 ? (cols <= 2 ? 40 : 32) : 26;
        for (int i = 0; i < n; i++)
        {
            var row = d.Rows[i];
            float tx = x + (i % cols) * (tw + gap), ty = y + (i / cols) * (th + gap);
            c.Round(tx, ty, tw, th, 20, WhiteA(0.06f));
            string label = row.GetValueOrDefault("label") is { Length: > 0 } l ? l : row.GetValueOrDefault("title", "");
            float lx = tx + 18;
            if (row.GetValueOrDefault("image") is { Length: > 0 } img)
            {
                c.Circle(tx + 30, ty + 25, 12, WhiteA(0.08f));
                c.Image(img, tx + 18, ty + 13, 24, 24, 12);
                lx = tx + 50;
            }
            else if (row.GetValueOrDefault("dot") is { Length: > 0 } dot) { c.Circle(tx + 22, ty + 25, 4, DotColor(dot)); lx = tx + 34; }
            c.Text(label, lx, ty + 16, 12, Secondary, FontWeight.SemiBold, 0, tw - (lx - tx) - 14);
            string value = row.GetValueOrDefault("value", "");
            float vs = big;
            while (vs > 16 && c.Measure(value, vs, FontWeight.SemiBold) > tw - 36) vs -= 2;
            string note = row.GetValueOrDefault("note", "");
            float vy = ty + th - (note.Length > 0 ? 30 : 18) - vs * 1.2f;
            c.Text(value, tx + 18, vy, vs, White, FontWeight.SemiBold, 0, tw - 36);
            if (note.Length > 0) c.Text(note, tx + 18, ty + th - 28, 12, NoteColor(note), FontWeight.SemiBold, 0, tw - 36);
        }
    }

    static Color4 DotColor(string v) => v.ToLowerInvariant() switch
    {
        "green" or "ok" or "good" => Rgba(Green),
        "orange" or "warn" or "warning" => Rgba(Orange),
        "red" or "bad" or "error" => Rgba(Red),
        "blue" or "info" => Rgba(Blue),
        var s when s.StartsWith('#') && uint.TryParse(s[1..], NumberStyles.HexNumber, null, out uint rgb) => Rgba(rgb),
        _ => WhiteA(0.35f),
    };
}
