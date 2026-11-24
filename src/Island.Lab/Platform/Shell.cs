using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Island.Lab.Platform;

/// <summary>Small desktop actions: clipboard, opening folders/files in Explorer or your code editor, terminals.</summary>
public static class Shell
{
    // ---------------------------------------------------------------- clipboard (CF_UNICODETEXT)

    public static bool SetClipboard(nint owner, string text)
    {
        if (!OpenClipboard(owner)) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, (nuint)bytes);
            var p = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            GlobalUnlock(h);
            return SetClipboardData(13, h) != 0;
        }
        finally { CloseClipboard(); }
    }

    public static string? GetClipboard(nint owner)
    {
        if (!OpenClipboard(owner)) return null;
        try
        {
            var h = GetClipboardData(13);
            if (h == 0) return null;
            var p = GlobalLock(h);
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// Copies a picture file: its pixels (pastes into chats, editors, documents) and the file itself (pastes into a
    /// folder). False when it can't be read as a picture.
    /// </summary>
    public static unsafe bool CopyImage(nint owner, string path)
    {
        nint dib = 0, drop = 0;
        try
        {
            using var wic = new Vortice.WIC.IWICImagingFactory();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var decoder = wic.CreateDecoderFromStream(fs, Vortice.WIC.DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            int w = frame.Size.Width, h = frame.Size.Height;
            if (w <= 0 || h <= 0 || (long)w * h > 60_000_000) return false;
            using var conv = wic.CreateFormatConverter();
            conv.Initialize(frame, Vortice.WIC.PixelFormat.Format24bppBGR);
            int stride = (w * 3 + 3) & ~3, bytes = stride * h;
            var px = new byte[bytes];
            fixed (byte* p = px) conv.CopyPixels(new Vortice.Mathematics.RectI(0, 0, w, h), (uint)stride, (uint)bytes, (nint)p);
            // a device-independent bitmap: a 40-byte header, then the rows from the bottom one up
            dib = GlobalAlloc(0x0042, (nuint)(40 + bytes));
            var d = (byte*)GlobalLock(dib);
            var head = (int*)d;
            head[0] = 40; head[1] = w; head[2] = h; ((short*)d)[6] = 1; ((short*)d)[7] = 24; head[4] = 0; head[5] = bytes;
            fixed (byte* p = px)
                for (int y = 0; y < h; y++) Buffer.MemoryCopy(p + (long)(h - 1 - y) * stride, d + 40 + (long)y * stride, stride, stride);
            GlobalUnlock(dib);
            // and the file: a DROPFILES header (20 bytes, wide names), the path, two zeros
            int chars = path.Length + 2;
            drop = GlobalAlloc(0x0042, (nuint)(20 + chars * 2));
            var f = (byte*)GlobalLock(drop);
            ((int*)f)[0] = 20; ((int*)f)[4] = 1;
            fixed (char* s = path) Buffer.MemoryCopy(s, f + 20, path.Length * 2, path.Length * 2);
            GlobalUnlock(drop);
            if (!OpenClipboard(owner)) return false;
            try
            {
                EmptyClipboard();
                bool ok = SetClipboardData(8 /* CF_DIB */, dib) != 0;
                if (ok) dib = 0;   // (the clipboard owns it now)
                if (SetClipboardData(15 /* CF_HDROP */, drop) != 0) drop = 0;
                return ok;
            }
            finally { CloseClipboard(); }
        }
        catch (Exception ex) { Diag.Log("copy image: " + ex.Message); return false; }
        finally
        {
            if (dib != 0) GlobalFree(dib);
            if (drop != 0) GlobalFree(drop);
        }
    }

    // ---------------------------------------------------------------- folders, files, editor

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Start("explorer.exe", path);
    }

    /// <summary>VS Code or a VS Code-style editor (Cursor, Antigravity…), if installed.</summary>
    public static string? Editor
    {
        get
        {
            if (_editorProbed) return _editor;
            _editorProbed = true;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] candidates =
            [
                Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(pf, "Microsoft VS Code", "Code.exe"),
                Path.Combine(local, "Programs", "cursor", "Cursor.exe"),
                Path.Combine(local, "Programs", "Antigravity", "Antigravity.exe"),
                Path.Combine(local, "Programs", "Windsurf", "Windsurf.exe"),
            ];
            _editor = candidates.FirstOrDefault(File.Exists);
            return _editor;
        }
    }
    static string? _editor;
    static bool _editorProbed;

    public static string EditorName => Editor is { } e ? Path.GetFileNameWithoutExtension(e) switch { "Code" => "VS Code", var n => n } : "Explorer";

    public static void OpenInEditor(string folder)
    {
        if (Editor is { } e) Start(e, $"\"{folder}\"");
        else OpenFolder(folder);
    }

    /// <summary>Opens a file in the editor (never "runs" it); without an editor, shows it in Explorer.</summary>
    public static void OpenFile(string file, string? folder = null)
    {
        if (Editor is { } e) Start(e, folder != null ? $"\"{folder}\" -g \"{file}\"" : $"-g \"{file}\"");
        else Start("explorer.exe", $"/select,\"{file}\"");
    }

    /// <summary>Opens a new PowerShell window that continues a Claude session interactively.</summary>
    public static void ResumeInTerminal(string cwd, string sessionId)
    {
        var cmd = $"Set-Location -LiteralPath '{cwd.Replace("'", "''")}'; claude --resume {sessionId}";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = cwd };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(cmd);
        try { Process.Start(psi); } catch (Exception ex) { Diag.Log("resume terminal: " + ex.Message); }
    }

    static void Start(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = false }); }
        catch (Exception ex) { Diag.Log($"start {exe}: {ex.Message}"); }
    }

    /// <summary>When each recent project was last used (filled by <see cref="RecentProjects"/>).</summary>
    public static readonly Dictionary<string, DateTime> ProjectUsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recent project folders you used Claude Code in (from ~/.claude/history.jsonl), newest first.</summary>
    public static List<string> RecentProjects(int max = 8)
    {
        var list = new List<string>();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "history.jsonl");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 512 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n');
            for (int i = lines.Length - 1; i >= 0 && list.Count < max; i--)
            {
                int k = lines[i].IndexOf("\"project\":\"", StringComparison.Ordinal);
                if (k < 0) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(lines[i]);
                    var p = doc.RootElement.GetProperty("project").GetString();
                    if (p != null && Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(p);
                        if (doc.RootElement.TryGetProperty("timestamp", out var ts) && ts.ValueKind == System.Text.Json.JsonValueKind.Number && ts.TryGetInt64(out long ms))
                            ProjectUsed[p] = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Diag.Log("recent projects: " + ex.Message); }
        return list;
    }

    // ---------------------------------------------------------------- things to do with a file

    /// <summary>Puts the files themselves on the clipboard: Ctrl+V in a folder, a chat or an e-mail pastes them.</summary>
    public static unsafe bool CopyFiles(nint owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !OpenClipboard(owner)) return false;
        try
        {
            EmptyClipboard();
            string list = string.Join("\0", paths) + "\0\0";
            var h = GlobalAlloc(0x0042, (nuint)(20 + list.Length * 2));
            byte* p = (byte*)GlobalLock(h);
            *(int*)p = 20;            // DROPFILES: where the names start…
            *(int*)(p + 16) = 1;      // …and that they are wide characters
            fixed (char* src = list) Buffer.MemoryCopy(src, p + 20, list.Length * 2, list.Length * 2);
            GlobalUnlock(h);
            if (SetClipboardData(15 /* CF_HDROP */, h) == 0) return false;
            var e = GlobalAlloc(0x0042, 4);
            *(int*)GlobalLock(e) = 1;  // (a copy, not a move)
            GlobalUnlock(e);
            SetClipboardData(RegisterClipboardFormatW("Preferred DropEffect"), e);
            return true;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);

    [DllImport("user32")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32")] static extern bool CloseClipboard();
    [DllImport("user32")] static extern bool EmptyClipboard();
    [DllImport("user32")] static extern nint SetClipboardData(uint format, nint h);
    [DllImport("user32")] static extern nint GetClipboardData(uint format);
    [DllImport("kernel32")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32")] static extern nint GlobalFree(nint h);
    [DllImport("kernel32")] static extern bool GlobalUnlock(nint h);
}
