using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Island.Lab.Render;

namespace Island.Lab.Platform;

/// <summary>
/// What Explorer shows for a file: a film's frame, a document's first page, a drawing's picture. Asked from the
/// shell (the same thumbnails, from the same cache), only when the file really has one: a plain icon is not a
/// preview, and the island draws its own sheet instead.
/// </summary>
public static unsafe class Thumbs
{
    // (thumbnail makers are shell extensions: they get one thread of their own, set up the way they expect)
    static readonly BlockingCollection<(string Path, int Size, int Kind, TaskCompletionSource<Images.Pic?> Done)> _jobs = new();
    static Thread? _thread;

    /// <summary>kind 0: its preview only (nothing when all it has is an icon). 1: its preview, else its icon. 2: the icon of that
    /// kind of file ("x.pdf", "folder"), which needs no file to exist.</summary>
    public static Task<Images.Pic?> Get(string path, int size, int kind = 0)
    {
        var done = new TaskCompletionSource<Images.Pic?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_jobs)
            if (_thread == null)
            {
                _thread = new Thread(Work) { IsBackground = true, Name = "thumbs" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        _jobs.Add((path, size, kind, done));
        return done.Task;
    }

    static void Work()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            Images.Pic? pic = null;
            try { pic = job.Kind == 2 ? TypeIcon(job.Path) : Make(job.Path, job.Size, job.Kind == 0); }
            catch (Exception ex) { Diag.Log($"thumbnail {System.IO.Path.GetExtension(job.Path)}: {ex.Message}"); }
            job.Done.SetResult(pic);
        }
    }

    static Images.Pic? Make(string path, int size, bool thumbOnly)
    {
        var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");   // IShellItemImageFactory
        nint factory;
        if (SHCreateItemFromParsingName(path, 0, &iid, &factory) != 0 || factory == 0) return null;
        nint bmp = 0;
        try
        {
            // GetImage(size, SIIGBF_THUMBNAILONLY): fails when all the file has is its icon
            int hr = ((delegate* unmanaged[Stdcall]<nint, SIZE, int, nint*, int>)(*(void***)factory)[3])(factory, new SIZE { W = size, H = size }, thumbOnly ? 0x8 : 0x0, &bmp);
            if (hr != 0 || bmp == 0) return null;
            BITMAP info;
            if (GetObjectW(bmp, sizeof(BITMAP), &info) == 0 || info.Width <= 0 || info.Height <= 0) return null;
            int w = info.Width, h = info.Height;
            var px = new byte[w * h * 4];
            var head = new BITMAPINFOHEADER { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
            nint dc = GetDC(0);
            int rows;
            fixed (byte* p = px) rows = GetDIBits(dc, bmp, 0, (uint)h, p, &head, 0);
            ReleaseDC(0, dc);
            if (rows != h) return null;
            // (a picture with no see-through parts often comes with its alpha left at zero: it is solid)
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return new Images.Pic { W = w, H = h, Px = px };
        }
        finally
        {
            if (bmp != 0) DeleteObject(bmp);
            Marshal.Release(factory);
        }
    }

    /// <summary>The icon Windows gives that kind of file, at its largest (256 px when the program that owns it has one).</summary>
    static Images.Pic? TypeIcon(string name)
    {
        bool folder = name == "folder";
        SHFILEINFOW info;
        if (SHGetFileInfoW(folder ? "folder" : name, folder ? 0x10u : 0x80u, &info, sizeof(SHFILEINFOW), 0x4000 | 0x10 /* SYSICONINDEX | USEFILEATTRIBUTES */) == 0) return null;
        var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");   // IImageList
        nint list;
        if (SHGetImageList(4 /* SHIL_JUMBO */, &iid, &list) != 0 || list == 0) return null;
        nint icon = 0;
        try
        {
            // IImageList::GetIcon(index, ILD_TRANSPARENT, &icon)
            if (((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)(*(void***)list)[10])(list, info.Icon, 1, &icon) != 0 || icon == 0) return null;
            const int S = 256;
            var head = new BITMAPINFOHEADER { Size = 40, Width = S, Height = -S, Planes = 1, BitCount = 32 };
            void* bits;
            nint screen = GetDC(0), dc = CreateCompatibleDC(screen), dib = CreateDIBSection(dc, &head, 0, &bits, 0, 0);
            ReleaseDC(0, screen);
            if (dib == 0) { DeleteDC(dc); return null; }
            nint old = SelectObject(dc, dib);
            new Span<byte>(bits, S * S * 4).Clear();
            DrawIconEx(dc, 0, 0, icon, S, S, 0, 0, 3 /* DI_NORMAL */);
            var px = new Span<byte>(bits, S * S * 4).ToArray();
            SelectObject(dc, old); DeleteObject(dib); DeleteDC(dc);

            // what was drawn: an icon with no large size sits small in the corner of the big square
            int right = -1, bottom = -1; bool anyAlpha = false;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    int i = (y * S + x) * 4;
                    if (px[i + 3] != 0) anyAlpha = true;
                    if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0 || px[i + 3] != 0) { if (x > right) right = x; if (y > bottom) bottom = y; }
                }
            if (right < 0) return null;
            if (!anyAlpha) for (int i = 0; i < px.Length; i += 4) if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) px[i + 3] = 255;
            int side = Math.Max(right, bottom) + 1;
            if (side > 96) return new Images.Pic { W = S, H = S, Px = px };
            side = side <= 32 ? 32 : side <= 48 ? 48 : side <= 64 ? 64 : 96;
            var small = new byte[side * side * 4];
            for (int y = 0; y < side; y++) Buffer.BlockCopy(px, y * S * 4, small, y * side * 4, side * 4);
            return new Images.Pic { W = side, H = side, Px = small };
        }
        finally
        {
            if (icon != 0) DestroyIcon(icon);
            Marshal.Release(list);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFOW { public nint Handle; public int Icon; public uint Attributes; public fixed char DisplayName[260]; public fixed char TypeName[80]; }
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern nint SHGetFileInfoW(string path, uint attributes, SHFILEINFOW* info, int size, uint flags);
    [DllImport("shell32")] static extern int SHGetImageList(int which, Guid* iid, nint* list);
    [DllImport("user32")] static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [DllImport("user32")] static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] static extern bool DeleteDC(nint dc);

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public ushort Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, nint bindCtx, Guid* iid, nint* item);
    [DllImport("gdi32")] static extern int GetObjectW(nint obj, int size, void* info);
    [DllImport("gdi32")] static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, void* bits, BITMAPINFOHEADER* info, uint usage);
    [DllImport("gdi32")] static extern bool DeleteObject(nint obj);
    [DllImport("user32")] static extern nint GetDC(nint hwnd);
    [DllImport("user32")] static extern int ReleaseDC(nint hwnd, nint dc);
}
