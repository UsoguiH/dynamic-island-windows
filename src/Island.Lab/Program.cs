using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Island.Lab.Island;
using Island.Lab.Platform;
using Island.Lab.Render;
using static Island.Lab.Platform.Native;

namespace Island.Lab;

/// <summary>
/// Island Lab — the motion prototype. A transparent, click-through, always-on-top overlay at the
/// top-center of the primary monitor, rendered at the display's refresh rate.
/// Ctrl+Alt+1..9 switch states, Ctrl+Alt+0 toggles slow motion, Ctrl+Alt+Q quits.
/// </summary>
static class Program
{
    const float LogicalW = 800, LogicalH = 560;

    static nint _hwnd;
    static float _scale = 1;
    static int _winX, _winY;
    static IslandModel _model = null!;
    static WndProc? _proc;
    static Vector2 _cursor;
    static bool _clickThrough = true;
    static char _retractKey = '?';

    static Mutex? _single;
    static Tray? _tray;

    [STAThread]
    static void Main()
    {
        // (the installer's hooks: first run after install, after an update, uninstall. Does nothing otherwise.)
        Velopack.VelopackApp.Build().Run();
        // `IslandLab --check-tab <recipe.json | id>`: fetch a custom tab's data once, print what it would show, exit
        var argv = Environment.GetCommandLineArgs();
        if (Array.IndexOf(argv, "--check-tab") is > 0 and var ci) { Environment.ExitCode = CustomTabs.Check(argv.ElementAtOrDefault(ci + 1)); return; }
        // `IslandLab --build-tab "<what to keep an eye on>"`: build a tab with Claude Code, print its progress and recipe (saves nothing)
        if (Array.IndexOf(argv, "--build-tab") is > 0 and var bi)
        {
            string? change = Array.IndexOf(argv, "--change") is > 0 and var changeAt ? argv.ElementAtOrDefault(changeAt + 1) : null;
            Environment.ExitCode = TabBuilder.Cli(argv.ElementAtOrDefault(bi + 1) ?? "", change);
            return;
        }
        // `IslandLab --pick-folder <start folder> <result file>`: Windows' folder window by itself (dev: the New agent form's "Other folder…")
        if (Array.IndexOf(argv, "--pick-folder") is > 0 and var pf)
        {
            File.WriteAllText(argv.ElementAtOrDefault(pf + 2) ?? "pick.txt", FolderPick.SelfTest(argv.ElementAtOrDefault(pf + 1)));
            return;
        }
        // `IslandLab --done-check <result file> [samples a second]`: the film's Finished as it is now played, sample by sample (dev: no pose may be held)
        if (Array.IndexOf(argv, "--done-check") is > 0 and var dc)
        {
            File.WriteAllText(argv.ElementAtOrDefault(dc + 1) ?? "done.txt", OrbFilm.DoneCheck(int.TryParse(argv.ElementAtOrDefault(dc + 2), out int hz) ? hz : 120));
            return;
        }
        // One island at a time: launching it again (shortcut, Start menu) just opens the running one.
        _single = new Mutex(true, "IslandLab.SingleInstance", out bool first);
        // measuring: the instance just killed may still be letting go of the mutex
        if (!first && Environment.GetEnvironmentVariable("ISLAND_BENCH") == "1")
            try { first = _single.WaitOne(4000); } catch (AbandonedMutexException) { first = true; }
        if (!first)
        {
            var other = FindWindowW("IslandLabWindow", "Island");
            // `IslandLab --file-demo`: the running island plays the file animation (as if a file had been dropped on it)
            if (other != 0) PostMessageW(other, Array.IndexOf(argv, "--file-demo") > 0 ? WM_APP_FILE : Array.IndexOf(argv, "--done-demo") > 0 ? WM_APP_DONE : WM_APP_SHOW, 0, 0);
            return;
        }
        timeBeginPeriod(1);
        Warm.Start();
        Updater.Start();
        KeepInMemory();
        // a collection that stops the drawing thread is a dropped frame: full blocking ones are held back
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        var inst = GetModuleHandleW(null);
        _proc = WindowProc;
        var cls = Marshal.StringToHGlobalUni("IslandLabWindow");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = inst,
            hCursor = LoadCursorW(0, 32512), // IDC_ARROW
            lpszClassName = cls,
        };
        RegisterClassExW(ref wc);

        _hwnd = CreateOverlay("Island");

        _scale = Shot.Scale ?? GetDpiForWindow(_hwnd) / 96f;
        int pw = (int)MathF.Round(LogicalW * _scale), ph = (int)MathF.Round(LogicalH * _scale);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(_hwnd, 1 /* primary */), ref mi);
        _winX = mi.rcMonitor.Left + (mi.rcMonitor.Right - mi.rcMonitor.Left - pw) / 2;
        _winY = mi.rcMonitor.Top;
        SetWindowPos(_hwnd, HWND_TOPMOST, _winX, _winY, pw, ph, SWP_NOACTIVATE);

        using var gpu = new Gpu(_hwnd, pw, ph, _scale);
        using var renderer = new FrameRenderer(gpu);
        _model = new IslandModel { Hwnd = _hwnd };
        // dev: ISLAND_UPTIME=253000 starts his clock as if the island had been running that many seconds (nothing may coarsen or freeze after days)
        if (double.TryParse(Environment.GetEnvironmentVariable("ISLAND_UPTIME"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double uptime)) _model.Clock = uptime;
        // the island is a real drop target: it knows when a file comes over it, leaves, and lands (see Platform/DragDrop)
        DragDrop.Register(_hwnd, p => _model.DragEntered(p), () => _model.DragLeft(), p =>
        {
            GetCursorPos(out var at);
            _model.DropLanded(p, new Vector2((at.X - _winX) / _scale, (at.Y - _winY) / _scale));
        });
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        _tray = new Tray(_hwnd);

        for (uint i = 0; i <= 9; i++) RegisterHotKey(_hwnd, (int)i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x30 + i);
        RegisterHotKey(_hwnd, 100, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'Q');
        RegisterHotKey(_hwnd, 101, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'B');
        RegisterHotKey(_hwnd, 103, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'W');
        RegisterHotKey(_hwnd, 104, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'A');
        RegisterHotKey(_hwnd, 105, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'D'); // preview the "done" celebration
        Diag.Log("hotkey Ctrl+Alt+G (done, in the island): " + RegisterHotKey(_hwnd, 107, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'G'));
        Diag.Log("hotkey Ctrl+Alt+F (file animation): " + RegisterHotKey(_hwnd, 106, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'F'));
        // Auto-retract: first free key wins (Ctrl+Alt+R is often taken by other apps).
        foreach (char k in "HRJ")
            if (RegisterHotKey(_hwnd, 102, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, k)) { _retractKey = k; break; }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { SetReserved(false); _model.Team.Dispose(); _tray?.Dispose(); Disk.Flush(); };

        using var pacer = new Pacer();
        // the loop must wake on every vblank even when other apps keep the CPU busy; it uses little time per tick
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        var clock = Stopwatch.StartNew();
        double last = 0, nextCheck = 0, nextTopmost = 0;
        bool welcomed = false;

        while (true)
        {
            // one tick per display refresh; a message wakes the loop early to handle it, then it waits on
            if (Perf && _pBusyFrom > 0) _pBusy += (Stopwatch.GetTimestamp() - _pBusyFrom) * 1000.0 / Stopwatch.Frequency;
            // nothing changed for a while: look every other refresh (a new animation starts at most one frame later; it then runs at full rate)
            long w0 = Stopwatch.GetTimestamp();
            bool tick = pacer.Wait(_stillTicks > 72 && _welcome == null && !_model.Hovered ? 2 : 1);
            _pBusyFrom = Stopwatch.GetTimestamp();
            _hWait += _pBusyFrom - w0;
            if (Perf && tick && pacer.Late > 1) _pLate++;
            while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) { SetReserved(false); _model.Team.Dispose(); _tray?.Dispose(); return; }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            _hMsg += Stopwatch.GetTimestamp() - _pBusyFrom;
            if (!tick) continue;
            if (Frame()) return;
            // a row of the Pocket was dragged out: Windows runs the drag in its own loop, and the island keeps drawing from a timer
            if (_model.Pocket.DragOut is { } outgoing) { _model.Pocket.DragOut = null; SendOut(outgoing, Frame); }
            if (_dryOut is { } dry) { _dryOut = null; SendOut(new PocketItem { Path = dry, Name = Path.GetFileName(dry), Kind = PocketItem.KindOf(dry) }, Frame, dry: true); }
        }

        // one tick: the model, the drawing, the stage. True when the island is done (a shot was saved, the demo ended).
        bool Frame()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now - last > 0.04 && last > 0) Hitch(now - last);
            long h0 = Stopwatch.GetTimestamp();
            float dt = (float)Math.Min(now - last, 0.05) * _model.TimeScale;
            last = now;

            if (!welcomed && now > 0.35) { welcomed = true; if (_demo == null) StartWelcome(mi.rcMonitor); }
            // Dev: ISLAND_DEMO="focus:<agent#>[:plan|files]" or "team" opens a screen without touching the mouse.
            if (_demo != null && now > 2.5) { RunDemo(_demo); _demo = null; }
            if (_demoPlan.Count > 0 && _model.Clock >= _demoPlan[0].At) { var d = _demoPlan[0]; _demoPlan.RemoveAt(0); d.Do(); }
            if (_demoToggle is { } dt_ && _model.Clock >= dt_.At)
            {
                if (_model.Tabs.Editing) _model.Tabs.Toggle(_model, dt_.Id);
                _demoToggle = (dt_.Id, _model.Clock + 1.8);
            }

            if (now >= nextCheck)
            {
                nextCheck = now + 0.25;
                _model.WindowAtTop = !Bench && WindowUnderIsland();
                if (now >= nextTopmost) { nextTopmost = now + 2; SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
                // Our own full-screen welcome makes Windows report "busy": ignore that while it plays.
                bool fullscreen = !Bench && _welcome == null && FullscreenAppActive();
                if (fullscreen != _model.Hidden)
                {
                    Log($"hidden={fullscreen} reason={_lastFullscreenReason}");
                    _model.Hidden = fullscreen;
                    _model.Visibility.To(fullscreen ? 0 : 1);
                }
            }

            GetCursorPos(out var p);
            _cursor = new Vector2((p.X - _winX) / _scale, (p.Y - _winY) / _scale);
            if (_fakeCursor is { } fake && now >= fake.At) _cursor = fake.P;
            bool inside = !_model.Hidden && HitTest(_cursor, _model.Catching ? 12 : 0);   // (holding a file over him: the edge is forgiving, so a bounce never drops it)
            // a button pressed outside the island and still held: something is being dragged (a file, a window)
            bool held = GetAsyncKeyState(1) < 0 || now >= _fakeHeld;
            if (!held) _dragging = false; else if (!_heldBefore && !inside) { _dragging = true; _dragFrom = _cursor; }
            _heldBefore = held;
            _model.ButtonHeld = held;
            _model.DragOver = _dragging || _model.Catching;
            // Something is being carried up to the island: the space around it takes it too, so a file is caught as
            // soon as it comes near (the folded line is 5 px high: nobody can aim a file at that). Only for a drag
            // that started somewhere else: a click or a window grabbed right here is left alone.
            bool zone = held && !_model.Hidden && (_dragging || _model.Catching) && InCatchZone(_cursor)
                        && !InCatchZone(_dragFrom) && Vector2.Distance(_cursor, _dragFrom) > 24;
            if (zone && _model.Catching) inside = true;   // (while the card opens under the file, it stays "over" him)
            if (zone != _inZone) { _inZone = zone; Log($"catch zone {(zone ? "on" : "off")} cursor={_cursor.X:0},{_cursor.Y:0} catching={_model.Catching}"); }
            SetClickThrough(!(inside || zone));

            long pt0 = Stopwatch.GetTimestamp();
            _hCheck = pt0 - h0;
            _model.Update(dt, _cursor, inside);
            KeyboardFocus(now);
            long pt1 = Stopwatch.GetTimestamp();
            renderer.Render(_model); // redraws (and presents) only what changed, if anything
            _stillTicks = renderer.Presented ? 0 : _stillTicks + 1;
            if (Shot.Plan is { } shot && now >= shot.At) { Shot.Save(gpu, shot.File); SetReserved(false); _model.Team.Dispose(); _tray?.Dispose(); return true; }
            if (Shot.Film != null && Shot.FilmFrame(gpu, now)) { SetReserved(false); _model.Team.Dispose(); _tray?.Dispose(); return true; }
            // the hover target is only known after drawing, so a mouse that stops on a button gets its cursor here
            if (inside && CursorId() is var want && want != _shownCursor) { _shownCursor = want; SetCursor(LoadCursorW(0, want)); }
            else if (!inside) _shownCursor = 0;
            if (Perf) PerfSample(now, pt0, pt1, Stopwatch.GetTimestamp());
            if (_replayWelcome) { _replayWelcome = false; EndWelcome(); StartWelcome(mi.rcMonitor); }
            // A session finished: Bloub comes out to tell you (only when nothing else is on stage).
            if (_welcome == null && _model.PendingCelebration is { } pc)
            {
                _model.PendingCelebration = null;
                if (!_model.Hidden)
                    StartStage(mi.rcMonitor, g => new Celebration(g, pc.Agent.Name), () => _model.CelebrationLanded(pc.Agent, pc.Title, pc.Text));
                else _model.AgentAlert(pc.Agent, pc.Title, pc.Text);
            }
            // A session finished: he himself goes out to your pointer and says so (nothing opens in the island).
            if (_welcome == null && _model.PendingFlight is { } pf)
            {
                _model.PendingFlight = null;
                if (!_model.Hidden)
                {
                    string who = pf.Agent?.Name ?? (pf.Title.EndsWith(" is done") ? pf.Title[..^8] : pf.Title);
                    StartStage(mi.rcMonitor, g => new DoneFlight(g, _model.Film, who, pf.Agent?.Color), () => _model.DoneLanded(pf.Agent, pf.Title, pf.Text));
                }
                else if (pf.Agent != null) _model.AgentAlert(pf.Agent, pf.Title, pf.Text);
            }
            long st0 = Stopwatch.GetTimestamp();
            _hUpdate = pt1 - pt0; _hRender = st0 - pt1;
            if (_welcome != null)
            {
                var (w, _, _, landed) = _welcome.Value;
                var sp = new Vector2((p.X - mi.rcMonitor.Left) / _scale, (p.Y - mi.rcMonitor.Top) / _scale);
                if (_fakeCursor is { } fk && now >= fk.At) sp = new Vector2((_winX - mi.rcMonitor.Left) / _scale + fk.P.X, (_winY - mi.rcMonitor.Top) / _scale + fk.P.Y);   // (dev: ISLAND_CURSOR is the stage's pointer too)
                w.Frame(dt, sp);
                // ISLAND_STAGESHOT="0.9,1.6,2.7:C:\out\intro": saves the stage's own frame at those moments (to check a scene without filming the screen)
                if (_stageShots is { } ss && _stageShot < ss.Times.Length && w.Clock >= ss.Times[_stageShot])
                    Shot.Save(_welcome.Value.gpu, $"{ss.Prefix}{_stageShot++}.png");
                if (w.Landed) { EndWelcome(); landed(); }
            }
            _hStage = Stopwatch.GetTimestamp() - st0;
            _hWait = _hMsg = 0;
            if (_stagesLeft >= 0 && _welcome == null && !_replayWelcome && _model.PendingCelebration == null && StageCheck(now)) return true;
            return false;
        }
    }

    // ---- dragging a file out of the Pocket. Windows runs the drag in its own loop and only returns when the file
    // is dropped or the drag is cancelled. That loop runs nothing of ours (no timers, no messages: measured) and
    // only moves on when a mouse or key message arrives; each time it does, it calls the drag's callbacks, and the
    // island ticks from those. So that it keeps ticking while the cursor rests, a mouse-move carrying the buttons'
    // real state is put in the island's own queue every 8 ms: it tells the loop nothing the mouse isn't saying.
    static bool _inDragFrame;

    /// <summary>Dev (ISLAND_DEMO=dragout:path): the drag-out with nothing allowed to be dropped and no button held.</summary>
    static string? _dryOut;

    static void SendOut(PocketItem item, Func<bool> frame, bool dry = false)
    {
        _model.PressUp();
        int ticks = 0, inside = 1;
        // (which button drags: the left one, or the right one when the buttons are swapped)
        bool swapped = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0;
        new Thread(() =>
        {
            for (int n = 0; Volatile.Read(ref inside) == 1; n++)
            {
                // (the dev test: the button is said to be held for a second, then let go; Esc if that did not end it)
                if (dry && n >= 250) { PostMessageW(_hwnd, WM_KEYDOWN, 0x1B, 0); break; }
                nint keys = dry ? (n < 100 ? 1 : 0) : (GetAsyncKeyState(swapped ? 2 : 1) < 0 ? 1 : 0) | (GetAsyncKeyState(swapped ? 1 : 2) < 0 ? 2 : 0);
                GetCursorPos(out var at); ScreenToClient(_hwnd, ref at);
                PostMessageW(_hwnd, 0x0200 /* WM_MOUSEMOVE */, keys, (nint)(((at.Y & 0xFFFF) << 16) | (at.X & 0xFFFF)));
                Thread.Sleep(dry ? 10 : 8);
            }
        }) { IsBackground = true, Name = "drag-out" }.Start();
        long lastTick = 0;
        bool dropped = DragDrop.Send(_hwnd, item.Path, dry, () =>
        {
            // one frame per display refresh at most
            long t = Stopwatch.GetTimestamp();
            if (_inDragFrame || (t - lastTick) * 1000.0 / Stopwatch.Frequency < 6.5) return;
            lastTick = t; _inDragFrame = true; ticks++;
            try { if (frame()) PostQuitMessage(0); } finally { _inDragFrame = false; }
        });
        Volatile.Write(ref inside, 0);
        // (what is left of the heartbeat must not reach the island as a mouse move of its own)
        while (PeekMessageW(out _, _hwnd, 0x0200, 0x0200, PM_REMOVE)) { }
        Diag.Log($"dragged out {item.Kind}: dropped={dropped} ticks={ticks}");
        if (dropped) _model.Bloub.React(Mascot.BloubState.Wink, _model.Clock, 1.2, Mascot.BloubExpressionId.Heureux);
    }

    // ---- where a hitch went: the wait and the messages since the last tick, and that tick's own work
    static long _hWait, _hMsg, _hCheck, _hUpdate, _hRender, _hStage;
    static int _hGc0, _hGc1, _hGc2; static TimeSpan _hPause;

    static void Hitch(double seconds)
    {
        double f = 1000.0 / Stopwatch.Frequency;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        Diag.Log($"hitch {seconds * 1000:0}ms mode={_model.Mode} layers={_model.Layers.Count} wait={_hWait * f:0.0} msg={_hMsg * f:0.0} " +
                 $"last tick: check={_hCheck * f:0.0} update={_hUpdate * f:0.0} render={_hRender * f:0.0} stage={_hStage * f:0.0} " +
                 $"gc={g0 - _hGc0}/{g1 - _hGc1}/{g2 - _hGc2} pause={(pause - _hPause).TotalMilliseconds:0.0}ms heap={GC.GetTotalMemory(false) >> 20}MB");
        (_hGc0, _hGc1, _hGc2, _hPause) = (g0, g1, g2, pause);
    }

    static (StageScene scene, Gpu gpu, nint hwnd, Action landed)? _welcome;
    static bool _dragging, _heldBefore;
    static Vector2 _dragFrom;
    static bool _inZone;
    // ISLAND_HELD=1.5 : from that second on the left button counts as held (to check the catch zone without the mouse)
    static readonly double _fakeHeld = double.TryParse(Environment.GetEnvironmentVariable("ISLAND_HELD"), System.Globalization.CultureInfo.InvariantCulture, out double fh) ? fh : double.MaxValue;

    /// <summary>Where a dragged file is caught: the top middle of the screen, around the island (DIPs).</summary>
    public const float CatchHalfW = 170, CatchH = 96;
    static bool InCatchZone(Vector2 p) => p.Y >= -2 && p.Y <= CatchH && MathF.Abs(p.X - IslandModel.CX) <= CatchHalfW;
    static GpuDevice? _stageDevice;
    static int _stillTicks;
    static nint _shownCursor;

    /// <summary>I-beam over a text field, hand over anything clickable, arrow elsewhere.</summary>
    static nint CursorId() =>
        _model.HoverHit is "mk:field" ? 32513 // IDC_IBEAM
        : _model.HoverHit != null || _model.Mode is Mode.Dormant or Mode.Media or Mode.Split or Mode.Focus ? 32649 // IDC_HAND
        : 32512; // IDC_ARROW

    // ---- ISLAND_PERF=1 logs, every 3 s: ticks and frames drawn per second, and where a tick's time goes.
    // ISLAND_BENCH=1: a fixed state for measuring (never auto-retracts or hides for other windows).
    static readonly bool Bench = Environment.GetEnvironmentVariable("ISLAND_BENCH") == "1";
    static readonly bool Perf = Environment.GetEnvironmentVariable("ISLAND_PERF") == "1";
    static double _perfAt, _pUpd, _pRen, _pPres, _pBusy; static int _pFrames, _pDrawn, _pLate; static long _pBusyFrom;
    static void PerfSample(double now, long t0, long t1, long t2)
    {
        double f = 1000.0 / Stopwatch.Frequency;
        _pUpd += (t1 - t0) * f; _pRen += (t2 - t1) * f; _pFrames++;
        // the slowest tick, and how many took most of a 144 Hz refresh (a frame that late is shown a refresh later)
        double work = (t2 - t0) * f;
        if (work > _pWorst) _pWorst = work;
        if (work > 6) _pSlow++;
        if (Gpu.LastPresentMs >= 0) { _pPres += Gpu.LastPresentMs; _pDrawn++; Gpu.LastPresentMs = -1; }
        if (now - _perfAt < 3) return;
        double s = now - _perfAt;
        int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2); var gp = GC.GetTotalPauseDuration();
        Log($"perf clock={(_model.Clock - _pClock) / s:0.00}x ticks={_pFrames / s:0}/s drawn={_pDrawn / s:0}/s busy={_pBusy / s:0.0}ms/s late={_pLate} worst={_pWorst:0.0}ms slow={_pSlow} " +
            $"gc={g0 - _pGc0}/{g2 - _pGc2} pause={(gp - _pPause).TotalMilliseconds:0.0}ms alloc={(GC.GetTotalAllocatedBytes(false) - _pAlloc) / s / 1024:0}KB/s " +
            $"update={_pUpd / _pFrames:0.000}ms render={(_pRen - _pPres) / _pFrames:0.000}ms present={_pPres / _pFrames:0.000}ms " +
            $"(probe={FrameRenderer.ProbeMs / _pFrames:0.000} draw={FrameRenderer.DrawMs / _pFrames:0.000} flush={FrameRenderer.FlushMs / _pFrames:0.000}) " +
            $"regions={(double)FrameRenderer.Regions / Math.Max(1, _pDrawn):0.0} verify={FrameRenderer.VerifyBad}/{FrameRenderer.VerifyFrames} mode={_model.Mode}");
        if (Canvas.Stats)
        {
            foreach (var kv in Canvas.StatCounts.OrderByDescending(k => k.Value).Take(12)) Log($"   changed {kv.Value,4}x {kv.Key}");
            Canvas.StatCounts.Clear();
        }
        Prof.Report(Log, _pFrames);
        FrameRenderer.ProbeMs = FrameRenderer.DrawMs = FrameRenderer.FlushMs = 0; FrameRenderer.Regions = 0;
        _pClock = _model.Clock;
        _perfAt = now; _pUpd = _pRen = _pPres = _pBusy = 0; _pFrames = _pDrawn = _pLate = 0;
        _pWorst = 0; _pSlow = 0; (_pGc0, _pGc2, _pPause, _pAlloc) = (g0, g2, gp, GC.GetTotalAllocatedBytes(false));
    }
    static double _pClock;   // (the island's clock at the last report: it must run at 1.00x of real time)
    static double _pWorst; static int _pSlow, _pGc0, _pGc2; static TimeSpan _pPause; static long _pAlloc;
    static string? _demo = Environment.GetEnvironmentVariable("ISLAND_DEMO");
    static (string Id, double At)? _demoToggle;
    static readonly bool DemoMode = Environment.GetEnvironmentVariable("ISLAND_DEMO") != null;
    static readonly List<(double At, Action Do)> _demoPlan = new();

    static void RunDemo(string demo)
    {
        var p = demo.Split(':');
        if (p[0] == "focus" && p.Length > 1 && int.TryParse(p[1], out int n))
        {
            _model.OpenTeam(_model.Team.Live.ElementAtOrDefault(n));
            if (p.Length > 2) _model.Team.Act(_model, ["atab", p[2]]);
        }
        else if (p[0] == "tab" && p.Length > 1) DemoTab(p[1]);
        else if (p[0] == "script")
        {
            // a timed tour, for filming: ISLAND_DEMO="script:0=tab servers;2.5=tab usage;5=team;7=focus 0;9=atab plan;11=rest"
            double t = _model.Clock;
            foreach (var step in demo[(demo.IndexOf(':') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = step.IndexOf('=');
                if (eq < 0 || !double.TryParse(step[..eq], System.Globalization.CultureInfo.InvariantCulture, out double at)) continue;
                string[] a = step[(eq + 1)..].Split(' ', 2);
                string arg = a.Length > 1 ? a[1] : "";
                _demoPlan.Add((t + at, () =>
                {
                    switch (a[0])
                    {
                        case "tab": DemoTab(arg); break;
                        case "team": _model.OpenTeam(); break;
                        case "focus": _model.OpenTeam(_model.Team.Live.ElementAtOrDefault(int.TryParse(arg, out int k) ? k : 0)); break;
                        case "atab": _model.Team.Act(_model, ["atab", arg]); break;
                        case "new": _model.OpenTeam(); _model.Team.SetView(TeamView.New); _model.Team.Task.Set(arg); _model.FocusField(_model.Team.Task); break;
                        case "rest": _model.FocusField(null); _model.SetMode(Mode.Dormant); break;
                        case "act": _model.Act(arg); break;
                        case "pose": if (_model.Mode != Mode.Agents) _model.OpenTeam(); _model.Team.DemoPose(_model, arg); break;   // (showcase: a pretend agent on stage, in one of its states)
                        case "catch": _model.DragEntered(_demoHeld = DemoPayload("drop", arg)); break;
                        case "droplink": _model.DragEntered(_demoHeld = DemoPayload("droplink", arg)); break;
                        case "leave": _model.DragLeft(); break;
                        case "land": if (_demoHeld is { } held) _model.DropLanded(held, new Vector2(IslandModel.CX + 150, _model.Top + 150)); break;
                        case "ask": _model.Team.NewAgentWith(_model, arg.Split('|').ToList()); break;
                    }
                }));
            }
        }
        else if (p[0] == "celebrate") _model.Team.CelebrateDemo(_model);
        else if (p[0] == "edit")
        {
            _model.SetMode(Mode.Full); _model.Tabs.SetEditing(_model, true);
            // "edit:usage": then unpin that tab (Bloub shrugs) and pin it back (he winks), on a loop
            if (p.Length > 1) _demoToggle = (p[1], _model.Clock + 1.6);
        }
        else if (p[0] == "make")
        {
            // "make:<what to keep an eye on>": the library, New tab, Bloub types it and builds it, then adds it
            _model.SetMode(Mode.Full); _model.Tabs.SetEditing(_model, true);
            double t = _model.Clock;
            string text = p.Length > 1 ? string.Join(":", p[1..]) : "bitcoin price with a chart";
            _demoPlan.Add((t + 1.6, () => _model.Act("mk:new")));
            _demoPlan.Add((t + 3.4, () => _model.Maker.Demo(_model, text)));
            _demoPlan.Add((t + 16, () => _model.Act("mk:add")));
        }
        else if (p[0] == "change")
        {
            // builds a tab, then asks for a change to it: ISLAND_DEMO="change:make it green"
            _model.SetMode(Mode.Full); _model.Tabs.SetEditing(_model, true);
            double t = _model.Clock;
            string text = p.Length > 1 ? string.Join(":", p[1..]) : "make it green";
            _demoPlan.Add((t + 0.25, () => _model.Act("mk:new")));
            _demoPlan.Add((t + 2.4, () => _model.Maker.Demo(_model, "bitcoin price with a chart")));
            _demoPlan.Add((t + 50, () => _model.Act("mk:change")));
            _demoPlan.Add((t + 52, () => _model.Maker.Demo(_model, text)));
        }
        else if (p[0] == "check")
        {
            // the "Checking your limits..." toast on the Usage tab: ISLAND_DEMO=check
            _model.SetMode(Mode.Full); _model.Tabs.Select(_model, "usage");
            _demoPlan.Add((_model.Clock + 1.0, () => _model.Act("limits")));
        }
        else if (p[0] == "intro") _replayWelcome = true;
        // the film states without the mouse: ISLAND_DEMO=greet (he has just landed and says hello), ISLAND_DEMO="file:Holiday.mp4"
        else if (p[0] == "greet") _model.WelcomeLanded();
        else if (p[0] == "done") _model.Team.DoneDemo(_model);
        else if (p[0] == "banner")
        {
            // the banner a session's news shows in the island (banner = done, banner:needs = needs you), without the flight
            bool needs = p.Length > 1 && p[1] == "needs";
            _demoPlan.Add((_model.Clock + 1.2, () =>
            {
                var a = _model.Team.Live.FirstOrDefault(x => needs == (x.Status == AgentStatus.NeedsYou)) ?? _model.Team.Live.FirstOrDefault();
                if (a != null) _model.AgentAlert(a, needs ? $"{a.Name} needs you" : $"{a.Name} is done", needs ? a.Activity : "Stopped both local servers because your PC was running low on memory");
            }));
        }
        else if (p[0] == "finish") _model.Team.FinishDemo(_model);
        else if (p[0] == "file") _model.FileDropped(p.Length > 1 ? string.Join(":", p[1..]) : "Holiday film.mp4");
        // giving him a file without the mouse: "catch:<path>" holds it over him, "drop:<path>|<path>" lets it go a second later,
        // "droplink:<url>", "droptext:<words>", "pocket" opens the Pocket tab, "ask:<path>" the New agent form with it attached
        else if (p[0] is "catch" or "drop" or "droplink" or "droptext")
        {
            var what = DemoPayload(p[0], string.Join(":", p[1..]));
            _model.DragEntered(what);
            if (p[0] != "catch") _demoPlan.Add((_model.Clock + 1.1, () => _model.DropLanded(what, new Vector2(IslandModel.CX + 150, _model.Top + 170))));
        }
        else if (p[0] == "pocket") { _model.SetMode(Mode.Full); _model.Act("tab:pocket"); }
        // the same through Windows' own drag-and-drop interface (no mouse): "oledrop:<path>" enters and drops, "oleleave:<path>" enters and leaves
        else if (p[0] is "oledrop" or "oleleave")
        {
            string path = string.Join(":", p[1..]);
            DragDrop.Test(path, 0);
            _demoPlan.Add((_model.Clock + 1.1, () => DragDrop.Test(path, p[0] == "oledrop" ? 2 : 1)));
        }
        else if (p[0] == "dragout") { string path = string.Join(":", p[1..]); _demoPlan.Add((_model.Clock + 1.0, () => _dryOut = path)); }
        else if (p[0] == "ask") _model.Team.NewAgentWith(_model, string.Join(":", p[1..]).Split('|').ToList());
        else if (p[0] == "stages")
        {
            // leak check: ISLAND_DEMO="stages:12" plays 12 celebrations back to back ("stages:4:intro": the intro),
            // writes the process's threads and memory after each to %TEMP%\island-stages.log, then quits.
            // A stage that leaves its graphics device behind adds about ten driver threads each time.
            _stagesLeft = p.Length > 1 && int.TryParse(p[1], out int count) ? count : 8;
            _stagesIntro = p.Length > 2 && p[2] == "intro";
        }
        else if (p[0] == "idle") { } // nothing: the island as it rests (no intro), for measuring
        else if (p[0] == "new")
        {
            // the New agent form, optionally with a task typed in: ISLAND_DEMO="new:fix the flaky login test"
            _model.OpenTeam(); _model.Team.SetView(TeamView.New);
            if (p.Length > 1) { _model.Team.Task.Set(string.Join(":", p[1..])); _model.FocusField(_model.Team.Task); }
        }
        else _model.OpenTeam();
    }

    // Dev: ISLAND_CURSOR="x,y" or "x,y@seconds" puts the cursor at that point of the island's window (DIPs), to look at hover states without the mouse.
    static readonly (Vector2 P, double At)? _fakeCursor = ParseCursor(Environment.GetEnvironmentVariable("ISLAND_CURSOR"));
    static (Vector2, double)? ParseCursor(string? v)
    {
        if (v == null) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string[] at = v.Split('@'), xy = at[0].Split(',');
        return xy.Length == 2 && float.TryParse(xy[0], inv, out float x) && float.TryParse(xy[1], inv, out float y)
            ? (new Vector2(x, y), at.Length > 1 && double.TryParse(at[1], inv, out double t) ? t : 0) : null;
    }

    static DropPayload? _demoHeld;
    static DropPayload DemoPayload(string kind, string arg) => kind switch
    {
        "droplink" => new DropPayload([], arg, null),
        "droptext" => new DropPayload([], null, arg),
        _ => new DropPayload(arg.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), null, null),
    };

    static int _stagesLeft = -1, _stagesPlayed;
    static bool _stagesIntro;
    static double _stageRestUntil;

    /// <summary>ISLAND_DEMO=stages: between two stages, notes what the process holds and starts the next. True when all are played.</summary>
    static bool StageCheck(double now)
    {
        if (_stageRestUntil == 0) { _stageRestUntil = now + 0.6; return false; } // the driver's threads take a moment to leave
        if (now < _stageRestUntil) return false;
        _stageRestUntil = 0;
        // what is only waiting for the garbage collector is not a leak: collect first, then count
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var me = Process.GetCurrentProcess();
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "island-stages.log"),
            $"{DateTime.Now:HH:mm:ss} stages={_stagesPlayed} threads={me.Threads.Count} handles={me.HandleCount} private={me.PrivateMemorySize64 >> 20}MB{Environment.NewLine}");
        if (_stagesLeft == 0) { SetReserved(false); _model.Team.Dispose(); _tray?.Dispose(); return true; }
        _stagesLeft--; _stagesPlayed++;
        if (_stagesIntro) _replayWelcome = true; else _model.Team.CelebrateDemo(_model);
        return false;
    }

    static void DemoTab(string id)
    {
        _model.SetMode(Mode.Full);
        // the demo pins a tab that isn't in your bar yet, so it shows up selected
        if (Showcase.On && TabBar.All.Any(d => d.Id == id) && !_model.Tabs.Enabled.Contains(id)) _model.Tabs.Toggle(_model, id);
        _model.Act("tab:" + id);
    }

    // ---- keyboard: the island normally never takes focus; it does while you type into one of its fields.
    static nint _prevForeground;
    static bool _wantFocus;
    static double _nextFocusTry;

    static void KeyboardFocus(double now)
    {
        bool want = _model.Focused != null;
        var fg = GetForegroundWindow();
        // (while Bloub builds a tab the island keeps your keys for Enter / Esc, but never takes them back from another window)
        bool grab = want && !DemoMode && !(_model.Focused == _model.Maker.Field && _model.Maker.Now != TabMaker.Stage.Line);
        if (grab && fg != _hwnd && now >= _nextFocusTry)
        {
            if (!_wantFocus) _prevForeground = fg;
            _nextFocusTry = now + 0.5;
            keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); // lets a background process take the foreground
            SetForegroundWindow(_hwnd);
        }
        if (!want && _wantFocus && fg == _hwnd && _prevForeground != 0 && IsWindow(_prevForeground) && !FolderPick.Open)   // (not while the folder window is coming up: it takes the keyboard)
            SetForegroundWindow(_prevForeground); // hand the keyboard back to where you were
        _wantFocus = want;
    }

    static void OnChar(char ch)
    {
        var f = _model.Focused;
        if (f == null) return;
        if (f == _model.Maker.Field && _model.Maker.Key(_model, ch)) return;
        bool ctrl = GetKeyState(0x11) < 0;
        switch (ch)
        {
            case (char)8: f.Back(); break;
            case (char)127: f.Back(word: true); break;           // Ctrl+Backspace
            case (char)22:                                      // Ctrl+V
                if (Shell.GetClipboard(_hwnd) is { } clip) f.Insert(clip);
                break;
            case (char)13: _model.Team.Submit(_model, f); break;
            case (char)27: if (f == _model.Tabs.TaskField) f.Clear(); _model.FocusField(null); break;
            default:
                if (ch >= ' ' && !ctrl) f.Insert(ch.ToString());
                break;
        }
        f.ChangedAt = _model.Clock;
    }

    static bool OnKey(int vk)
    {
        var f = _model.Focused;
        if (f == null) return false;
        if (f == _model.Maker.Field && (_model.Maker.Now != TabMaker.Stage.Line || _model.Maker.Typing)) return true;
        bool ctrl = GetKeyState(0x11) < 0;
        switch (vk)
        {
            case 0x25: f.Left(ctrl); break;
            case 0x27: f.Right(ctrl); break;
            case 0x24: f.Home(); break;
            case 0x23: f.End(); break;
            case 0x2E: f.Delete(); break;
            default: return false;
        }
        f.ChangedAt = _model.Clock;
        return true;
    }
    static bool _replayWelcome;
    static int _stageShot;
    static readonly (double[] Times, string Prefix)? _stageShots = Environment.GetEnvironmentVariable("ISLAND_STAGESHOT") is { } v && v.IndexOf(':') is > 0 and var cut
        ? (v[..cut].Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray(), v[(cut + 1)..]) : null;

    static nint CreateOverlay(string title)
    {
        int ex = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT;
        var h = CreateWindowExW(ex, "IslandLabWindow", title, WS_POPUP, 0, 0, 1, 1, 0, 0, GetModuleHandleW(null), 0);
        SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
        return h;
    }

    /// <summary>Full-screen, click-through welcome window above everything; Bloub then flies into the island.</summary>
    static void StartWelcome(RECT mon)
    {
        if (_model.Mode != Mode.Dormant) _model.SetMode(Mode.Dormant);
        StartStage(mon, g => new Welcome(g), () => _model.WelcomeLanded());
    }

    /// <summary>Opens a full-screen click-through stage where Bloub leaves the island for a moment.</summary>
    static void StartStage(RECT mon, Func<Gpu, StageScene> make, Action landed)
    {
        var hwnd = CreateOverlay("Island Stage");
        int w = mon.Right - mon.Left, h = mon.Bottom - mon.Top;
        SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, w, h, SWP_NOACTIVATE);
        // every stage draws on the same device, made at the first one and kept (a driver reset replaces it)
        if (_stageDevice is { Lost: true }) { _stageDevice.Dispose(); _stageDevice = null; }
        _stageDevice ??= new GpuDevice();
        var gpu = new Gpu(_stageDevice, hwnd, w, h, _scale);
        var scene = make(gpu);
        // The island's Bloub anchor, converted to the stage window's DIPs.
        scene.Target = () => (new Vector2((_winX - mon.Left) / _scale + _model.Face.X.Value, (_winY - mon.Top) / _scale + _model.Face.Y.Value),
                              11f * _model.Face.Size.Value);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        _model.Welcoming = true; // Bloub is out of the island
        _model.Face.Opacity.Snap(0);
        _welcome = (scene, gpu, hwnd, landed);
        Diag.Log($"stage start {scene.GetType().Name}");
    }

    static void EndWelcome()
    {
        if (_welcome is not { } w) return;
        _welcome = null;
        w.scene.Dispose();
        w.gpu.Dispose();
        DestroyWindow(w.hwnd);
        Diag.Log("welcome end");
    }

    /// <summary>
    /// Reserved-bar mode: registers the island as a top AppBar, so Windows shrinks the work area and
    /// maximized windows (Chrome tabs etc.) start below the island instead of under it.
    /// </summary>
    static void SetReserved(bool on)
    {
        if (on == _model.ReserveMode) return;
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uEdge = ABE_TOP };
        if (on)
        {
            _model.RetractMode = false;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromWindow(_hwnd, 1), ref mi);
            SHAppBarMessage(ABM_NEW, ref abd);
            abd.rc = mi.rcMonitor;
            abd.rc.Bottom = abd.rc.Top + (int)MathF.Round(IslandModel.ReservedHeight * _scale);
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            abd.rc.Bottom = abd.rc.Top + (int)MathF.Round(IslandModel.ReservedHeight * _scale);
            SHAppBarMessage(ABM_SETPOS, ref abd);
        }
        else SHAppBarMessage(ABM_REMOVE, ref abd);
        _model.ReserveMode = on;
    }

    /// <summary>The foreground window reaches the top of the screen right under the island.</summary>
    static bool WindowUnderIsland()
    {
        var fg = GetForegroundWindow();
        if (fg == 0 || fg == _hwnd || fg == GetShellWindow() || !IsWindowVisible(fg) || IsIconic(fg)) return false;
        if (ShellSurface(ClassName(fg))) return false;
        GetWindowRect(fg, out var r);
        int cx = _winX + (int)(IslandModel.CX * _scale), reach = (int)(110 * _scale);
        return r.Top <= _winY + (int)(46 * _scale) && r.Bottom > _winY + (int)(46 * _scale) &&
               r.Left < cx + reach && r.Right > cx - reach;
    }

    static bool HitTest(Vector2 p, float grow = 0)
    {
        var (w, h, r) = _model.Live;
        float dx = MathF.Abs(p.X - IslandModel.CX);
        // Folded: only the white line itself (or the little pill while Bloub peeks) opens the island, not the area
        // around it. It used to be a 200 x 14 px strip that grew to 34 px whenever the mouse came near, so
        // reaching for a browser tab under it opened the island every time.
        if (_model.Retracted) return p.Y >= -2 && p.Y <= _model.Top + h + 1.5f && dx <= w / 2 + 1.5f;
        // Open: the space between the screen edge and the island counts too, so hover never drops
        // out when moving up to the very top (no flapping between folded and open).
        if (p.Y >= -2 && p.Y < _model.Top + h / 2 && dx < w / 2) return true;
        r = MathF.Min(r, MathF.Min(w, h) / 2);
        var q = new Vector2(MathF.Abs(p.X - IslandModel.CX), MathF.Abs(p.Y - (_model.Top + h / 2)))
                - new Vector2(w / 2 - r, h / 2 - r);
        float d = Vector2.Max(q, Vector2.Zero).Length() + MathF.Min(MathF.Max(q.X, q.Y), 0) - r;
        if (d <= 1.5f + grow) return true;
        if (_model.Bubble.Value > 0.5f)
        {
            var (c, br) = FrameRenderer.BubbleGeometry(_model);
            if (Vector2.Distance(p, c) <= br + 1.5f) return true;
        }
        return false;
    }

    static void SetClickThrough(bool on)
    {
        if (on == _clickThrough) return;
        _clickThrough = on;
        long ex = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (nint)ex);
    }

    /// <summary>A game/video is fullscreen: Windows says so, or the foreground window covers the whole monitor.</summary>
    static string _lastFullscreenReason = "";
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "islandlab.log");
    static void Log(string s) => Diag.Write(s);

    static bool FullscreenAppActive()
    {
        var fg = GetForegroundWindow();
        var cls = fg != 0 ? ClassName(fg) : "";
        // Windows' own surfaces cover the whole screen too (Start, Search, the notification centre, Task View,
        // Alt+Tab): the island stays for those. It used to vanish every time the Start menu opened.
        if (ShellSurface(cls)) return false;
        if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4)
        { _lastFullscreenReason = $"quns={state}"; return true; }
        if (fg == 0 || fg == _hwnd || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        GetWindowRect(fg, out var r);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(fg, 2), ref mi);
        _lastFullscreenReason = $"fg={cls} rect={r.Left},{r.Top},{r.Right},{r.Bottom}";
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>A window that belongs to Windows itself, not to an app or a game.</summary>
    static bool ShellSurface(string cls) => cls is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
        or "Windows.UI.Core.CoreWindow"            // Start, Search, the notification centre, the volume / network flyouts
        or "MultitaskingViewFrame" or "TaskSwitcherWnd" or "XamlExplorerHostIslandWindow" // Task View, Alt+Tab
        or "ForegroundStaging" or "ApplicationManager_ImmersiveShellWindow" or "ImmersiveLauncher"
        or "TopLevelWindowForOverflowXamlIsland" or "Xaml_WindowedPopupClass" or "Shell_InputSwitchTopLevelWindow"
        or "LockScreenBackstopFrame" or "SnipOverlayRootWindow";

    /// <summary>Tray icon (and second launches): open the island on a screen, toggle autostart, quit.</summary>
    static void OnTray(Tray.Command cmd)
    {
        if (cmd is Tray.Command.Open or Tray.Command.Agents or Tray.Command.Usage && _model.Hidden)
        { _model.Hidden = false; _model.Visibility.To(1); }
        switch (cmd)
        {
            case Tray.Command.Open: _model.SetMode(Mode.Full); break;
            case Tray.Command.Agents: _model.OpenTeam(); break;
            case Tray.Command.Usage: _model.SetMode(Mode.Full); _model.Act("tab:usage"); break;
            case Tray.Command.Startup:
                Tray.StartsWithWindows = !Tray.StartsWithWindows;
                _model.ShowInfo(Tray.StartsWithWindows ? "Starts with Windows" : "Won't start with Windows",
                    Tray.StartsWithWindows ? "I'll be here every time you log in" : "Open me from the Start menu when you want me");
                break;
            case Tray.Command.Quit: PostQuitMessage(0); break;
            case Tray.Command.Preview: _model.Team.DoneDemo(_model); break;
            case Tray.Command.Intro: _replayWelcome = true; break;
            case Tray.Command.File: _model.FileDropped("Holiday film.mp4"); break;
        }
    }

    static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (hwnd == _hwnd && _tray != null && (msg == Tray.WM_TRAY || msg >= 0xC000))
        {
            var cmd = _tray.Handle(msg, lParam);
            if (cmd != Tray.Command.None) OnTray(cmd);
            if (msg == Tray.WM_TRAY) return 0;
        }
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_SETCURSOR:
                _shownCursor = CursorId();
                SetCursor(LoadCursorW(0, _shownCursor));
                return 1;
            case WM_LBUTTONDOWN:
                _model.PressDown();
                return 0;
            case WM_LBUTTONUP:
                _model.PressUp();
                _model.Click(_cursor);
                return 0;
            case WM_RBUTTONUP:
                _model.SetMode(Mode.Dormant);
                return 0;
            case WM_HOTKEY:
                OnHotkey((int)wParam);
                return 0;
            case WM_APP_SHOW when hwnd == _hwnd:
                OnTray(Tray.Command.Open);
                return 0;
            case WM_APP_DONE when hwnd == _hwnd:
                _model.Team.DoneDemo(_model);
                return 0;
            case WM_APP_FILE when hwnd == _hwnd:
                OnTray(Tray.Command.File);
                return 0;
            case WM_CHAR when hwnd == _hwnd:
                OnChar((char)wParam);
                return 0;
            case WM_KEYDOWN when hwnd == _hwnd:
                if (OnKey((int)wParam)) return 0;
                break;
            case WM_ACTIVATE when hwnd == _hwnd:
                if ((wParam & 0xFFFF) == 0 && _model.Focused != null) { _wantFocus = false; _model.FocusField(null); } // clicked elsewhere
                break;
            case WM_MOUSEWHEEL when hwnd == _hwnd:
                float wheel = (short)((wParam >> 16) & 0xFFFF) / 120f * 40;
                if (_model.Mode == Mode.Full && !_model.Tabs.Editing && _model.Tabs.Selected == "pocket") _model.Pocket.Wheel(wheel);
                else if (_model.Mode != Mode.Full || _model.Tabs.Editing || !_model.Tabs.Wheel(wheel)) _model.Team.Wheel(wheel);
                return 0;
            case WM_DESTROY:
                if (hwnd == _hwnd) PostQuitMessage(0);
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    static void OnHotkey(int id)
    {
        switch (id)
        {
            case 1: _model.SetMode(Mode.Dormant); break;
            case 2: _model.SetMode(Mode.Media); break;
            case 3: _model.SetMode(Mode.Split); break;
            case 4: _model.ShowAlert(AlertKind.ClaudePermission); break;
            case 5: _model.SetMode(Mode.Expanded); break;
            case 6: _model.SetMode(Mode.Full); break;
            case 7: _model.SetMode(Mode.Command); break;
            case 8: _model.SetMode(Mode.Focus); break;
            case 9: _model.SetMode(Mode.Gallery); break;
            case 0: _model.TimeScale = _model.TimeScale < 1 ? 1 : 0.2f; break;
            case 100: PostQuitMessage(0); break;
            case 103: _replayWelcome = true; break;
            case 104: _model.SetMode(Mode.Agents); break;
            case 105: _model.Team.DoneDemo(_model); break;             // (Ctrl+Alt+D: what a finished session now plays)
            case 106: _model.FileDropped("Holiday film.mp4"); break; // preview the file animation
            case 107: _model.Team.DoneDemo(_model); break;             // "done", shown in the island itself: the film's Finished
            case 101:
                SetReserved(!_model.ReserveMode);
                _model.ShowInfo(_model.ReserveMode ? "Reserved bar" : "Floating",
                    _model.ReserveMode ? "Apps now start below the island — tabs are never covered"
                                       : "No space reserved — the island floats over windows");
                break;
            case 102:
                _model.RetractMode = !_model.RetractMode;
                if (_model.RetractMode) SetReserved(false);
                _model.ShowInfo(_model.RetractMode ? "Auto-retract" : "Floating",
                    _model.RetractMode ? "Over a window I shrink into a line — hover the line to open me"
                                       : "No space reserved — the island floats over windows");
                break;
        }
    }
}
