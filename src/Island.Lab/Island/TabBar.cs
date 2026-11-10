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
/// <summary>A task, and how its row moves: its slot in the list, arriving and leaving, the tick and the strike line.</summary>
public sealed class TaskItem(string text, bool done)
{
    public string Text = text;
    public bool Done = done;
    /// <summary>Its place in the list (from the top of the list), so rows glide when others come and go.</summary>
    public readonly Spring Y = new(0, TabBar.Calm, 0.05f);
    /// <summary>0 → 1 as it arrives, back to 0 as it leaves.</summary>
    public readonly Spring Show = new(1, TabBar.Calm, 0.004f);
    /// <summary>The green dot pops, then the tick draws itself and the line strikes through.</summary>
    public readonly Spring Dot = new(done ? 1 : 0, SpringSpec.Bounce, 0.004f), Mark = new(done ? 1 : 0, SpringSpec.Quick, 0.004f), Strike = new(done ? 1 : 0, TabBar.Calm, 0.004f);
    public bool Placed, Leaving;
    /// <summary>When it moves to its section (ticked tasks sink below the open ones once the tick has played).</summary>
    public double SinkAt = -1;

    public void Place(float y)
    {
        if (!Placed) { Y.Snap(y); Placed = true; return; }
        Y.To(y);
    }

    public void Step(float dt)
    {
        Y.Step(dt); Show.Step(dt); Dot.Step(dt); Mark.Step(dt); Strike.Step(dt);
    }
}
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
    /// <summary>Every tab you can pin: the built-in ones, then the ones you described (custom recipes).</summary>
    public static TabDef[] All { get; private set; } = Catalogue;
    public static void RefreshAll() =>
        All = [.. Catalogue, .. CustomTabs.All.Select(t => new TabDef(t.Id, t.Recipe.Name, t.Recipe.Icon, t.Recipe.Color, t.Recipe.Title))];
    public static TabDef Def(string id) => All.FirstOrDefault(d => d.Id == id) ?? Catalogue[0];
    public static bool IsCustom(string id) => id.StartsWith("x-");
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
    readonly Dictionary<string, Slot> _slots = new();
    public Slot this[string id] => _slots.TryGetValue(id, out var s) ? s : _slots[id] = new Slot();

    // ---- drag to reorder (customize mode)
    public string? Dragging;
    float _dragGrab, _dragStartX;
    bool _dragMoved;
    public bool SwallowClick;

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");
    static readonly string File_ = Path.Combine(Dir, "settings.json");

    public TabBar()
    {
        CustomTabs.Load();
        RefreshAll();
        Load();
        foreach (var id in Enabled) { this[id].A.Snap(1); this[id].On.Snap(1); this[id].Fill.Snap(1); }
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
        _hinted = false;
    }

    /// <summary>A passing hint under Bloub (hovering his orb or the New tile); null puts back what was there.</summary>
    public void Hint(string? text, char icon)
    {
        if (text != null)
        {
            if (!_hinted) _beforeHint = (Status, StatusIcon);
            Say(text, icon);
            _hinted = true;
        }
        else if (_hinted) Say(_beforeHint.Text, _beforeHint.Icon);
    }
    bool _hinted;
    (string Text, char Icon) _beforeHint;

    /// <summary>The tab whose × was tapped once (a second tap within 3 s deletes it).</summary>
    public string? DeleteArmed;
    public double DeleteArmedAt = -10;

    /// <summary>A tab you described: the first tap on its × asks, the second deletes it (recipe file and all).</summary>
    public void Delete(IslandModel m, string id)
    {
        if (!IsCustom(id) || CustomTabs.Get(id) is not { } tab) return;
        var s = this[id];
        if (DeleteArmed != id || m.Clock - DeleteArmedAt > 3)
        {
            DeleteArmed = id; DeleteArmedAt = m.Clock;
            s.Shake.Velocity = 700;
            Say($"Tap × again to delete {tab.Recipe.Name}", '\uE74D', true);
            m.Bloub.React(Mascot.BloubState.Wide, m.Clock, 0.9, Mascot.BloubExpressionId.Surpris);
            return;
        }
        DeleteArmed = null;
        Enabled.Remove(id);
        if (Enabled.Count == 0) Enabled.Add("overview");
        if (Selected == id) Selected = Enabled[0];
        s.A.To(0); s.On.To(0); s.Fill.To(0);
        CustomTabs.Remove(id);
        RefreshAll();
        Say($"Deleted {tab.Recipe.Name}", '\uE74D');
        Shrug(m);
        Save();
    }

    /// <summary>A tab that is in the bar but not drawn yet (its chip is still flying in).</summary>
    public string? HiddenTab;

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
        if (m.Focused == TaskField || m.Focused == NoteField) m.FocusField(null);
        m.TabFade.Snap(0); m.TabFade.To(1);
        m.Bloub.React(Mascot.BloubState.Swirl, m.Clock, 0.9);
        if (id is "downloads") RefreshDownloads();
        if (id is "projects") RefreshProjects();
        if (id is "servers") { _serversAt = m.Clock; _serversSnap = true; _showcaseStopped.Clear(); ServerScroll.Snap(0); RefreshServers(m); }
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
        var s = this[id];
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
        foreach (var id in Enabled.Except(want).ToList()) { Enabled.Remove(id); this[id].A.To(0); this[id].On.To(0); }
        Enabled.Clear();
        foreach (var id in want)
        {
            var s = this[id];
            if (s.A.Target < 0.5f) { s.Placed = false; s.A.Snap(0); s.FillAt = null; s.Fill.Snap(0); }
            s.A.To(1); s.On.To(1); s.Fill.To(1); s.Pop.Snap(1.1f); s.Pop.To(1);
            Enabled.Add(id);
        }
        foreach (var d in All) if (!want.Contains(d.Id)) this[d.Id].Fill.To(0);
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
        _dragGrab = cursorX - this[id].X.Value;
        _dragStartX = cursorX;
        _dragMoved = false;
        this[id].Pop.To(1.08f);
    }

    public void Release()
    {
        if (Dragging is { } id) { this[id].Pop.To(1); if (_dragMoved) { SwallowClick = true; Save(); } }
        Dragging = null;
    }

    /// <summary>Moves the dragged tab with the cursor and re-orders the others around it (x relative to the island's left).</summary>
    public void Drag(float cursorX, Func<string, float> center)
    {
        if (Dragging is not { } id) return;
        if ((GetAsyncKeyState(0x01) & 0x8000) == 0) { Release(); return; } // released outside the island
        if (MathF.Abs(cursorX - _dragStartX) > 4) _dragMoved = true;
        var s = this[id];
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
    public readonly TextField TaskField = new("What needs doing?");

    /// <summary>A calm spring: about 1% overshoot, no visible bounce (the "+ New task" field, rows gliding).</summary>
    public static readonly SpringSpec Calm = new(0.52f, 0.82f);
    /// <summary>The "+ New task" pill opening into the field.</summary>
    public readonly Spring TaskOpen = new(0, Calm, 0.002f);
    /// <summary>The field's text coming back in after a task is added.</summary>
    public readonly Spring TaskFieldIn = new(1, Calm, 0.004f);
    public readonly Spring TaskScroll = new(0, new SpringSpec(0.3f, 1f), 0.1f), DoneHeader = new(0, Calm, 0.004f), DoneHeaderY = new(0, Calm, 0.05f);
    public float TaskScrollMax;
    bool _doneHeaderPlaced;

    public void PlaceDoneHeader(float y)
    {
        if (!_doneHeaderPlaced) { DoneHeaderY.Snap(y); _doneHeaderPlaced = true; } else DoneHeaderY.To(y);
    }

    /// <summary>The mouse wheel, for the tabs that scroll. False when this tab doesn't.</summary>
    public bool Wheel(float delta)
    {
        if (Selected == "servers") { ServersWheel(delta); return true; }
        if (Selected == "tasks") { TaskScroll.To(Math.Clamp(TaskScroll.Target - delta * 0.6f, 0, TaskScrollMax)); return true; }
        if (IsCustom(Selected) && CustomTabs.Get(Selected) is { ScrollMax: > 0 } ct) { ct.Wheel(delta); return true; }
        return false;
    }

    void StepTasks(IslandModel m, float dt)
    {
        TaskOpen.To(m.Focused == TaskField || TaskField.Text.Length > 0 ? 1 : 0);
        TaskOpen.Step(dt); TaskFieldIn.Step(dt);
        bool changed = false;
        foreach (var t in Tasks)
        {
            t.Step(dt);
            if (t.SinkAt >= 0 && m.Clock >= t.SinkAt) { t.SinkAt = -1; changed = true; }
        }
        // ticked tasks sink below the open ones (and unticked ones rise back), keeping their order otherwise
        if (changed)
        {
            var sorted = Tasks.Where(t => !t.Done).Concat(Tasks.Where(t => t.Done)).ToList();
            Tasks.Clear(); Tasks.AddRange(sorted);
            Save();
        }
        if (Tasks.RemoveAll(t => t.Leaving && t.Show.Value < 0.01f) > 0) Save();
        DoneHeader.To(Tasks.Any(t => t.Done && !t.Leaving) ? 1 : 0);
        DoneHeader.Step(dt); DoneHeaderY.Step(dt);
        TaskScroll.To(Math.Clamp(TaskScroll.Target, 0, TaskScrollMax));
        TaskScroll.Step(dt);
    }
    public readonly TextField NoteField = new("Write a note — Enter saves it");

    public bool Submit(IslandModel m, TextField f)
    {
        var text = f.Text.Trim();
        if (f == TaskField)
        {
            if (text.Length == 0) return true;
            // the list makes room at the top and the new task rises into it; the field stays open for the next one
            var t = new TaskItem(text, false);
            t.Show.Snap(0); t.Show.To(1, 0.06f);
            Tasks.Insert(0, t);
            TaskScroll.To(0);
            f.Clear(); Save();
            TaskFieldIn.Snap(0); TaskFieldIn.To(1);
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
        if (i < 0 || i >= Tasks.Count || Tasks[i].Leaving) return;
        var t = Tasks[i];
        t.Done = !t.Done;
        if (t.Done)
        {
            // the dot pops, the tick draws itself, the line strikes through; then the row sinks to "Done"
            t.Dot.To(1); t.Mark.To(1, 0.14f); t.Strike.To(1, 0.12f);
            t.SinkAt = m.Clock + 0.65;
            m.Bloub.React(Mascot.BloubState.Burst, m.Clock, 2.4, Mascot.BloubExpressionId.Excite);
        }
        else { t.Dot.To(0); t.Mark.To(0); t.Strike.To(0); t.SinkAt = m.Clock + 0.12; }
        Save();
    }

    /// <summary>The done tasks fold away one after another.</summary>
    public void ClearDone()
    {
        int k = 0;
        foreach (var t in Tasks.Where(t => t.Done && !t.Leaving)) { t.Leaving = true; t.Show.To(0, SpringSpec.Quick, k++ * 0.05f); }
    }

    public void DeleteTask(int i)
    {
        if (i < 0 || i >= Tasks.Count || Tasks[i].Leaving) return;
        Tasks[i].Leaving = true;
        Tasks[i].Show.To(0, SpringSpec.Quick);
    }
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
        if (m.Mode == Mode.Full && Selected == "servers" && !Editing && m.Clock - _serversAt > 2.5) { _serversAt = m.Clock; RefreshServers(m); }
        StepServers(m, dt);
        StepTasks(m, dt);

        // the network is read once a second on a worker thread (asking Windows for its adapters can take tens of
        // milliseconds), and only while the Network tab is in your bar
        if ((Enabled.Contains("network") || Selected == "network") && m.Clock - _netAt >= 1 && Interlocked.CompareExchange(ref _netBusy, 1, 0) == 0)
        {
            _netAt = m.Clock;
            ThreadPool.UnsafeQueueUserWorkItem(static tb => { try { tb._netRead = ReadNetwork(); } finally { Volatile.Write(ref tb._netBusy, 0); } }, this, false);
        }
        if (_netRead is { } read && !ReferenceEquals(read, _netSeen)) { TakeNetwork(read); _netSeen = read; }
        // the tabs you described: each looks again at its source on its own schedule while it's pinned
        foreach (var t in CustomTabs.All)
        {
            t.Tick(m.Clock, Enabled.Contains(t.Id) || Selected == t.Id, m.Mode == Mode.Full && Selected == t.Id && !Editing);
            t.Step(dt);
        }
        if (m.Mode == Mode.Full && Selected == "downloads" && m.Clock - _filesAt > 4) { _filesAt = m.Clock; RefreshDownloads(); }
        if (m.Mode == Mode.Full && Selected == "projects" && m.Clock - _projectsAt > 20) { _projectsAt = m.Clock; RefreshProjects(); }
    }

    /// <summary>One look at the network: bytes received and sent so far, and the busiest adapter.</summary>
    sealed record NetRead(long Rx, long Tx, string Name, string Kind, string Ip, long Stamp);
    volatile NetRead? _netRead;
    NetRead? _netSeen;
    int _netBusy;

    /// <summary>Worker thread: asks Windows for the adapters and their counters.</summary>
    static NetRead? ReadNetwork()
    {
        long rx = 0, tx = 0;
        string name = "", kind = "", ip = "";
        if (Showcase.On)
        {
            double t = Environment.TickCount64 / 1000.0;
            rx = (long)(t * 2_600_000 + Math.Abs(Math.Sin(t * 0.7)) * 9_000_000);
            tx = (long)(t * 420_000 + Math.Abs(Math.Sin(t * 1.3)) * 600_000);
            name = "Wi-Fi"; kind = "Wireless · 5 GHz"; ip = "192.168.1.24";
        }
        else
        {
            try
            {
                NetworkInterface? best = null;
                long bestRx = -1;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived; tx += st.BytesSent;
                    if (st.BytesReceived > bestRx) { best = ni; bestRx = st.BytesReceived; }
                }
                if (best != null)
                {
                    name = best.Name;
                    kind = best.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wireless",
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                        _ => best.NetworkInterfaceType.ToString(),
                    } + (best.Speed > 0 ? $" · {best.Speed / 1_000_000} Mbps link" : "");
                    ip = best.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                }
            }
            catch (Exception ex) { Diag.Log("network: " + ex.Message); return null; }
        }
        return new NetRead(rx, tx, name, kind, ip, Stopwatch.GetTimestamp());
    }

    /// <summary>The island's thread: turns a look at the network into one more second of the graphs.</summary>
    void TakeNetwork(NetRead n)
    {
        NetName = n.Name; NetKind = n.Kind; NetIp = n.Ip;
        double seconds = _netSeen == null ? 0 : (n.Stamp - _netSeen.Stamp) / (double)Stopwatch.Frequency;
        if (_lastRx >= 0 && seconds > 0)
        {
            long dr = Math.Max(0, n.Rx - _lastRx), dtx = Math.Max(0, n.Tx - _lastTx);
            Array.Copy(Down, 1, Down, 0, Down.Length - 1); Down[^1] = (float)(dr / seconds);
            Array.Copy(Up, 1, Up, 0, Up.Length - 1); Up[^1] = (float)(dtx / seconds);
            NetDownTotal += dr; NetUpTotal += dtx;
        }
        _lastRx = n.Rx; _lastTx = n.Tx;
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

    /// <param name="Session">The Claude Code session that started it, when we can tell (its session id).</param>
    public sealed record ServerItem(int Port, int Pid, string Process, string Kind, DateTime? Started, bool Web, string? Session = null);
    public List<ServerItem> Servers = new();
    public bool ServersLoaded;
    double _serversAt = -100;
    /// <summary>Stop needs a second click: (port, until when).</summary>
    public (int Port, double Until) ConfirmStop = (0, 0);

    /// <summary>A server as the panel draws it: it springs in, eases out once it stops, and shows its buttons on hover.</summary>
    public sealed class ServerAnim(ServerItem item)
    {
        public ServerItem Item = item;
        public readonly Spring Show = new(0, SpringSpec.Bounce, 0.004f);
        public readonly Spring Hover = new(0, SpringSpec.Content, 0.004f);
        public readonly Spring Confirm = new(0, SpringSpec.Bounce, 0.004f);
        /// <summary>Its place in its session's card (relative to the card), so it glides when a neighbour leaves.</summary>
        public readonly Spring X = new(0, SpringSpec.Content, 0.05f), Y = new(0, SpringSpec.Content, 0.05f), W = new(0, SpringSpec.Content, 0.05f);
        public bool Placed;

        /// <summary>Moves it to its slot (the first time, it simply starts there).</summary>
        public void Place(float x, float y, float w)
        {
            if (!Placed) { X.Snap(x); Y.Snap(y); W.Snap(w); Placed = true; return; }
            X.To(x); Y.To(y); W.To(w);
        }
    }
    readonly Dictionary<int, ServerAnim> _serverAnims = new();
    /// <summary>What the panel draws, by port: the running servers and the ones still easing out.</summary>
    public readonly List<ServerAnim> ServersShown = new();
    List<ServerItem>? _serversSeen;
    bool _serversSnap;
    readonly HashSet<int> _showcaseStopped = new();
    public readonly Spring ServerScroll = new(0, new SpringSpec(0.3f, 1f), 0.1f);
    public float ServerScrollMax;

    public void ServersWheel(float delta) => ServerScroll.To(Math.Clamp(ServerScroll.Target - delta * 0.6f, 0, ServerScrollMax));

    void StepServers(IslandModel m, float dt)
    {
        if (!ReferenceEquals(_serversSeen, Servers))
        {
            _serversSeen = Servers;
            foreach (var s in Servers)
            {
                if (!_serverAnims.TryGetValue(s.Port, out var a))
                {
                    _serverAnims[s.Port] = a = new ServerAnim(s);
                    if (_serversSnap) a.Show.Snap(1); // opening the tab: everything is simply there
                }
                a.Item = s;
                a.Show.To(1, SpringSpec.Bounce);
            }
            foreach (var (port, a) in _serverAnims)
                if (!Servers.Any(s => s.Port == port)) a.Show.To(0, SpringSpec.Quick);
            if (ServersLoaded) _serversSnap = false;
        }
        string? hov = m.HoverHit;
        bool confirming = m.Clock < ConfirmStop.Until;
        bool changed = false;
        foreach (var a in _serverAnims.Values)
        {
            string tail = ":" + a.Item.Port;
            bool hot = hov != null && hov.StartsWith("srv:") && hov.EndsWith(tail);
            bool sure = confirming && ConfirmStop.Port == a.Item.Port;
            a.Hover.To(hot || sure ? 1 : 0);
            a.Confirm.To(sure ? 1 : 0);
            a.Show.Step(dt); a.Hover.Step(dt); a.Confirm.Step(dt);
            a.X.Step(dt); a.Y.Step(dt); a.W.Step(dt);
            if (a.Show.Target == 0 && a.Show.Value < 0.01f) { _serverAnims.Remove(a.Item.Port); changed = true; }
        }
        if (changed || ServersShown.Count != _serverAnims.Count)
        {
            ServersShown.Clear();
            ServersShown.AddRange(_serverAnims.Values.OrderBy(a => a.Item.Port));
        }
        ServerScroll.To(Math.Clamp(ServerScroll.Target, 0, ServerScrollMax));
        ServerScroll.Step(dt);
    }

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

    public void RefreshServers(IslandModel m)
    {
        if (Showcase.On)
        {
            var now = DateTime.Now;
            Servers = new List<ServerItem>
            {
                new(4321, 0, "node", "Astro", now.AddMinutes(-7), true, "sc-orbit"),
                new(5173, 0, "node", "Vite", now.AddMinutes(-42), true, "sc-aurora"),
                new(5432, 0, "postgres", "PostgreSQL", now.AddDays(-1), false),
                new(6006, 0, "node", "Storybook", now.AddMinutes(-18), true, "sc-aurora"),
                new(6379, 0, "redis-server", "Redis", now.AddDays(-1), false),
                new(8000, 0, "python", "uvicorn", now.AddHours(-2), true, "sc-pixel"),
            }.Where(s => !_showcaseStopped.Contains(s.Port)).ToList();
            ServersLoaded = true;
            return;
        }
        var sessions = m.Team.Agents.Where(a => !a.Gone && a.Cwd.Length > 0)
            .Select(a => new Owner(a.SessionId, a.Pid, a.Cwd.TrimEnd('\\', '/').Replace('/', '\\'), a.StartedAt)).ToList();
        Task.Run(() =>
        {
            try
            {
                var names = new Dictionary<int, (string name, DateTime? at)>();
                var parents = sessions.Count > 0 ? Processes.Parents() : [];
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
                    if (info.name.Length == 0 || list.Any(s => s.Port == port)) continue;
                    bool known = KnownPorts.TryGetValue(port, out var kind);
                    bool dev = DevProcesses.Contains(info.name) || known && port >= 1024;
                    // anything a Claude session started counts too (your own app.exe on :8090)
                    if (!dev && port < 1024) continue;
                    string? session = SessionOf(pid, sessions, parents);
                    if (!dev && session == null) continue;
                    list.Add(new(port, pid, info.name, kind ?? info.name, info.at, !NotWeb.Contains(port), session));
                }
                Servers = list.OrderBy(s => s.Port).Take(24).ToList();
            }
            catch (Exception ex) { Diag.Log("servers: " + ex.Message); }
            ServersLoaded = true;
        });
    }

    sealed record Owner(string Id, int Pid, string Cwd, DateTime StartedAt);

    /// <summary>
    /// Which Claude Code session started the server <paramref name="pid"/>: a session it still descends from, else
    /// the session whose project folder it runs in or names on its command line (a server started in the
    /// background outlives the shell that launched it, so its process tree no longer reaches the session).
    /// </summary>
    static string? SessionOf(int pid, List<Owner> sessions, Dictionary<int, int> parents)
    {
        if (sessions.Count == 0) return null;
        var self = Processes.Peek(pid);
        if (self == null) return null;

        // up the tree, as long as each parent is older than its child (pids get reused)
        var tree = new List<Processes.Info> { self };
        for (int cur = pid, hops = 0; hops < 12 && parents.TryGetValue(cur, out int up) && up > 4; cur = up, hops++)
        {
            if (sessions.FirstOrDefault(s => s.Pid == up) is { } direct && Processes.Created(up) <= tree[^1].Created) return direct.Id;
            var p = Processes.Peek(up);
            if (p == null || p.Created > tree[^1].Created) break;
            tree.Add(p);
        }

        // the nearest process that runs in a project folder, else names one on its command line (a server can borrow
        // another project's tools, like its vite, to serve this folder); the deepest folder wins
        foreach (var p in tree.Take(4))
            foreach (bool byCwd in new[] { true, false })
            {
                Owner? best = null;
                foreach (var s in sessions)
                {
                    if (byCwd ? !InFolder(p.Cwd, s.Cwd) : !Names(p.CommandLine, s.Cwd)) continue;
                    // two sessions in one folder: the one most recently started before the server
                    if (best == null || s.Cwd.Length > best.Cwd.Length
                        || s.Cwd.Length == best.Cwd.Length && Before(s, self.Created) && (!Before(best, self.Created) || s.StartedAt > best.StartedAt))
                        best = s;
                }
                if (best != null) return best.Id;
            }
        return null;

        static bool Before(Owner s, DateTime at) => s.StartedAt <= at;
    }

    static bool InFolder(string? path, string folder)
    {
        if (string.IsNullOrEmpty(path)) return false;
        path = path.TrimEnd('\\');
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.Length > folder.Length && path[folder.Length] == '\\' && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    static bool Names(string? commandLine, string folder)
    {
        if (string.IsNullOrEmpty(commandLine)) return false;
        commandLine = commandLine.Replace('/', '\\');
        for (int i = commandLine.IndexOf(folder, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = commandLine.IndexOf(folder, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = i + folder.Length;
            if (end == commandLine.Length || commandLine[end] is '\\' or '"' or '\'' or ' ') return true;
        }
        return false;
    }

    public void StopServer(IslandModel m, ServerItem s)
    {
        if (ConfirmStop.Port != s.Port || m.Clock > ConfirmStop.Until) { ConfirmStop = (s.Port, m.Clock + 3); return; }
        ConfirmStop = (0, 0);
        try
        {
            // the demo stops nothing real: the sample server just leaves
            if (Showcase.On) _showcaseStopped.Add(s.Port);
            else { using var p = System.Diagnostics.Process.GetProcessById(s.Pid); p.Kill(); }
            Servers = Servers.Where(x => x.Port != s.Port).ToList(); // eases out now, not at the next look
            m.ShowToast($"Stopped {s.Kind} on :{s.Port}");
            m.Bloub.React(Mascot.BloubState.Comet, m.Clock, 1.4);
        }
        catch (Exception ex) { m.ShowToast("Couldn't stop it"); Diag.Log("stop server: " + ex.Message); }
        _serversAt = m.Clock - 1.5; // look again soon
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
                var list = tabs.Select(t => t?.GetValue<string>()).Where(t => t != null && All.Any(d => d.Id == t)).Distinct().Take(MaxTabs).ToList();
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
            Disk.Write(File_, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Diag.Log("settings save: " + ex.Message); }
    }
}
