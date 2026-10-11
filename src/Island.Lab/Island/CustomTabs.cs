using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Island.Lab.Motion;
using Island.Lab.Platform;

namespace Island.Lab.Island;

/// <summary>
/// A tab you described: not code, a recipe. Where the data comes from, which part of it is the list, which
/// fields to show, and one of a few layouts the island knows how to draw (score rows, a big number with a
/// chart, day cards, a plain list). Recipes live as JSON files in %APPDATA%\Island\tabs\; the island fetches
/// and refreshes the data itself, so no AI runs after a tab is made.
/// <code>
/// {
///   "name": "Matches", "icon": "E7C1", "color": "#30D158", "title": "Today's matches", "view": "scores",
///   "source": { "urls": ["https://…/scoreboard?dates={today:yyyyMMdd}"], "every": 30 },
///   "items": "events",
///   "fields": { "group": "$.leagues[0].name", "home": "competitors[?homeAway=home].team.name",
///               "state": { "path": "status.type.state", "map": { "in": "live", "post": "done", "pre": "soon" } } }
/// }
/// </code>
/// Paths: dot-separated names, <c>[0]</c> / <c>[-1]</c> indexes, <c>[*]</c> every element, <c>[?key=value]</c>
/// the element whose key equals value, and a leading <c>$</c> for the whole response instead of the item.
/// "items" may also be <c>{ "zip": { "day": "daily.time", "high": "daily.max" } }</c> for parallel arrays.
/// </summary>
public sealed class Recipe
{
    public string Id = "", Name = "", Title = "", View = "list", Empty = "Nothing to show right now";
    public char Icon = '';
    public uint Color = 0x8E8E93;
    public string[] Urls = [];
    /// <summary>A JSON body: the sources are POSTed to with it instead of fetched (GraphQL APIs such as AniList).</summary>
    public string? Post;
    public double Every = 60;
    public JsonNode? Items;
    /// <summary>Fixed rows instead of a list from the response: each row is { field: spec } (e.g. one row per prayer).</summary>
    public JsonArray? Rows;
    public JsonObject Fields = new(), Options = new();
    public static readonly string[] Views = ["scores", "price", "days", "list", "schedule", "stats", "posters"];

    /// <summary>Reads a recipe; returns null (with the reason) when it can't be used.</summary>
    public static Recipe? Parse(string id, JsonObject o, out string? error)
    {
        error = null;
        var r = new Recipe { Id = id };
        r.Name = o["name"]?.GetValue<string>() ?? id;
        r.Title = o["title"]?.GetValue<string>() ?? r.Name;
        r.View = (o["view"]?.GetValue<string>() ?? "list").ToLowerInvariant();
        r.Empty = o["empty"]?.GetValue<string>() ?? r.Empty;
        if (o["icon"]?.GetValue<string>() is { Length: > 0 } ic)
            r.Icon = ic.Length == 1 ? ic[0] : int.TryParse(ic.TrimStart('\\', 'u', 'U', '#'), NumberStyles.HexNumber, null, out int cp) ? (char)cp : r.Icon;
        if (o["color"]?.GetValue<string>() is { } col && uint.TryParse(col.TrimStart('#'), NumberStyles.HexNumber, null, out uint c)) r.Color = c;
        if (o["source"] is JsonObject src)
        {
            if (src["urls"] is JsonArray urls) r.Urls = urls.Select(u => u?.GetValue<string>()).OfType<string>().ToArray();
            else if (src["url"]?.GetValue<string>() is { } u) r.Urls = [u];
            r.Every = Math.Clamp(src["every"]?.GetValue<double>() ?? 60, 10, 24 * 3600);
            r.Post = src["post"] switch { JsonObject po => po.ToJsonString(), JsonValue pv => pv.ToString(), _ => null };
        }
        r.Items = o["items"]?.DeepClone();
        r.Rows = o["rows"]?.DeepClone() as JsonArray;
        r.Fields = o["fields"]?.DeepClone() as JsonObject ?? new();
        r.Options = o["options"]?.DeepClone() as JsonObject ?? new();
        if (r.Urls.Length == 0) error = "no source";
        else if (!Views.Contains(r.View)) error = $"unknown view \"{r.View}\" (use one of {string.Join(", ", Views)})";
        return error == null ? r : null;
    }

    public string Opt(string key, string fallback = "") => Options[key]?.ToString() ?? fallback;
}

/// <summary>What a custom tab shows right now: rows of formatted fields, and for a price, the number and its series.</summary>
public sealed class CustomData
{
    public enum Kind { Loading, Ready, Failed }
    public Kind State = Kind.Loading;
    public string Message = "";
    public List<Dictionary<string, string>> Rows = new();
    public double? Value, Change;
    public float[] Series = [];
    public DateTime At = DateTime.MinValue;
    public string Host = "";
}

/// <summary>One custom tab at run time: its recipe, its latest data, and when to look again.</summary>
public sealed class CustomTab(Recipe recipe)
{
    public readonly Recipe Recipe = recipe;
    public string Id => "x-" + Recipe.Id;
    public volatile CustomData Data = new() { Host = HostOf(recipe.Urls.FirstOrDefault()) };
    /// <summary>A row lights up when its score changes (key: home|away).</summary>
    public readonly Dictionary<string, Spring> Flash = new();
    double _next;
    volatile bool _busy;

    static string HostOf(string? url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? Regex.Replace(u.Host, @"^((www|site|api|graphql)\.)+", "") : "";

    public void Tick(double now, bool wanted, bool soon)
    {
        if (_busy || !wanted) return;
        if (soon && Data.At < DateTime.Now.AddSeconds(-Recipe.Every)) _next = Math.Min(_next, now);
        if (now < _next) return;
        // on screen: at the recipe's pace; pinned but not open: a calmer pace, so a bar full of tabs stays light
        _next = now + (soon ? Recipe.Every : Math.Max(Recipe.Every * 4, 120));
        _busy = true;
        Task.Run(async () =>
        {
            try { Swap(await CustomTabs.Fetch(this)); }
            catch (Exception ex)
            {
                Diag.Log($"custom tab {Recipe.Id}: {ex.Message}");
                var d = Data;
                // keep what we had; only say it failed when there is nothing to show
                Data = d.State == CustomData.Kind.Ready ? d : new CustomData { State = CustomData.Kind.Failed, Message = $"Couldn't reach {d.Host}", Host = d.Host };
                _next = Math.Min(_next, now + 15);
            }
            finally { _busy = false; }
        });
    }

    /// <summary>New data arrives: any score that changed lights its row up.</summary>
    void Swap(CustomData d)
    {
        var old = Data;
        if (Recipe.View == "scores" && old.State == CustomData.Kind.Ready)
            foreach (var row in d.Rows)
            {
                string key = row.GetValueOrDefault("home") + "|" + row.GetValueOrDefault("away");
                var was = old.Rows.FirstOrDefault(r => r.GetValueOrDefault("home") + "|" + r.GetValueOrDefault("away") == key);
                if (was != null && (was.GetValueOrDefault("homeScore") != row.GetValueOrDefault("homeScore") || was.GetValueOrDefault("awayScore") != row.GetValueOrDefault("awayScore")))
                    lock (Flash) { var s = Flash.TryGetValue(key, out var f) ? f : Flash[key] = new Spring(0, new SpringSpec(1.4f, 1f), 0.002f); s.Snap(1); s.To(0); }
            }
        Data = d;
    }

    public void Step(float dt)
    {
        lock (Flash) foreach (var f in Flash.Values) f.Step(dt);
        Scroll.To(Math.Clamp(Scroll.Target, 0, ScrollMax));
        Scroll.Step(dt);
        foreach (var h in _hover.Values) h.Step(dt);
    }
    public float FlashOf(string key) { lock (Flash) return Flash.TryGetValue(key, out var f) ? Math.Clamp(f.Value, 0, 1) : 0; }

    /// <summary>How far the rows (or the poster shelf) are scrolled; the view sets how far they can go.</summary>
    public readonly Spring Scroll = new(0, new SpringSpec(0.3f, 1f), 0.1f);
    public float ScrollMax;
    public void Wheel(float delta) => Scroll.To(Math.Clamp(Scroll.Target - delta * 0.8f, 0, ScrollMax));

    /// <summary>A card easing up under the mouse (0..1), by row.</summary>
    readonly Dictionary<int, Spring> _hover = new();
    public float Hover(int i, bool on)
    {
        if (!_hover.TryGetValue(i, out var s)) _hover[i] = s = new Spring(0, new SpringSpec(0.32f, 1f), 0.002f);
        s.To(on ? 1 : 0);
        return Math.Clamp(s.Value, 0, 1);
    }
}

/// <summary>Loads the recipes, fetches their data, and turns responses into rows.</summary>
public static class CustomTabs
{
    public static readonly List<CustomTab> All = new();
    // (dev: ISLAND_TABS_DIR reads the recipes from another folder)
    static readonly string Dir = Environment.GetEnvironmentVariable("ISLAND_TABS_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "tabs");
    static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("IslandLab/1.0 (+https://github.com/UsoguiH/dynamic-island-windows)");
        // JSON first, anything else after: JSON:API sources (kitsu.io) answer 406 to a bare application/json
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*;q=0.8");
        return h;
    }

    public static CustomTab? Get(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Reads every recipe in %APPDATA%\Island\tabs\. The first time, that folder gets three examples (football,
    /// bitcoin, weather). The demo never reads your files: it uses the examples with canned data.
    /// </summary>
    public static void Load()
    {
        All.Clear();
        if (Showcase.On)
        {
            foreach (var (id, json) in Examples) Add(id, JsonNode.Parse(json) as JsonObject);
            return;
        }
        try
        {
            if (!Directory.Exists(Dir))
            {
                Directory.CreateDirectory(Dir);
                foreach (var (id, json) in Examples) File.WriteAllText(Path.Combine(Dir, id + ".json"), json);
            }
            foreach (var f in Directory.GetFiles(Dir, "*.json").OrderBy(f => File.GetCreationTimeUtc(f)))
            {
                string id = Regex.Replace(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), "[^a-z0-9-]", "-");
                try { Add(id, JsonNode.Parse(File.ReadAllText(f), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject); }
                catch (Exception ex) { Diag.Log($"recipe {f}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Diag.Log("custom tabs: " + ex.Message); }
    }

    static void Add(string id, JsonObject? o)
    {
        if (o == null || All.Any(t => t.Recipe.Id == id)) return;
        var r = Recipe.Parse(id, o, out var err);
        if (r == null) { Diag.Log($"recipe {id}: {err}"); return; }
        All.Add(new CustomTab(r));
    }

    // ================================================================ checking a recipe (the island's own test, and later the AI's)

    /// <summary>Loads one recipe (a file, or the id of an installed tab), fetches once, prints the result as JSON.</summary>
    public static int Check(string? what)
    {
        var result = new JsonObject();
        try
        {
            Recipe? r;
            if (what != null && File.Exists(what))
            {
                r = Recipe.Parse(Path.GetFileNameWithoutExtension(what), JsonNode.Parse(File.ReadAllText(what)) as JsonObject ?? new(), out var err);
                if (r == null) throw new InvalidDataException(err);
            }
            else
            {
                Load();
                r = All.FirstOrDefault(t => t.Recipe.Id == what || t.Id == what)?.Recipe ?? throw new FileNotFoundException($"no tab \"{what}\" (have: {string.Join(", ", All.Select(t => t.Recipe.Id))})");
            }
            var tab = new CustomTab(r);
            var d = Fetch(tab).GetAwaiter().GetResult();
            result["ok"] = true;
            result["view"] = r.View;
            result["host"] = d.Host;
            result["rows"] = d.Rows.Count;
            result["sample"] = new JsonArray(d.Rows.Take(12).Select(row => (JsonNode)new JsonObject(row.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))).ToArray());
            if (r.View == "price") { result["value"] = d.Value; result["change"] = d.Change; result["points"] = d.Series.Length; }
            // a field that came out empty on every row is almost always a wrong path
            var blank = r.Fields.Select(f => f.Key).Where(k => k != "series" && d.Rows.Count > 0 && d.Rows.All(row => string.IsNullOrEmpty(row.GetValueOrDefault(k)))).ToArray();
            if (blank.Length > 0) result["emptyFields"] = new JsonArray(blank.Select(b => (JsonNode)b).ToArray());
        }
        catch (Exception ex) { result["ok"] = false; result["error"] = ex.Message; }
        Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return result["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    // ================================================================ fetching

    internal static async Task<CustomData> Fetch(CustomTab tab)
    {
        var (roots, _) = await FetchRaw(tab.Recipe);
        if (roots.All(x => x == null)) throw new IOException("no source answered");
        return Shape(tab.Recipe, roots.Where(x => x != null).ToArray(), tab.Data.Host);
    }

    /// <summary>Every source of a recipe, parsed (null where it failed, with why).</summary>
    internal static async Task<(JsonNode?[] Roots, string[] Errors)> FetchRaw(Recipe r)
    {
        if (Showcase.On) return (SampleFor(r.Id), []);
        var tasks = r.Urls.Select(Expand).Select(async u =>
        {
            try
            {
                using var res = r.Post is { } body
                    ? await Http.PostAsync(u, new StringContent(Expand(body), System.Text.Encoding.UTF8, "application/json"))
                    : await Http.GetAsync(u);
                if (!res.IsSuccessStatusCode) return ((JsonNode?)null, $"HTTP {(int)res.StatusCode} from {new Uri(u).Host}");
                return (JsonNode.Parse(await res.Content.ReadAsStringAsync()), "");
            }
            catch (Exception ex)
            {
                Diag.Log($"fetch {u}: {ex.Message}");
                string host = Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : "the source";
                return (null, ex switch
                {
                    JsonException => $"{host} didn't answer with JSON",
                    TaskCanceledException or TimeoutException => $"{host} can't be reached from here (no answer in 12 s)",
                    HttpRequestException => $"{host} can't be reached from here",
                    _ => ex.Message,
                });
            }
        }).ToArray();
        var got = await Task.WhenAll(tasks);
        return (got.Select(g => g.Item1).ToArray(), got.Select(g => g.Item2).ToArray());
    }

    // ================================================================ installing a tab you described

    /// <summary>Saves a new recipe as a file and adds it to the tabs (its data already fetched, so it shows at once).</summary>
    public static CustomTab Install(JsonObject json, CustomData? data)
    {
        string name = json["name"]?.ToString() ?? "tab";
        string stem = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (stem.Length == 0) stem = "tab";
        string id = stem;
        for (int i = 2; All.Any(t => t.Recipe.Id == id) || (!Showcase.On && File.Exists(Path.Combine(Dir, id + ".json"))); i++) id = $"{stem}-{i}";
        if (!Showcase.On)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, id + ".json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        var r = Recipe.Parse(id, json, out var err) ?? throw new InvalidDataException(err);
        var tab = new CustomTab(r);
        if (data != null) { data.Host = tab.Data.Host; tab.Data = data; }
        All.Add(tab);
        return tab;
    }

    public static JsonObject ExampleJson(string id) => (JsonObject)JsonNode.Parse(Examples.First(e => e.Id == id).Json)!;

    /// <summary>{today}, {today:yyyyMMdd}, {tomorrow:…}, and Unix seconds {unix} / {unix-172800} in a URL or a POST body.</summary>
    static string Expand(string url)
    {
        url = Regex.Replace(url, @"\{unix(?:([+-])(\d+))?\}", m =>
            (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (m.Groups[1].Success ? (m.Groups[1].Value == "-" ? -1 : 1) * long.Parse(m.Groups[2].Value) : 0)).ToString(CultureInfo.InvariantCulture));
        return Regex.Replace(url, @"\{(today|tomorrow|yesterday)(?::([^}]+))?\}", m =>
        {
            var d = DateTime.Now.AddDays(m.Groups[1].Value switch { "tomorrow" => 1, "yesterday" => -1, _ => 0 });
            return d.ToString(m.Groups[2].Success ? m.Groups[2].Value : "yyyy-MM-dd", CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Responses → what the view draws.</summary>
    public static CustomData Shape(Recipe r, JsonNode?[] roots, string host)
    {
        var d = new CustomData { State = CustomData.Kind.Ready, At = DateTime.Now, Host = host };
        if (r.Rows != null && roots.Length > 0)
        {
            // fixed rows: every field of every row is read from the (first) response
            foreach (var spec in r.Rows.OfType<JsonObject>())
            {
                var row = new Dictionary<string, string>();
                foreach (var (name, fs) in spec) row[name] = FieldValue(fs, roots[0], roots[0], literal: name is "name" or "title" or "label");
                d.Rows.Add(row);
            }
        }
        else foreach (var root in roots)
            foreach (var item in ItemsOf(r, root))
            {
                var row = new Dictionary<string, string>();
                foreach (var (name, spec) in r.Fields)
                    if (name != "series") row[name] = FieldValue(spec, root, item);
                d.Rows.Add(row);
            }
        // "sort": a field to order the rows by ("-popularity": biggest first); "limit": how many to keep
        if (r.Opt("sort") is { Length: > 0 } sort)
        {
            string key = sort.TrimStart('-', '+');
            double? N(Dictionary<string, string> row) => double.TryParse(row.GetValueOrDefault(key, "").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            bool numeric = d.Rows.Any(row => N(row) != null), desc = sort.StartsWith('-');
            d.Rows = numeric
                ? (desc ? d.Rows.OrderByDescending(row => N(row) ?? double.MinValue) : d.Rows.OrderBy(row => N(row) ?? double.MaxValue)).ToList()
                : (desc ? d.Rows.OrderByDescending(row => row.GetValueOrDefault(key, ""), StringComparer.OrdinalIgnoreCase) : d.Rows.OrderBy(row => row.GetValueOrDefault(key, ""), StringComparer.OrdinalIgnoreCase)).ToList();
        }
        if (int.TryParse(r.Opt("limit"), out int limit) && limit > 0 && d.Rows.Count > limit) d.Rows = d.Rows.Take(limit).ToList();
        if (r.View == "price")
        {
            var root = roots[0];
            var item = ItemsOf(r, root).FirstOrDefault() ?? root;
            if (r.Fields["series"] is { } sp)
                d.Series = Eval(root, item, PathOf(sp)).Select(Num).Where(v => v.HasValue).Select(v => (float)v!.Value).ToArray();
            d.Value = r.Fields["value"] is { } vp ? Num(Eval(root, item, PathOf(vp)).FirstOrDefault()) : d.Series.Length > 0 ? d.Series[^1] : null;
            d.Change = r.Fields["change"] is { } cp ? Num(Eval(root, item, PathOf(cp)).FirstOrDefault())
                     : d.Series.Length > 1 && d.Series[0] != 0 ? (d.Series[^1] - d.Series[0]) / d.Series[0] * 100 : null;
        }
        return d;
    }

    static IEnumerable<JsonNode?> ItemsOf(Recipe r, JsonNode? root)
    {
        switch (r.Items)
        {
            case null: return [root];
            case JsonObject eo when eo["entries"] is { } ep:
                {
                    // an object's entries as rows: { "key": name, "value": … }, optionally only some keys, in that order
                    if (Eval(root, root, ep.ToString(), expand: false).FirstOrDefault() is not JsonObject obj) return [];
                    var keys = eo["only"] is JsonArray only ? only.Select(k => k?.ToString() ?? "").Where(obj.ContainsKey).ToList() : obj.Select(kv => kv.Key).ToList();
                    return keys.Select(k => (JsonNode?)new JsonObject { ["key"] = k, ["value"] = obj[k]?.DeepClone() }).ToList();
                }
            case JsonObject zo when zo["zip"] is JsonObject zip:
                {
                    var cols = zip.Select(kv => (kv.Key, Vals: Eval(root, root, PathOf(kv.Value)).ToList())).ToList();
                    int n = cols.Count == 0 ? 0 : cols.Min(c => c.Vals.Count);
                    return Enumerable.Range(0, n).Select(i =>
                    {
                        var o = new JsonObject();
                        foreach (var (k, vals) in cols) o[k] = vals[i]?.DeepClone();
                        return (JsonNode?)o;
                    }).ToList();
                }
            default:
                return Eval(root, root, r.Items.ToString());
        }
    }

    static string PathOf(JsonNode? spec) => spec is JsonObject o ? o["path"] is JsonValue pv ? pv.ToString() : "" : spec is JsonArray a ? a[0]?.ToString() ?? "" : spec?.ToString() ?? "";

    /// <summary>A field: a path, or { path, text, map, add, format, prefix, suffix, default }.</summary>
    static string FieldValue(JsonNode? spec, JsonNode? root, JsonNode? item, bool literal = false)
    {
        var o = spec as JsonObject;
        JsonNode? node = null;
        string raw;
        if (o?["text"] is { } text) raw = text.ToString();
        else
        {
            // a path, or a list of them: the first that gives something ("english title, else romaji")
            var paths = (o?["path"] ?? spec) is JsonArray alts ? alts.Select(p => p?.ToString() ?? "").ToArray() : [PathOf(spec)];
            raw = "";
            foreach (var p in paths)
            {
                node = Eval(root, item, p).FirstOrDefault();
                raw = Str(node);
                if (raw.Length > 0 && raw != "null") break;
            }
            if (raw == "null") raw = "";
            // (a row's name written as plain text instead of { "text": … })
            if (raw.Length == 0 && literal && spec is JsonValue) raw = spec.ToString();
        }
        // "add": minutes after a time (an iqama 20 minutes after the adhan)
        if (o?["add"] is { } add && double.TryParse(add.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mins) && ParseClock(raw) is { } at)
            raw = at.AddMinutes(mins).ToString("HH:mm", CultureInfo.InvariantCulture);
        if (o?["map"] is JsonObject map) raw = map[raw]?.ToString() ?? map["*"]?.ToString() ?? raw;
        if (o?["format"]?.GetValue<string>() is { } fmt) raw = Format(raw, node, fmt);
        if (raw.Length == 0) raw = o?["default"]?.ToString() ?? "";
        else raw = (o?["prefix"]?.ToString() ?? "") + raw + (o?["suffix"]?.ToString() ?? "");
        return raw;
    }

    static string Format(string raw, JsonNode? node, string fmt)
    {
        string kind = fmt.Split(':')[0], arg = fmt.Contains(':') ? fmt[(fmt.IndexOf(':') + 1)..] : "";
        switch (kind)
        {
            case "time":
                return ParseClock(raw) is { } t ? t.ToString(arg.Length > 0 ? arg : "HH:mm", CultureInfo.InvariantCulture) : raw;
            case "weekday":
                if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return raw;
                return day.Date == DateTime.Today ? "Today" : day.ToString("ddd", CultureInfo.InvariantCulture);
            case "round":
                return Num(node) is { } v ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : raw;
            case "number":
                return Num(node) is { } n ? n.ToString("N" + (int.TryParse(arg, out int dp) ? dp : 0), CultureInfo.InvariantCulture) : raw;
            case "signed":
                return Num(node) is { } sv ? (sv >= 0 ? "+" : "−") + Math.Abs(sv).ToString("N" + (int.TryParse(arg, out int sd) ? sd : 1), CultureInfo.InvariantCulture) : raw;
            case "ago":
                // a moment, as how long ago or how soon: "3 h ago", "in 2 d"
                return When(raw, node) is { } at ? Ago(at) : raw;
            case "date":
                return When(raw, node) is { } dd ? dd.ToString(arg.Length > 0 ? arg : "MMM d", CultureInfo.InvariantCulture) : raw;
            case "wmo":
                return int.TryParse(raw, out int code) ? code switch
                {
                    0 => "sun", 1 or 2 => "partly", 3 => "cloud", 45 or 48 => "fog",
                    >= 51 and <= 67 or >= 80 and <= 82 => "rain", >= 71 and <= 77 or 85 or 86 => "snow",
                    >= 95 => "storm", _ => "cloud",
                } : raw;
            default: return raw;
        }
    }

    /// <summary>A moment in a response: Unix seconds or milliseconds, or a date-time string; local time.</summary>
    static DateTime? When(string raw, JsonNode? node)
    {
        if (Num(node) is { } n && n > 1e8)
            return (n > 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)n) : DateTimeOffset.FromUnixTimeSeconds((long)n)).LocalDateTime;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto) ? dto.LocalDateTime : null;
    }

    static string Ago(DateTime at)
    {
        var d = DateTime.Now - at;
        bool past = d.Ticks >= 0;
        d = d.Duration();
        string s = d.TotalMinutes < 1 ? "" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h" : d.TotalDays < 60 ? $"{(int)d.TotalDays} d" : at.ToString("MMM d", CultureInfo.InvariantCulture);
        if (s.Length == 0) return "just now";
        if (d.TotalDays >= 60) return s;
        return past ? s + " ago" : "in " + s;
    }

    static readonly Regex ClockOnly =new(@"^\s*(\d{1,2}):(\d{2})(?::(\d{2}))?\s*([AaPp][Mm])?");

    /// <summary>
    /// A time in a response, as local time: "04:42" or "4:42 PM" (today, as written; a trailing "(+03)" is ignored),
    /// or a date-time ("2026-10-09T18:00:00Z" is converted to local; without a zone it is taken as local).
    /// </summary>
    public static DateTime? ParseClock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = ClockOnly.Match(raw);
        if (m.Success && Regex.IsMatch(raw[(m.Index + m.Length)..], @"^\s*(\(.*\))?\s*$"))
        {
            int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
            if (m.Groups[4].Success) { bool pm = char.ToLowerInvariant(m.Groups[4].Value[0]) == 'p'; h = h % 12 + (pm ? 12 : 0); }
            if (h > 23 || mi > 59) return null;
            return DateTime.Today.AddHours(h).AddMinutes(mi).AddSeconds(m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return Regex.IsMatch(raw, @"(Z|[+-]\d{2}:?\d{2})\s*$") ? dto.LocalDateTime : dto.DateTime;
        return null;
    }

    // ================================================================ removing a tab you made

    /// <summary>Deletes a tab you described (its recipe file too).</summary>
    public static void Remove(string id)
    {
        var tab = Get(id);
        if (tab == null) return;
        All.Remove(tab);
        if (Showcase.On) return;
        try { File.Delete(Path.Combine(Dir, tab.Recipe.Id + ".json")); }
        catch (Exception ex) { Diag.Log($"remove tab {id}: {ex.Message}"); }
    }

    static string Str(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => n.ToJsonString(),
    };

    static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    static readonly Regex Token = new(@"([^.\[\]]+)|\[([^\]]*)\]", RegexOptions.Compiled);

    /// <summary>Evaluates a path; an array at the end is expanded into its elements.</summary>
    public static List<JsonNode?> Eval(JsonNode? root, JsonNode? item, string path, bool expand = true)
    {
        path = path.Trim();
        List<JsonNode?> cur = [path.StartsWith('$') ? root : item];
        if (path.StartsWith('$')) path = path.TrimStart('$').TrimStart('.');
        foreach (Match m in Token.Matches(path))
        {
            var next = new List<JsonNode?>();
            foreach (var n in cur)
            {
                if (m.Groups[1].Success)
                {
                    if (n is JsonObject o) next.Add(o[m.Groups[1].Value]);
                    continue;
                }
                if (n is not JsonArray a) continue;
                string b = m.Groups[2].Value.Trim();
                if (b == "*") next.AddRange(a);
                else if (int.TryParse(b, out int i)) { if (i < 0) i += a.Count; if (i >= 0 && i < a.Count) next.Add(a[i]); }
                else if (b.StartsWith('?') && b.IndexOf('=') is > 1 and var eq)
                {
                    string k = b[1..eq].Trim(), want = b[(eq + 1)..].Trim().Trim('\'', '"');
                    next.AddRange(a.Where(e => e is JsonObject eo && Str(k.Contains('.') ? Eval(eo, eo, k, expand: false).FirstOrDefault() : eo[k]) == want));
                }
            }
            cur = next;
        }
        // a path that ends on an array means "its elements"
        if (expand && cur.Count == 1 && cur[0] is JsonArray arr) return arr.ToList();
        return cur;
    }

    // ================================================================ the examples

    static readonly (string Id, string Json)[] Examples =
    [
        ("matches", """
        {
          "name": "Matches",
          "icon": "E7C1",
          "color": "#30D158",
          "title": "Today's matches",
          "view": "scores",
          "empty": "No matches today",
          "source": {
            "urls": [
              "https://site.api.espn.com/apis/site/v2/sports/soccer/ksa.1/scoreboard?dates={today:yyyyMMdd}",
              "https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/scoreboard?dates={today:yyyyMMdd}",
              "https://site.api.espn.com/apis/site/v2/sports/soccer/esp.1/scoreboard?dates={today:yyyyMMdd}",
              "https://site.api.espn.com/apis/site/v2/sports/soccer/ger.1/scoreboard?dates={today:yyyyMMdd}",
              "https://site.api.espn.com/apis/site/v2/sports/soccer/uefa.champions/scoreboard?dates={today:yyyyMMdd}"
            ],
            "every": 30
          },
          "items": "events",
          "fields": {
            "group": "$.leagues[0].name",
            "home": "competitions[0].competitors[?homeAway=home].team.shortDisplayName",
            "away": "competitions[0].competitors[?homeAway=away].team.shortDisplayName",
            "homeScore": "competitions[0].competitors[?homeAway=home].score",
            "awayScore": "competitions[0].competitors[?homeAway=away].score",
            "state": { "path": "status.type.state", "map": { "in": "live", "post": "done", "pre": "soon" } },
            "clock": "status.displayClock",
            "time": { "path": "date", "format": "time" }
          }
        }
        """),
        ("bitcoin", """
        {
          "name": "Bitcoin",
          "icon": "EAFC",
          "color": "#FF9F0A",
          "title": "Bitcoin",
          "view": "price",
          "source": { "url": "https://api.coingecko.com/api/v3/coins/bitcoin/market_chart?vs_currency=usd&days=1", "every": 60 },
          "fields": { "series": "prices[*][1]" },
          "options": { "prefix": "$", "decimals": 0, "span": "24 h" }
        }
        """),
        ("weather", """
        {
          "name": "Weather",
          "icon": "E706",
          "color": "#64D2FF",
          "title": "Riyadh · this week",
          "view": "days",
          "source": { "url": "https://api.open-meteo.com/v1/forecast?latitude=24.71&longitude=46.68&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=5", "every": 900 },
          "items": { "zip": { "day": "daily.time", "high": "daily.temperature_2m_max", "low": "daily.temperature_2m_min", "code": "daily.weather_code" } },
          "fields": {
            "day": { "path": "day", "format": "weekday" },
            "high": { "path": "high", "format": "round", "suffix": "°" },
            "low": { "path": "low", "format": "round", "suffix": "°" },
            "icon": { "path": "code", "format": "wmo" }
          }
        }
        """),
    ];

    /// <summary>The demo's canned responses: believable, never from your machine or the network.</summary>
    internal static JsonNode?[] SampleFor(string id)
    {
        switch (id)
        {
            case "matches":
                {
                    int min = 30 + (int)(DateTime.Now.TimeOfDay.TotalMinutes % 55);
                    JsonObject Ev(string h, string a, int hs, int @as, string state, string clock, int hours) => new()
                    {
                        ["date"] = DateTime.UtcNow.Date.AddHours(hours).ToString("yyyy-MM-ddTHH:mmZ"),
                        ["status"] = new JsonObject { ["displayClock"] = clock, ["type"] = new JsonObject { ["state"] = state } },
                        ["competitions"] = new JsonArray(new JsonObject
                        {
                            ["competitors"] = new JsonArray(
                                new JsonObject { ["homeAway"] = "home", ["score"] = hs.ToString(), ["team"] = new JsonObject { ["shortDisplayName"] = h } },
                                new JsonObject { ["homeAway"] = "away", ["score"] = @as.ToString(), ["team"] = new JsonObject { ["shortDisplayName"] = a } }),
                        }),
                    };
                    JsonObject League(string name, params JsonObject[] evs) => new() { ["leagues"] = new JsonArray(new JsonObject { ["name"] = name }), ["events"] = new JsonArray(evs) };
                    return
                    [
                        League("Saudi Pro League", Ev("Al Hilal", "Al Nassr", 1, 1, "in", $"{min}'", 15)),
                        League("English Premier League", Ev("Arsenal", "Chelsea", 2, 1, "in", $"{min + 22}'", 14), Ev("Liverpool", "Man City", 2, 2, "post", "FT", 11)),
                        League("Spanish LALIGA", Ev("Real Madrid", "Barcelona", 0, 0, "pre", "0'", 19)),
                    ];
                }
            case "bitcoin":
                {
                    var prices = new JsonArray();
                    double v = 66_200, t0 = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
                    for (int i = 0; i < 96; i++)
                    {
                        v += Math.Sin(i * 0.31) * 140 + Math.Cos(i * 0.13) * 90 + (i > 60 ? 38 : 6);
                        prices.Add(new JsonArray(t0 + i * 900_000, Math.Round(v, 2)));
                    }
                    return [new JsonObject { ["prices"] = prices }];
                }
            case "weather":
                return [JsonNode.Parse($$"""
                    { "daily": { "time": [{{string.Join(",", Enumerable.Range(0, 5).Select(i => $"\"{DateTime.Today.AddDays(i):yyyy-MM-dd}\""))}}],
                                 "weather_code": [0, 1, 3, 61, 0], "temperature_2m_max": [38.4, 37.2, 33.9, 31.6, 36.1], "temperature_2m_min": [27.5, 26.1, 24.8, 23.2, 25.4] } }
                    """)];
            default: return [];
        }
    }
}
