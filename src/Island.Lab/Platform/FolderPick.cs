using System.Runtime.InteropServices;

namespace Island.Lab.Platform;

/// <summary>
/// Windows' own "Select Folder" window (the New agent form's "Other folder…"). It runs on a thread of its own, so
/// the island keeps moving while it is open; the answer is picked up with <see cref="TryTake"/>.
/// </summary>
public static unsafe class FolderPick
{
    static readonly object _lock = new();
    static volatile bool _open;
    static bool _done;
    static string? _result;

    /// <summary>The window is on screen.</summary>
    public static bool Open => _open;

    /// <summary>Opens it over <paramref name="owner"/>, starting in <paramref name="startIn"/> when that folder exists.</summary>
    public static void Ask(nint owner, string? startIn, string title)
    {
        if (_open) return;
        _open = true;
        lock (_lock) { _done = false; _result = null; }
        var t = new Thread(() =>
        {
            string? path = null;
            try { path = Show(owner, startIn, title); }
            catch (Exception ex) { Diag.Log("folder window: " + ex.Message); }
            lock (_lock) { _result = path; _done = true; }
            _open = false;
        }) { IsBackground = true, Name = "folder window" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>True once, when the window has closed: the folder that was chosen, or null when it was cancelled.</summary>
    public static bool TryTake(out string? path)
    {
        lock (_lock)
        {
            path = _result;
            if (!_done) return false;
            _done = false; _result = null;
            return true;
        }
    }

    static string? Show(nint owner, string? startIn, string title)
    {
        var clsid = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");   // FileOpenDialog
        var iid = new Guid("d57c7288-d4ad-4768-be02-9d969532d960");     // IFileOpenDialog
        nint dlg;
        if (CoCreateInstance(&clsid, 0, 1, &iid, &dlg) != 0 || dlg == 0) return null;
        try
        {
            void** vt = *(void***)dlg;
            uint opts;
            ((delegate* unmanaged[Stdcall]<nint, uint*, int>)vt[10])(dlg, &opts);                          // GetOptions
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)vt[9])(dlg, opts | 0x20 | 0x40);               // SetOptions: pick folders, real folders only
            fixed (char* tt = title) ((delegate* unmanaged[Stdcall]<nint, char*, int>)vt[17])(dlg, tt);    // SetTitle
            if (startIn != null && Directory.Exists(startIn))
            {
                var itemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");   // IShellItem
                nint start;
                if (SHCreateItemFromParsingName(startIn, 0, &itemId, &start) == 0 && start != 0)
                {
                    ((delegate* unmanaged[Stdcall]<nint, nint, int>)vt[12])(dlg, start);                  // SetFolder
                    Marshal.Release(start);
                }
            }
            if (((delegate* unmanaged[Stdcall]<nint, nint, int>)vt[3])(dlg, owner) != 0) return null;      // Show: anything but S_OK is "cancelled"
            nint item;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)vt[20])(dlg, &item) != 0 || item == 0) return null;   // GetResult
            try { return PathOf(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(dlg); }
    }

    /// <summary>A shell item's path on disk.</summary>
    static string? PathOf(nint item)
    {
        char* name;
        if (((delegate* unmanaged[Stdcall]<nint, uint, char**, int>)(*(void***)item)[5])(item, 0x80058000, &name) != 0 || name == null) return null;   // GetDisplayName(SIGDN_FILESYSPATH)
        string s = new(name);
        Marshal.FreeCoTaskMem((nint)name);
        return s;
    }

    /// <summary>
    /// Dev (<c>IslandLab --pick-folder &lt;start folder&gt; &lt;result file&gt;</c>): reads a folder's path back through
    /// the shell the way the chosen one is read, then shows the window by itself and says what came of it.
    /// </summary>
    public static string SelfTest(string? startIn)
    {
        string? round = null, picked = null;
        var t = new Thread(() =>
        {
            try
            {
                var itemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                nint item;
                if (startIn != null && SHCreateItemFromParsingName(startIn, 0, &itemId, &item) == 0 && item != 0) { round = PathOf(item); Marshal.Release(item); }
                picked = Show(0, startIn, "Where should the new agent work?");
            }
            catch (Exception ex) { picked = "error: " + ex.Message; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return $"item={round ?? "(none)"}\npicked={picked ?? "(cancelled)"}\n";
    }

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* obj);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, nint bindContext, Guid* iid, nint* item);
}
