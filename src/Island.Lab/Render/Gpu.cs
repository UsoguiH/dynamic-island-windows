using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Island.Lab.Render;

/// <summary>
/// D3D11 + D2D on a premultiplied-alpha flip-model swap chain, presented through
/// DirectComposition so the window has true per-pixel transparency at the monitor's refresh rate.
/// </summary>
public sealed class Gpu : IDisposable
{
    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Context;
    public readonly IDXGISwapChain1 SwapChain;
    public readonly ID2D1Factory1 D2DFactory;
    public readonly ID2D1DeviceContext D2D;
    public readonly IDWriteFactory DWrite;

    readonly IDCompositionDevice _dcomp;
    readonly IDCompositionTarget _target;
    readonly IDCompositionVisual _visual;
    // D2D draws the whole frame into an offscreen texture; Present copies the finished frame into the
    // swap chain's back buffer in one GPU copy. Drawing D2D straight into buffer 0 occasionally let a
    // half-drawn frame reach the screen (the body without content, or only the glow).
    readonly ID3D11Texture2D _frameTex;
    readonly ID3D11Texture2D _backTex;
    readonly ID2D1Bitmap1 _frame;
    public ID2D1Bitmap1 BackBuffer => _frame;

    /// <summary>Binds the frame target. Call before BeginDraw every frame.</summary>
    public void BeginFrame() => D2D.Target = _frame;

    public readonly int Width, Height;
    public readonly float Scale;

    public Gpu(nint hwnd, int width, int height, float scale)
    {
        Width = width; Height = height; Scale = scale;

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0], out Device!, out Context!).CheckError();

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        SwapChain = factory.CreateSwapChainForComposition(Device, new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            Scaling = Scaling.Stretch,
        });

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _visual = _dcomp.CreateVisual();
        _visual.SetContent(SwapChain);
        _target.SetRoot(_visual);
        _dcomp.Commit();

        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using var d2dDevice = D2DFactory.CreateDevice(dxgiDevice);
        D2D = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _frameTex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _backTex = SwapChain.GetBuffer<ID3D11Texture2D>(0);
        using (var surface = _frameTex.QueryInterface<IDXGISurface>())
            _frame = D2D.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f * scale, 96f * scale, BitmapOptions.Target | BitmapOptions.CannotDraw));
        D2D.Target = _frame;
        D2D.SetDpi(96f * scale, 96f * scale);
        D2D.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>();

    }

    /// <summary>Sync 1 waits for vsync (only one window per frame should do that); 0 presents immediately.</summary>
    public void Present(uint sync = 1)
    {
        Context.CopyResource(_backTex, _frameTex);
        SwapChain.Present(sync, PresentFlags.None);
    }

    public void Dispose()
    {
        _frame.Dispose(); _frameTex.Dispose(); _backTex.Dispose(); D2D.Dispose(); D2DFactory.Dispose(); DWrite.Dispose();
        _visual.Dispose(); _target.Dispose(); _dcomp.Dispose();
        SwapChain.Dispose(); Context.Dispose(); Device.Dispose();
    }
}
