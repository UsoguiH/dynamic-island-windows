using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Island.Lab.Render;

/// <summary>
/// ISLAND_VERIFY=1: after every partial redraw, draws the same frame in full into a second texture and
/// compares the two pixel by pixel, logging any area where they differ (a stale or missed redraw).
/// </summary>
sealed class FrameCheck : IDisposable
{
    public static readonly bool On = Environment.GetEnvironmentVariable("ISLAND_VERIFY") == "1";

    readonly Gpu _gpu;
    readonly ID3D11Texture2D _tex, _stageA, _stageB;
    public readonly ID2D1Bitmap1 Target;
    byte[] _a = [], _b = [];
    public int Frames, Bad;
    double _logAt;
    int _dumps;
    /// <summary>What the frame was (logged with a mismatch).</summary>
    public string Note = "";
    public Func<Area, string>? Describe;

    public FrameCheck(Gpu gpu)
    {
        _gpu = gpu;
        var desc = new Texture2DDescription
        {
            Width = (uint)gpu.Width, Height = (uint)gpu.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        _tex = gpu.Device.CreateTexture2D(desc);
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read;
        _stageA = gpu.Device.CreateTexture2D(desc);
        _stageB = gpu.Device.CreateTexture2D(desc);
        using var surface = _tex.QueryInterface<IDXGISurface>();
        Target = gpu.D2D.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f * gpu.Scale, 96f * gpu.Scale, BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    /// <summary>Compares the presented frame with the full redraw just drawn into <see cref="Target"/>.</summary>
    public void Compare(ID3D11Texture2D frame, double now)
    {
        var ctx = _gpu.Context;
        ctx.CopyResource(_stageA, frame);
        ctx.CopyResource(_stageB, _tex);
        Read(_stageA, ref _a);
        Read(_stageB, ref _b);
        int w = _gpu.Width, h = _gpu.Height, bad = 0, maxD = 0, l = w, t = h, r = 0, btm = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, d = 0;
                for (int k = 0; k < 4; k++) d = Math.Max(d, Math.Abs(_a[i + k] - _b[i + k]));
                if (d > 24) { bad++; l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); btm = Math.Max(btm, y); }
                maxD = Math.Max(maxD, d);
            }
        Frames++;
        if (bad > 0) Bad++;
        if (bad > 0 && now - _logAt > 0.25)
        {
            _logAt = now;
            float sc = _gpu.Scale;
            Diag.Log($"verify MISMATCH {bad} px (max diff {maxD}) in [{l},{t} .. {r},{btm}] px #{_dumps} {Note} prims:{Describe?.Invoke(new Area(l / sc, t / sc, (r + 1) / sc, (btm + 1) / sc))}");
            if (_dumps < 12)
            {
                var dir = Path.Combine(Path.GetTempPath(), "islandverify");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{_dumps}_partial_{w}x{h}.bgra"), _a);
                File.WriteAllBytes(Path.Combine(dir, $"{_dumps}_full_{w}x{h}.bgra"), _b);
                _dumps++;
            }
        }
    }

    public int SwapFrames, SwapBad;

    /// <summary>The swap chain's back buffer, about to be presented, must hold exactly the frame.</summary>
    public void CompareBuffers(ID3D11Texture2D back, ID3D11Texture2D frame)
    {
        var ctx = _gpu.Context;
        ctx.CopyResource(_stageA, back);
        ctx.CopyResource(_stageB, frame);
        Read(_stageA, ref _a);
        Read(_stageB, ref _b);
        int bad = 0;
        for (int i = 0; i < _a.Length; i++) if (_a[i] != _b[i]) bad++;
        SwapFrames++;
        if (bad > 0) { SwapBad++; if (SwapBad < 6) Diag.Log($"verify SWAP MISMATCH {bad} bytes"); }
    }

    void Read(ID3D11Texture2D stage, ref byte[] into)
    {
        int w = _gpu.Width, h = _gpu.Height;
        if (into.Length != w * h * 4) into = new byte[w * h * 4];
        var map = _gpu.Context.Map(stage, 0, MapMode.Read);
        try
        {
            unsafe
            {
                byte* src = (byte*)map.DataPointer;
                fixed (byte* dst = into)
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + y * map.RowPitch, dst + y * w * 4, w * 4, w * 4);
            }
        }
        finally { _gpu.Context.Unmap(stage, 0); }
    }

    public void Dispose() { Target.Dispose(); _tex.Dispose(); _stageA.Dispose(); _stageB.Dispose(); }
}
