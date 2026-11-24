using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Island.Lab.Platform;

/// <summary>What is being dragged over the island (or was just dropped on it): files and folders, a link, or text.</summary>
public sealed record DropPayload(string[] Files, string? Link, string? Text)
{
    public bool Empty => Files.Length == 0 && Link == null && Text == null;
}

/// <summary>
/// The island as a real drop target (OLE drag and drop), so it knows when something enters, hovers, leaves and
/// lands, and what it is, before the mouse button is released. WM_DROPFILES only ever said "a file landed".
/// It also starts drags the other way: a file the Pocket holds, dragged out to any window.
/// </summary>
public static unsafe class DragDrop
{
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        [PreserveSig] int DragEnter([In, MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, long point, ref uint effect);
        [PreserveSig] int DragOver(uint keys, long point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([In, MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, long point, ref uint effect);
    }

    const uint None = 0, Copy = 1, Link = 4;

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    /// <summary>The island's side of a drag out of the Pocket: Esc or the other button cancels, letting go drops.</summary>
    sealed class Source(bool dry, Action? tick) : IDropSource
    {
        public int Calls, Feedbacks;

        public int QueryContinueDrag(bool escape, uint keys)
        {
            Calls++;
            int answer = escape || (keys & 2) != 0 ? 0x00040101 /* DRAGDROP_S_CANCEL */
                       : (keys & 1) == 0 ? 0x00040100 /* DRAGDROP_S_DROP: the button was let go */ : 0;
            if (dry && (Calls <= 2 || answer != 0)) Diag.Log($"drag out (dry): asked #{Calls} escape={escape} keys={keys} -> {(answer == 0 ? "go on" : answer == 0x00040100 ? "drop" : "cancel")}");
            return answer;
        }

        // (Windows asks this after every mouse or key message of the drag, and runs nothing else of ours meanwhile:
        //  it is where the island ticks until the file is let go)
        public int GiveFeedback(uint effect)
        {
            Feedbacks++;
            try { tick?.Invoke(); } catch (Exception ex) { Diag.Log("drag out tick: " + ex.Message); }
            return 0x00040102;     // DRAGDROP_S_USEDEFAULTCURSORS
        }
    }

    sealed class Target(Func<DropPayload, bool> enter, Action leave, Action<DropPayload> drop) : IDropTarget
    {
        bool _accepted;

        static uint Effect(uint allowed) => (allowed & Copy) != 0 ? Copy : (allowed & Link) != 0 ? Link : None;

        public int DragEnter(IDataObject data, uint keys, long point, ref uint effect)
        {
            _accepted = false;
            try
            {
                var p = Read(data);
                _accepted = !Sending && !p.Empty && enter(p);
            }
            catch (Exception ex) { Diag.Log("drag enter: " + ex.Message); }
            effect = _accepted ? Effect(effect) : None;
            return 0;
        }

        public int DragOver(uint keys, long point, ref uint effect)
        {
            effect = _accepted ? Effect(effect) : None;
            return 0;
        }

        public int DragLeave()
        {
            if (_accepted) try { leave(); } catch (Exception ex) { Diag.Log("drag leave: " + ex.Message); }
            _accepted = false;
            return 0;
        }

        public int Drop(IDataObject data, uint keys, long point, ref uint effect)
        {
            bool ok = _accepted;
            _accepted = false;
            if (!ok) { effect = None; return 0; }
            try
            {
                var p = Read(data);
                if (p.Empty) leave(); else drop(p);
            }
            catch (Exception ex) { Diag.Log("drop: " + ex.Message); try { leave(); } catch { } }
            effect = Effect(effect);
            return 0;
        }
    }

    static Target? _target;   // (kept alive: OLE only holds a COM reference)

    /// <summary>Makes <paramref name="hwnd"/> a drop target. <paramref name="enter"/> answers whether the island takes it.</summary>
    public static void Register(nint hwnd, Func<DropPayload, bool> enter, Action leave, Action<DropPayload> drop)
    {
        int hr = OleInitialize(0);
        _target = new Target(enter, leave, drop);
        int reg = RegisterDragDrop(hwnd, _target);
        Diag.Log($"drop target: ole={hr:X} register={reg:X}");
    }

    public static void Unregister(nint hwnd) { try { RevokeDragDrop(hwnd); } catch { } }

    // ---- reading what is dragged

    static readonly short CfUrl = (short)RegisterClipboardFormatW("UniformResourceLocatorW");

    static DropPayload Read(IDataObject data)
    {
        var files = Files(data);
        if (files.Length > 0) return new DropPayload(files, null, null);
        string? url = Str(data, CfUrl), text = Str(data, 13 /* CF_UNICODETEXT */);
        if (url == null && text != null && text.Length < 2100 && !text.Contains('\n') && LooksLikeLink(text.Trim())) url = text.Trim();
        if (url != null && LooksLikeLink(url)) return new DropPayload([], url, null);
        text = text?.Trim();
        return new DropPayload([], null, string.IsNullOrEmpty(text) ? null : text);
    }

    static bool LooksLikeLink(string s) =>
        (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        && Uri.TryCreate(s, UriKind.Absolute, out _) && !s.Contains(' ');

    static bool Get(IDataObject data, short format, out STGMEDIUM medium)
    {
        var f = new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        medium = default;
        try
        {
            if (data.QueryGetData(ref f) != 0) return false;
            data.GetData(ref f, out medium);
            return medium.unionmember != 0;
        }
        catch { return false; }
    }

    static string[] Files(IDataObject data)
    {
        if (!Get(data, 15 /* CF_HDROP */, out var m)) return [];
        try
        {
            uint n = DragQueryFileW(m.unionmember, 0xFFFFFFFF, null, 0);
            var list = new List<string>();
            char* buf = stackalloc char[1024];
            for (uint i = 0; i < n && list.Count < 40; i++)
            {
                uint len = DragQueryFileW(m.unionmember, i, buf, 1024);
                if (len > 0) list.Add(new string(buf, 0, (int)len));
            }
            return list.ToArray();
        }
        finally { ReleaseStgMedium(ref m); }
    }

    static string? Str(IDataObject data, short format)
    {
        if (format == 0 || !Get(data, format, out var m)) return null;
        try
        {
            var p = GlobalLock(m.unionmember);
            if (p == 0) return null;
            try
            {
                // (never past the block: a source can hand over text without its final zero)
                int max = (int)Math.Min((long)GlobalSize(m.unionmember) / 2, 200_000);
                var s = new ReadOnlySpan<char>((void*)p, max);
                int end = s.IndexOf('\0');
                return new string(end >= 0 ? s[..end] : s);
            }
            finally { GlobalUnlock(m.unionmember); }
        }
        finally { ReleaseStgMedium(ref m); }
    }

    // ---- dragging a file out of the island

    /// <summary>The island itself is dragging something out right now (it never catches its own drag).</summary>
    public static bool Sending { get; private set; }

    static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
        BhidDataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4"), IidDataObject = new("0000010e-0000-0000-c000-000000000046");

    /// <summary>
    /// Drags the file (or folder) at <paramref name="path"/> out of the island, as Explorer would: this returns when it
    /// has been dropped somewhere or the drag was cancelled. Windows runs its own loop meanwhile; the island draws
    /// from <paramref name="tick"/> (see Program). True when it was dropped on something.
    /// Dry (the dev test): what is dragged is empty and nothing is allowed to be dropped, so letting go lands nothing.
    /// </summary>
    public static bool Send(nint hwnd, string path, bool dry = false, Action? tick = null)
    {
        nint item = 0, obj = 0, src = 0;
        var source = new Source(dry, tick);
        try
        {
            var iid = IidShellItem;
            var bhid = BhidDataObject; var want = IidDataObject;
            if (dry)
            {
                if (SHCreateDataObject(0, 0, 0, 0, &want, &obj) != 0 || obj == 0) return false;
            }
            else
            {
                if (SHCreateItemFromParsingName(path, 0, &iid, &item) != 0 || item == 0) return false;
                // IShellItem::BindToHandler is the first method after IUnknown's three
                var bind = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(void***)item)[3];
                if (bind(item, 0, &bhid, &want, &obj) != 0 || obj == 0) return false;
            }
            Sending = true;
            uint done = 0;
            // (dry, for the dev test: no effect is allowed, so whatever is under the cursor is offered it and can take nothing)
            src = Marshal.GetComInterfaceForObject<Source, IDropSource>(source);
            if (dry) Diag.Log("drag out (dry): starting");
            int hr = SHDoDragDrop(hwnd, obj, src, dry ? None : Copy | Link, &done);
            if (dry) Diag.Log($"drag out (dry): hr={hr:X} effect={done} asked {source.Calls} times, feedback {source.Feedbacks} times");
            return hr == 0x00040100 /* DRAGDROP_S_DROP */ && done != None;
        }
        catch (Exception ex) { Diag.Log("drag out: " + ex.Message); return false; }
        finally
        {
            Sending = false;
            if (src != 0) Marshal.Release(src);
            if (obj != 0) Marshal.Release(obj);
            if (item != 0) Marshal.Release(item);
        }
    }

    static nint FileData(string path)
    {
        nint item = 0, obj = 0;
        var iid = IidShellItem;
        if (SHCreateItemFromParsingName(path, 0, &iid, &item) != 0 || item == 0) return 0;
        var bhid = BhidDataObject; var want = IidDataObject;
        var bind = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(void***)item)[3];
        bind(item, 0, &bhid, &want, &obj);
        Marshal.Release(item);
        return obj;
    }

    static nint _testData;

    /// <summary>
    /// Dev (ISLAND_DEMO=oledrop:path): calls the registered drop target through its COM interface with Explorer's
    /// own data object for <paramref name="path"/>, as a real drag would. Step 0 enters, 1 leaves, 2 drops.
    /// </summary>
    public static void Test(string path, int step)
    {
        if (_target == null) return;
        nint unk = Marshal.GetComInterfaceForObject<Target, IDropTarget>(_target);
        try
        {
            var vt = *(void***)unk;
            uint effect = Copy | Link | 2;
            if (step == 0)
            {
                if (_testData != 0) Marshal.Release(_testData);
                _testData = FileData(path);
                int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, long, uint*, int>)vt[3])(unk, _testData, 1, 0, &effect);
                Diag.Log($"oledrop enter: data={_testData != 0} hr={hr:X} effect={effect}");
            }
            else if (step == 1) Diag.Log($"oledrop leave: hr={((delegate* unmanaged[Stdcall]<nint, int>)vt[5])(unk):X}");
            else
            {
                int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, long, uint*, int>)vt[6])(unk, _testData, 0, 0, &effect);
                Diag.Log($"oledrop drop: hr={hr:X} effect={effect}");
            }
        }
        finally { Marshal.Release(unk); }
    }

    [DllImport("ole32")] static extern int OleInitialize(nint reserved);
    [DllImport("ole32")] static extern int RegisterDragDrop(nint hwnd, IDropTarget target);
    [DllImport("ole32")] static extern int RevokeDragDrop(nint hwnd);
    [DllImport("ole32")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(nint drop, uint index, char* buf, uint max);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, nint bindCtx, Guid* iid, nint* item);
    [DllImport("shell32")] static extern int SHCreateDataObject(nint folder, uint count, nint items, nint inner, Guid* iid, nint* obj);
    [DllImport("shell32")] static extern int SHDoDragDrop(nint hwnd, nint dataObject, nint dropSource, uint effects, uint* effect);
    [DllImport("kernel32")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32")] static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32")] static extern nuint GlobalSize(nint h);
}
