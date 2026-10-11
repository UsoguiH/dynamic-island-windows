using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Vortice.WIC;

namespace Island.Lab.Render;

/// <summary>
/// Pictures from the web (posters, covers, logos, thumbnails): each URL is downloaded once, kept on disk in
/// %LOCALAPPDATA%\Island\images, and decoded in the background to premultiplied BGRA, at most 520 px a side.
/// Drawing asks for a URL every frame; until it is ready there is nothing to draw, and the view shows its
/// placeholder.
/// </summary>
public static class Images
{
    public sealed class Pic { public int W, H; public byte[] Px = []; }

    sealed class Entry { public volatile Pic? Pic; public volatile bool Failed; }

    static readonly ConcurrentDictionary<string, Entry> _all = new();
    static readonly SemaphoreSlim _gate = new(6);
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Island", "images");
    const int Max = 520;

    static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IslandLab/1.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/png,image/jpeg,image/*;q=0.8,*/*;q=0.5");
        return h;
    }

    /// <summary>The picture, or null while it loads (or when it can't be loaded).</summary>
    public static Pic? Get(string url)
    {
        if (!Usable(url)) return null;
        var e = _all.GetOrAdd(url, u => { var n = new Entry(); _ = Load(u, n); return n; });
        return e.Pic;
    }

    /// <summary>True once the URL is known not to give a picture.</summary>
    public static bool Failed(string url) => !Usable(url) || _all.TryGetValue(url, out var e) && e.Failed;

    static bool Usable(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>For the tab builder: does this URL give a picture? (null when it does, otherwise why not)</summary>
    public static async Task<string?> Check(string url)
    {
        if (!Usable(url)) return $"\"{Short(url)}\" isn't an image URL";
        try
        {
            var pic = Decode(await Bytes(url));
            return pic == null ? $"{Host(url)} didn't give a picture" : null;
        }
        catch (Exception ex) { return $"the picture at {Host(url)} didn't load ({ex.Message})"; }
    }

    static async Task Load(string url, Entry e)
    {
        await _gate.WaitAsync();
        try { e.Pic = Decode(await Bytes(url)); e.Failed = e.Pic == null; }
        catch (Exception ex) { e.Failed = true; Diag.Log($"image {url}: {ex.Message}"); }
        finally { _gate.Release(); }
    }

    static async Task<byte[]> Bytes(string url)
    {
        string file = Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20] + ".img");
        if (File.Exists(file)) { try { return await File.ReadAllBytesAsync(file); } catch { } }
        using var res = await Http.GetAsync(url);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync();
        try { Directory.CreateDirectory(Dir); await File.WriteAllBytesAsync(file, bytes); } catch { }
        return bytes;
    }

    /// <summary>Any format Windows can read (JPEG, PNG, GIF, WebP…), scaled down to fit <see cref="Max"/>.</summary>
    static unsafe Pic? Decode(byte[] bytes)
    {
        if (bytes.Length < 16) return null;
        using var wic = new IWICImagingFactory();
        using var ms = new MemoryStream(bytes);
        using var decoder = wic.CreateDecoderFromStream(ms, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        if (size.Width <= 0 || size.Height <= 0) return null;
        float k = MathF.Min(1, (float)Max / Math.Max(size.Width, size.Height));
        int w = Math.Max(1, (int)MathF.Round(size.Width * k)), h = Math.Max(1, (int)MathF.Round(size.Height * k));
        using var scaler = wic.CreateBitmapScaler();
        scaler.Initialize(frame, (uint)w, (uint)h, BitmapInterpolationMode.HighQualityCubic);
        using var conv = wic.CreateFormatConverter();
        conv.Initialize(scaler, PixelFormat.Format32bppPBGRA);
        var px = new byte[w * h * 4];
        fixed (byte* p = px) conv.CopyPixels(new Vortice.Mathematics.RectI(0, 0, w, h), (uint)(w * 4), (uint)px.Length, (nint)p);
        return new Pic { W = w, H = h, Px = px };
    }

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "the source";
    static string Short(string s) => s.Length <= 40 ? s : s[..39] + "…";
}
