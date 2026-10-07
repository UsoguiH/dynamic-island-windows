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
        // One island at a time: launching it again (shortcut, Start menu) just opens the running one.
        _single = new Mutex(true, "IslandLab.SingleInstance", out bool first);
        if (!first)
        {
            var other = FindWindowW("IslandLabWindow", "Island");
            if (other != 0) PostMessageW(other, WM_APP_SHOW, 0, 0);
            return;
        }
        timeBeginPeriod(1);
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

        _scale = GetDpiForWindow(_hwnd) / 96f;
        int pw = (int)MathF.Round(LogicalW * _scale), ph = (int)MathF.Round(LogicalH * _scale);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(_hwnd, 1 /* primary */), ref mi);
        _winX = mi.rcMonitor.Left + (mi.rcMonitor.Right - mi.rcMonitor.Left - pw) / 2;
        _winY = mi.rcMonitor.Top;
        SetWindowPos(_hwnd, HWND_TOPMOST, _winX, _winY, pw, ph, SWP_NOACTIVATE);

        using var gpu = new Gpu(_hwnd, pw, ph, _scale);
        using var renderer = new FrameRenderer(gpu);
        _model = new IslandModel { Hwnd = _hwnd };
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        _tray = new Tray(_hwnd);

        for (uint i = 0; i <= 9; i++) RegisterHotKey(_hwnd, (int)i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x30 + i);
        RegisterHotKey(_hwnd, 100, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'Q');
        RegisterHotKey(_hwnd, 101, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'B');
        RegisterHotKey(_hwnd, 103, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'W');
        RegisterHotKey(_hwnd, 104, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'A');
        RegisterHotKey(_hwnd, 105, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'D'); // preview the "done" celebration
        // Auto-retract: first free key wins (Ctrl+Alt+R is often taken by other apps).
        foreach (char k in "HRJ")
            if (RegisterHotKey(_hwnd, 102, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, k)) { _retractKey = k; break; }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { SetReserved(false); _model.Team.Dispose(); _tray?.Dispose(); };

        var clock = Stopwatch.StartNew();
        double last = 0, nextCheck = 0, nextTopmost = 0;
        bool welcomed = false;

        while (true)
        {
            while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) { SetReserved(false); _model.Team.Dispose(); _tray?.Dispose(); return; }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            double now = clock.Elapsed.TotalSeconds;
            if (now - last > 0.04 && last > 0) Diag.Log($"hitch {(now - last) * 1000:0}ms mode={_model.Mode} layers={_model.Layers.Count}");
            float dt = (float)Math.Min(now - last, 0.05) * _model.TimeScale;
            last = now;

            if (!welcomed && now > 0.35) { welcomed = true; if (_demo == null) StartWelcome(mi.rcMonitor); }
            // Dev: ISLAND_DEMO="focus:<agent#>[:plan|files]" or "team" opens a screen without touching the mouse.
            if (_demo != null && now > 2.5) { RunDemo(_demo); _demo = null; }

            if (now >= nextCheck)
            {
                nextCheck = now + 0.25;
                _model.WindowAtTop = WindowUnderIsland();
                if (now >= nextTopmost) { nextTopmost = now + 2; SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
                // Our own full-screen welcome makes Windows report "busy": ignore that while it plays.
                bool fullscreen = _welcome == null && FullscreenAppActive();
                if (fullscreen != _model.Hidden)
                {
                    Log($"hidden={fullscreen} reason={_lastFullscreenReason}");
                    _model.Hidden = fullscreen;
                    _model.Visibility.To(fullscreen ? 0 : 1);
                }
            }

            GetCursorPos(out var p);
            _cursor = new Vector2((p.X - _winX) / _scale, (p.Y - _winY) / _scale);
            bool inside = !_model.Hidden && HitTest(_cursor);
            SetClickThrough(!inside);

            _model.Update(dt, _cursor, inside);
            KeyboardFocus(now);
            renderer.Render(_model); // Present(1) waits for vsync → runs at the monitor's 144 Hz
            if (_replayWelcome) { _replayWelcome = false; EndWelcome(); StartWelcome(mi.rcMonitor); }
            // A session finished: Bloub comes out to tell you (only when nothing else is on stage).
            if (_welcome == null && _model.PendingCelebration is { } pc)
            {
                _model.PendingCelebration = null;
                if (!_model.Hidden)
                    StartStage(mi.rcMonitor, g => new Celebration(g, pc.Agent.Name), () => _model.CelebrationLanded(pc.Agent, pc.Title, pc.Text));
                else _model.AgentAlert(pc.Agent, pc.Title, pc.Text);
            }
            if (_welcome != null)
            {
                var (w, _, _, landed) = _welcome.Value;
                w.Frame(dt, new Vector2((p.X - mi.rcMonitor.Left) / _scale, (p.Y - mi.rcMonitor.Top) / _scale));
                if (w.Landed) { EndWelcome(); landed(); }
            }
        }
    }

    static (StageScene scene, Gpu gpu, nint hwnd, Action landed)? _welcome;
    static string? _demo = Environment.GetEnvironmentVariable("ISLAND_DEMO");

    static void RunDemo(string demo)
    {
        var p = demo.Split(':');
        if (p[0] == "focus" && p.Length > 1 && int.TryParse(p[1], out int n))
        {
            _model.OpenTeam(_model.Team.Live.ElementAtOrDefault(n));
            if (p.Length > 2) _model.Team.Act(_model, ["atab", p[2]]);
        }
        else if (p[0] == "tab" && p.Length > 1) { _model.SetMode(Mode.Full); _model.Act("tab:" + p[1]); }
        else if (p[0] == "celebrate") _model.Team.CelebrateDemo(_model);
        else if (p[0] == "intro") _replayWelcome = true;
        else if (p[0] == "list") { _model.OpenTeam(); _model.Team.SetView(TeamView.List); }
        else if (p[0] == "new") { _model.OpenTeam(); _model.Team.SetView(TeamView.New); }
        else _model.OpenTeam();
    }

    // ---- keyboard: the island normally never takes focus; it does while you type into one of its fields.
    static nint _prevForeground;
    static bool _wantFocus;
    static double _nextFocusTry;

    static void KeyboardFocus(double now)
    {
        bool want = _model.Focused != null;
        var fg = GetForegroundWindow();
        if (want && fg != _hwnd && now >= _nextFocusTry)
        {
            if (!_wantFocus) _prevForeground = fg;
            _nextFocusTry = now + 0.5;
            keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); // lets a background process take the foreground
            SetForegroundWindow(_hwnd);
        }
        if (!want && _wantFocus && fg == _hwnd && _prevForeground != 0 && IsWindow(_prevForeground))
            SetForegroundWindow(_prevForeground); // hand the keyboard back to where you were
        _wantFocus = want;
    }

    static void OnChar(char ch)
    {
        var f = _model.Focused;
        if (f == null) return;
        bool ctrl = GetKeyState(0x11) < 0;
        switch (ch)
        {
            case (char)8: f.Back(); break;
            case (char)127: f.Back(word: true); break;           // Ctrl+Backspace
            case (char)22:                                      // Ctrl+V
                if (Shell.GetClipboard(_hwnd) is { } clip) f.Insert(clip);
                break;
            case (char)13: _model.Team.Submit(_model, f); break;
            case (char)27: _model.FocusField(null); break;
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
        var gpu = new Gpu(hwnd, w, h, _scale);
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
        if (ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        GetWindowRect(fg, out var r);
        int cx = _winX + (int)(IslandModel.CX * _scale), reach = (int)(110 * _scale);
        return r.Top <= _winY + (int)(46 * _scale) && r.Bottom > _winY + (int)(46 * _scale) &&
               r.Left < cx + reach && r.Right > cx - reach;
    }

    static bool HitTest(Vector2 p)
    {
        var (w, h, r) = _model.Live;
        // The retracted line is only 5 px tall: give it a forgiving hover zone along the screen edge.
        float dx = MathF.Abs(p.X - IslandModel.CX);
        // Folded: a forgiving strip along the screen edge (taller while Bloub peeks).
        if (_model.Retracted) return p.Y >= -2 && p.Y < (_model.Peeking ? 34 : 14) && dx < (_model.Peeking ? IslandModel.PeekW / 2 + 8 : 100);
        // Open: the space between the screen edge and the island counts too, so hover never drops
        // out when moving up to the very top (no flapping between folded and open).
        if (p.Y >= -2 && p.Y < _model.Top + h / 2 && dx < w / 2) return true;
        r = MathF.Min(r, MathF.Min(w, h) / 2);
        var q = new Vector2(MathF.Abs(p.X - IslandModel.CX), MathF.Abs(p.Y - (_model.Top + h / 2)))
                - new Vector2(w / 2 - r, h / 2 - r);
        float d = Vector2.Max(q, Vector2.Zero).Length() + MathF.Min(MathF.Max(q.X, q.Y), 0) - r;
        if (d <= 1.5f) return true;
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
    static void Log(string s) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}" + Environment.NewLine); } catch { } }

    static bool FullscreenAppActive()
    {
        if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4)
        { _lastFullscreenReason = $"quns={state}"; return true; }
        var fg = GetForegroundWindow();
        if (fg == 0 || fg == _hwnd || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        var cls = ClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        GetWindowRect(fg, out var r);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(fg, 2), ref mi);
        _lastFullscreenReason = $"fg={cls} rect={r.Left},{r.Top},{r.Right},{r.Bottom}";
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>Tray icon (and second launches): open the island on a screen, toggle autostart, quit.</summary>
    static void OnTray(Tray.Command cmd)
    {
        if (cmd is Tray.Command.Open or Tray.Command.Agents or Tray.Command.Usage && _model.Hidden)
        { _model.Hidden = false; _model.Visibility.To(1); }
        switch (cmd)
        {
            case Tray.Command.Open: _model.SetMode(Mode.Full); break;
            case Tray.Command.Agents: _model.OpenTeam(); break;
            case Tray.Command.Usage: _model.SetMode(Mode.Full); _model.Act("tab:2"); break;
            case Tray.Command.Startup:
                Tray.StartsWithWindows = !Tray.StartsWithWindows;
                _model.ShowInfo(Tray.StartsWithWindows ? "Starts with Windows" : "Won't start with Windows",
                    Tray.StartsWithWindows ? "I'll be here every time you log in" : "Open me from the Start menu when you want me");
                break;
            case Tray.Command.Quit: PostQuitMessage(0); break;
            case Tray.Command.Preview: _model.Team.CelebrateDemo(_model); break;
            case Tray.Command.Intro: _replayWelcome = true; break;
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
                // Hand over anything clickable, arrow elsewhere.
                SetCursor(LoadCursorW(0, _model.HoverHit != null || _model.Mode is Mode.Dormant or Mode.Media or Mode.Split or Mode.Focus ? 32649 : 32512));
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
                _model.Team.Wheel((short)((wParam >> 16) & 0xFFFF) / 120f * 40);
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
            case 105: _model.Team.CelebrateDemo(_model); break;
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
