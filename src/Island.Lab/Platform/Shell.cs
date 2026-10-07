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
                    if (p != null && Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
                }
                catch { }
            }
        }
        catch (Exception ex) { Diag.Log("recent projects: " + ex.Message); }
        return list;
    }

    [DllImport("user32")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32")] static extern bool CloseClipboard();
    [DllImport("user32")] static extern bool EmptyClipboard();
    [DllImport("user32")] static extern nint SetClipboardData(uint format, nint h);
    [DllImport("user32")] static extern nint GetClipboardData(uint format);
    [DllImport("kernel32")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32")] static extern bool GlobalUnlock(nint h);
}
