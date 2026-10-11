using System.IO.Compression;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Island.Lab.Render;

/// <summary>
/// Dev: ISLAND_SHOT="&lt;seconds&gt;:&lt;file.png&gt;" saves the island's own frame (what it drew, not the screen) as a
/// PNG over a dark backdrop, then quits. With ISLAND_DEMO it checks a screen without touching the mouse.
/// </summary>
public static class Shot
{
    public static readonly (double At, string File)? Plan = Parse(Environment.GetEnvironmentVariable("ISLAND_SHOT"));

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
