using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Island.Lab.Island;

/// <summary>What a link's own page says about itself: its title, its icon, its preview picture.</summary>
public sealed record LinkInfo(string? Title, string? Icon, string? Image);

/// <summary>Reading what a link's page says about itself (its title, icon and picture), off the drawing thread.</summary>
public static class PocketOps
{
    // ---------------------------------------------------------------- a link's own page

    static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        h.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return h;
    }

    static readonly Regex Tag = new(@"<(meta|link)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Attr = new(@"([a-zA-Z:\-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);
    static readonly Regex TitleTag = new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Asks the page (only the page itself, nobody else) for its title, its icon and its preview picture.</summary>
    public static async Task<LinkInfo?> Link(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        string html = "";
        try
        {
            using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            uri = res.RequestMessage?.RequestUri ?? uri;
            if (res.IsSuccessStatusCode && (res.Content.Headers.ContentType?.MediaType ?? "").Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                using var body = await res.Content.ReadAsStreamAsync();
                var buf = new byte[400_000];
                int n = 0, got;
                while (n < buf.Length && (got = await body.ReadAsync(buf.AsMemory(n, buf.Length - n))) > 0) n += got;
                html = Encoding.UTF8.GetString(buf, 0, n);
            }
        }
        catch (Exception ex) { Diag.Log("link preview: " + ex.Message); }

        string? title = null, image = null, icon = null;
        int best = -1;
        foreach (Match t in Tag.Matches(html))
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Attr.Matches(t.Value))
                a[m.Groups[1].Value] = System.Net.WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
            if (t.Groups[1].Value.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                string key = a.GetValueOrDefault("property") ?? a.GetValueOrDefault("name") ?? "";
                if (!a.TryGetValue("content", out var content) || content.Length == 0) continue;
                if (key.Equals("og:title", StringComparison.OrdinalIgnoreCase)) title = content;
                else if (key.Equals("twitter:title", StringComparison.OrdinalIgnoreCase)) title ??= content;
                else if (key.Equals("og:image", StringComparison.OrdinalIgnoreCase) || key.Equals("og:image:secure_url", StringComparison.OrdinalIgnoreCase)) image ??= content;
                else if (key.Equals("twitter:image", StringComparison.OrdinalIgnoreCase)) image ??= content;
            }
            else
            {
                string rel = a.GetValueOrDefault("rel") ?? "";
                if (!rel.Contains("icon", StringComparison.OrdinalIgnoreCase) || !a.TryGetValue("href", out var href) || href.Length == 0) continue;
                if (href.Contains(".svg", StringComparison.OrdinalIgnoreCase) || rel.Contains("mask", StringComparison.OrdinalIgnoreCase)) continue;
                int score = rel.Contains("apple", StringComparison.OrdinalIgnoreCase) ? 180 : 32;
                if (a.TryGetValue("sizes", out var sizes) && int.TryParse(sizes.Split('x', 'X')[0], out int px)) score = px;
                if (score > 512) score = 100;   // (a huge icon is a slow download for a small tile)
                if (score > best) { best = score; icon = href; }
            }
        }
        if (title == null && TitleTag.Match(html) is { Success: true } tm) title = System.Net.WebUtility.HtmlDecode(tm.Groups[1].Value);
        title = title == null ? null : Regex.Replace(title, @"\s+", " ").Trim();
        if (title is { Length: 0 }) title = null;
        if (title is { Length: > 140 }) title = title[..140];
        string? Abs(string? s) => s != null && Uri.TryCreate(uri, s, out var u) && u.Scheme is "http" or "https" ? u.AbsoluteUri : null;
        return new LinkInfo(title, Abs(icon) ?? new Uri(uri, "/favicon.ico").AbsoluteUri, Abs(image));
    }
}
