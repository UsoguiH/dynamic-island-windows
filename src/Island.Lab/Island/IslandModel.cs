using System.Numerics;
using Island.Lab.Motion;
using Island.Lab.Platform;
using Island.Lab.Render;

namespace Island.Lab.Island;

public enum Mode { Dormant, Media, Focus, Split, Alert, Expanded, Full, Command, Gallery, Agents }
public enum AlertKind { Welcome, ClaudePermission, Allowed, Info, Agent, Drop, File }

public sealed record Song(string Title, string Artist, uint C0, uint C1, float Length);

/// <summary>
/// Content of one state, cross-faded with blur + scale. Leaving layers fade out fast while the
/// shape springs; entering layers wait ~90 ms, then un-blur into place (Apple's choreography).
/// </summary>
public sealed class ContentLayer
{
    public readonly Mode Mode;
    public readonly Spring Opacity = new(0, SpringSpec.Content, 0.002f);
    public readonly Spring Blur = new(10, SpringSpec.Content, 0.02f);
    public readonly Spring Scale = new(0.88f, SpringSpec.Content, 0.0005f);
    public bool Leaving { get; private set; }

    public ContentLayer(Mode mode, float delay)
    {
        Mode = mode;
        Opacity.To(1, delay);
        Blur.To(0, delay);
        Scale.To(1, delay);
    }

    public void Leave(bool slow = false)
    {
        if (Leaving) return;
        Leaving = true;
        var fast = slow ? new SpringSpec(0.24f, 1f) : new SpringSpec(0.2f, 1f);
        Opacity.To(0, fast);
        Blur.To(8, fast);
        Scale.To(0.92f, fast);
    }

    public bool Dead => Leaving && Opacity.Value < 0.01f;

    public void Step(float dt) { Opacity.Step(dt); Blur.Step(dt); Scale.Step(dt); }
}

public sealed class IslandModel
{
    public const float CX = 400;
    public float Top => TopY.Value;
    public const float BubbleR = 18, BubbleGap = 10;

    public Mode Mode { get; private set; } = Mode.Dormant;
    public Mode Base { get; private set; } = Mode.Dormant;
    public AlertKind Alert { get; private set; }
    Mode _beforeAlert = Mode.Dormant;

    public readonly Spring W = new(232, SpringSpec.Island, 0.05f);
    public readonly Spring H = new(36, SpringSpec.Island, 0.05f);
    public readonly Spring R = new(18, SpringSpec.Island, 0.05f);
    public readonly Spring Bubble = new(0, SpringSpec.Bounce, 0.001f);
    public readonly Spring Press = new(1, SpringSpec.Press, 0.0005f);
    public readonly Spring HoverGrow = new(0, SpringSpec.Bounce, 0.002f);
    public readonly Spring GlowR = new(1, SpringSpec.Gentle, 0.002f);
    public readonly Spring GlowG = new(1, SpringSpec.Gentle, 0.002f);
    public readonly Spring GlowB = new(1, SpringSpec.Gentle, 0.002f);
    public readonly Spring GlowA = new(0, SpringSpec.Gentle, 0.002f);
    public readonly Spring Visibility = new(1, SpringSpec.Content, 0.002f);
    public readonly Spring TopY = new(8, SpringSpec.Island, 0.01f);
    public readonly Spring Retract = new(0, SpringSpec.Content, 0.002f);

    /// <summary>Top-space strategy: a reserved bar (Windows work area shrinks) or auto-retract into a line.</summary>
    public bool ReserveMode, RetractMode = Environment.GetEnvironmentVariable("ISLAND_RETRACT") != "0";
    /// <summary>A window's top edge is under the island (e.g. Chrome's tab strip).</summary>
    public bool WindowAtTop;
    public const float ReservedHeight = 40;
    public bool Retracted { get; private set; }
    float _retractHold;
    /// <summary>Leaving the island closes it almost immediately (just enough grace to not flicker on the edge).</summary>
    public const float LeaveGrace = 0.12f;
    public const float HoverIntent = 0.25f;

    /// <summary>While folded into the line, Bloub peeks out when the cursor comes near (and now and then by himself).</summary>
    public bool Peeking { get; private set; }
    /// <summary>Peek pill: Bloub and the whole team look out of the folded line.</summary>
    public const float PeekW = 132;
    float _peekCooldown;
    double _peekUntil = -1, _nextRandomPeek = 40;
    readonly Random _rng = new();

    // ---- Interaction: scenes register clickable regions while drawing; clicks/hover use them.
    public Vector2 Cursor;
    public bool HitsEnabled;
    readonly List<(System.Drawing.RectangleF r, string id)> _hits = new();
    public string? HoverHit { get; private set; }

    public void BeginHits() => _hits.Clear();

    /// <summary>Registers a clickable region; returns true when the cursor is over it (for hover styling).</summary>
    public bool Hit(string id, float x, float y, float w, float h)
    {
        if (!HitsEnabled) return false;
        var r = new System.Drawing.RectangleF(x, y, w, h);
        _hits.Add((r, id));
        return Hovered && r.Contains(Cursor.X, Cursor.Y);
    }

    public void EndHits()
    {
        HoverHit = null;
        if (!Hovered) return;
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].r.Contains(Cursor.X, Cursor.Y)) { HoverHit = _hits[i].id; break; }
    }

    // ---- Workspace (Full) state
    /// <summary>Your dashboard tabs (which, in what order, customize mode) and the state the tabs keep.</summary>
    public readonly TabBar Tabs = new();
    /// <summary>"New tab · Describe it": Bloub becomes the line you type in, then builds the tab with Claude Code.</summary>
    public readonly TabMaker Maker = new();
    public readonly Spring TabFade = new(1, SpringSpec.Content, 0.002f);
    public readonly Spring TabPillX = new(0, SpringSpec.Bounce, 0.05f);
    public readonly Spring TabPillW = new(0, SpringSpec.Bounce, 0.05f);
    public bool TabPillReady;
    public bool FocusPaused;
    public bool PermissionResolved;
    public string Toast = "";
    public double ToastTime = -10;

    public void ShowToast(string text) { Toast = text; ToastTime = Clock; }

    // A toast that waits, then confirms (the Phantom beat, see Render/PhantomIcon): the wait line with three
    // waving dots, then the words cut to the result as the green check lands.
    public string CheckWait = "", CheckDone = "";
    public double CheckAt = -100, CheckGoAt = -1;   // when it appeared; when the hand-over started (< 0: still waiting)
    Func<(string Text, bool Ok)?>? _checkPoll;

    /// <summary>Shows the waiting toast; <paramref name="poll"/> is asked every frame and answers once the work is over.</summary>
    public void ShowCheck(string wait, Func<(string Text, bool Ok)?> poll)
    {
        CheckWait = wait; CheckDone = ""; CheckAt = Clock; CheckGoAt = -1; _checkPoll = poll;
        ToastTime = -10;
    }

    void StepCheck()
    {
        // (the wait line always shows for at least 0.6 s, as in the original, even when the answer is instant)
        if (_checkPoll == null || Clock - CheckAt < 0.6f || _checkPoll() is not { } r) return;
        _checkPoll = null;
        if (r.Ok) { CheckDone = r.Text; CheckGoAt = Clock; }
        else { CheckAt = -100; ShowToast(r.Text); }
    }

    /// <summary>Bloub's extra vertical offset (his hops in the tab library).</summary>
    public float BloubLift => Mode == Mode.Full ? Tabs.LibHop.Value * Math.Clamp(Tabs.EditT.Value, 0, 1) : 0;
    public readonly Face Face = new();
    public readonly BloubHost Bloub = new();
    /// <summary>The Claude Code agents, embodied as a team of coloured Bloubs.</summary>
    public readonly AgentTeam Team = new();
    /// <summary>Your Claude Code usage (tokens, limit window, activity) from local transcripts.</summary>
    public readonly ClaudeUsage Usage = new();
    /// <summary>Your real 5-hour, weekly and Fable plan limits.</summary>
    public readonly ClaudeLimits Limits = new();
    int _fiveWarned, _weekWarned, _fableWarned;

    /// <summary>Banner when a limit passes 80% / 95% (once per level until it resets).</summary>
    void WatchLimits()
    {
        Limits.Refresh(TimeSpan.FromMinutes(Mode == Mode.Full ? 2 : 10), Mode == Mode.Full ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(2));
        Friend.Update(this);
        StepCheck();
        if (Mode == Mode.Dormant && !Hidden && !Welcoming && Updater.TakeNotice() is { } version)
            ShowInfo($"Island {version} is ready", "It installs by itself the next time the island closes");
        static int Level(float? u) => u >= 0.95f ? 2 : u >= 0.8f ? 1 : 0;
        int f = Level(Limits.FiveHour), w = Level(Limits.Week), fb = Level(Limits.Fable);
        if (f < _fiveWarned) _fiveWarned = f;
        if (w < _weekWarned) _weekWarned = w;
        if (fb < _fableWarned) _fableWarned = fb;
        if (Mode is not (Mode.Dormant or Mode.Media or Mode.Focus or Mode.Split) || Welcoming) return;
        if (f > _fiveWarned)
        {
            _fiveWarned = f;
            ShowInfo($"5-hour limit {Limits.FiveHour * 100:0}% used", Limits.FiveResets is { } t ? $"Resets at {t:HH:mm}" : "Pace yourself");
        }
        else if (w > _weekWarned)
        {
            _weekWarned = w;
            ShowInfo($"Weekly limit {Limits.Week * 100:0}% used", Limits.WeekResets is { } t ? $"Resets {t:ddd HH:mm}" : "Pace yourself");
        }
        else if (fb > _fableWarned)
        {
            _fableWarned = fb;
            ShowInfo($"Fable limit {Limits.Fable * 100:0}% used", Limits.FableResets is { } t ? $"Resets {t:ddd HH:mm}" : "Pace yourself");
        }
    }
    /// <summary>Text box receiving the keyboard (the island takes focus while you type).</summary>
    public TextField? Focused { get; private set; }
    public void FocusField(TextField? f) { if (f != null) f.ChangedAt = Clock; Focused = f; }
    /// <summary>The island's window (clipboard owner).</summary>
    public nint Hwnd;
    /// <summary>Seconds since the user last hovered or clicked the island.</summary>
    public float IdleTime;
    public readonly List<ContentLayer> Layers = new();
    public readonly RollingText FocusDigits = new();
    public readonly float[] Bars = new float[5];

    public static readonly Song[] Songs =
    [
        new("Midnight City", "M83", 0x6E4BFF, 0xFF5FA2, 243),
        new("Blinding Lights", "The Weeknd", 0xFF3B30, 0xFF9F0A, 200),
        new("Weightless", "Marconi Union", 0x0AB3FF, 0x30D158, 480),
    ];
    public int SongIndex;
    public Song Song => Songs[SongIndex];
    public bool Playing = true;
    public float TrackPos = 102;

    /// <summary>
    /// Seconds since the island started. A double: a float that only grows loses its small steps (at 36 hours it can no
    /// longer add one 144 Hz tick at all, and everything timed by it froze or stuttered: the island "lagged" after a day
    /// or two of running). Take differences from it first, then narrow to float.
    /// </summary>
    public double Clock;
    public float ModeTime;
    double _modeClock;   // (ModeTime, counted exactly: the pill can stay in one mode for days)
    public float TimeScale = 1;
    public bool Hidden;
    public bool Hovered { get; private set; }
    public float FocusLength = 25 * 60;
    public float FocusElapsed = 47;
    public const string CommandText = "open vs code";

    (float W, float H, float R) _teamSize;
    float _hoverTime, _outTime, _alertTime;
    double _happyUntil = -1;
    bool _enteredThisMode;


    public IslandModel()
    {
        Layers.Add(new ContentLayer(Mode.Dormant, 0));
    }

    public (float W, float H, float R) Geometry(Mode m) => m switch
    {
        Mode.Dormant => (232 + (Pocket.Count > 0 ? Scenes.PocketBadgeW : 0), 36, 18),   // (a little wider when he holds something: the count sits beside him)
        Mode.Agents => Team.Size,
        Mode.Media => (296, 36, 18),
        Mode.Focus => (252, 36, 18),
        Mode.Split => (256, 36, 18),
        // (catching a file and showing what he caught are the same size, so he stays put while he swallows it)
        Mode.Alert when Alert == AlertKind.Drop => (460, 238, 54),
        Mode.Alert when Alert == AlertKind.File => (460, 308, 54),   // (it grows downwards for the buttons)
        Mode.Alert => (Alert is AlertKind.Welcome or AlertKind.Info or AlertKind.Agent ? 530 : 420, 80, 32),
        Mode.Expanded => (448, 196, 42),
        Mode.Full => (724, 404, 46),
        Mode.Gallery => (560, 224, 46),
        Mode.Command => (600, CommandChars >= 4 ? 56 + 3 * 46 + 8 : 56, 28),
        _ => (128, 34, 17),
    };

    public int CommandChars => Mode == Mode.Command
        ? Math.Clamp((int)((ModeTime - 0.35f) * 12), 0, CommandText.Length) : 0;

    public (float W, float H, float R) Live =>
        (W.Value * Press.Value + HoverGrow.Value * 8, H.Value * Press.Value + HoverGrow.Value * 2, R.Value);

    public string InfoTitle = "", InfoText = "";

    /// <summary>The Claude Code session a "needs you" / "done" banner is about.</summary>
    public Agent? AlertAgent;

    /// <summary>Banner about one of your Claude Code sessions — only while you're not already looking at the island.</summary>
    public void AgentAlert(Agent a, string title, string text)
    {
        bool quiet = Mode is Mode.Dormant or Mode.Media or Mode.Focus or Mode.Split
                     || Mode == Mode.Alert && Alert is AlertKind.Agent or AlertKind.Info or AlertKind.Welcome;
        if (!quiet || Welcoming) return;
        InfoTitle = title; InfoText = text; AlertAgent = a;
        ShowAlert(AlertKind.Agent);
    }

    /// <summary>
    /// A session has finished: he himself goes out to your pointer and tells you (<see cref="DoneFlight"/>); nothing opens
    /// in the island. The host takes him out (he leaves as himself and plays Finished at the pointer), and when he is back
    /// the island shows what finished, as it always did.
    /// </summary>
    public void ShowDone(Agent? a, string title, string text)
    {
        if (Welcoming || PendingFlight != null) return;
        if (Hidden) { Hidden = false; Visibility.To(1); }
        PendingFlight = (a, title, text);
    }

    /// <summary>He is ready to go out to your pointer: the host starts the stage.</summary>
    public (Agent? Agent, string Title, string Text)? PendingFlight;

    /// <summary>He is back from your pointer: the island catches him with a squish, its own Bloub takes over in a blink, and it shows what finished.</summary>
    public void DoneLanded(Agent? a, string title, string text)
    {
        Welcoming = false;
        Film.Out = false;
        Film.Stop();
        Face.Opacity.Snap(1);
        Press.Snap(0.86f); Press.To(1, SpringSpec.Bounce);
        Bloub.Squish.Snap(0.72f); Bloub.Squish.To(1, SpringSpec.Bounce);
        if (a != null) AgentAlert(a, title, text);
    }

    /// <summary>Opens the team widget, optionally focused on one session.</summary>
    public void OpenTeam(Agent? focus = null)
    {
        if (focus != null && !focus.Gone) Team.Focus(focus); else Team.SetView(TeamView.Grid);
        SetMode(Mode.Agents);
    }

    public void ShowInfo(string title, string text)
    {
        InfoTitle = title; InfoText = text;
        ShowAlert(AlertKind.Info);
    }

    public static readonly SpringSpec Collapse = new(0.52f, 0.94f);

    /// <summary>The full-screen welcome is playing: Bloub is out there, not in the island yet.</summary>
    public bool Welcoming;

    /// <summary>A finished session waiting for Bloub's celebration (the host plays it, then calls <see cref="CelebrationLanded"/>).</summary>
    public (Agent Agent, string Title, string Text)? PendingCelebration;
    DateTime _lastCelebration;

    readonly List<(Agent Agent, string Title, string Text, DateTime At)> _doneQueue = new();

    /// <summary>
    /// A session is done: Bloub flies out to tell you. It waits in a queue while you're using the island,
    /// a game is fullscreen, or he's already out — and plays as soon as you're back (within 10 minutes).
    /// </summary>
    public void Celebrate(Agent a, string title, string text)
    {
        _doneQueue.RemoveAll(q => q.Agent == a);
        _doneQueue.Add((a, title, text, DateTime.Now));
        Diag.Log($"done queued: {a.Name} (mode={Mode} hovered={Hovered} hidden={Hidden})");
    }

    /// <summary>Hands the next queued "done" to the host when the moment is right.</summary>
    void DeliverDone()
    {
        _doneQueue.RemoveAll(q => q.Agent.Gone || (DateTime.Now - q.At).TotalMinutes > 10);
        if (_doneQueue.Count == 0 || PendingCelebration != null || PendingFlight != null || Welcoming || Hidden) return;
        bool quiet = Mode is Mode.Dormant or Mode.Media or Mode.Focus or Mode.Split && !Hovered && _outTime > 0.6f;
        if (!quiet || (DateTime.Now - _lastCelebration).TotalSeconds < 5) return;
        var (a, title, text, _) = _doneQueue[0];
        _doneQueue.RemoveAt(0);
        _lastCelebration = DateTime.Now;
        // he goes out to your pointer with the film's Finished look and says it in a bubble. (The older celebration, the
        // white Bloub with the tick badge, is still here: PendingCelebration, the demo word "celebrate".)
        ShowDone(a, title, text);
        Diag.Log($"done: he goes out to the pointer for {a.Name}");
    }

    /// <summary>Bloub is back from celebrating: the island catches him and shows what finished.</summary>
    public void CelebrationLanded(Agent a, string title, string text)
    {
        Welcoming = false;
        Face.Opacity.Snap(1);
        Press.Snap(0.86f); Press.To(1, SpringSpec.Bounce);
        Bloub.Squish.Snap(0.72f); Bloub.Squish.To(1, SpringSpec.Bounce);
        AgentAlert(a, title, text);
    }

    public readonly Friend Friend = new();

    /// <summary>The states from the reference film, played in the island: the Greeting when he arrives, the file when one is dropped on him.</summary>
    public readonly OrbFilm Film = new();
    (OrbFilm.Clip Clip, double At)? _filmDue;
    bool _filmBlinked;
    /// <summary>A film state is about to start (the island is opening for it).</summary>
    public bool FilmPending => _filmDue != null;
    /// <summary>A mouse button went down outside the island and is still held: something is being dragged over it (hovering then opens nothing).</summary>
    public bool DragOver;
    /// <summary>The left mouse button is down (wherever it went down).</summary>
    public bool ButtonHeld;

    // ---- giving Bloub a file (see Platform/DragDrop, Pocket, Scenes.Drop)

    /// <summary>Everything he holds for you.</summary>
    public readonly Pocket Pocket = new();
    /// <summary>What hangs over the island right now (he shows its name while his slot is open).</summary>
    public List<PocketItem> Hovering = new();
    /// <summary>What was just dropped: the row with its name and the things to do with it.</summary>
    public List<PocketItem> Dropped = new();
    /// <summary>The word that confirms what you just did with it ("Path copied"), and when.</summary>
    public (string Text, double At)? RowNote;
    /// <summary>When the row's words appeared (its buttons pop in after it).</summary>
    public double DroppedShownAt = -100;
    /// <summary>Something is being dragged over the island and it said yes to it.</summary>
    public bool Catching { get; private set; }
    double _rowCloseAt = -1;
    bool _mustLeave;
    (DropPayload What, double At)? _previewDrop;

    /// <summary>Something dragged just came over the island. False: he doesn't take it (nothing happens).</summary>
    public bool DragEntered(DropPayload p)
    {
        if (Welcoming || Hidden) return false;
        // an agent's console or the New agent form is open: the file is for that agent
        if (Mode == Mode.Agents && Team.View != TeamView.Grid && p.Files.Length > 0) { Team.DropHover = true; Catching = true; return true; }
        var items = Pocket.Take(p, keep: false);
        if (items.Count == 0) return false;
        Hovering = items;
        Catching = true;
        if (Mode != Mode.Alert || Alert != AlertKind.Drop) ShowAlert(AlertKind.Drop);
        _beforeAlert = Base;   // (afterwards he goes back to resting, not to whatever was open)
        if (!Film.Waiting) { _filmDue = (OrbFilm.Clip.Slot, Clock + 0.1f); _filmBlinked = false; }
        return true;
    }

    /// <summary>It was taken away again: the slot closes.</summary>
    public void DragLeft()
    {
        Catching = false; Team.DropHover = false;
        if (Mode != Mode.Alert || Alert != AlertKind.Drop) return;
        _filmDue = null;
        Film.CloseSlot();
        SetMode(Base);
    }

    /// <summary>It was let go over the island, at <paramref name="at"/> (null: nowhere in particular, as in a preview).</summary>
    public void DropLanded(DropPayload p, Vector2? at)
    {
        Catching = false;
        if (Team.DropHover) { Team.DropHover = false; Team.Attach(this, p.Files); return; }
        var items = Pocket.Take(p);
        if (items.Count == 0) { DragLeft(); return; }
        if (Hidden) { Hidden = false; Visibility.To(1); }
        Dropped = items; RowNote = null; _rowCloseAt = -1; DroppedShownAt = -100;
        if (p.Text is { } text && !Showcase.On) { Tabs.Notes.Insert(0, new NoteItem(text, DateTime.Now)); Tabs.Save(); }
        if (items.Any(i => !i.Demo && i.Kind != PocketKind.Text)) Tabs.PinPocket();
        ShowAlert(AlertKind.File);
        _beforeAlert = Base;
        _filmDue = null;
        // the sheet flies in from where you let go (in his radii, from his centre)
        float r = 11f * Face.Size.Value * Bloub.Squish.Value;
        Film.Drop(at is { } c && r > 1 ? (c - new Vector2(Face.X, Face.Y + BloubLift)) / r : null);
        Diag.Log($"dropped {items.Count} item(s): {string.Join(", ", items.Select(i => i.Kind))}");
    }

    /// <summary>One of the row's buttons.</summary>
    void RowAct(string action)
    {
        if (action == "more")
        {
            // the whole board for this file, in the Pocket
            Tabs.Select(this, "pocket");
            if (Dropped.FirstOrDefault(i => !i.Demo && i.Kind != PocketKind.Text) is { } d) Pocket.Selected = d.Key;
            SetMode(Mode.Full);
            return;
        }
        if (action == "forget")
        {
            Pocket.Forget(Dropped);
            RowNote = ("Forgotten", Clock); _rowCloseAt = Clock + 1.0f;
            return;
        }
        if (Pocket.Run(this, Dropped, action) is { } note) { RowNote = (note, Clock); _rowCloseAt = Clock + 1.25f; }
    }

    /// <summary>The preview (tray, Ctrl+Alt+F, <c>--file-demo</c>): as if <paramref name="path"/> were held over him for a second, then let go.</summary>
    public void FileDropped(string path)
    {
        if (Welcoming) return;
        if (Hidden) { Hidden = false; Visibility.To(1); }
        var p = new DropPayload([path], null, null);
        if (DragEntered(p)) _previewDrop = (p, Clock + 1.0f);
    }

    void StepDrop()
    {
        if (_previewDrop is { } pd && Clock >= pd.At) { _previewDrop = null; DropLanded(pd.What, null); }
        if (Mode != Mode.Alert) return;
        // (a drag that ended without telling us, e.g. the window it came from closed)
        if (Alert == AlertKind.Drop && !Catching && _previewDrop == null && ModeTime > 0.3f) { Film.CloseSlot(); SetMode(Base); }
        if (Alert == AlertKind.File)
        {
            if (DroppedShownAt < 0 && (!Film.Active || Film.Now != OrbFilm.Clip.File || Film.TextIn > 0.05f)) DroppedShownAt = Clock;
            if (_rowCloseAt > 0 && Clock >= _rowCloseAt) { _rowCloseAt = -1; SetMode(Base); _mustLeave = Mode == Mode.Dormant; }
        }
    }

    /// <summary>Starts a film state once the island has opened for it, and stops it when the island moves on to something else.</summary>
    void StepFilm(float dt)
    {
        bool greet = Mode == Mode.Alert && Alert == AlertKind.Welcome, file = Mode == Mode.Alert && Alert == AlertKind.File;
        bool slot = Mode == Mode.Alert && Alert == AlertKind.Drop;
        if (_filmDue is { } due)
        {
            // the hand-over hides in a blink: the island's Bloub shuts his eyes, the film's Bloub opens his
            if (!(due.Clip switch { OrbFilm.Clip.Greet => greet, OrbFilm.Clip.Slot => slot, _ => file })) _filmDue = null;
            else if (!_filmBlinked && Clock >= due.At - 0.1f) { _filmBlinked = true; Bloub.Engine.Blink(Clock); }
            else if (Clock >= due.At && !Film.Active) { _filmDue = null; Film.Play(due.Clip); }
        }
        // the hello is over: the welcome closes (the island's own Bloub is never shown in it; he takes over as it folds away)
        if (greet && !Hovered && Film.GreetedFor > 0) { SetMode(_beforeAlert == Mode.Alert ? Base : _beforeAlert); greet = false; }   // (no pause on his last pose)
        // (while he is out at your pointer the film goes on, whatever the island shows, until he lands)
        if (!Film.Out && (Film.Now == OrbFilm.Clip.Greet && !greet || Film.Now is OrbFilm.Clip.File or OrbFilm.Clip.Done && !file)) Film.Stop();
        if (Film.Now == OrbFilm.Clip.Slot && !slot) Film.CloseSlot();
        bool playing = Film.Active;
        Film.Step(dt);
        if (playing && !Film.Active) Bloub.Engine.Blink(Clock - 0.1);   // (…and back: the island's Bloub appears with his eyes shut, opening)
    }

    /// <summary>The welcome's Bloub just flew into the island: catch him with a squish and say hi.</summary>
    public void WelcomeLanded()
    {
        Welcoming = false;
        Face.Opacity.Snap(1);
        Press.Snap(0.84f); Press.To(1, SpringSpec.Bounce);
        Bloub.Squish.Snap(0.72f); Bloub.Squish.To(1, SpringSpec.Bounce);
        ShowAlert(AlertKind.Welcome);
        _filmDue = null;
        Film.Play(OrbFilm.Clip.Greet, hold: 0.7f);   // he lands in the hello's first pose, settles in his seat, and then it begins: nothing to cut between
    }

    bool WillRetract(Mode m) => !Welcoming && RetractMode && WindowAtTop && !Hovered &&
                                m is Mode.Dormant or Mode.Media or Mode.Focus or Mode.Split;

    public void ShowAlert(AlertKind kind)
    {
        if (Mode != Mode.Alert) _beforeAlert = Mode;
        Alert = kind;
        _alertTime = 0;
        ForceMode(Mode.Alert);
    }

    public void SetMode(Mode m)
    {
        if (m == Mode) return;
        ForceMode(m);
    }

    void ForceMode(Mode m)
    {
        Diag.Log($"mode {Mode} -> {m} willRetract={WillRetract(m)} windowAtTop={WindowAtTop} hovered={Hovered}");
        var (ow, oh, _) = Geometry(Mode);
        var old = Mode;
        Mode = m;
        if (old == Mode.Full && m != Mode.Full) { Maker.Reset(this); Tabs.Editing = false; Tabs.EditT.To(0); Tabs.Release(); }
        ModeTime = 0; _modeClock = 0;
        _enteredThisMode = false;
        // back to the pill after he took a file: the count beside him pops up
        if (old == Mode.Alert && Alert == AlertKind.File && m == Mode.Dormant && Pocket.Count > 0) { Pocket.BadgePop.Snap(0.3f); Pocket.BadgePop.To(1); }
        // Going straight to the retracted line (e.g. leaving Full over Chrome) is ONE continuous motion.
        Retracted = WillRetract(m);
        Peeking = false;
        if (Retracted) _retractHold = 0;
        var (nw, nh, nr) = Retracted ? (160f, 5f, 60f) : Geometry(m);

        bool grow = nw * nh > ow * oh;
        bool bigCollapse = !grow && ow * oh > 3 * nw * nh;
        var spec = m == Mode.Alert ? SpringSpec.Bounce : grow ? SpringSpec.Island
                 : bigCollapse ? Collapse : SpringSpec.IslandSnappy;
        // Expanding: width leads, height follows ~30 ms later. Collapsing: content leaves first.
        float delay = grow ? 0f : 0.045f;
        W.To(nw, spec, delay);
        H.To(nh, spec, delay + (grow ? 0.03f : 0f));
        R.To(nr, spec, delay);

        foreach (var l in Layers) l.Leave(bigCollapse);
        Layers.Add(new ContentLayer(m, old == m ? 0.05f : 0.09f));

        Bubble.To(m == Mode.Split && !Retracted ? 1 : 0, SpringSpec.Bounce);
        if (m is Mode.Dormant or Mode.Media or Mode.Focus or Mode.Split) Base = m;

        Face.SetExpression(m switch
        {
            Mode.Alert when Alert == AlertKind.ClaudePermission => Expression.Exclaim,
            Mode.Alert when Alert == AlertKind.Allowed => Expression.Happy,
            Mode.Alert => Expression.Wide,
            _ => Expression.Neutral,
        });

        var (glow, a) = m switch
        {
            Mode.Media or Mode.Split or Mode.Expanded => (Song.C1, 0.42f),
            Mode.Focus => (0x30D158u, 0.30f),
            Mode.Alert when Alert == AlertKind.ClaudePermission => (0xFF9F0Au, 0.50f),
            Mode.Alert when Alert == AlertKind.Drop => (0x4FA8E8u, 0.5f),
            Mode.Alert => (0x0A84FFu, 0.32f),
            Mode.Command => (0x0A84FFu, 0.28f),
            Mode.Full => (0x5E5CE6u, 0.22f),
            Mode.Gallery => (0xBF5AF2u, 0.32f),
            Mode.Agents => (0x5E5CE6u, 0.2f),
            _ => (0xFFFFFFu, 0f),
        };
        SetGlow(glow, a);
    }

    void SetGlow(uint rgb, float a)
    {
        var c = Canvas.Rgba(rgb);
        GlowR.To(c.R); GlowG.To(c.G); GlowB.To(c.B); GlowA.To(a);
    }

    public void NextSong(int dir)
    {
        SongIndex = (SongIndex + dir + Songs.Length) % Songs.Length;
        TrackPos = 0;
        Face.Blink();
        if (Mode is Mode.Media or Mode.Split or Mode.Expanded) SetGlow(Song.C1, 0.42f);
    }

    public void Update(float dt, Vector2 cursor, bool inside)
    {
        var pAll = Prof.Start();
        try { UpdateCore(dt, cursor, inside); }
        finally { Prof.Stop("update", pAll); }
    }

    void UpdateCore(float dt, Vector2 cursor, bool inside)
    {
        Clock += dt;
        _modeClock += dt; ModeTime = (float)_modeClock;
        Cursor = cursor;

        // Hover intent
        Hovered = inside && !Hidden;
        // (the team can open under a cursor that isn't on its final shape — e.g. clicking Bloub on the wide
        //  dashboard — so it only counts as "entered" once the cursor is on it after the morph)
        if (Hovered) { if (!DragOver) _hoverTime += dt; _outTime = 0; if (Mode != Mode.Agents || ModeTime > 0.4f) _enteredThisMode = true; IdleTime = 0; }
        else { _outTime += dt; _hoverTime = 0; IdleTime += dt; }
        // the file card just closed under the cursor: that is not a hover, the dashboard waits until you come back
        if (_mustLeave) { if (!Hovered || Mode != Mode.Dormant) _mustLeave = false; else _hoverTime = 0; }
        // While you're typing into the island it stays open, wherever the cursor wanders.
        if (Focused != null && Mode is Mode.Agents or Mode.Full) _outTime = 0;
        // …while Windows' folder window is up for the New agent form, and while Bloub sends the new agent off
        if (Mode == Mode.Agents && Team.KeepsOpen) _outTime = 0;
        // …and while Bloub builds a tab you described (Cancel or Esc leave)
        if (Mode == Mode.Full && Maker.KeepsOpen) _outTime = 0;
        if (Mode is not (Mode.Agents or Mode.Full)) Focused = null;

        HoverGrow.To(Hovered && Mode == Mode.Dormant ? 1 : 0);
        // Hover opens the island (short intent delay so passing over Chrome's tabs doesn't trigger it).
        if (Mode is Mode.Media or Mode.Split && _hoverTime > HoverIntent) SetMode(Mode.Expanded);
        // The pill has two halves: Bloub + status open the dashboard, the little team opens your Claude sessions.
        else if (Mode == Mode.Dormant && _hoverTime > HoverIntent + (Retracted || Peeking ? 0.1f : 0f))
        {
            // (from the folded line it is always the dashboard — the team is one click away there)
            if (!Retracted && !Peeking && cursor.X >= Team.PillTeamLeft) OpenTeam(); else { if (OnPocketBadge(cursor)) Tabs.Select(this, "pocket"); SetMode(Mode.Full); }
        }
        else if (Mode == Mode.Focus && _hoverTime > HoverIntent) SetMode(Mode.Full);
        else if (Mode == Mode.Alert && Alert is AlertKind.Welcome or AlertKind.Info && _hoverTime > HoverIntent) SetMode(Mode.Full);
        else if (Mode == Mode.Alert && Alert == AlertKind.Agent && _hoverTime > HoverIntent) OpenTeam(AlertAgent);
        else if (Mode == Mode.Expanded && _enteredThisMode && _outTime > LeaveGrace) SetMode(Base);
        else if (Mode is Mode.Full or Mode.Gallery or Mode.Agents && _enteredThisMode && _outTime > LeaveGrace) SetMode(Base);
        else if (Mode == Mode.Alert)
        {
            if (!Hovered) _alertTime += dt;
            float ttl = Alert switch
            {
                AlertKind.Welcome => 6f, AlertKind.Info => 3.2f, AlertKind.Allowed => 1.6f, AlertKind.File => 6.5f, AlertKind.Drop => 600f,
                AlertKind.Agent => AlertAgent?.Status == AgentStatus.NeedsYou ? 8f : 4.5f,
                _ => 9f,
            };
            if (_alertTime > ttl) { bool card = Alert == AlertKind.File; SetMode(_beforeAlert == Mode.Alert ? Base : _beforeAlert); _mustLeave = card && Mode == Mode.Dormant; }
        }

        if (Mode == Mode.Command && !Retracted) H.To(Geometry(Mode.Command).H);
        if (Mode == Mode.Dormant && !Retracted && !Peeking && Geometry(Mode.Dormant).W is var pillW && W.Target != pillW) W.To(pillW, SpringSpec.Bounce);
        // The team widget resizes between its views (grid / agent console / new-agent form).
        if (Mode == Mode.Agents && !Retracted)
        {
            var size = Team.Size;
            // (a view chosen in the same instant the widget opened, e.g. "New agent" straight from a file, still gets its own size)
            if (size != _teamSize && (ModeTime > 0.05f || W.Target != size.W || H.Target != size.H)) { W.To(size.W, SpringSpec.Island); H.To(size.H, SpringSpec.Island, 0.03f); R.To(size.R, SpringSpec.Island); }
            _teamSize = size;
        }

        // Auto-retract: with a window under the island, base states shrink into a thin line at the
        // screen edge. Hovering the line (or any alert/expanded state) brings Bloub back out.
        if (Hovered) { _retractHold = LeaveGrace; _peekCooldown = 2.5f; } else { _retractHold -= dt; _peekCooldown -= dt; }
        bool shouldRetract = _retractHold <= 0 && WillRetract(Mode);
        if (shouldRetract != Retracted)
        {
            Retracted = shouldRetract;
            Diag.Log($"retracted={Retracted} mode={Mode} windowAtTop={WindowAtTop}");
            if (Retracted)
            {
                W.To(160, Collapse, 0.05f);
                H.To(5, Collapse, 0.05f);
                R.To(60f, Collapse, 0.05f);
                Bubble.To(0, Collapse);
            }
            else
            {
                Peeking = false;
                var (gw, gh, gr) = Geometry(Mode);
                W.To(gw, SpringSpec.Bounce); H.To(gh, SpringSpec.Bounce, 0.03f); R.To(gr, SpringSpec.Bounce);
                Bubble.To(Mode == Mode.Split ? 1 : 0, SpringSpec.Bounce);
            }
        }
        // Peek: the line swells into a small pill and Bloub looks out at the cursor.
        // (He no longer swells when the mouse merely comes near: the pill grew under the cursor and counted as
        // touching him. He still looks out by himself now and then, but not while the mouse is right there.)
        bool nearTop = cursor.Y < 90 && MathF.Abs(cursor.X - CX) < PeekW;
        if (Retracted && Clock > _nextRandomPeek)
        {
            if (!nearTop) _peekUntil = Clock + 2.6f;
            _nextRandomPeek = Clock + 45 + (float)_rng.NextDouble() * 45;
        }
        bool wantPeek = Retracted && _peekCooldown <= 0 && Clock < _peekUntil;
        if (wantPeek != Peeking)
        {
            Peeking = wantPeek;
            Diag.Log($"peek={Peeking} cursor={cursor} retracted={Retracted}");
            if (Peeking) { W.To(PeekW, SpringSpec.Bounce); H.To(30, SpringSpec.Bounce); R.To(60, SpringSpec.Bounce); }
            else if (Retracted) { W.To(160, Collapse); H.To(5, Collapse); R.To(60, Collapse); }
        }

        // Folding fades content out gently; popping out must reveal it quickly so it never lags the shape.
        if (Retracted) Retract.To(1, SpringSpec.Content); else Retract.To(0, new SpringSpec(0.16f, 1f));
        TopY.To(Retracted ? 0 : ReserveMode ? (ReservedHeight - 34) / 2 : 8);

        if (_happyUntil > 0 && Clock > _happyUntil) { _happyUntil = -1; Face.SetExpression(Expression.Neutral); }

        // Face anchor follows the live shape (springs give it a slight secondary lag).
        var (w, h, _) = Live;
        float left = CX - w / 2;
        var (ax, ay, s) = Peeking ? (left + 18, Top + h / 2, 0.82f) : Mode switch
        {
            Mode.Dormant => (left + 21, Top + h / 2, 1f + 0.08f * HoverGrow.Value),
            // in the New agent form he sits beside the question, large: he is the one asking
            Mode.Agents => Team.FormFace is { } seat ? (left + seat.X, Top + seat.Y, seat.Size) : (left + 26, Top + 24, 0.95f),
            Mode.Media or Mode.Focus or Mode.Split => (left + 22, Top + h / 2, 0.95f),
            // holding a file over him, and just after: he is big, in the middle of the card
            Mode.Alert when Alert is AlertKind.Drop or AlertKind.File => (CX, Top + Scenes.DropFaceY, Scenes.DropFaceR / 11f),
            Mode.Alert => (left + 42, Top + h / 2, 1.5f),
            Mode.Expanded => (left + w - 40, Top + 38, 1.3f),
            // customizing: Bloub flies down into the library's orb
            Mode.Full when Tabs.Editing => (left + Scenes.LibOrbX, Top + Scenes.LibOrbY, Scenes.LibOrbR / 11f),
            Mode.Full => (left + 40, Top + 32, 1.3f),
            Mode.Command => (left + 30, Top + 28, 1.15f),
            Mode.Gallery => (left + 118, Top + h / 2, 5.2f),
            _ => (CX, Top + h / 2, 1f),
        };
        if (Mode == Mode.Full && Maker.Pose is { } pose) { Face.X.Snap(left + pose.X); Face.Y.Snap(Top + pose.Y); Face.Size.Snap(pose.Z / 11); }
        else { Face.X.To(ax); Face.Y.To(ay); Face.Size.To(s); }
        if (Mode == Mode.Full && Maker.HidesBloub) Face.Opacity.Snap(0);
        else if (Hidden || Welcoming || (Retracted && !Peeking)) Face.Opacity.To(0, new SpringSpec(0.12f, 1f)); else Face.Opacity.To(1, new SpringSpec(0.2f, 1f));

        if (Mode == Mode.Command) { Face.GazeX.To(0.9f); Face.GazeY.To(0.1f); }
        else if (Hovered || Vector2.Distance(cursor, new(CX, Top)) < 420) Face.Look(cursor, dt);
        else Face.Look(null, dt);
        if (Hovered && Mode == Mode.Dormant && _happyUntil < 0) Face.Wide.To(1);
        else if (Mode == Mode.Dormant && _happyUntil < 0) Face.Wide.To(0);

        // Fake media + focus data
        if (Playing) TrackPos = (TrackPos + dt) % Song.Length;
        for (int i = 0; i < Bars.Length; i++)
        {
            float target = Playing
                ? 0.25f + 0.75f * MathF.Abs((float)Math.Sin(Clock * (5.1f + i * 1.7f) + i * 1.3f) * (float)Math.Sin(Clock * (2.3f + i * 0.6f)))
                : 0.12f;
            Bars[i] += (target - Bars[i]) * MathF.Min(1, dt * 18);
        }
        if (!FocusPaused) FocusElapsed += dt;
        float left_ = MathF.Max(0, FocusLength - FocusElapsed);
        FocusDigits.Set($"{(int)left_ / 60:00}:{(int)left_ % 60:00}");
        FocusDigits.Step(dt);

        foreach (var sp in _springs ??= [W, H, R, Bubble, Press, HoverGrow, GlowR, GlowG, GlowB, GlowA, Visibility, TopY, Retract, TabFade, TabPillX, TabPillW]) sp.Step(dt);
        // The team colours the glow: orange when an agent needs you, the focused agent's status colour in the widget.
        if (Mode == Mode.Dormant && !Retracted) SetGlow(Team.AnyNeeds ? 0xFF9F0Au : 0xFFFFFFu, Team.AnyNeeds ? 0.3f : 0f);
        // waiting for a file the light under the island breathes; once he has it, it turns green
        else if (Mode == Mode.Alert && Alert == AlertKind.Drop) SetGlow(0x4FA8E8u, 0.46f + 0.14f * (float)Math.Sin(Clock * 3.4f));
        else if (Mode == Mode.Alert && Alert == AlertKind.File && DroppedShownAt > 0) SetGlow(RowNote != null || Dropped.Count > 0 ? 0x30D158u : 0x0A84FFu, 0.3f);
        else if (Mode == Mode.Agents)
            SetGlow(Team.View == TeamView.Focus && Team.Selected != null ? Team.Selected.StatusColor : 0x5E5CE6u, Team.View == TeamView.Focus ? 0.34f : 0.2f);
        Face.Step(dt);
        var p = Prof.Start();
        Tabs.Update(this, dt);
        Prof.Stop("upd tabs", p); p = Prof.Start();
        Maker.Update(this, dt);
        Prof.Stop("upd maker", p); p = Prof.Start();
        Team.Update(this, dt);
        Prof.Stop("upd team", p); p = Prof.Start();
        WatchLimits();
        DeliverDone();
        Bloub.Update(Clock, this, new Vector2(Face.X, Face.Y));
        Pocket.Update(this, dt);
        StepDrop();
        StepFilm(dt);
        Prof.Stop("upd bloub+limits", p);
        foreach (var l in Layers) l.Step(dt);
        Layers.RemoveAll(l => l.Dead);
    }

    Spring[]? _springs;

    /// <summary>The cursor is on the count beside Bloub in the pill.</summary>
    bool OnPocketBadge(Vector2 p) => Pocket.Count > 0 && !Retracted && !Peeking && p.X - (CX - Live.W / 2) is > 33 and < 72;

    public void PressDown()
    {
        if (HoverHit is { } ph && (ph.StartsWith("pk:row:") || ph.StartsWith("pk:pre:")) && int.TryParse(ph[7..], out int pressKey)) Pocket.Press(this, pressKey);
        if (Mode == Mode.Full && Tabs.Editing && HoverHit is { } h && h.StartsWith("tab:"))
            Tabs.PressTab(h[4..], Cursor.X - (CX - Geometry(Mode.Full).W / 2));
        if (HoverHit == "bloub") Bloub.PressDown();
        else if (Geometry(Mode).H < 90) Press.To(0.96f, SpringSpec.Press);
    }

    public void PressUp() { Press.To(1, SpringSpec.Bounce); Bloub.PressUp(); Tabs.Release(); Pocket.Release(); }

    /// <summary>Click at a point in DIPs (already known to be on the island).</summary>
    public void Click(Vector2 p)
    {
        IdleTime = 0;
        if (Tabs.SwallowClick) { Tabs.SwallowClick = false; return; } // that was a drag, not a click
        if (HoverHit is { } id) { Act(id); return; }
        var (w, h, _) = Geometry(Mode);
        float x0 = CX - w / 2, y0 = Top;
        switch (Mode)
        {
            case Mode.Dormant:
                // Clicking the idle island (or the retracted line) opens the island.
                Face.SetExpression(Expression.Happy);
                _happyUntil = Clock + 0.6f;
                if (p.X >= Team.PillTeamLeft) OpenTeam(); else { if (OnPocketBadge(p)) Tabs.Select(this, "pocket"); SetMode(Mode.Full); }
                break;
            case Mode.Media or Mode.Split:
                SetMode(Mode.Expanded);
                break;
            case Mode.Focus:
                SetMode(Mode.Full);
                break;
            case Mode.Expanded:
            {
                float cy = y0 + 160, cx = CX;
                if (Vector2.Distance(p, new(cx, cy)) < 24) { Playing = !Playing; Face.Blink(); }
                else if (Vector2.Distance(p, new(cx - 64, cy)) < 22) NextSong(-1);
                else if (Vector2.Distance(p, new(cx + 64, cy)) < 22) NextSong(1);
                else SetMode(Mode.Full);
                break;
            }
            case Mode.Alert when Alert == AlertKind.ClaudePermission:
            {
                bool allow = p.X > x0 + w - 92 && p.X < x0 + w - 12;
                bool deny = p.X > x0 + w - 172 && p.X < x0 + w - 94;
                if (allow) ShowAlert(AlertKind.Allowed);
                else if (deny) { Face.SetExpression(Expression.Sleepy); _happyUntil = Clock + 0.8f; SetMode(_beforeAlert); }
                break;
            }
            case Mode.Alert when Alert == AlertKind.Agent:
                OpenTeam(AlertAgent);
                break;
            case Mode.Alert when Alert == AlertKind.Drop:
                break;   // (he is waiting for the file)
            case Mode.Alert:
                SetMode(_beforeAlert == Mode.Alert ? Base : _beforeAlert);
                break;
            case Mode.Full:
                if (!Maker.Active) FocusField(null);
                break; // stays open while you use it; collapses when the cursor leaves
            case Mode.Command:
                SetMode(Mode.Dormant);
                break;
        }
    }

    public void Act(string id)
    {
        var parts = id.Split(':');
        if (parts[0] == "mk") { Maker.Act(this, parts); return; }
        if (Mode == Mode.Full && parts[0] != "tf") FocusField(null);
        if (Team.Act(this, parts)) return;
        if (ActPocket(parts)) return;
        if (ActTabs(parts)) return;
        // a row or poster of a tab you described: its "link", in the browser
        if (parts is ["cx", var tabId, var row] && CustomTabs.Get(tabId) is { } ct && int.TryParse(row, out int ri)
            && ct.Data.Rows.ElementAtOrDefault(ri)?.GetValueOrDefault("link") is { } link && link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            TabBar.Open(link);
            return;
        }
        switch (parts[0])
        {
            case "tab":
                // "tab:usage", or an index into your tabs ("tab:2")
                string tab = int.TryParse(parts[1], out int ti) ? Tabs.Enabled[Math.Clamp(ti, 0, Tabs.Enabled.Count - 1)] : parts[1];
                if (TabBar.All.Any(d => d.Id == tab)) Tabs.Select(this, tab);
                break;
            case "play": Playing = !Playing; Face.Blink(); break;
            case "prev": NextSong(-1); break;
            case "next": NextSong(1); break;
            case "focus" when parts.Length > 2 && parts[1] == "len":
                FocusLength = int.Parse(parts[2]) * 60; FocusElapsed = 0; FocusPaused = false;
                ShowToast($"{parts[2]}-minute session started");
                Bloub.React(Mascot.BloubState.Hexagon, Clock, 1.6);
                break;
            case "focus" when parts.Length > 1 && parts[1] == "reset":
                FocusElapsed = 0; FocusPaused = false; ShowToast("Fresh start");
                break;
            case "focus":
                FocusPaused = !FocusPaused;
                ShowToast(FocusPaused ? "Focus paused" : "Focus resumed");
                Bloub.React(FocusPaused ? Mascot.BloubState.Sleep : Mascot.BloubState.Hexagon, Clock, 1.8);
                break;
            case "allow":
                PermissionResolved = true;
                ShowToast("Allowed — Claude continues in portfolio");
                Bloub.React(Mascot.BloubState.Wink, Clock, 1.8, Mascot.BloubExpressionId.Heureux);
                break;
            case "deny":
                PermissionResolved = true;
                ShowToast("Denied — Claude was told to stop");
                Bloub.React(Mascot.BloubState.Alert, Clock, 1.8, Mascot.BloubExpressionId.Triste);
                break;
            case "limits":
                Limits.Refresh(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                ShowCheck("Checking your limits...", () => Limits.Probing ? null : Limits.Error is { } err ? (err, false) : ("Limits updated", true));
                break;
            case "agent":
                OpenTeam();
                break;
            case "clip": ShowToast("Copied to clipboard"); Bloub.React(Mascot.BloubState.Wink, Clock, 1.4, Mascot.BloubExpressionId.Heureux); break;
            case "file": ShowToast("Opening " + parts[1]); Bloub.React(Mascot.BloubState.Comet, Clock, 2.2); break;
            case "bloub":
                // Clicking Bloub takes you to your team of agents (in the gallery it's just a poke).
                if (Mode == Mode.Gallery) Bloub.Poke(Clock);
                else
                {
                    Bloub.React(Mascot.BloubState.Wink, Clock, 1.0, Mascot.BloubExpressionId.Heureux);
                    if (Mode == Mode.Agents) { Team.SetView(TeamView.Grid); FocusField(null); } else OpenTeam();
                }
                break;
            case "g":
                switch (parts[1])
                {
                    case "prev": Bloub.GalleryStep(-1, Clock); Bloub.GalleryAuto = false; break;
                    case "next": Bloub.GalleryStep(1, Clock); Bloub.GalleryAuto = false; break;
                    case "mood": Bloub.MoodIndex = (Bloub.MoodIndex + 1) % BloubHost.Moods.Length; break;
                    case "shape": Bloub.ShapeIndex = (Bloub.ShapeIndex + 1) % BloubHost.Shapes.Length; break;
                    case "auto": Bloub.GalleryAuto = !Bloub.GalleryAuto; break;
                    case "pick": Bloub.GalleryIndex = int.Parse(parts[2]); Bloub.GalleryAuto = false; break;
                }
                break;
            case "expand": SetMode(Mode.Full); break;
        }
    }

    /// <summary>Clicks on what he holds: the row after a drop ("dz:…"), the Pocket tab ("pk:…").</summary>
    bool ActPocket(string[] p)
    {
        switch (p[0])
        {
            case "dz" when p.Length > 1:
                RowAct(p[1] == "a" && p.Length > 2 ? p[2] : p[1]);
                return true;
            case "pk" when p.Length > 1:
            {
                if (p[1] == "clear") { Pocket.Clear(); ShowToast("Pocket emptied"); Bloub.React(Mascot.BloubState.Idle, Clock, 0.95, Mascot.BloubExpressionId.Bof); return true; }
                if (p[1] == "do" && p.Length > 2)
                {
                    // the Claude button: pressed, it comes apart into the three things Claude can do
                    if (p[2] == "claude") { Pocket.ToggleAsk(this); return true; }
                    if (Pocket.Current is { } cur)
                    {
                        string? said = Pocket.Run(this, [cur], p[2]);
                        Pocket.AskOpen = false;
                        if (said != null) { ShowToast(said); Bloub.React(Mascot.BloubState.Wink, Clock, 1.2, Mascot.BloubExpressionId.Heureux); }
                    }
                    return true;
                }
                if (p.Length < 3 || !int.TryParse(p[2], out int key) || Pocket.Items.FirstOrDefault(i => i.Key == key) is not { } it) return true;
                switch (p[1])
                {
                    case "x": Pocket.Forget([it]); break;
                    case "row" or "pre" when it.Missing: ShowToast("That one was moved or deleted"); break;
                    // a click on the row opens it (a folder in Explorer, a link in the browser)
                    case "row" or "pre": if (Pocket.Run(this, [it], "open") is { } opened) { ShowToast(opened == "Opening" ? "Opening " + it.Name : opened); if (opened == "Opening") Bloub.React(Mascot.BloubState.Comet, Clock, 1.6); } break;
                    case "a" when p.Length > 3:
                        if (Pocket.Run(this, [it], p[3]) is { } note) { ShowToast(note); Bloub.React(Mascot.BloubState.Wink, Clock, 1.2, Mascot.BloubExpressionId.Heureux); }
                        break;
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Clicks that belong to the dashboard's tabs and their panels.</summary>
    bool ActTabs(string[] p)
    {
        var tb = Tabs;
        int N(int i) => p.Length > i && int.TryParse(p[i], out int n) ? n : -1;
        switch (p[0])
        {
            case "tabs":
                switch (p[1])
                {
                    case "edit": if (Maker.Active) Maker.Cancel(this); else tb.SetEditing(this, !tb.Editing); break;
                    case "team": Bloub.React(Mascot.BloubState.Wink, Clock, 1.0, Mascot.BloubExpressionId.Heureux); OpenTeam(); break;
                    case "toggle": tb.Toggle(this, p[2]); break;
                    case "preset": tb.ApplyPreset(this, N(2)); break;
                }
                return true;
            case "tf":
                FocusField(p[1] == "task" ? tb.TaskField : tb.NoteField);
                return true;
            case "tasks":
                switch (p[1])
                {
                    case "clear": tb.ClearDone(); break;
                    case "add": tb.Submit(this, tb.TaskField); FocusField(tb.TaskField); break;
                    case "del": tb.DeleteTask(N(2)); break;
                    default: tb.ToggleTask(this, N(2)); break;
                }
                return true;
            case "note" when N(2) is int ni && ni >= 0 && ni < tb.Notes.Count:
                if (p[1] == "del") { tb.DeleteNote(ni); ShowToast("Note deleted"); }
                else if (Shell.SetClipboard(Hwnd, tb.Notes[ni].Text)) { ShowToast("Copied"); Bloub.React(Mascot.BloubState.Wink, Clock, 1.2, Mascot.BloubExpressionId.Heureux); }
                return true;
            case "sw":
                switch (p[1])
                {
                    case "toggle": tb.SwRunning = !tb.SwRunning; break;
                    case "lap": tb.Laps.Add(tb.SwElapsed); break;
                    case "reset": tb.SwElapsed = 0; tb.Laps.Clear(); tb.SwRunning = false; break;
                }
                return true;
            case "cd":
                switch (p[1])
                {
                    case "len": tb.CdLength = tb.CdLeft = N(2) * 60; tb.CdRunning = false; break;
                    case "toggle":
                        if (tb.CdLeft <= 0) tb.CdLeft = tb.CdLength;
                        tb.CdRunning = !tb.CdRunning;
                        if (tb.CdRunning) Bloub.React(Mascot.BloubState.Thinking, Clock, 1.2);
                        break;
                    case "reset": tb.CdLeft = tb.CdLength; tb.CdRunning = false; break;
                }
                return true;
            case "cal":
                tb.MonthOffset = p[1] switch { "prev" => tb.MonthOffset - 1, "next" => tb.MonthOffset + 1, _ => 0 };
                return true;
            case "song" when N(1) >= 0:
                SongIndex = N(1) % Songs.Length; TrackPos = 0; Playing = true; Face.Blink();
                return true;
            case "proj" when N(2) is int pi && pi >= 0 && pi < tb.Projects.Count:
            {
                var path = tb.Projects[pi];
                if (Showcase.On && p[1] != "agent") { ShowToast("Showcase mode: nothing opens"); return true; }
                switch (p[1])
                {
                    case "open" or "folder": Shell.OpenFolder(path); ShowToast("Opened the folder"); break;
                    case "editor": Shell.OpenInEditor(path); ShowToast($"Opening in {Shell.EditorName}"); break;
                    case "agent": Team.NewAgentIn(this, path); break;
                }
                return true;
            }
            case "srv" when N(2) is int port && tb.Servers.FirstOrDefault(x => x.Port == port) is { } s:
            {
                if (p[1] == "stop") tb.StopServer(this, s);
                else if (!s.Web) return true;
                else if (Showcase.On) ShowToast("Showcase mode: nothing opens");
                else { TabBar.Open($"http://localhost:{s.Port}"); ShowToast($"Opening localhost:{s.Port}"); }
                return true;
            }
            case "dl" when N(2) is int di && di >= 0 && di < tb.Downloads.Count:
            {
                var f = tb.Downloads[di];
                if (Showcase.On) { ShowToast("Showcase mode: nothing opens"); return true; }
                if (p[1] == "show") TabBar.Reveal(f.Path);
                else if (!f.Partial) { TabBar.Open(f.Path); Bloub.React(Mascot.BloubState.Comet, Clock, 1.6); }
                return true;
            }
        }
        return false;
    }

    public void ToggleSleep()
    {
        bool sleepy = Face.Sleepy.Target < 0.5f;
        Face.SetExpression(sleepy ? Expression.Sleepy : Expression.Neutral);
        if (sleepy) SetMode(Mode.Dormant);
    }
}
