using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Island.Lab.Platform;
using Island.Lab.Render;

namespace Island.Lab.Island;

/// <summary>
/// Turns a sentence ("live scores of today's matches") into a working tab. Claude Code, through your own login,
/// looks for a free public source and writes a recipe; it can only search and read the web (no files, no
/// commands, no other tools). The island then fetches the source itself and checks every field, and when
/// something is off it hands Claude what the source really returned, up to twice. Nothing runs after that:
/// the tab is the recipe.
/// </summary>
public sealed class TabBuilder
{
    /// <summary>A line of progress: "Found a live source" <b>coingecko.com</b>.</summary>
    public sealed record Step(string Lead, string Bold, bool Live = false, bool Done = false, bool Bad = false);

    public readonly string Request;
    readonly ConcurrentQueue<Step> _steps = new();
    readonly CancellationTokenSource _cancel = new();
    Process? _proc;

    public volatile bool Finished;
    public Recipe? Recipe { get; private set; }
    public JsonObject? Json { get; private set; }
    public CustomData? Data { get; private set; }
    public string? Error { get; private set; }
    /// <summary>One-tap variations Claude offered ("Only the Premier League"), each a JSON merge patch.</summary>
    public List<(string Label, JsonObject Patch)> Tweaks { get; } = new();
    public string Layout { get; private set; } = "";

    public TabBuilder(string request) => Request = request.Trim();

    /// <summary>A tweak was applied: it is now the recipe.</summary>
    public void Adopt(Recipe r, JsonObject json, CustomData? d) { Recipe = r; Json = json; Data = d; }

    public bool Next(out Step step) => _steps.TryDequeue(out step!);
    void Say(string lead, string bold, bool live = false) => _steps.Enqueue(new Step(lead, bold, live));

    public void Start(string about)
    {
        Say("Understanding", about);
        Task.Run(async () =>
        {
            try { await Run(); }
            catch (OperationCanceledException) { Error = "cancelled"; }
            catch (Exception ex) { Error = ex.Message; Diag.Log("tab builder: " + ex); }
            if (Error != null && !_cancel.IsCancellationRequested) _steps.Enqueue(new Step("Couldn't build it", Error, Bad: true));
            Finished = true;
        });
    }

    public void Cancel()
    {
        _cancel.Cancel();
        try { if (_proc is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch { }
    }

    async Task Run()
    {
        if (Showcase.On) { await Pretend(); return; }
        string prompt = $"Build a tab for: {Request}\n\n{Context()}";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string answer = await Ask(prompt, attempt == 0);
            _cancel.Token.ThrowIfCancellationRequested();
            var json = Extract(answer) ?? throw new InvalidDataException(attempt == 0 ? "Claude didn't answer with a recipe" : "no recipe came back");
            var recipe = Recipe.Parse("new", Strip(json), out var err);
            if (recipe != null && attempt == 0) { Say("Found a live source", HostOf(recipe.Urls[0]), live: true); }
            if (recipe != null && attempt == 0) Say("Laying it out", json["layout"]?.ToString() is { Length: > 0 } lay ? lay : Describe(recipe.View));
            if (recipe != null) Say("Filling in", "real data");
            var (data, problem, roots) = recipe == null ? (null, err, Array.Empty<JsonNode?>()) : await Check(recipe, picky: attempt < 2);
            problem ??= Missing(data);
            _cancel.Token.ThrowIfCancellationRequested();
            if (problem == null)
            {
                Recipe = recipe; Json = json; Data = data;
                Layout = json["layout"]?.ToString() ?? "";
                if (json["tweaks"] is JsonArray tw)
                    foreach (var t in tw.OfType<JsonObject>().Take(2))
                        if (t["label"]?.ToString() is { Length: > 0 } label && t["patch"] is JsonObject patch) Tweaks.Add((label, patch));
                return;
            }
            Diag.Log($"tab builder: attempt {attempt + 1}: {problem}");
            if (attempt == 2)
            {
                // the pictures never came, but the tab works: better a tab without them than none
                if (data != null && recipe != null && problem.StartsWith("the user asked for pictures")) { Recipe = recipe; Json = json; Data = data; Layout = json["layout"]?.ToString() ?? ""; return; }
                throw new InvalidDataException(problem);
            }
            Say("Fixing a detail", Short(problem));
            prompt = $"""
                Build a tab for: {Request}

                {Context()}

                Your previous recipe:
                ```json
                {json.ToJsonString(Pretty)}
                ```

                The island fetched it and found a problem: {problem}{(problem.Contains("can't be reached") ? ". A better path won't help: pick a different source." : "")}

                {(roots.Length > 0 && roots[0] != null ? $"What the first source really returned (a skeleton: arrays cut to their first element, long text cut):\n```json\n{Skeleton(roots[0])}\n```" : "")}

                Fix the recipe (or pick another free source). Answer with one ```json block.
                """;
        }
    }

    /// <summary>Where and when the user is (Windows' region and time zone), for requests that don't say.</summary>
    static string Context()
    {
        string region = "";
        try { region = System.Globalization.RegionInfo.CurrentRegion.EnglishName; } catch { }
        return $"(The user's Windows region is {region}, time zone {TimeZoneInfo.Local.DisplayName}; it is now {DateTime.Now:yyyy-MM-dd HH:mm}. If they don't name a place, use their region's main city.)";
    }

    /// <summary>A tweak: the recipe with a merge patch applied, checked against the live source like the first one.</summary>
    public static async Task<(Recipe? r, JsonObject? json, CustomData? data, string? problem)> Apply(JsonObject json, JsonObject patch)
    {
        var merged = (JsonObject)json.DeepClone();
        Merge(merged, patch);
        var r = Recipe.Parse("new", Strip(merged), out var err);
        if (r == null) return (null, null, null, err);
        var (d, problem, _) = await Check(r);
        return (problem == null ? r : null, merged, d, problem);
    }

    static void Merge(JsonObject into, JsonObject patch)
    {
        foreach (var (k, v) in patch.ToList())
        {
            if (v == null) into.Remove(k);
            else if (v is JsonObject po && into[k] is JsonObject io) Merge(io, po);
            else into[k] = v.DeepClone();
        }
    }

    /// <summary>The recipe as saved: the builder's own notes (what it understood, layout, tweaks) left out.</summary>
    public static JsonObject Strip(JsonObject json)
    {
        var o = (JsonObject)json.DeepClone();
        foreach (var k in new[] { "about", "layout", "tweaks" }) o.Remove(k);
        return o;
    }

    // ================================================================ checking: the island fetches it itself

    static async Task<(CustomData? data, string? problem, JsonNode?[] roots)> Check(Recipe r, bool picky = false)
    {
        var (roots, errors) = await CustomTabs.FetchRaw(r);
        if (roots.All(x => x == null)) return (null, errors.FirstOrDefault(e => e.Length > 0) ?? "the source didn't answer", roots);
        var ok = roots.Where(x => x != null).ToArray();
        CustomData d;
        try { d = CustomTabs.Shape(r, ok, HostOf(r.Urls[0])); }
        catch (Exception ex) { return (null, "reading the response failed: " + ex.Message, roots); }
        if (r.View == "price")
            return d.Series.Length < 2 && d.Value == null ? (d, "\"series\" found no numbers (and there is no \"value\")", roots) : (d, null, roots);
        if (d.Rows.Count == 0)
        {
            // an empty list is fine (no matches today) as long as the list itself is there
            bool listThere = r.Items is JsonValue v && ok.Any(root => CustomTabs.Eval(root, root, v.ToString(), expand: false) is [JsonArray]);
            return listThere ? (d, null, roots) : (d, "\"items\" found no rows in the response", roots);
        }
        string[] required = r.View switch
        {
            "scores" => ["home", "away"], "days" => ["day", "high"], "schedule" => ["time"], "stats" => ["value"], "posters" => ["title", "image"], _ => ["title"],
        };
        var keys = d.Rows.SelectMany(row => row.Keys).Distinct().ToList();
        var missing = required.Where(f => !keys.Contains(f)).ToArray();
        if (r.View == "schedule" && !keys.Contains("name") && !keys.Contains("title")) missing = [.. missing, "name"];
        if (missing.Length > 0) return (d, $"the {r.View} view needs the field{(missing.Length > 1 ? "s" : "")} {string.Join(", ", missing)}", roots);
        var blank = keys.Where(k => d.Rows.All(row => string.IsNullOrEmpty(row.GetValueOrDefault(k)))).ToArray();
        if (blank.Intersect(required).Any() || blank.Length * 2 > keys.Count)
            return (d, $"these fields came out empty on every row: {string.Join(", ", blank)}", roots);
        if (r.View == "schedule" && d.Rows.Count(row => CustomTabs.ParseClock(row.GetValueOrDefault("time", "")) != null) * 2 < d.Rows.Count)
            return (d, "\"time\" isn't a time of day on most rows (it needs \"04:42\", \"4:42 PM\" or an ISO date-time)", roots);
        if (picky && d.Rows.Count == 1 && r.View is "list" or "schedule" or "stats")
            return (d, $"only one row came out, but the {r.View} view shows one row per thing. If the values sit side by side in one object, make each its own row: \"items\": {{ \"entries\": path, \"only\": [keys] }} or fixed \"rows\"", roots);
        // the pictures have to be real pictures: fetch the first one the way the island will
        var pics = d.Rows.SelectMany(row => new[] { "image", "homeLogo", "awayLogo" }.Select(k => row.GetValueOrDefault(k, ""))).Where(u => u.Length > 0).Take(1).ToArray();
        foreach (var u in pics)
            if (await Images.Check(u) is { } bad) return (d, $"\"image\" doesn't give a picture: {bad}. Use a direct https link to the image file from the response", roots);
        return (d, null, roots);
    }

    /// <summary>The request asks for pictures (posters, covers, logos, photos), in English or Arabic.</summary>
    static readonly Regex WantsPictures = new(@"pic|photo|image|poster|cover|logo|thumbnail|artwork|صور|بوستر|بوسترات|غلاف|شعار", RegexOptions.IgnoreCase);

    /// <summary>What the user asked for that the recipe left out, or null.</summary>
    string? Missing(CustomData? d)
    {
        if (d == null || d.Rows.Count == 0 || !WantsPictures.IsMatch(Request)) return null;
        bool any = d.Rows.Any(row => row.GetValueOrDefault("image", "").Length > 0 || row.GetValueOrDefault("homeLogo", "").Length > 0);
        return any ? null : "the user asked for pictures, but no row has an \"image\". Use a source that gives a picture for each thing, add \"image\" (a direct https link to it), and use the \"posters\" view";
    }

    // ================================================================ Claude Code

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    async Task<string> Ask(string prompt, bool first)
    {
        string cwd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Island", "builder");
        Directory.CreateDirectory(cwd);
        var psi = new ProcessStartInfo(AgentRunner.ClaudeExe())
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // least privilege: web search and fetch only, nothing from your settings or MCP servers, no saved session
        foreach (var a in new[] { "-p", "--output-format", "stream-json", "--verbose", "--restricted", "--strict-mcp-config", "--no-session-persistence",
                                  "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch", "WebFetch", "--permission-mode", "dontAsk",
                                  "--model", Environment.GetEnvironmentVariable("ISLAND_BUILDER_MODEL") ?? "opus", "--system-prompt", Brief })
            psi.ArgumentList.Add(a);
        foreach (var k in psi.Environment.Keys.ToList())
            if (k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) || k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(k);

        try { _proc = Process.Start(psi) ?? throw new IOException(); }
        catch { throw new IOException("Claude Code isn't installed"); }
        var p = _proc;
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();

        string? result = null, failure = null;
        bool searched = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            while (await p.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                JsonNode? ev;
                try { ev = JsonNode.Parse(line); } catch { continue; }
                switch (ev?["type"]?.ToString())
                {
                    case "assistant" when (ev["message"]?["content"] is JsonArray parts):
                        foreach (var part in parts.OfType<JsonObject>())
                        {
                            if (part["type"]?.ToString() != "tool_use") continue;
                            var input = part["input"];
                            if (part["name"]?.ToString() == "WebSearch" && first)
                            {
                                Say(searched ? "Still looking" : "Looking for a live source", Short(input?["query"]?.ToString() ?? "", 34));
                                searched = true;
                            }
                            else if (part["name"]?.ToString() == "WebFetch") Say("Reading", HostOf(input?["url"]?.ToString()));
                        }
                        break;
                    case "result":
                        if (ev["is_error"]?.GetValue<bool>() == true || ev["subtype"]?.ToString() is { } st && st != "success")
                            failure = ev["result"]?.ToString() is { Length: > 0 } msg ? msg : ev["subtype"]?.ToString();
                        else result = ev["result"]?.ToString();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Claude took too long");
        }
        await p.WaitForExitAsync(_cancel.Token);
        if (result != null) return result;
        string why = failure ?? (await err).Trim();
        Diag.Log("tab builder: claude failed: " + why);
        if (Regex.IsMatch(why, "login|log in|api key|auth|credential", RegexOptions.IgnoreCase)) throw new IOException("sign in to Claude Code first");
        throw new IOException(why.Length > 0 ? Short(why, 60) : "Claude Code stopped");
    }

    /// <summary>The recipe in Claude's answer: a ```json block, or failing that the outermost braces.</summary>
    static JsonObject? Extract(string text)
    {
        var m = Regex.Match(text, @"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline);
        string body = m.Success ? m.Groups[1].Value : text.IndexOf('{') is >= 0 and var a && text.LastIndexOf('}') is var b && b > a ? text[a..(b + 1)] : "";
        try { return JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch { return null; }
    }

    /// <summary>A response, cut down so Claude can see its shape: arrays keep their first element, long strings are cut.</summary>
    static string Skeleton(JsonNode? root)
    {
        JsonNode? Cut(JsonNode? n, int depth) => n switch
        {
            JsonObject o => depth > 7 ? JsonValue.Create("{…}") : new JsonObject(o.Take(40).Select(kv => KeyValuePair.Create(kv.Key, Cut(kv.Value, depth + 1)))),
            JsonArray a => a.Count == 0 ? new JsonArray() : new JsonArray(Cut(a[0], depth + 1), JsonValue.Create($"… {a.Count} items")),
            JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 60 => JsonValue.Create(s[..60] + "…"),
            _ => n?.DeepClone(),
        };
        var s = Cut(root, 0)?.ToJsonString(Pretty) ?? "null";
        return s.Length > 7000 ? s[..7000] + "\n…" : s;
    }

    static string HostOf(string? url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "the web";
    static string Short(string s, int n = 44) { s = Regex.Replace(s, @"\s+", " ").Trim(); return s.Length <= n ? s : s[..(n - 1)].TrimEnd() + "…"; }
    static string Describe(string view) => view switch { "scores" => "score rows by league", "price" => "big number + chart", "days" => "a card per day", "posters" => "a shelf of posters", _ => "a list" };

    /// <summary>What the finished tab holds, for the last line ("Ready · 4 matches today").</summary>
    public static string Summary(Recipe r, CustomData? d)
    {
        int n = d?.Rows.Count ?? 0;
        return r.View switch
        {
            "price" => d?.Series.Length is > 1 and var p ? $"{p} prices" : "live price",
            "scores" => n == 0 ? "nothing on right now" : n == 1 ? "1 match" : $"{n} matches",
            "days" => $"{n} days",
            _ => n == 1 ? "1 item" : $"{n} items",
        };
    }

    /// <summary>The command-line test: builds, prints each step and the recipe, saves nothing.</summary>
    public static int Cli(string request)
    {
        var b = new TabBuilder(request);
        var t0 = DateTime.Now;
        b.Start(request);
        while (true)
        {
            while (b.Next(out var s)) Console.WriteLine($"{(DateTime.Now - t0).TotalSeconds,5:0.0}s  {s.Lead} · {s.Bold}");
            if (b.Finished && !b.Next(out _)) break;
            Thread.Sleep(100);
        }
        if (b.Recipe == null) { Console.WriteLine("failed: " + b.Error); return 1; }
        Console.WriteLine($"ready · {Summary(b.Recipe, b.Data)}");
        Console.WriteLine(b.Json!.ToJsonString(Pretty));
        foreach (var row in b.Data?.Rows.Take(6) ?? []) Console.WriteLine("  " + string.Join("  ", row.Select(kv => $"{kv.Key}={kv.Value}")));
        if (b.Data?.Value is { } v) Console.WriteLine($"  value={v} change={b.Data.Change} points={b.Data.Series.Length}");
        return 0;
    }

    // ================================================================ showcase: the demo builds from the examples

    async Task Pretend()
    {
        string t = Request.ToLowerInvariant();
        string id = Regex.IsMatch(t, "match|score|football|soccer|game|league|goal") ? "matches"
                  : Regex.IsMatch(t, "bitcoin|crypto|btc|eth|price|stock") ? "bitcoin"
                  : Regex.IsMatch(t, "weather|temperature|rain|forecast|sun") ? "weather" : "";
        await Task.Delay(1100, _cancel.Token);
        if (id == "") throw new InvalidDataException("the demo builds scores, prices or weather");
        var json = CustomTabs.ExampleJson(id);
        Say("Found a live source", HostOf(json["source"]?["url"]?.ToString() ?? json["source"]?["urls"]?[0]?.ToString()), live: true);
        await Task.Delay(1250, _cancel.Token);
        var r = Recipe.Parse(id, json, out _)!;
        Say("Laying it out", Describe(r.View));
        await Task.Delay(1250, _cancel.Token);
        Say("Filling in", "real data");
        Data = await CustomTabs.Fetch(new CustomTab(r));
        await Task.Delay(500, _cancel.Token);
        Recipe = r; Json = json;
    }

    // ================================================================ what Claude is told

    static readonly string Brief = $$$$"""
        You build live tabs for a Windows "Dynamic Island". The user says what they want to keep an eye on; you find a free public JSON API for it and answer with a recipe. The island fetches and draws it by itself from then on (no AI runs after this), so the recipe must be exactly right, and the tab should look great.

        First understand what they mean, not only their words (they may write in Arabic or any language, and casually):
        - "new episodes" means episodes that came out recently (newest first, with the episode number and when it aired), not the most popular shows of all time. "Live" means it refreshes on its own.
        - Anything people recognise by a picture (anime, shows, movies, games, books, albums, products, people, teams) belongs in the "posters" view with a picture per row. If they mention pictures, photos, posters, covers, logos or images (صورة, صور, بوستر, غلاف, شعار), every row MUST have an "image"; the island checks it.
        - Pick the source that gives the best result, not the first one: real pictures (cover, poster, thumbnail, logo URLs), a link to each thing's page, and the freshest data.

        Rules
        - The source must answer HTTPS with JSON and need no key, login, cookie or special header. GET by default; GraphQL APIs are POSTed to with "post". Keyless APIs that work: ESPN site API (site.api.espn.com/apis/site/v2/sports/<sport>/<league>/scoreboard, e.g. soccer/eng.1, soccer/esp.1, soccer/ksa.1, soccer/uefa.champions, basketball/nba, football/nfl), CoinGecko (api.coingecko.com/api/v3), Open-Meteo (api.open-meteo.com/v1/forecast; geocoding-api.open-meteo.com/v1/search for coordinates), Hacker News via Algolia (hn.algolia.com/api/v1/search?tags=front_page), Frankfurter exchange rates (api.frankfurter.app), Launch Library 2 (ll.thespacedevs.com/2.2.0/launch/upcoming/), USGS earthquakes (earthquake.usgs.gov/earthquakes/feed/v1.0/summary/...geojson), GitHub's public REST API (api.github.com, unauthenticated), Reddit (www.reddit.com/r/<sub>/top.json), Wikipedia REST, Aladhan prayer times (api.aladhan.com/v1/timingsByCity?city=Riyadh&country=Saudi%20Arabia&method=4, times are local "HH:mm"), AniList GraphQL for anime and manga (POST https://graphql.anilist.co; airingSchedules gives the episodes that just aired, media.coverImage.extraLarge the poster, media.coverImage.color its colour, media.siteUrl its page; see the example), Kitsu anime (kitsu.io/api/edge/anime?filter[status]=current&sort=-userCount, posters at attributes.posterImage.large), TVmaze for TV (api.tvmaze.com/schedule/web?date={today}, show.image.original), Apple's charts with artwork (rss.applemarketingtools.com/api/v2/us/music/most-played/25/albums.json, also /apps/top-free/, /books/top-paid/), FreeToGame (www.freetogame.com/api/games, thumbnail), CheapShark game deals (www.cheapshark.com/api/1.0/deals?pageSize=20, thumb), Open Library (openlibrary.org/search.json, covers at covers.openlibrary.org/b/id/<cover_i>-L.jpg). Jikan (api.jikan.moe) often can't be reached: don't use it.
        - Use WebSearch / WebFetch only to find or confirm an endpoint and its field names, and be quick: a few lookups at most. Prefer an API you know well.
        - Only use paths you have seen in a real response or in official docs. The island will fetch your URLs, check every field, and tell you what really came back if something is off.
        - Answer with one ```json block and nothing else.

        Recipe
        {
          "name": the tab's label, 1-2 words,
          "title": its header line (e.g. "Today's matches", "Bitcoin", "Riyadh · this week"),
          "about": what you understood, 2-4 words joined by " · " (e.g. "bitcoin · price", "live scores · today"),
          "layout": how it is laid out, 3-6 words (e.g. "big number + 24 h chart", "score rows by league"),
          "icon": one of E7C1 flag/sport, EAFC market/price, E706 sun/weather, E774 globe, E909 world, E787 calendar, E823 clock, E709 airplane, E804 car, E8D6 music, E714 video/anime/tv, E8B2 movies, E7FC games, E736 books, E734 star, E715 mail, E80F home, E8BD chat, E9D9 pulse, E719 shop, E8C7 card/money, E7BE school, E8A5 document, E946 info, E7BA warning,
          "color": accent "#RRGGBB",
          "view": "scores" | "price" | "days" | "list" | "schedule" | "stats" | "posters" (pick the one that shows the thing best, see below),
          "empty": shown when there is nothing (optional, e.g. "No matches today"),
          "source": { "urls": [1 to 6 URLs, each parsed the same way, rows concatenated], "post": a JSON body to POST instead of GET (optional, for GraphQL: { "query": "..." }), "every": seconds between refreshes (30 live scores, 60 prices, 300 news, 600 new episodes, 900 weather) },
          "items": where the rows come from, one row per thing. The path to a list in each response; or { "zip": { "name": "path to an array", ... } } to pair parallel arrays; or { "entries": "path to an object", "only": [keys, in order] } to make each key of an object a row (its row has "key" and "value"),
          "rows": instead of "items" and "fields", fixed rows read from the response: [ { field: spec, ... }, ... ] (a row's constant text is { "text": "Fajr" }),
          "fields": { field: a path, or a list of paths (the first that has a value: ["title.english", "title.romaji"]), or { "path": ..., "text": constant, "map": { raw value: shown value, "*": fallback }, "add": minutes added to a time, "format": "time" | "time:HH:mm" | "weekday" | "date" | "date:MMM d" | "ago" (Unix seconds/ms or a date → "3 h ago", "in 2 d") | "round" | "number:N" | "signed:N" (+2.1 / −0.4) | "wmo", "prefix": ..., "suffix": ..., "default": ... } },
          "options": any view: { "sort": a field to order rows by ("-popularity": biggest first; the field may be one the view doesn't show), "limit": how many rows to keep }; price view: { "prefix": "$", "suffix": "", "decimals": 0, "span": "24 h" }; list view: { "numbered": true } for a ranking,
          "tweaks": up to 2 one-tap variations the user may like, each { "label": "Only the Premier League", "patch": a JSON merge patch applied to this recipe }
        }
        Templates, in URLs and POST bodies: {today}, {today:yyyyMMdd}, {tomorrow:...}, {yesterday:...} (.NET date formats, local time); {unix} (now, Unix seconds), {unix-172800} (two days ago), {unix+86400}.
        Paths are relative to the row: "a.b[0].c", "[-1]" the last element, "[*]" every element, "[?key=value]" the elements whose key (or nested key, "[?media.isAdult=false]") equals value; a leading "$." starts from the whole response.

        Views and the fields they draw
        - posters: a shelf of tall picture cards that slides sideways (anime, episodes, shows, movies, games, books, albums, products): image (required: a direct https link to the picture, the biggest the source has), title, detail (a short second line, e.g. "TV" or the genre), note (e.g. when: format "ago"), badge (a short chip on the picture: "EP 7", "NEW", "-60%"), value (a chip top-right: a rating "8.4", a price), tint (the picture's main colour "#hex", if the source gives one), link (the thing's page; a click opens it). Tall pictures make tall poster cards; wide ones (game art, video thumbnails) make landscape cards with the words under them (options "shape": "tall" | "wide" to choose).
        - scores: group (league label), home, away, homeScore, awayScore, state ("live" | "done" | "soon": map the API's own values), clock (e.g. "67'"), time (kick-off; format "time"), homeLogo and awayLogo (team logo URLs; ESPN: competitors[...].team.logo).
        - price: series (numbers, oldest first, e.g. "prices[*][1]"); value and change (%) are optional and otherwise come from the series.
        - days: day (format "weekday"), high and low (format "round", suffix "°"), icon ("sun" | "partly" | "cloud" | "fog" | "rain" | "snow" | "storm"; Open-Meteo weather codes: format "wmo").
        - schedule: the times of one day, the next one counting down (prayer times, iqama times, departures, today's events): name, time (a time of day, or a date-time), detail (optional, e.g. "Adhan 04:42").
        - stats: 1 to 8 big numbers side by side (several coins, servers, cities, scores of one thing): label, value, note (optional; "+2.1%" shows green, "-0.4%" red), image (optional logo, e.g. CoinGecko's "image"), dot (optional).
        - list: one row per thing, scrollable (stories, launches, releases, rankings): title, detail (a second line, optional), value (right, optional), note (under the value, optional), image (optional thumbnail), link (optional), dot (optional: "green" | "orange" | "red" | "blue" | "#hex", usually through "map").
        Never squeeze several things into one row: five prayer times are five rows, three coins are three tiles. Give pictures and links whenever the source has them.
        Keep names and labels short, in English unless the user asks otherwise. Iqama is the adhan time plus the mosque's wait; without a mosque, use Umm al-Qura's usual waits (Fajr 25, Dhuhr 20, Asr 20, Maghrib 10, Isha 20 minutes).

        Examples
        ```json
        {{{{CustomTabs.ExampleJson("matches").ToJsonString()}}}}
        ```
        ```json
        {{{{CustomTabs.ExampleJson("bitcoin").ToJsonString()}}}}
        ```
        ```json
        {{{{CustomTabs.ExampleJson("weather").ToJsonString()}}}}
        ```
        ```json
        {"name":"Prayer","title":"Riyadh · prayer times","icon":"E823","color":"#30D158","view":"schedule","source":{"url":"https://api.aladhan.com/v1/timingsByCity?city=Riyadh&country=Saudi%20Arabia&method=4","every":3600},"items":{"entries":"data.timings","only":["Fajr","Dhuhr","Asr","Maghrib","Isha"]},"fields":{"name":"key","time":"value"}}
        ```
        ```json
        {"name":"Iqama","title":"Riyadh · iqama","icon":"E823","color":"#30D158","view":"schedule","source":{"url":"https://api.aladhan.com/v1/timingsByCity?city=Riyadh&country=Saudi%20Arabia&method=4","every":3600},"rows":[{"name":{"text":"Fajr"},"time":{"path":"data.timings.Fajr","add":25},"detail":{"path":"data.timings.Fajr","prefix":"Adhan "}},{"name":{"text":"Dhuhr"},"time":{"path":"data.timings.Dhuhr","add":20},"detail":{"path":"data.timings.Dhuhr","prefix":"Adhan "}}]}
        ```
        ```json
        {"name":"Anime","title":"New episodes","icon":"E714","color":"#BF5AF2","view":"posters","empty":"No new episodes in the last two days","source":{"url":"https://graphql.anilist.co","post":{"query":"{ Page(perPage: 40) { airingSchedules(airingAt_greater: {unix-172800}, airingAt_lesser: {unix}, sort: TIME_DESC) { episode airingAt media { title { romaji english } coverImage { extraLarge color } siteUrl isAdult format popularity } } } }"},"every":600},"items":"data.Page.airingSchedules[?media.isAdult=false]","fields":{"title":{"path":["media.title.english","media.title.romaji"]},"image":"media.coverImage.extraLarge","tint":"media.coverImage.color","link":"media.siteUrl","badge":{"path":"episode","prefix":"EP "},"note":{"path":"airingAt","format":"ago"},"detail":{"path":"media.format","map":{"TV":"TV","TV_SHORT":"TV short","ONA":"Web","MOVIE":"Movie","*":""}},"popularity":"media.popularity"},"options":{"sort":"-popularity","limit":24}}
        ```
        ```json
        {"name":"Coins","title":"Crypto","icon":"EAFC","color":"#FF9F0A","view":"stats","source":{"url":"https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&ids=bitcoin,ethereum,solana","every":60},"items":"","fields":{"label":"name","value":{"path":"current_price","format":"number:0","prefix":"$"},"note":{"path":"price_change_percentage_24h","format":"signed:1","suffix":"%"}}}
        ```
        """;
}
