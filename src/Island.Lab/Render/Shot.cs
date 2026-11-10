using System.IO.Compression;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Island.Lab.Render;

/// <summary>
/// Dev: ISLAND_SHOT="&lt;seconds&gt;:&lt;file.png&gt;" saves the island's own frame (what it drew, not the screen) as a
/// PNG over a dark backdrop, then quits. With ISLAND_DEMO it checks a screen without touching the mouse.
/// ISLAND_SHOT_SCALE=2 draws that frame at twice the size (for pictures that stay sharp on any display).
/// ISLAND_FILM="&lt;from&gt;:&lt;to&gt;:&lt;fps&gt;:&lt;file.raw&gt;" saves every frame between those seconds, one after another
/// (premultiplied BGRA; the size and count go in &lt;file.raw&gt;.txt), then quits: a clip of the island with no screen recording.
/// </summary>
public static class Shot
{
    public static readonly (double At, string File)? Plan = Parse(Environment.GetEnvironmentVariable("ISLAND_SHOT"));

    public static readonly (double From, double To, double Fps, string File)? Film = ParseFilm(Environment.GetEnvironmentVariable("ISLAND_FILM"));

    static (double, double, double, string)? ParseFilm(string? v)
    {
        var p = v?.Split(':', 4);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return p is { Length: 4 } && double.TryParse(p[0], inv, out double a) && double.TryParse(p[1], inv, out double b) && double.TryParse(p[2], inv, out double f) && f > 0
            ? (a, b, f, p[3]) : null;
    }

    static FileStream? _film;
    static ID3D11Texture2D? _filmStaging;
    static byte[]? _filmRow;
    static int _filmFrames;

    /// <summary>Adds the frame on screen now to the clip (again if a slot was missed, so the clip keeps real time). True when the clip is complete.</summary>
    public static unsafe bool FilmFrame(Gpu gpu, double now)
    {
        var f = Film!.Value;
        if (now < f.From) return false;
        var desc = gpu.FrameTexture.Description;
        int w = (int)desc.Width, h = (int)desc.Height;
        if (now >= f.To)
        {
            _film?.Dispose();
            File.WriteAllText(f.File + ".txt", $"{w} {h} {_filmFrames} {f.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return true;
        }
        if (now < f.From + _filmFrames / f.Fps) return false;
        if (_filmStaging == null)
        {
            desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
            _filmStaging = gpu.Device.CreateTexture2D(desc);
            _film = File.Create(f.File);
            _filmRow = new byte[w * 4 * h];
        }
        gpu.Context.CopyResource(_filmStaging, gpu.FrameTexture);
        var map = gpu.Context.Map(_filmStaging, 0, MapMode.Read);
        try
        {
            for (int y = 0; y < h; y++)
                new ReadOnlySpan<byte>((byte*)map.DataPointer + y * map.RowPitch, w * 4).CopyTo(_filmRow.AsSpan(y * w * 4));
        }
        finally { gpu.Context.Unmap(_filmStaging, 0); }
        while (now >= f.From + _filmFrames / f.Fps) { _film!.Write(_filmRow); _filmFrames++; }
        return false;
    }

    /// <summary>The scale to draw at instead of the monitor's, while taking a shot.</summary>
    public static readonly float? Scale = (Plan != null || Film != null) && float.TryParse(Environment.GetEnvironmentVariable("ISLAND_SHOT_SCALE"),
        System.Globalization.CultureInfo.InvariantCulture, out float k) && k is >= 1 and <= 4 ? k : null;

    static (double, string)? Parse(string? v) =>
        v != null && v.IndexOf(':') is > 0 and var i && double.TryParse(v[..i], System.Globalization.CultureInfo.InvariantCulture, out double s) ? (s, v[(i + 1)..]) : null;

    public static unsafe void Save(Gpu gpu, string file)
    {
        var desc = gpu.FrameTexture.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = gpu.Device.CreateTexture2D(desc);
        gpu.Context.CopyResource(staging, gpu.FrameTexture);
        var map = gpu.Context.Map(staging, 0, MapMode.Read);
        int w = (int)desc.Width, h = (int)desc.Height;
        var raw = new byte[h * (1 + w * 3)];
        try
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)map.DataPointer + y * map.RowPitch;
                int o = y * (1 + w * 3);
                raw[o++] = 0;
                for (int x = 0; x < w; x++, o += 3)
                {
                    // premultiplied BGRA over #1C1C1E
                    float a = row[x * 4 + 3] / 255f;
                    raw[o] = (byte)Math.Min(255, row[x * 4 + 2] + 0x1C * (1 - a));
                    raw[o + 1] = (byte)Math.Min(255, row[x * 4 + 1] + 0x1C * (1 - a));
                    raw[o + 2] = (byte)Math.Min(255, row[x * 4] + 0x1E * (1 - a));
                }
            }
        }
        finally { gpu.Context.Unmap(staging, 0); }

        using var fs = File.Create(file);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
            Chunk(fs, "IDAT", ms.ToArray());
        }
        Chunk(fs, "IEND", []);
    }

    static void BE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        data.CopyTo(body, 4);
        s.Write(body);
        var crc = new byte[4]; BE(crc, 0, Crc(body)); s.Write(crc);
    }

    static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
