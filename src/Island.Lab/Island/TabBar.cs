using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Island.Lab.Motion;
using Island.Lab.Platform;

namespace Island.Lab.Island;

/// <summary>One kind of dashboard tab.</summary>
public sealed record TabDef(string Id, string Name, char Icon, uint Color, string Blurb);

public sealed record NoteItem(string Text, DateTime At);
public sealed record TaskItem(string Text, bool Done);
public sealed record FileItem(string Path, string Name, long Size, DateTime At, bool Partial);

/// <summary>
/// The dashboard's tabs: which ones you picked and in what order (saved to %APPDATA%\Island\settings.json),
/// the springs that slide them around, the "customize" mode, drag-to-reorder, and the small amount of
/// state the newer tabs keep (tasks, notes, timers, network samples, downloads, projects).
/// </summary>
public sealed class TabBar
{
    public static readonly TabDef[] Catalogue =
    [
        new("overview",  "Overview",  '', 0x5E5CE6, "Everything at a glance"),
        new("agents",    "Agents",    '', 0x0A84FF, "Your Claude sessions"),
        new("usage",     "Usage",     '', 0x30D158, "Tokens, limits, streaks"),
        new("projects",  "Projects",  '', 0xFF9F0A, "Recent projects"),
        new("media",     "Music",     '', 0xFF375F, "Now playing, up next"),
        new("focus",     "Focus",     '', 0x30D158, "Deep-work sessions"),
        new("tasks",     "Tasks",     '', 0x64D2FF, "Today's to-do list"),
        new("calendar",  "Calendar",  '', 0xFF453A, "This month"),
        new("timer",     "Timer",     '', 0xFF9F0A, "Stopwatch, countdown"),
        new("notes",     "Notes",     '', 0xFFD60A, "Quick notes, kept"),
        new("clipboard", "Clipboard", '', 0xBF5AF2, "Recent copies"),
        new("downloads", "Downloads", '', 0x0A84FF, "Latest downloads"),
        new("clocks",    "Clocks",    '', 0x64D2FF, "World time"),
        new("network",   "Network",   '', 0x30D158, "Live up/down speed"),
        new("system",    "System",    '', 0x8E8E93, "CPU, GPU, memory"),
        new("battery",   "Battery",   '', 0x66D4CF, "Charge, time left, power"),
        new("servers",   "Servers",   '', 0xFF6482, "Your localhost dev servers"),
    ];
    public static TabDef Def(string id) => Catalogue.FirstOrDefault(d => d.Id == id) ?? Catalogue[0];
    public const int MaxTabs = 10;

    public static readonly (string Name, string[] Tabs)[] Presets =
    [
        ("Minimal", ["overview", "agents"]),
        ("Essentials", ["overview", "agents", "usage"]),
        ("Coder", ["agents", "usage", "projects", "timer", "notes"]),
        ("Everything", ["overview", "agents", "usage", "projects", "media", "tasks", "calendar", "timer", "notes", "system"]),
    ];
    static readonly string[] Defaults = ["overview", "agents", "usage", "clipboard", "system"];

    /// <summary>Your tabs, in order.</summary>
    public readonly List<string> Enabled = new(Defaults);
    public string Selected = "overview";
    /// <summary>Customize mode: the panel becomes the tab library and tabs can be dragged.</summary>
    public bool Editing;
    /// <summary>Never opened the customizer yet: the pencil gently pulses.</summary>
    public bool Discovered;
    public readonly Spring EditT = new(0, SpringSpec.Content, 0.002f);

    /// <summary>Per-tab springs: position and width in the header, presence (0 = gone), and the library tile's pop.</summary>
    public sealed class Slot
    {
        public readonly Spring X = new(0, new SpringSpec(0.42f, 0.78f), 0.05f);
        public readonly Spring W = new(0, new SpringSpec(0.42f, 0.82f), 0.05f);
        public readonly Spring A = new(0, SpringSpec.Content, 0.002f);
        public readonly Spring Pop = new(1, SpringSpec.Bounce, 0.0005f);
        public readonly Spring On = new(0, SpringSpec.Bounce, 0.002f);
        public readonly Spring Shake = new(0, new SpringSpec(0.22f, 0.25f), 0.05f);
        /// <summary>The library tile's colour fill, spreading out from where you clicked (0 = none, 1 = whole tile).</summary>
        public readonly Spring Fill = new(0, new SpringSpec(0.5f, 1f), 0.002f);
        public System.Numerics.Vector2? FillAt;
        public bool Placed;
        public void Step(float dt) { X.Step(dt); W.Step(dt); A.Step(dt); Pop.Step(dt); On.Step(dt); Shake.Step(dt); Fill.Step(dt); }
    }
    readonly Dictionary<string, Slot> _slots = Catalogue.ToDictionary(d => d.Id, _ => new Slot());
    public Slot this[string id] => _slots[id];

    // ---- drag to reorder (customize mode)
    public string? Dragging;
    float _dragGrab, _dragStartX;
    bool _dragMoved;
    public bool SwallowClick;

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");
    static readonly string File_ = Path.Combine(Dir, "settings.json");

    public TabBar()
    {
        Load();
        foreach (var id in Enabled) { _slots[id].A.Snap(1); _slots[id].On.Snap(1); _slots[id].Fill.Snap(1); }
        if (!Enabled.Contains(Selected)) Selected = Enabled[0];
    }

    // ================================================================ the library's Bloub

    /// <summary>The line under Bloub in the library ("Pinned Usage · 4 of 10").</summary>
    public string Status = "Tap a tile to pin it";
    public char StatusIcon = '';
    public bool StatusWarn;
    public readonly Spring StatusT = new(1, SpringSpec.Content, 0.002f);
    /// <summary>Bloub's hop in the library (an offset in DIPs, negative = up).</summary>
    public readonly Spring LibHop = new(0, new SpringSpec(0.42f, 0.5f), 0.05f);
    /// <summary>The glow behind Bloub: blue, then the colour of the tab you last pinned.</summary>
    public uint GlowColor = 0x2F6BFF;
    /// <summary>The two moons: their orbit angle and how fast they go (a boost decays back to 1).</summary>
    public float MoonAngle;
    public readonly Spring MoonSpin = new(1, new SpringSpec(1.4f, 1f), 0.01f);
    /// <summary>When the last shrug started (the moons lift like shoulders for ~0.95 s).</summary>
    public double ShrugAt = -10;
    public float ShrugAngle;
    double _shrugHop1 = -1, _shrugHop2 = -1;

    void Say(string text, char icon, bool warn = false)
    {
        Status = text; StatusIcon = icon; StatusWarn = warn;
        StatusT.Snap(0); StatusT.To(1);
    }

    void Boost(float p) { MoonSpin.Snap(1 + 3.2f * p); MoonSpin.To(1); }

    /// <summary>Removing a tab: "oh well". Eyes roll up and away while the moons lift like shoulders, twice.</summary>
    void Shrug(IslandModel m)
    {
        ShrugAt = m.Clock;
        ShrugAngle = MoonAngle;
        _shrugHop1 = m.Clock + 0.13; _shrugHop2 = m.Clock + 0.46;
        m.Bloub.React(Mascot.BloubState.Idle, m.Clock, 0.95, Mascot.BloubExpressionId.Bof);
    }

    // ================================================================ choosing tabs

    public void Select(IslandModel m, string id)
    {
        if (id == Selected) return;
        Selected = id;
        m.TabFade.Snap(0); m.TabFade.To(1);
        m.Bloub.React(Mascot.BloubState.Swirl, m.Clock, 0.9);
        if (id is "downloads") RefreshDownloads();
        if (id is "projects") RefreshProjects();
        if (id is "servers") { _serversAt = m.Clock; RefreshServers(); }
        if (id is "battery") _powerAt = -100;
    }

    public void SetEditing(IslandModel m, bool on)
    {
        if (on == Editing) return;
        Editing = on;
        Dragging = null;
        EditT.To(on ? 1 : 0);
        m.TabFade.Snap(0); m.TabFade.To(1);
        if (on && !Discovered) { Discovered = true; Save(); }
        if (on) { Say("Tap a tile to pin it", ''); GlowColor = 0x2F6BFF; Boost(0.6f); ShrugAt = -10; }
        m.FocusField(null);
        m.Bloub.React(on ? Mascot.BloubState.Wide : Mascot.BloubState.Wink, m.Clock, 1.0,
            on ? Mascot.BloubExpressionId.Attentif : Mascot.BloubExpressionId.Heureux);
    }

    /// <summary>Adds or removes a tab from the library.</summary>
    public void Toggle(IslandModel m, string id)
    {
        var s = _slots[id];
        var def = Def(id);
        if (Enabled.Contains(id))
        {
            if (Enabled.Count == 1)
            {
                s.Shake.Velocity = 900; Say("Keep at least one tab", '', true);
                m.Bloub.React(Mascot.BloubState.Wide, m.Clock, 0.7, Mascot.BloubExpressionId.Surpris);
                return;
            }
            Enabled.Remove(id);
            s.A.To(0); s.On.To(0); s.Pop.Snap(0.9f); s.Pop.To(1);
            s.Fill.To(0, new SpringSpec(0.26f, 1f));
            if (Selected == id) Selected = Enabled[0];
            Say($"Unpinned {def.Name}", '');
            Shrug(m);
        }
        else
        {
            if (Enabled.Count >= MaxTabs)
            {
                s.Shake.Velocity = 900; Say("Your bar is full", '', true);
                m.Bloub.React(Mascot.BloubState.Wide, m.Clock, 0.7, Mascot.BloubExpressionId.Surpris);
                return;
            }
            Enabled.Add(id);
            s.Placed = false;
            s.A.Snap(0); s.A.To(1); s.On.To(1); s.Pop.Snap(1.12f); s.Pop.To(1);
            // the colour spreads out from the click
            s.FillAt = m.Cursor; s.Fill.Snap(0); s.Fill.To(1, new SpringSpec(0.5f, 1f));
            Say($"Pinned {def.Name} · {Enabled.Count} of {MaxTabs}", '');
            // a wink and a little hop, the moons speed up, the glow takes the tab's colour
            m.Bloub.React(Mascot.BloubState.Wink, m.Clock, 0.95, Mascot.BloubExpressionId.Heureux);
            LibHop.Velocity = -170;
            Boost(0.6f);
            GlowColor = def.Color;
            ShrugAt = -10; _shrugHop1 = _shrugHop2 = -1;
        }
        Save();
    }

    public void ApplyPreset(IslandModel m, int i)
    {
        var want = Presets[i].Tabs;
        foreach (var id in Enabled.Except(want).ToList()) { Enabled.Remove(id); _slots[id].A.To(0); _slots[id].On.To(0); }
        Enabled.Clear();
        foreach (var id in want)
        {
            var s = _slots[id];
            if (s.A.Target < 0.5f) { s.Placed = false; s.A.Snap(0); s.FillAt = null; s.Fill.Snap(0); }
            s.A.To(1); s.On.To(1); s.Fill.To(1); s.Pop.Snap(1.1f); s.Pop.To(1);
            Enabled.Add(id);
        }
        foreach (var d in Catalogue) if (!want.Contains(d.Id)) _slots[d.Id].Fill.To(0);
        if (!Enabled.Contains(Selected)) Selected = Enabled[0];
        m.ShowToast($"{Presets[i].Name} — {Enabled.Count} tabs");
        m.Bloub.React(Mascot.BloubState.Burst, m.Clock, 1.6, Mascot.BloubExpressionId.Excite);
        Save();
    }

    // ---- dragging a tab in the header

    public void PressTab(string id, float cursorX)
    {
        if (!Editing) return;
        Dragging = id;
        _dragGrab = cursorX - _slots[id].X.Value;
        _dragStartX = cursorX;
        _dragMoved = false;
        _slots[id].Pop.To(1.08f);
    }

    public void Release()
    {
        if (Dragging is { } id) { _slots[id].Pop.To(1); if (_dragMoved) { SwallowClick = true; Save(); } }
        Dragging = null;
    }

    /// <summary>Moves the dragged tab with the cursor and re-orders the others around it (x relative to the island's left).</summary>
    public void Drag(float cursorX, Func<string, float> center)
    {
        if (Dragging is not { } id) return;
        if ((GetAsyncKeyState(0x01) & 0x8000) == 0) { Release(); return; } // released outside the island
        if (MathF.Abs(cursorX - _dragStartX) > 4) _dragMoved = true;
        var s = _slots[id];
        s.X.Snap(cursorX - _dragGrab);
        float mid = s.X.Value + s.W.Value / 2;
        int want = Enabled.Count(o => o != id && center(o) < mid);
        int cur = Enabled.IndexOf(id);
        if (want != cur) { Enabled.RemoveAt(cur); Enabled.Insert(want, id); }
    }

    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);

    // ================================================================ tasks, notes, timers

    public readonly List<TaskItem> Tasks = new();
    public readonly List<NoteItem> Notes = new();
    public readonly TextField TaskField = new("Add a task…");
    public readonly TextField NoteField = new("Write a note — Enter saves it");

    public bool Submit(IslandModel m, TextField f)
    {
        var text = f.Text.Trim();
        if (f == TaskField)
        {
            if (text.Length == 0) return true;
            Tasks.Insert(Tasks.TakeWhile(t => !t.Done).Count(), new TaskItem(text, false));
            f.Clear(); Save();
            m.Bloub.React(Mascot.BloubState.Comet, m.Clock, 1.4);
            return true;
        }
        if (f == NoteField)
        {
            if (text.Length == 0) return true;
            Notes.Insert(0, new NoteItem(text, DateTime.Now));
            if (Notes.Count > 40) Notes.RemoveAt(Notes.Count - 1);
            f.Clear(); Save();
            m.ShowToast("Note saved");
            m.Bloub.React(Mascot.BloubState.Wink, m.Clock, 1.2, Mascot.BloubExpressionId.Heureux);
            return true;
        }
        return false;
    }

    public void ToggleTask(IslandModel m, int i)
    {
        if (i < 0 || i >= Tasks.Count) return;
        var t = Tasks[i] with { Done = !Tasks[i].Done };
        Tasks.RemoveAt(i);
        // done tasks sink below the open ones
        Tasks.Insert(t.Done ? Tasks.Count : Tasks.TakeWhile(x => !x.Done).Count(), t);
        if (t.Done) { m.ShowToast("Nice — task done"); m.Bloub.React(Mascot.BloubState.Burst, m.Clock, 2.4, Mascot.BloubExpressionId.Excite); }
        Save();
    }

    public void ClearDone() { Tasks.RemoveAll(t => t.Done); Save(); }
    public void DeleteNote(int i) { if (i >= 0 && i < Notes.Count) { Notes.RemoveAt(i); Save(); } }

    // stopwatch
    public double SwElapsed;
    public bool SwRunning;
    public readonly List<double> Laps = new();
    // countdown
    public double CdLength = 5 * 60, CdLeft = 5 * 60;
    public bool CdRunning;
    public readonly Spring CdFlash = new(0, SpringSpec.Content, 0.002f);

    // calendar
    public int MonthOffset;

    // ================================================================ network, downloads, projects

    public readonly float[] Down = new float[60], Up = new float[60]; // bytes/s, oldest first
    public long NetDownTotal, NetUpTotal;
    public string NetName = "", NetKind = "", NetIp = "";
    long _lastRx = -1, _lastTx = -1;
    double _netAt;

    public List<FileItem> Downloads = new();
    public List<string> Projects = new();
    double _filesAt = -100, _projectsAt = -100;

    public void Update(IslandModel m, float dt)
    {
        EditT.Step(dt);
        foreach (var s in _slots.Values) s.Step(dt);
        if (!Editing && Dragging != null) Dragging = null;

        if (SwRunning) SwElapsed += dt;
        if (CdRunning)
        {
            CdLeft -= dt;
            if (CdLeft <= 0)
            {
                CdLeft = 0; CdRunning = false;
                CdFlash.Snap(1); CdFlash.To(0);
                string len = CdLength >= 60 ? $"{CdLength / 60:0} min" : $"{CdLength:0} s";
                if (m.Mode == Mode.Full) m.ShowToast($"Time's up — {len}");
                else m.ShowInfo("Time's up", $"Your {len} timer finished");
                m.Bloub.React(Mascot.BloubState.Burst, m.Clock, 2.6, Mascot.BloubExpressionId.Excite);
            }
        }
        CdFlash.Step(dt);

        // the library's Bloub: status line, hops (a shrug lifts him twice), the moons' orbit
        StatusT.Step(dt); LibHop.Step(dt); MoonSpin.Step(dt);
        if (_shrugHop1 > 0 && m.Clock >= _shrugHop1) { _shrugHop1 = -1; LibHop.Velocity = -95; m.Bloub.Squish.Snap(1.05f); m.Bloub.Squish.To(1, SpringSpec.Bounce); }
        if (_shrugHop2 > 0 && m.Clock >= _shrugHop2) { _shrugHop2 = -1; LibHop.Velocity = -60; }
        MoonAngle += dt * 0.7f * MoonSpin.Value;

        if (m.Clock - _powerAt >= (Selected == "battery" ? 2 : 15)) { _powerAt = m.Clock; SamplePower(m); }
        if (m.Mode == Mode.Full && Selected == "servers" && !Editing && m.Clock - _serversAt > 2.5) { _serversAt = m.Clock; RefreshServers(); }

        if (m.Clock - _netAt >= 1) { SampleNetwork(m.Clock - _netAt); _netAt = m.Clock; }
        if (m.Mode == Mode.Full && Selected == "downloads" && m.Clock - _filesAt > 4) { _filesAt = m.Clock; RefreshDownloads(); }
        if (m.Mode == Mode.Full && Selected == "projects" && m.Clock - _projectsAt > 20) { _projectsAt = m.Clock; RefreshProjects(); }
    }

    void SampleNetwork(double seconds)
    {
        long rx = 0, tx = 0;
        if (Showcase.On)
        {
            double t = Environment.TickCount64 / 1000.0;
            rx = (long)(t * 2_600_000 + Math.Abs(Math.Sin(t * 0.7)) * 9_000_000);
            tx = (long)(t * 420_000 + Math.Abs(Math.Sin(t * 1.3)) * 600_000);
            NetName = "Wi-Fi"; NetKind = "Wireless · 5 GHz"; NetIp = "192.168.1.24";
        }
        else
        {
            try
            {
                NetworkInterface? best = null;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived; tx += st.BytesSent;
                    if (best == null || st.BytesReceived > best.GetIPStatistics().BytesReceived) best = ni;
                }
                if (best != null)
                {
                    NetName = best.Name;
                    NetKind = best.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wireless",
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                        _ => best.NetworkInterfaceType.ToString(),
                    } + (best.Speed > 0 ? $" · {best.Speed / 1_000_000} Mbps link" : "");
                    NetIp = best.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                }
            }
            catch (Exception ex) { Diag.Log("network: " + ex.Message); return; }
        }
        if (_lastRx >= 0 && seconds > 0)
        {
            long dr = Math.Max(0, rx - _lastRx), dtx = Math.Max(0, tx - _lastTx);
            Array.Copy(Down, 1, Down, 0, Down.Length - 1); Down[^1] = (float)(dr / seconds);
            Array.Copy(Up, 1, Up, 0, Up.Length - 1); Up[^1] = (float)(dtx / seconds);
            NetDownTotal += dr; NetUpTotal += dtx;
        }
        _lastRx = rx; _lastTx = tx;
    }

    public void RefreshDownloads()
    {
        if (Showcase.On)
        {
            var now = DateTime.Now;
            Downloads =
            [
                new("", "node-v24.9.0-x64.msi.crdownload", 31_400_000, now.AddSeconds(-4), true),
                new("", "aurora-web-v2.3.0.zip", 18_200_000, now.AddMinutes(-3), false),
                new("", "design-tokens.fig", 4_700_000, now.AddMinutes(-26), false),
                new("", "invoice-september.pdf", 212_000, now.AddHours(-2), false),
                new("", "hero-render-4k.png", 9_800_000, now.AddHours(-5), false),
                new("", "dotfiles-backup.tar.gz", 1_300_000, now.AddDays(-1), false),
            ];
            return;
        }
        Task.Run(() =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (!Directory.Exists(dir)) return;
                Downloads = new DirectoryInfo(dir).EnumerateFiles()
                    .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime).Take(6)
                    .Select(f => new FileItem(f.FullName, f.Name, f.Length, f.LastWriteTime,
                        f.Extension is ".crdownload" or ".part" or ".partial" or ".download" or ".tmp"))
                    .ToList();
            }
            catch (Exception ex) { Diag.Log("downloads: " + ex.Message); }
        });
    }

    public void RefreshProjects()
    {
        if (Showcase.On) { Projects = Showcase.RecentProjects(); return; }
        Task.Run(() => Projects = Shell.RecentProjects(6));
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Diag.Log("open: " + ex.Message); }
    }

    public static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Diag.Log("reveal: " + ex.Message); }
    }

    // ================================================================ battery & power

    public sealed record PowerInfo(bool HasBattery, bool PluggedIn, bool Charging, float Percent, int SecondsLeft, bool Saver, string Mode);
    public PowerInfo Power = new(false, true, false, 1, -1, false, "Balanced");
    /// <summary>Charge over this session, one sample every 20 s (oldest first, NaN = no sample yet).</summary>
    public readonly float[] ChargeLog = Enumerable.Repeat(float.NaN, 90).ToArray();
    double _powerAt = -100, _chargeLogAt = -100;
    bool? _wasPlugged;

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("powrprof")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

    static string PowerMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return "Balanced";
            return g.ToString().ToLowerInvariant() switch
            {
                "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best power efficiency",
                "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => "Better battery",
                "ded574b5-45a0-4f42-8737-46345c09c238" => "Best performance",
                _ => "Balanced",
            };
        }
        catch { return "Balanced"; } // older Windows: no power-mode overlay
    }

    void SamplePower(IslandModel m)
    {
        if (Showcase.On)
        {
            // a laptop charging slowly, for the demo
            float pct = Math.Min(1, 0.62f + (float)(m.Clock / 3600));
            Power = new(true, true, pct < 1, pct, -1, false, "Balanced");
        }
        else if (GetSystemPowerStatus(out var s))
        {
            bool has = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
            Power = new(has, s.ACLineStatus == 1, (s.BatteryFlag & 8) != 0,
                s.BatteryLifePercent <= 100 ? s.BatteryLifePercent / 100f : 1, s.BatteryLifeTime, (s.SystemStatusFlag & 1) != 0, PowerMode());
        }
        if (Power.HasBattery && m.Clock - _chargeLogAt >= 20)
        {
            _chargeLogAt = m.Clock;
            Array.Copy(ChargeLog, 1, ChargeLog, 0, ChargeLog.Length - 1);
            ChargeLog[^1] = Power.Percent;
        }
        // plugging in / unplugging a laptop gets a small banner
        if (Power.HasBattery && _wasPlugged is { } was && was != Power.PluggedIn)
        {
            if (Power.PluggedIn) { m.ShowInfo("Charging", $"{Power.Percent * 100:0}% · plugged in"); m.Bloub.React(Mascot.BloubState.Comet, m.Clock, 1.6); }
            else m.ShowInfo("On battery", $"{Power.Percent * 100:0}%" + (Power.SecondsLeft > 0 ? $" · about {Duration(Power.SecondsLeft)} left" : ""));
        }
        _wasPlugged = Power.PluggedIn;
    }

    public static string Duration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60:00}m" : $"{Math.Max(1, seconds / 60)} min";

    // ================================================================ dev servers (what's listening on localhost)

    public sealed record ServerItem(int Port, int Pid, string Process, string Kind, DateTime? Started, bool Web);
    public List<ServerItem> Servers = new();
    public bool ServersLoaded;
    double _serversAt = -100;
    /// <summary>Stop needs a second click: (pid, until when).</summary>
    public (int Pid, double Until) ConfirmStop = (0, 0);

    static readonly HashSet<string> DevProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "bun", "deno", "python", "python3", "pythonw", "uvicorn", "gunicorn", "dotnet", "java", "javaw", "ruby", "php", "php-cgi",
        "go", "air", "hugo", "nginx", "httpd", "caddy", "docker", "com.docker.backend", "wslrelay", "postgres", "mysqld", "redis-server",
        "mongod", "esbuild", "vite", "next-server", "rails", "flask", "jupyter", "jupyter-lab", "streamlit", "ollama", "LM Studio", "cargo",
    };

    static readonly Dictionary<int, string> KnownPorts = new()
    {
        [3000] = "Dev server", [3001] = "Dev server", [4000] = "Dev server", [4200] = "Angular", [4321] = "Astro", [5000] = "Flask / dev server",
        [5173] = "Vite", [5174] = "Vite", [4173] = "Vite preview", [6006] = "Storybook", [8000] = "Django / uvicorn", [8080] = "HTTP server",
        [8081] = "Metro", [8888] = "Jupyter", [19000] = "Expo", [1313] = "Hugo", [9229] = "Node inspector", [11434] = "Ollama",
        [5432] = "PostgreSQL", [3306] = "MySQL", [6379] = "Redis", [27017] = "MongoDB", [1234] = "LM Studio",
    };

    static readonly HashSet<int> NotWeb = [5432, 3306, 6379, 27017, 9229];

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>(port, pid) of every listening TCP socket, IPv4 and IPv6.</summary>
    static IEnumerable<(int port, int pid)> Listeners()
    {
        foreach (var (af, rowSize, portOff, pidOff) in new[] { (2, 24, 8, 20), (23, 56, 20, 52) })
        {
            int size = 0;
            GetExtendedTcpTable(0, ref size, false, af, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (size <= 0) continue;
            nint buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, 3, 0) != 0) continue;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    nint row = buf + 4 + i * rowSize;
                    int raw = Marshal.ReadInt32(row + portOff);
                    yield return (((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF), Marshal.ReadInt32(row + pidOff));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    public void RefreshServers()
    {
        if (Showcase.On)
        {
            var now = DateTime.Now;
            Servers =
            [
                new(5173, 0, "node", "Vite", now.AddMinutes(-42), true),
                new(3000, 0, "node", "Next.js", now.AddMinutes(-7), true),
                new(8000, 0, "python", "uvicorn", now.AddHours(-2), true),
                new(5432, 0, "postgres", "PostgreSQL", now.AddDays(-1), false),
                new(6379, 0, "redis-server", "Redis", now.AddDays(-1), false),
            ];
            ServersLoaded = true;
            return;
        }
        Task.Run(() =>
        {
            try
            {
                var names = new Dictionary<int, (string name, DateTime? at)>();
                var list = new List<ServerItem>();
                foreach (var (port, pid) in Listeners().Distinct())
                {
                    if (pid <= 4) continue;
                    if (!names.TryGetValue(pid, out var info))
                    {
                        try { using var p = System.Diagnostics.Process.GetProcessById(pid); DateTime? at = null; try { at = p.StartTime; } catch { } info = (p.ProcessName, at); }
                        catch { info = ("", null); }
                        names[pid] = info;
                    }
                    bool known = KnownPorts.TryGetValue(port, out var kind);
                    if (info.name.Length == 0 || !(DevProcesses.Contains(info.name) || known && port >= 1024)) continue;
                    if (list.Any(s => s.Port == port)) continue;
                    list.Add(new(port, pid, info.name, kind ?? info.name, info.at, !NotWeb.Contains(port)));
                }
                Servers = list.OrderBy(s => s.Port).Take(8).ToList();
            }
            catch (Exception ex) { Diag.Log("servers: " + ex.Message); }
            ServersLoaded = true;
        });
    }

    public void StopServer(IslandModel m, int i)
    {
        if (i < 0 || i >= Servers.Count) return;
        var s = Servers[i];
        if (Showcase.On) { m.ShowToast("Showcase mode: nothing stops"); return; }
        if (ConfirmStop.Pid != s.Pid || m.Clock > ConfirmStop.Until) { ConfirmStop = (s.Pid, m.Clock + 3); return; }
        ConfirmStop = (0, 0);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(s.Pid);
            p.Kill();
            m.ShowToast($"Stopped {s.Process} on :{s.Port}");
            m.Bloub.React(Mascot.BloubState.Comet, m.Clock, 1.4);
        }
        catch (Exception ex) { m.ShowToast("Couldn't stop it"); Diag.Log("stop server: " + ex.Message); }
        _serversAt = -100;
    }

    // ================================================================ settings file

    void Load()
    {
        if (Showcase.On)
        {
            // The demo never reads your files: a believable set of tasks and notes instead.
            Tasks.AddRange([new("Review the dark-mode PR", false), new("Reply to design feedback", false),
                            new("Ship notes-cli 2.1", false), new("Fix flaky auth tests", true)]);
            Notes.AddRange([new("Idea: island reacts to CI status — green burst on pass, red wobble on fail", DateTime.Now.AddMinutes(-12)),
                            new("API key rotation is on the 14th", DateTime.Now.AddHours(-3)),
                            new("Try 0.42 response for the tab pill spring", DateTime.Now.AddDays(-1))]);
            return;
        }
        try
        {
            if (!File.Exists(File_)) return;
            var o = JsonNode.Parse(File.ReadAllText(File_)) as JsonObject;
            if (o == null) return;
            if (o["tabs"] is JsonArray tabs)
            {
                var list = tabs.Select(t => t?.GetValue<string>()).Where(t => t != null && Catalogue.Any(d => d.Id == t)).Distinct().Take(MaxTabs).ToList();
                if (list.Count > 0) { Enabled.Clear(); Enabled.AddRange(list!); }
            }
            Discovered = o["discovered"]?.GetValue<bool>() ?? false;
            if (o["tasks"] is JsonArray ta)
                foreach (var t in ta.OfType<JsonObject>())
                    if (t["text"]?.GetValue<string>() is { } s) Tasks.Add(new TaskItem(s, t["done"]?.GetValue<bool>() ?? false));
            if (o["notes"] is JsonArray na)
                foreach (var n in na.OfType<JsonObject>())
                    if (n["text"]?.GetValue<string>() is { } s)
                        Notes.Add(new NoteItem(s, DateTime.TryParse(n["at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.Now));
        }
        catch (Exception ex) { Diag.Log("settings load: " + ex.Message); }
    }

    public void Save()
    {
        if (Showcase.On) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var o = new JsonObject
            {
                ["tabs"] = new JsonArray(Enabled.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
                ["discovered"] = Discovered,
                ["tasks"] = new JsonArray(Tasks.Select(t => (JsonNode)new JsonObject { ["text"] = t.Text, ["done"] = t.Done }).ToArray()),
                ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)new JsonObject { ["text"] = n.Text, ["at"] = n.At.ToString("o") }).ToArray()),
            };
            File.WriteAllText(File_, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Diag.Log("settings save: " + ex.Message); }
    }
}
