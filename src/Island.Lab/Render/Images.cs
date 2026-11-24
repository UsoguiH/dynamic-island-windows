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

    static bool Usable(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith(Local, StringComparison.Ordinal)
        || url.StartsWith(Big, StringComparison.Ordinal) || url.StartsWith(Thumb, StringComparison.Ordinal)
        || url.StartsWith(Icon, StringComparison.Ordinal) || url.StartsWith(Kind, StringComparison.Ordinal);

    /// <summary>"file:C:\pictures\cat.png": a picture on this PC, decoded small (a thumbnail), never copied anywhere.</summary>
    public const string Local = "file:";
    /// <summary>"filebig:C:\pictures\cat.png": the same picture, decoded large enough to be shown big.</summary>
    public const string Big = "filebig:";
    /// <summary>"thumb:C:\films\trip.mp4": what Windows shows for that file in Explorer (a film's frame, a document's first page), when it has one.</summary>
    public const string Thumb = "thumb:";
    /// <summary>"icon:C:/docs/plan.pdf": that file as Explorer shows it: its preview when it has one, otherwise its real icon.</summary>
    public const string Icon = "icon:";
    /// <summary>"kind:x.pdf", "kind:folder": the icon Windows gives that kind of file (no file needed).</summary>
    public const string Kind = "kind:";

    /// <summary>A picture file's size in pixels, read from its header (null when it isn't a picture Windows can read).</summary>
    public static (int W, int H)? SizeOf(string path)
    {
        try
        {
            using var wic = new IWICImagingFactory();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var decoder = wic.CreateDecoderFromStream(fs, DecodeOptions.CacheOnDemand);
            using var frame = decoder.GetFrame(0);
            return (frame.Size.Width, frame.Size.Height);
        }
        catch { return null; }
    }

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
        try
        {
            e.Pic = url.StartsWith(Local, StringComparison.Ordinal) ? Decode(await File.ReadAllBytesAsync(url[Local.Length..]), 168)
                  : url.StartsWith(Big, StringComparison.Ordinal) ? Decode(await File.ReadAllBytesAsync(url[Big.Length..]))
                  : url.StartsWith(Thumb, StringComparison.Ordinal) ? await Platform.Thumbs.Get(url[Thumb.Length..], 400)
                  : url.StartsWith(Icon, StringComparison.Ordinal) ? await Platform.Thumbs.Get(url[Icon.Length..], 256, 1)
                  : url.StartsWith(Kind, StringComparison.Ordinal) ? await Platform.Thumbs.Get(url[Kind.Length..], 256, 2)
                  : Decode(await Bytes(url));
            e.Failed = e.Pic == null;
        }
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
    static unsafe Pic? Decode(byte[] bytes, int max = Max)
    {
        if (bytes.Length < 16) return null;
        using var wic = new IWICImagingFactory();
        using var ms = new MemoryStream(bytes);
        using var decoder = wic.CreateDecoderFromStream(ms, DecodeOptions.CacheOnLoad);
        using var frame = Largest(decoder);
        var size = frame.Size;
        if (size.Width <= 0 || size.Height <= 0) return null;
        float k = MathF.Min(1, (float)max / Math.Max(size.Width, size.Height));
        int w = Math.Max(1, (int)MathF.Round(size.Width * k)), h = Math.Max(1, (int)MathF.Round(size.Height * k));
        using var scaler = wic.CreateBitmapScaler();
        scaler.Initialize(frame, (uint)w, (uint)h, BitmapInterpolationMode.HighQualityCubic);
        using var turned = Upright(wic, frame, scaler);
        if (turned != null) { var ts = turned.Size; w = ts.Width; h = ts.Height; }
        using var conv = wic.CreateFormatConverter();
        conv.Initialize(turned != null ? turned : scaler, PixelFormat.Format32bppPBGRA);
        var px = new byte[w * h * 4];
        fixed (byte* p = px) conv.CopyPixels(new Vortice.Mathematics.RectI(0, 0, w, h), (uint)(w * 4), (uint)px.Length, (nint)p);
        return new Pic { W = w, H = h, Px = px };
    }

    /// <summary>An icon file holds the same picture at several sizes: the largest.</summary>
    public static IWICBitmapFrameDecode Largest(IWICBitmapDecoder decoder)
    {
        uint n = decoder.FrameCount;
        if (n <= 1) return decoder.GetFrame(0);
        uint best = 0; int area = -1;
        for (uint i = 0; i < n && i < 24; i++)
        {
            using var f = decoder.GetFrame(i);
            var s = f.Size;
            if (s.Width * s.Height > area) { area = s.Width * s.Height; best = i; }
        }
        return decoder.GetFrame(best);
    }

    /// <summary>A camera writes "this side up" beside a photo instead of turning it: the picture turned the right way, or null when it already is.</summary>
    public static IWICBitmapFlipRotator? Upright(IWICImagingFactory wic, IWICBitmapFrameDecode frame, IWICBitmapSource source)
    {
        try
        {
            using var meta = frame.MetadataQueryReader;
            object? v = null;
            try { v = meta.GetMetadataByName("/app1/ifd/{ushort=274}"); } catch { }
            if (v == null) try { v = meta.GetMetadataByName("/ifd/{ushort=274}"); } catch { }
            int o = v == null ? 1 : Convert.ToInt32(v);
            var how = o switch { 3 => BitmapTransformOptions.Rotate180, 6 => BitmapTransformOptions.Rotate90, 8 => BitmapTransformOptions.Rotate270, _ => BitmapTransformOptions.Rotate0 };
            if (how == BitmapTransformOptions.Rotate0) return null;
            var r = wic.CreateBitmapFlipRotator();
            r.Initialize(source, how);
            return r;
        }
        catch { return null; }
    }

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "the source";
    static string Short(string s) => s.Length <= 40 ? s : s[..39] + "…";
}
