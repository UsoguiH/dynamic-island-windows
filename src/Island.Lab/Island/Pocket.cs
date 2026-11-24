using System.Numerics;
using System.Text.Json.Nodes;
using Island.Lab.Motion;
using Island.Lab.Platform;

namespace Island.Lab.Island;

public enum PocketKind { File, Doc, Code, Image, Folder, Link, Text }

/// <summary>One thing Bloub holds: where it is (never a copy), what it is, and how its row moves.</summary>
public sealed class PocketItem
{
    static int _keys;
    public readonly int Key = ++_keys;
    public string Path = "";      // a file or folder; for a link its address; for text the words themselves
    public string Name = "";
    public PocketKind Kind;
    public long Size;
    public DateTime At = DateTime.Now;
    /// <summary>"1920 × 1080", "12 items": found in the background after it lands.</summary>
    public volatile string? Detail;
    public volatile bool Missing;
    /// <summary>Not a real file (the preview shortcut, the showcase): nothing is opened, copied or saved for it.</summary>
    public bool Demo;

    /// <summary>Its place in the list and its presence (0 = gone), so rows glide when others come and go.</summary>
    public readonly Spring Y = new(0, TabBar.Calm, 0.05f), Show = new(0, TabBar.Calm, 0.004f);
    public bool Placed, Leaving;
    /// <summary>A link: what its page says about itself (found in the background).</summary>
    public volatile string? Title, IconUrl, ImageUrl;
    public volatile bool Asked;
    /// <summary>What to call it: a link's page title when it is known, otherwise its name.</summary>
    public string Display => Kind == PocketKind.Link && Title is { Length: > 0 } t ? t : Name;
    /// <summary>The Windows icon of this kind of thing ("x.pdf", "folder"), for <see cref="Render.Images.Kind"/>.</summary>
    public string KindName => Kind switch { PocketKind.Folder => "folder", PocketKind.Link => "x.url", _ => "x" + System.IO.Path.GetExtension(Name).ToLowerInvariant() };

    public bool OnDisk => Kind is not (PocketKind.Link or PocketKind.Text);
    public string Ext => System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();

    /// <summary>"PDF · 2.1 MB", "Folder · 12 items", "github.com".</summary>
    public string Meta
    {
        get
        {
            string size = Size >= 1e9 ? $"{Size / 1e9:0.0} GB" : Size >= 1e6 ? $"{Size / 1e6:0.0} MB" : Size >= 1e3 ? $"{Size / 1e3:0} KB" : $"{Size} B";
            return Kind switch
            {
                PocketKind.Folder => Detail is { } d ? "Folder · " + d : "Folder",
                PocketKind.Link => Uri.TryCreate(Path, UriKind.Absolute, out var u) ? u.Host : "Link",
                PocketKind.Text => $"{Path.Length} characters",
                PocketKind.Image => (Detail is { } d ? d + " · " : Ext.Length > 0 ? Ext + " · " : "") + size,
                _ => (Ext.Length > 0 ? Ext + " · " : "") + size,
            };
        }
    }

    /// <summary>Its tile: an icon (Segoe MDL2) and a colour.</summary>
    public (char Icon, uint Color) Look => Kind switch
    {
        PocketKind.Folder => ('\uE8B7', 0xFF9F0A),
        PocketKind.Link => ('\uE71B', 0x30D158),
        PocketKind.Text => ('\uE8D2', 0xFFD60A),
        PocketKind.Image => ('\uE91B', 0xFF375F),
        PocketKind.Code => ('\uE943', 0x64D2FF),
        PocketKind.Doc => ('\uE8A5', 0xFF453A),
        _ => Ext switch
        {
            "MP4" or "MOV" or "MKV" or "WEBM" or "AVI" => ('\uE714', 0xBF5AF2),
            "MP3" or "WAV" or "FLAC" or "OGG" or "M4A" => ('\uE8D6', 0xFF9F0A),
            "ZIP" or "7Z" or "RAR" or "GZ" or "TAR" => ('\uE7B8', 0xFFD60A),
            "EXE" or "MSI" or "MSIX" => ('\uE74C', 0x0A84FF),
            _ => ('\uE8A5', 0x8E8E93),
        },
    };

    public static PocketKind KindOf(string path)
    {
        if (Directory.Exists(path)) return PocketKind.Folder;
        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => PocketKind.Image,
            ".pdf" or ".docx" or ".ipynb" => PocketKind.Doc,
            ".cs" or ".js" or ".jsx" or ".ts" or ".tsx" or ".py" or ".rs" or ".go" or ".java" or ".kt" or ".swift" or ".c" or ".cc" or ".cpp" or ".h" or ".hpp"
                or ".json" or ".md" or ".txt" or ".yml" or ".yaml" or ".toml" or ".xml" or ".html" or ".htm" or ".css" or ".scss" or ".sh" or ".ps1" or ".bat"
                or ".sql" or ".csproj" or ".sln" or ".log" or ".csv" or ".ini" or ".cfg" or ".env" or ".vue" or ".svelte" or ".rb" or ".php" or ".lua" or ".svg" => PocketKind.Code,
            _ => PocketKind.File,
        };
    }
}

/// <summary>One thing you can do with what he holds.</summary>
public readonly record struct PocketAction(string Id, string Label, char Icon, uint Color, bool Main = false, bool Claude = false);

/// <summary>
/// The Pocket: what you dropped on Bloub. He keeps where each file is (never a copy) until you take it back out,
/// hand it to Claude, or tell him to forget it. Saved in %APPDATA%\Island\pocket.json.
/// </summary>
public sealed class Pocket
{
    public const int Max = 40;
    public readonly List<PocketItem> Items = new();
    public int Count => Items.Count(i => !i.Leaving);

    /// <summary>The count beside Bloub in the pill pops when it changes.</summary>
    public readonly Spring Badge = new(0, SpringSpec.Bounce, 0.002f), BadgePop = new(1, SpringSpec.Bounce, 0.0005f);
    public readonly Spring Scroll = new(0, new SpringSpec(0.3f, 1f), 0.1f);
    public float ScrollMax;

    /// <summary>The one shown big beside the list (its key; the newest when nothing was picked), and when it was picked.</summary>
    public int Selected;
    public double SelectedAt = -100;
    /// <summary>Where the list's highlight is (it glides from row to row), and how far the big preview has risen under the cursor.</summary>
    public readonly Spring SelY = new(0, TabBar.Calm, 0.05f), PreHover = new(0, new SpringSpec(0.22f, 0.9f), 0.002f);
    /// <summary>The Claude button has been pressed: it is apart, as the three things Claude can do with the file.</summary>
    public bool AskOpen;
    /// <summary>How far apart they are (0: one button, 1: three). Opening overshoots and settles, like a drop letting go.</summary>
    public readonly Spring AskT = new(0, new SpringSpec(0.5f, 0.62f), 0.002f);
    double _askSeen;
    public void ToggleAsk(IslandModel m) { AskOpen = !AskOpen; _askSeen = m.Clock; }
    readonly System.Collections.Concurrent.ConcurrentQueue<Action<IslandModel>> _done = new();
    /// <summary>The link whose QR code is on show (0: none), and since when: scan it and the link opens on your phone.</summary>
    public int QrKey;
    public double QrAt;
    int _selWas, _hoverKey;
    double _hoverSince;
    bool _wasLooking;
    public PocketItem? Current => Items.FirstOrDefault(i => i.Key == Selected && !i.Leaving) ?? Items.FirstOrDefault(i => !i.Leaving);

    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "pocket.json");

    public Pocket()
    {
        Load();
        Badge.Snap(Count > 0 ? 1 : 0);
    }

    // ================================================================ taking things in

    /// <summary>Turns what was dropped into items. Real ones go into the Pocket (newest first); <paramref name="keep"/> false only describes them.</summary>
    public List<PocketItem> Take(DropPayload p, bool keep = true)
    {
        var got = new List<PocketItem>();
        foreach (var path in p.Files)
        {
            string trimmed = path.TrimEnd('\\', '/');
            bool real = File.Exists(path) || Directory.Exists(path);
            var kind = real ? PocketItem.KindOf(path) : PocketItem.KindOf(path) is PocketKind.Folder ? PocketKind.File : PocketItem.KindOf(path);
            var it = new PocketItem { Path = path, Name = Path.GetFileName(trimmed) is { Length: > 0 } n ? n : path, Kind = kind, Demo = !real };
            if (real && kind != PocketKind.Folder) try { it.Size = new FileInfo(path).Length; } catch { }
            if (!real) it.Size = 48_300_000;
            got.Add(it);
        }
        if (p.Link is { } link)
        {
            string name = Uri.TryCreate(link, UriKind.Absolute, out var u) ? u.Host + (u.AbsolutePath.Length > 1 ? u.AbsolutePath : "") : link;
            got.Add(new PocketItem { Path = link, Name = name, Kind = PocketKind.Link });
        }
        if (p.Text is { } text)
        {
            string first = text.Split('\n')[0].Trim();
            got.Add(new PocketItem { Path = text, Name = first.Length > 0 ? first : "Text", Kind = PocketKind.Text });
        }
        if (!keep) return got;
        foreach (var it in got)
        {
            if (it.Demo || it.Kind == PocketKind.Text) continue;   // (text goes to your Notes instead)
            Items.RemoveAll(o => string.Equals(o.Path, it.Path, StringComparison.OrdinalIgnoreCase));
            Items.Insert(0, it);
            Selected = it.Key;
            Describe(it);
            AskLink(it);
        }
        while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
        if (got.Any(i => !i.Demo && i.Kind != PocketKind.Text)) { BadgePop.Snap(1.5f); BadgePop.To(1); Save(); }
        return got;
    }

    /// <summary>The details that need the disk (a picture's size, how much is in a folder), found off the drawing thread.</summary>
    static void Describe(PocketItem it)
    {
        if (!it.OnDisk || it.Demo) return;
        Task.Run(() =>
        {
            try
            {
                if (it.Kind == PocketKind.Folder)
                {
                    int n = Directory.EnumerateFileSystemEntries(it.Path).Take(1000).Count();
                    it.Detail = n >= 1000 ? "1000+ items" : n == 1 ? "1 item" : $"{n} items";
                }
                else if (it.Kind == PocketKind.Image && Render.Images.SizeOf(it.Path) is { } s) it.Detail = $"{s.W} × {s.H}";
                it.Missing = !(File.Exists(it.Path) || Directory.Exists(it.Path));
            }
            catch { }
        });
    }

    /// <summary>A link: its page is asked once for its title, icon and picture (the page itself, nobody else).</summary>
    void AskLink(PocketItem it)
    {
        if (it.Kind != PocketKind.Link || it.Asked) return;
        it.Asked = true;
        Task.Run(async () =>
        {
            if (await PocketOps.Link(it.Path) is not { } info) return;
            it.Title = info.Title; it.IconUrl = info.Icon; it.ImageUrl = info.Image;
            _done.Enqueue(_ => Save());
        });
    }

    public void Forget(IEnumerable<PocketItem> items)
    {
        foreach (var it in items) if (Items.Contains(it)) { it.Leaving = true; it.Show.To(0); }
        Save();
    }

    public void Clear() => Forget(Items.ToList());

    public void Wheel(float delta) => Scroll.To(Math.Clamp(Scroll.Target - delta * 0.6f, 0, ScrollMax));

    // ================================================================ per frame

    double _checkedAt = -100;

    public void Update(IslandModel m, float dt)
    {
        Badge.To(Count > 0 ? 1 : 0);
        Badge.Step(dt); BadgePop.Step(dt);
        Scroll.To(Math.Clamp(Scroll.Target, 0, ScrollMax)); Scroll.Step(dt);
        while (_done.TryDequeue(out var done)) done(m);
        foreach (var it in Items) { it.Y.Step(dt); it.Show.Step(dt); }
        Items.RemoveAll(i => i.Leaving && i.Show.Value < 0.02f);
        // while you look at the Pocket: files that were moved or deleted since are marked (every few seconds, off this thread)
        bool looking = m.Mode == Mode.Full && m.Tabs.Selected == "pocket";
        StepSelection(m, dt, looking);
        if (looking && m.Clock - _checkedAt > 3 && !Showcase.On)
        {
            _checkedAt = m.Clock;
            var list = Items.Where(i => i.OnDisk && !i.Demo).ToList();
            Task.Run(() => { foreach (var i in list) try { i.Missing = !(File.Exists(i.Path) || Directory.Exists(i.Path)); } catch { } });
        }
        StepDrag(m);
    }

    /// <summary>Resting on a row for a moment shows that file big on the right (it stays when the cursor goes over there).</summary>
    void StepSelection(IslandModel m, float dt, bool looking)
    {
        string? hit = looking ? m.HoverHit : null;
        int over = hit != null && hit.StartsWith("pk:row:") && int.TryParse(hit[7..], out int k) ? k : 0;
        if (over != _hoverKey) { _hoverKey = over; _hoverSince = m.Clock; }
        if (over != 0 && over != Selected && m.Clock - _hoverSince > 0.09f && !m.ButtonHeld) Selected = over;
        var cur = Current;
        if (cur != null && cur.Key != Selected) Selected = cur.Key;
        bool fresh = looking && !_wasLooking;
        _wasLooking = looking;
        if (Selected != _selWas || fresh) { _selWas = Selected; SelectedAt = m.Clock; QrKey = 0; AskOpen = false; }
        // the three things Claude can do come back together when they are left alone
        if (AskOpen) { if (hit != null && hit.StartsWith("pk:do:")) _askSeen = m.Clock; else if (m.Clock - _askSeen > 3.5f) AskOpen = false; }
        AskT.To(AskOpen ? 1 : 0, AskOpen ? new SpringSpec(0.62f, 0.6f) : new SpringSpec(0.36f, 0.9f));
        AskT.Step(dt);
        if (cur != null)
        {
            int slot = 0;
            foreach (var i in Items) { if (i == cur) break; if (!i.Leaving) slot++; }
            if (fresh) SelY.Snap(slot * Scenes.PocketPitch); else SelY.To(slot * Scenes.PocketPitch);
        }
        PreHover.To(cur != null && hit == "pk:pre:" + cur.Key ? 1 : 0);
        SelY.Step(dt); PreHover.Step(dt);
    }

    // ================================================================ dragging a row back out

    PocketItem? _pressed;
    Vector2 _pressedAt;
    /// <summary>A row was dragged far enough: the host starts a real drag with this file (see Program).</summary>
    public PocketItem? DragOut;

    public void Press(IslandModel m, int key)
    {
        _pressed = Items.FirstOrDefault(i => i.Key == key && i.OnDisk && !i.Demo && !i.Missing);
        _pressedAt = m.Cursor;
    }

    public void Release() => _pressed = null;

    void StepDrag(IslandModel m)
    {
        if (_pressed == null) return;
        if (!m.ButtonHeld) { _pressed = null; return; }
        if (Vector2.Distance(m.Cursor, _pressedAt) > 7) { DragOut = _pressed; _pressed = null; }
    }

    // ================================================================ what you can do with them

    const uint Coral = 0xD97757, Sky = 0x0A84FF, Mint = 0x63E6BE, Sun = 0xFFD60A, Amber = 0xFF9F0A, Leaf = 0x30D158, Ice = 0x64D2FF;

    /// <summary>
    /// What Claude can do with it: three choices behind one button (the first is always "hand it over and say what
    /// you want"), or a single one where there is nothing more specific to offer.
    /// </summary>
    public static PocketAction[] Claude(IReadOnlyList<PocketItem> items)
    {
        if (items.Count == 0 || !items.All(i => i.OnDisk)) return [];
        PocketAction give = new("ask", "Give to an agent", '\uE99A', Coral, true, true);
        if (items.Count > 1) return [give];
        return items[0].Kind switch
        {
            PocketKind.Folder => [new("agent", "New agent here", '\uE99A', Coral, true, true), new("tour", "Explain project", '\uE82F', Coral, true), new("fix", "Find what to fix", '\uE90F', Coral, true)],
            PocketKind.Doc => [give, new("sum", "Summarize", '\uE8FD', Coral, true), new("explain", "Explain it", '\uE82F', Coral, true)],
            PocketKind.Code => [give, new("explain", "Explain it", '\uE82F', Coral, true), new("review", "Review it", '\uE73E', Coral, true)],
            PocketKind.Image => [give, new("describe", "Describe it", '\uE7B3', Coral, true), new("read", "Read its text", '\uE8D2', Coral, true)],
            _ => [give],
        };
    }

    /// <summary>The everyday things: open it, copy it, copy its path, show it in its folder.</summary>
    public static PocketAction[] Actions(IReadOnlyList<PocketItem> items)
    {
        if (items.Count == 0) return [];
        if (items.Count > 1)
            return items.All(i => i.OnDisk)
                ? [new("file", "Copy files", '\uE7C3', Mint), new("path", "Copy paths", '\uE8C8', Ice), new("show", "Show in folder", '\uE838', Amber)]
                : [new("copy", "Copy", '\uE8C8', Sky, true)];
        return items[0].Kind switch
        {
            PocketKind.Link => [new("open", "Open", '\uE8A7', Sky, true), new("copy", "Copy link", '\uE8C8', Ice), new("qr", "To my phone", '\uE8EA', Leaf)],
            PocketKind.Text => [new("notes", "Open Notes", '\uE70B', Sun, true), new("copy", "Copy", '\uE8C8', Ice)],
            PocketKind.Folder => [new("open", "Open", '\uE8A7', Sky), new("file", "Copy folder", '\uE7C3', Mint), new("path", "Copy path", '\uE8C8', Ice), new("show", "Show in folder", '\uE838', Amber)],
            _ => [new("open", "Open", '\uE8A7', Sky), new("file", "Copy file", '\uE7C3', Mint), new("path", "Copy path", '\uE8C8', Ice), new("show", "Show in folder", '\uE838', Amber)],
        };
    }

    /// <summary>Does it. Returns the short line that confirms it ("Path copied"), or null when the island moved on to another screen.</summary>
    public string? Run(IslandModel m, IReadOnlyList<PocketItem> items, string action)
    {
        if (items.Count == 0) return null;
        var first = items[0];
        if (action == "qr")
        {
            if (QrKey == first.Key) { QrKey = 0; return null; }
            if (Render.Qr.Encode(first.Path) == null) return "This link is too long for a code";
            QrKey = first.Key; QrAt = m.Clock;
            return null;      // (the card itself says what to do with it)
        }
        // what Claude is asked, when the choice says more than "here, take this"
        string? ask = action switch
        {
            "sum" => "Summarize this document: the key points first, then anything I should act on",
            "explain" => first.Kind == PocketKind.Code ? "Explain what this file does, simply, and point out anything that looks wrong"
                                                       : "Explain this document to me simply, as if I had never seen the subject before",
            "review" => "Review this file: bugs first, then anything that would be simpler or safer written another way",
            "describe" => "Describe this image: what it shows, and anything worth noticing",
            "read" => "Read all the text in this image and write it out exactly, keeping its layout",
            "tour" => "Explain this project: what it does, how it is organised, and where to start reading",
            "fix" => "Look through this project and list the most important things to fix, the most serious first",
            _ => null,
        };
        bool claude = ask != null || action is "ask" or "agent";
        if ((Showcase.On || items.Any(i => i.Demo)) && !claude && action != "notes")
            return items.Any(i => i.Demo) && !Showcase.On ? "Just a preview: drop a real file" : "Showcase mode: nothing opens";
        var paths = items.Where(i => i.OnDisk).Select(i => i.Path).ToList();
        switch (action)
        {
            case "agent" or "tour" or "fix":
                m.Team.NewAgentIn(m, first.Path);
                if (ask != null) m.Team.Task.Set(ask);
                return null;
            case "ask":
                // your agents are shown: you pick the one that gets it
                m.Team.GiveTo(m, paths);
                return null;
            case "sum" or "explain" or "review" or "describe" or "read":
                m.Team.NewAgentWith(m, paths);
                if (ask != null) m.Team.Task.Set(ask);
                return null;
            case "open":
                TabBar.Open(first.Path);
                return "Opening";
            case "show":
                TabBar.Reveal(first.Path);
                return "Shown in its folder";
            case "path":
                return Shell.SetClipboard(m.Hwnd, string.Join(Environment.NewLine, items.Select(i => i.Path))) ? items.Count > 1 ? "Paths copied" : "Path copied" : "Couldn't copy";
            case "copy":
                return Shell.SetClipboard(m.Hwnd, string.Join(Environment.NewLine, items.Select(i => i.Path))) ? "Copied" : "Couldn't copy";
            case "file":
                return Shell.CopyFiles(m.Hwnd, paths) ? "Copied: paste it anywhere (Ctrl+V)" : "Couldn't copy";
            case "notes":
                m.Tabs.Select(m, "notes");
                m.SetMode(Mode.Full);
                return null;
        }
        return null;
    }

    /// <summary>The folder Claude should work in for a file: its repository, else a project you used that contains it, else its own folder.</summary>
    public static string ProjectFor(string path, IEnumerable<string> recent)
    {
        string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        try
        {
            var d = new DirectoryInfo(dir);
            for (int i = 0; i < 10 && d != null; i++, d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        }
        catch { }
        var hit = recent.Where(r => dir.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(dir, r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(r => r.Length).FirstOrDefault();
        return hit ?? dir;
    }

    // ================================================================ saved list

    void Load()
    {
        if (Showcase.On)
        {
            var now = DateTime.Now;
            (string Name, PocketKind Kind, long Size, string? Detail, double Minutes)[] demo =
            [
                ("hero-render-4k.png", PocketKind.Image, 9_800_000, "3840 × 2160", 1), ("aurora-web", PocketKind.Folder, 0, "214 items", 9),
                ("SettingsPage.tsx", PocketKind.Code, 6_400, null, 26), ("invoice-september.pdf", PocketKind.Doc, 212_000, null, 130),
                ("linear.app/aurora/issue/AUR-412", PocketKind.Link, 0, null, 300), ("release-v2.3.0.zip", PocketKind.File, 18_200_000, null, 1500),
            ];
            foreach (var d in demo)
                Items.Add(new PocketItem { Name = d.Name, Kind = d.Kind, Size = d.Size, Detail = d.Detail, At = now.AddMinutes(-d.Minutes), Demo = true,
                                           Path = d.Kind == PocketKind.Link ? "https://" + d.Name : @"C:\Users\you\Projects\" + d.Name });
            foreach (var it in Items) AskLink(it);
            return;
        }
        try
        {
            if (!File.Exists(File_)) return;
            var root = JsonNode.Parse(File.ReadAllText(File_));
            if (root is JsonObject ro) root = ro["items"];
            if (root is not JsonArray a) return;
            foreach (var o in a.OfType<JsonObject>())
            {
                if (o["path"]?.GetValue<string>() is not { Length: > 0 } path) continue;
                var kind = Enum.TryParse<PocketKind>(o["kind"]?.GetValue<string>(), out var k) ? k : PocketKind.File;
                var it = new PocketItem
                {
                    Path = path, Name = o["name"]?.GetValue<string>() ?? Path.GetFileName(path), Kind = kind, Size = o["size"]?.GetValue<long>() ?? 0,
                    At = DateTime.TryParse(o["at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.Now,
                    Title = o["title"]?.GetValue<string>(), IconUrl = o["icon"]?.GetValue<string>(), ImageUrl = o["image"]?.GetValue<string>(),
                };
                it.Asked = it.IconUrl != null;
                Items.Add(it);
                Describe(it);
                AskLink(it);
                if (Items.Count >= Max) break;
            }
        }
        catch (Exception ex) { Diag.Log("pocket load: " + ex.Message); }
    }

    public void Save()
    {
        if (Showcase.On) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            var a = new JsonArray(Items.Where(i => !i.Leaving && !i.Demo).Select(i => (JsonNode)new JsonObject
            {
                ["path"] = i.Path, ["name"] = i.Name, ["kind"] = i.Kind.ToString(), ["size"] = i.Size, ["at"] = i.At.ToString("o"),
                ["title"] = i.Title, ["icon"] = i.IconUrl, ["image"] = i.ImageUrl,
            }).ToArray());
            var all = new JsonObject { ["items"] = a };
            Disk.Write(File_, all.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Diag.Log("pocket save: " + ex.Message); }
    }
}
