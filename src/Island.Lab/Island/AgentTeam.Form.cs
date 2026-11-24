using System.Numerics;
using Island.Lab.Mascot;
using Island.Lab.Motion;
using Island.Lab.Platform;
using Island.Lab.Render;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Island.Lab.Render.Canvas;

namespace Island.Lab.Island;

// ---------------------------------------------------------------- new agent: one question at a time
//
//  (design/new-agent.html, version D "Ask")
//
//  ┌ (←) [Fix the login tests]                    • ▬ • ┐   what you answered rides along at the top: tap one to change it
//  │ (Bloub)  Where should it work?                     │   he asks; one question on screen at a time
//  │ ┌ D DynmicIsland   now ┐ ┌ D Duolingo      2h ┐    │   the seven you used last…
//  │ ┌ L logo            2w ┐ ┌ ▱ Other folder… Browse ┐ │   …and any other folder (Windows' own folder window)
//  │ Pick one, or Enter for DynmicIsland   ( Continue ↵ ) │   Enter goes on: three Enters and it is launched
//  └────────────────────────────────────────────────────┘
//
//  Launch: the page clears, he takes the middle of the island and splits in two; the new one wakes, its light
//  comes on in the agent's colour and it goes out through the top (SendOff). He hops home and tells you.

public sealed partial class AgentTeam
{
    public const float FormW = 500, FormH = 444, StageY = 196;
    const float FormPad = 22, FaceX = 54, FaceY = 112, FaceR = 30, BodyTop = 158, FootY = 378;
    const float TaskSize = 19, TaskLine = 26; const int TaskRows = 3;
    static readonly uint FormAccent = 0x0A84FF;
    static readonly string[] Asks = ["What should it do?", "Where should it work?", "How careful should it be?", "Ready when you are."];
    static readonly string[] ModeSays =
    [
        "Follows your terminal's rules. Its questions come to you here.",
        "Asks you before every edit and every command.",
        "Looks and answers. It changes nothing.",
        "Edits files without asking. Still asks before commands.",
    ];
    const char IcEdit = '\uE70F', IcSliders = '\uE9E9', IcAsk = '\uE8BD', IcEye = '\uE890', IcBolt = '\uE945';
    static readonly char[] ModeIcons = [IcSliders, IcAsk, IcEye, IcBolt];
    static readonly uint[] ProjectColors = [0xBF5AF2, 0x30D158, 0x0A84FF, 0xFF9F0A, 0x5AC8FA, 0xFF453A, 0xFFD60A, 0xFF5C9E];

    int _step;                         // 0 what, 1 where, 2 how careful, 3 ready; 4: he sends it off; 5: it is working
    int _stepWas = -1;                 // the answer that is sliding away
    float _stepT = 10;                 // seconds since this question was asked
    double _advanceAt = -1; int _advanceFrom;
    string? _other;                    // the folder chosen with "Other folder…"
    bool _picking;                     // Windows' folder window is open
    Agent? _sent;                      // the one that was just launched
    string _sentTask = "", _sentRule = "";
    double _sendAt = -1, _noAt = -10;
    float _sentRowW = 300;
    Vector2? _caret;                   // where the typing is (he looks at it)
    readonly List<uint> _recentColor = new();
    /// <summary>Keeps the keyboard while there is nothing to type, so that Enter goes on.</summary>
    public readonly TextField Keys = new("");
    /// <summary>The launch: he splits in two, and the new one goes.</summary>
    public readonly SendOff Parting = new();
    readonly Spring _tilt = new(0, new SpringSpec(0.5f, 0.55f), 0.002f), _formHop = new(0, new SpringSpec(0.38f, 0.45f), 0.01f);

    /// <summary>The island stays open by itself: the folder window is up, or he is sending the new one off.</summary>
    public bool KeepsOpen => _picking || Parting.Active || View == TeamView.New && _step == 4;

    /// <summary>Where Bloub sits in the form, from the island's top-left: beside the question, large.</summary>
    public (float X, float Y, float Size)? FormFace => View == TeamView.New ? (FaceX, FaceY, FaceR / 11f) : null;

    /// <summary>His head tilts with each question, and he hops when you choose.</summary>
    public Matrix3x2 FormXf(IslandModel m, Vector2 face) =>
        m.Mode == Mode.Agents && View == TeamView.New ? Matrix3x2.CreateRotation(_tilt.Value * MathF.PI / 180, face) * Matrix3x2.CreateTranslation(0, _formHop.Value) : Matrix3x2.Identity;

    /// <summary>His face for the question on screen.</summary>
    public BloubExpressionId? FormMood(IslandModel m) => View != TeamView.New ? null : _step switch
    {
        0 => m.Focused == Task && Task.Text.Length > 0 ? BloubExpressionId.Attentif : BloubExpressionId.Curieux,
        2 => BloubExpressionId.Mefiant,
        3 => BloubExpressionId.Excite,
        _ => BloubExpressionId.Neutre,
    };

    /// <summary>What he looks at instead of your pointer: the words as you type them.</summary>
    public Vector2? FormGaze(IslandModel m) =>
        m.Mode == Mode.Agents && View == TeamView.New && _step == 0 && _caret is { } at && Task.Text.Length > 0 && m.Clock - Task.ChangedAt < 0.9 ? at : null;

    static float Seg(float t, float a, float b) => Math.Clamp((t - a) / (b - a), 0, 1);
    /// <summary>A spring's way in: over the mark, and back (the design page's --spring).</summary>
    static float Sp(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - MathF.Exp(-7 * t) * MathF.Cos(11.5f * t);
    static Color4 Mix(Color4 a, uint b, float t) { var o = Rgba(b); return new(a.R * t + o.R * (1 - t), a.G * t + o.G * (1 - t), a.B * t + o.B * (1 - t), 1); }
    static bool SameFolder(string? a, string? b) => a != null && b != null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    static string ProjectName(string? folder) => folder == null ? "" : Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;

    static string Ago(DateTime? when)
    {
        if (when is not { } t) return "";
        var d = DateTime.UtcNow - t;
        return d.TotalMinutes < 20 ? "now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m" : d.TotalDays < 1 ? $"{(int)d.TotalHours}h" : d.TotalDays < 7 ? $"{(int)d.TotalDays}d"
            : d.TotalDays < 30 ? $"{(int)(d.TotalDays / 7)}w" : d.TotalDays < 365 ? $"{(int)(d.TotalDays / 30)}mo" : $"{(int)(d.TotalDays / 365)}y";
    }

    DateTime? Used(int i) => Showcase.On ? DateTime.UtcNow - TimeSpan.FromHours(i switch { 0 => 0, 1 => 2, 2 => 26, 3 => 100, _ => 340 })
        : Shell.ProjectUsed.TryGetValue(_recent[i], out var t) ? t : null;

    /// <summary>The form opens on its first question.</summary>
    void OpenForm()
    {
        _step = 0; _stepWas = -1; _stepT = 0.17f; _advanceAt = -1; _sendAt = -1; _sent = null;
        _tilt.To(-8);
        // each project keeps a colour of its own (two on screen never share one)
        _recentColor.Clear();
        foreach (var p in _recent)
        {
            int at = (int)(Hash(ProjectName(p).ToLowerInvariant()) % (uint)ProjectColors.Length);
            for (int k = 0; k < ProjectColors.Length && _recentColor.Contains(ProjectColors[at]); k++) at = (at + 1) % ProjectColors.Length;
            _recentColor.Add(ProjectColors[at]);
        }
        if (_other != null && _recent.Take(7).Any(r => SameFolder(r, _other))) _other = null;
    }

    void Go(IslandModel m, int step)
    {
        step = Math.Clamp(step, 0, 3);
        if (step == _step || _step >= 4) return;
        _stepWas = _step; _step = step; _stepT = 0; _advanceAt = -1;
        _tilt.To(step switch { 0 => -8, 1 => 7, 2 => -4, _ => 0 });
        _formHop.Velocity = step == 3 ? -190 : -130;
        m.FocusField(step == 0 ? Task : Keys);
    }

    /// <summary>Enter, or the button: on to the next question; on the last one, launch.</summary>
    void FormEnter(IslandModel m)
    {
        switch (_step)
        {
            case 0 when Task.Text.Trim().Length == 0: _noAt = _now; m.FocusField(Task); break;
            case 1 when _newFolder == null: _noAt = _now; break;
            case 0 or 1 or 2: Go(m, _step + 1); break;
            case 3: LaunchFromForm(m); break;
        }
    }

    /// <summary>You chose: he hops, and a moment later the next question comes by itself.</summary>
    void Chose(int step, double after)
    {
        if (_step != step) return;
        _formHop.Velocity = -110;
        _advanceAt = _now + after; _advanceFrom = step;
    }

    /// <summary>"Other folder…": Windows' own folder window.</summary>
    void Browse(IslandModel m)
    {
        if (_picking || _step != 1) return;
        if (Showcase.On) { PickedFolder(@"C:\dev\field-notes"); return; }   // (nothing of yours is opened in showcase mode)
        _picking = true; _advanceAt = -1;
        m.FocusField(null);   // (the folder window gets the keyboard)
        FolderPick.Ask(m.Hwnd, _other ?? _newFolder, "Where should the new agent work?");
    }

    void PickedFolder(string path)
    {
        if (path.Length > 3) path = path.TrimEnd('\\', '/');
        int i = _recent.FindIndex(r => SameFolder(r, path));
        if (i >= 0 && i < 7) _newFolder = _recent[i]; else _newFolder = _other = path;
        Chose(1, 1.0);
    }

    void LaunchFromForm(IslandModel m)
    {
        if (_step >= 4) return;
        if (_newFolder == null) { if (_step != 1) Go(m, 1); _noAt = _now; m.ShowToast("Pick a folder first"); return; }
        if (Task.Text.Trim().Length == 0) { if (_step != 0) Go(m, 0); m.FocusField(Task); _noAt = _now; m.ShowToast("Tell it what to do"); return; }
        if (Count >= MaxShown) { m.ShowToast($"The team is full ({MaxShown}) — close an agent first"); return; }
        var task = Task.Text.Trim();
        Task.Clear();
        var files = Attached.ToList();
        Attached.Clear();
        var a = Launch(m, _newFolder, task, Modes[_newMode].mode, files: files, focus: false);
        // sent: the page clears; he blinks, and in that blink the split takes over from him
        _sent = a; _sentTask = task; _sentRule = Modes[_newMode].label;
        _stepWas = _step; _step = 4; _stepT = 0; _advanceAt = -1;
        _tilt.To(0);
        m.FocusField(null);
        m.Bloub.Engine.Blink(m.Clock);
        _sendAt = _now + 0.1;
    }

    void StepForm(IslandModel m, float dt, bool widget, float left, float top, float cx)
    {
        _stepT += dt;
        _tilt.Step(dt); _formHop.Step(dt);
        if (Keys.Text.Length > 0) Keys.Clear();

        // the folder window closed: back to the question, with what was chosen
        if (_picking && FolderPick.TryTake(out var path))
        {
            _picking = false;
            if (View != TeamView.New || !widget) { SetView(TeamView.New); m.SetMode(Mode.Agents); _step = 1; _stepWas = -1; _tilt.To(7); }
            if (path != null) PickedFolder(path);
            m.FocusField(Keys);
        }

        bool on = widget && View == TeamView.New;
        if (!on)
        {
            if (Parting.Active) Parting.Stop();
            _sendAt = -1; _advanceAt = -1;
            return;
        }
        if (_advanceAt > 0 && _now >= _advanceAt) { _advanceAt = -1; if (_step == _advanceFrom) Go(m, _step + 1); }

        if (_step == 4 && _sendAt > 0 && _now >= _sendAt)
        {
            _sendAt = -1;
            Parting.Play(_sent?.Color ?? FormAccent, new Vector2(Math.Clamp(m.Face.GazeX.Value, -1, 1), Math.Clamp(m.Face.GazeY.Value, -1, 1)));
        }
        bool was = Parting.Active;
        Parting.Step(dt);
        if (Parting.TakeKick()) { m.Press.Snap(0.972f); m.Press.To(1, SpringSpec.Bounce); }   // (the island feels it go)
        if (Parting.TakeHome() && _step == 4)
        {
            // and there it is, in your island, at work: its own Bloub drops in from where it left
            _stepWas = -1; _step = 5; _stepT = 0.17f;
            if (_sent is { } a) { a.X.Snap(cx - _sentRowW / 2 + 29); a.Y.Snap(top - 26); a.R.Snap(17); a.A.Snap(1); }
        }
        if (was && !Parting.Active) m.Bloub.Engine.Blink(m.Clock - 0.1);   // (…and back: the island's Bloub appears with his eyes shut, opening)
        if (_step == 4 && !Parting.Active && _sendAt < 0) { _step = 5; _stepWas = -1; _stepT = 0.17f; }   // (it was cut short)
    }

    /// <summary>Where the just-launched agent's Bloub sits once it is working: in its row, in the middle of the form.</summary>
    Vector2? SentSpot(Agent a, float top, float cx) => a == _sent && _step == 5 ? new Vector2(cx - _sentRowW / 2 + 29, top + BodyTop + 57) : null;

    // Where each line of the task starts (kept until the text or the width changes).
    string? _wrapOf; float _wrapW; readonly List<int> _wrap = new();

    List<int> WrapTask(Canvas c, string text, float width)
    {
        if (text == _wrapOf && width == _wrapW) return _wrap;
        _wrapOf = text; _wrapW = width; _wrap.Clear(); _wrap.Add(0);
        int start = 0, space = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ') { space = i; continue; }
            if (i == start || c.Measure(text[start..(i + 1)], TaskSize, FontWeight.SemiBold) <= width) continue;
            start = space >= start ? space + 1 : i; // after the last space, or inside a word longer than the line
            _wrap.Add(start);
        }
        return _wrap;
    }

    /// <summary>A path shortened from the left, so the folders nearest the project stay readable.</summary>
    static string TailPath(Canvas c, string path, float size, float max)
    {
        string s = path;
        while (c.Measure(s, size) > max)
        {
            int cut = s.IndexOf('\\', s[0] == '…' ? 2 : 1);
            if (cut < 0) break;
            s = "…" + s[cut..];
        }
        return s;
    }

    void NewForm(Canvas c, IslandModel m, float x0, float y0, float w, float h, float vf)
    {
        var accent = Rgba(FormAccent);
        float x = x0 + FormPad, iw = w - FormPad * 2;
        bool sending = _step == 4, sent = _step == 5;
        // what clears away while he sends it off
        float chrome = vf * (sending ? 1 - Seg(_stepT, 0, 0.22f) : 1);
        // a new question waits for the old answer to slide away
        float tIn = _stepT - 0.17f;
        bool leaving = tIn < 0 && _stepWas is >= 0 and <= 3 && !sending;
        int shown = leaving ? _stepWas : _step;

        m.Hit("new:poke", x0 + FaceX - FaceR, y0 + FaceY - FaceR, FaceR * 2, FaceR * 2);

        // ---- back
        {
            float bx = x + 18, by = y0 + 38;
            bool hov = m.Hit("team:back", bx - 18, by - 18, 36, 36);
            c.Circle(bx, by, 18, WhiteA((hov ? 0.18f : 0.09f) * vf));
            c.Icon(IcBack, bx, by, 13, WhiteA(0.9f * vf));
        }

        // ---- what you already answered rides along at the top: tap one to change it
        if (_step is 1 or 2)
        {
            float cx = x + 46;
            for (int i = 0; i < _step; i++)
            {
                string text = i == 0 ? Task.Text.Trim() : ProjectName(_newFolder);
                bool newest = i == _step - 1 && _stepWas == i;
                float p = newest ? Seg(_stepT, 0.12f, 0.67f) : 1, k = chrome * MathF.Min(1, p * 3), rise = (1 - Sp(p)) * 30;
                float tw = MathF.Min(c.Measure(text, 12.5f, FontWeight.SemiBold), 126), cw = tw + 24, cy = y0 + 24 + rise;
                bool hov = k > 0.5f && m.Hit($"new:step:{i}", cx, y0 + 24, cw, 28);
                c.Round(cx, cy, cw, 28, 14, WithA(Mix(accent, 0x0C0C0E, hov ? 0.42f : 0.28f), k));
                c.Text(text, cx + 12, cy + 5, 12.5f, WhiteA(0.86f * k), FontWeight.SemiBold, 0, 126);
                cx += cw + 6;
            }
        }

        // ---- where you are: three dots, the one you are on drawn out
        if (!sent)
        {
            float e = 1 - MathF.Pow(1 - Seg(_stepT, 0, 0.4f), 3), dx = x0 + w - FormPad;
            for (int i = 2; i >= 0; i--)
            {
                bool now = i == _step, wasNow = i == _stepWas && _stepWas != _step;
                float dw = now ? 7 + 13 * e : wasNow ? 20 - 13 * e : 7;
                var col = now ? accent : i < _step ? WhiteA(0.6f) : WhiteA(0.18f);
                if (wasNow && i >= _step) col = Lerp4(accent, WhiteA(0.18f), e); else if (wasNow) col = Lerp4(accent, WhiteA(0.6f), e);
                dx -= dw;
                c.Round(dx, y0 + 34.5f, dw, 7, 3.5f, WithA(col, col.A * chrome));
                dx -= 5;
            }
        }

        // ---- he asks (each word comes up by itself)
        {
            string q = sent ? _sent?.Status == AgentStatus.Ended ? "It couldn't start" : "It's working in " + ProjectName(_sent?.Cwd) : Asks[Math.Clamp(shown, 0, 3)];
            float qx = x0 + 100, qy = y0 + FaceY - 17;
            float gap = c.Measure("a a", 25, FontWeight.Bold) - c.Measure("aa", 25, FontWeight.Bold);
            var words = q.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                float p = leaving ? 1 : Seg(tIn - i * 0.045f, 0, 0.6f), e = Sp(p);
                string word = i == words.Length - 1 ? c.Fit(words[i], 25, FontWeight.Bold, MathF.Max(40, x0 + w - FormPad - qx)) : words[i];
                c.Text(word, qx, qy + (1 - e) * 18, 25, WhiteA((sending ? chrome : vf) * MathF.Min(1, p * 4)), FontWeight.Bold);
                qx += c.Measure(word, 25, FontWeight.Bold) + gap;
            }
        }

        // ---- the answer
        float by0 = y0 + BodyTop;
        if (leaving) { float u = _stepT / 0.16f; FormBody(c, m, _stepWas, x, by0 - 14 * u * u, iw, vf * Math.Clamp(1 - u, 0, 1), false, x0, w); }
        else if (!sending) { float p = Seg(tIn, 0, 0.6f); FormBody(c, m, _step, x, by0 + (1 - Sp(p)) * 22, iw, vf * MathF.Min(1, p * 4), true, x0, w); }

        // ---- what Enter does, and the button that does the same
        if (_step <= 4)
        {
            float k = chrome, fy = y0 + FootY;
            string project = ProjectName(_newFolder);
            int foot = sending ? 3 : shown;   // (while he sends it off, the last question's foot is what fades away)
            string tip = foot switch { 0 => "Enter to continue", 1 => project.Length > 0 ? "Pick one, or Enter for " + project : "Pick a folder", 2 => "You can change this later", _ => "Tap an answer to change it" };
            string label = foot == 3 ? "Launch" : "Continue";
            bool ready = sending || (foot == 0 ? Task.Text.Trim().Length > 0 : foot != 1 || _newFolder != null);
            float lw = c.Measure(label, 14.5f, FontWeight.Bold), bw = lw + 9 + 26 + 40, bx = x0 + w - FormPad - bw;
            float no = (float)(_now - _noAt); if (no < 0.4f) bx += MathF.Sin(no * 40) * 7 * (1 - no / 0.4f);   // (not yet: it shakes its head)
            c.Text(tip, x, fy + 14, 12, WhiteA(0.38f * k), FontWeight.Regular, 0, bx - x - 14);
            bool hov = !sending && m.Hit("new:next", bx, fy, bw, 44);
            c.Round(bx, fy, bw, 44, 22, ready ? WithA(hov ? Rgba(0x339BFF) : accent, k) : Rgba(0x151517, k));
            c.Text(label, bx + 20, fy + 12, 14.5f, WhiteA((ready ? 1 : 0.38f) * k), FontWeight.Bold);
            if (ready)
            {
                c.Round(bx + 20 + lw + 9, fy + 12, 26, 20, 7, WhiteA(0.22f * k));   // Enter does the same
                c.Text("↵", bx + 20 + lw + 9 + 13, fy + 11.5f, 13, WhiteA(k), FontWeight.Bold, 0.5f);
            }
        }
    }

    static Color4 Lerp4(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    void FormBody(Canvas c, IslandModel m, int step, float x, float y, float iw, float k, bool live, float x0, float w)
    {
        var accent = Rgba(FormAccent);
        var bright = Rgba(0x45A6FF);
        switch (step)
        {
            case 0:
            {
                // the task: big, on one line that it writes on; it wraps to three
                bool focused = m.Focused == Task;
                var lines = Task.Text.Length == 0 ? null : WrapTask(c, Task.Text, iw);
                int rows = lines == null ? 1 : Math.Min(TaskRows, lines.Count);
                float ty = y + 2, uy = ty + rows * TaskLine + 9;
                if (live) m.Hit("input:task", x - 8, y - 6, iw + 16, uy - y + 14);
                bool blink = focused && (float)((m.Clock - Task.ChangedAt) % 1.06) < 0.6;
                if (lines == null)
                {
                    c.Text(Task.Placeholder, x, ty, TaskSize, WhiteA(0.3f * k), FontWeight.SemiBold, 0, iw);
                    if (blink) c.Round(x, ty + 2, 1.8f, TaskLine - 3, 0.9f, WithA(accent, k));
                    _caret = new Vector2(x, ty + TaskLine);
                }
                else
                {
                    int at = lines.Count - 1;
                    while (at > 0 && lines[at] > Task.Caret) at--;
                    int first = Math.Clamp(at - (TaskRows - 1), 0, Math.Max(0, lines.Count - TaskRows)); // keep the caret's line in view
                    for (int i = first; i < lines.Count && i < first + TaskRows; i++)
                    {
                        int s = lines[i], e = i + 1 < lines.Count ? lines[i + 1] : Task.Text.Length;
                        c.Text(Task.Text[s..e], x, ty + (i - first) * TaskLine, TaskSize, WhiteA(0.96f * k), FontWeight.SemiBold);
                    }
                    float cx = x + c.Measure(Task.Text[lines[at]..Task.Caret], TaskSize, FontWeight.SemiBold), cy = ty + (at - first) * TaskLine;
                    if (blink) c.Round(cx, cy + 2, 1.8f, TaskLine - 3, 0.9f, WithA(accent, k));
                    _caret = new Vector2(cx, cy + TaskLine);
                }
                c.Round(x, uy, iw, 2, 1, WhiteA(0.16f * k));
                float fy = uy + 2 + 14;

                // the files it starts with (dropped on Bloub, or onto this form)
                if (Attached.Count > 0)
                {
                    float ax = x;
                    for (int i = 0; i < Attached.Count; i++)
                    {
                        string name = Path.GetFileName(Attached[i].TrimEnd('\\', '/'));
                        var look = new PocketItem { Name = name, Kind = PocketItem.KindOf(Attached[i]) }.Look;
                        float nw = MathF.Min(c.Measure(name, 12, FontWeight.SemiBold), 200), cw = 34 + nw + 30;
                        if (ax + cw > x + iw) break;
                        bool xh = live && m.Hit($"new:detach:{i}", ax + cw - 28, fy + 2, 26, 26);
                        c.Round(ax, fy, cw, 30, 15, WithA(Mix(accent, 0x0C0C0E, 0.28f), k));
                        c.Round(ax + 5, fy + 5, 20, 20, 6.5f, Rgba(look.Color, 0.3f * k));
                        c.Icon(look.Icon, ax + 15, fy + 15, 10, Rgba(look.Color, k));
                        c.Text(name, ax + 32, fy + 6, 12, WhiteA(0.95f * k), FontWeight.SemiBold, 0, 200);
                        c.Icon('\uE711', ax + cw - 15, fy + 15, 7.5f, WhiteA((xh ? 1 : 0.45f) * k));
                        ax += cw + 6;
                    }
                    fy += 30 + 8;
                }

                // starters: a tap writes the task
                var starters = Starters;
                float sx = x; int row = 0;
                for (int i = 0; i < starters.Length; i++)
                {
                    float cw = c.Measure(starters[i].label, 12.5f, FontWeight.SemiBold) + 26;
                    if (sx + cw > x + iw) { if (++row > 1) break; sx = x; }
                    float sy = fy + row * 36;
                    bool hov = live && m.Hit($"new:tpl:{i}", sx, sy, cw, 30);
                    c.Round(sx, sy, cw, 30, 15, WhiteA((hov ? 0.16f : 0.08f) * k));
                    c.Text(starters[i].label, sx + 13, sy + 6, 12.5f, WhiteA((hov ? 1 : 0.86f) * k), FontWeight.SemiBold);
                    sx += cw + 6;
                }
                break;
            }
            case 1:
            {
                // the seven you used last, and any other folder
                int n = Math.Min(_recent.Count, 7);
                float cw = (iw - 6) / 2, rh = 44;
                bool otherOn = _other != null && SameFolder(_newFolder, _other);
                for (int i = 0; i <= n; i++)
                {
                    float rx = x + (i % 2) * (cw + 6), ry = y + (i / 2) * (rh + 6);
                    bool other = i == n;
                    bool on = other ? otherOn : !otherOn && SameFolder(_newFolder, _recent[i]);
                    bool hov = live && m.Hit(other ? "new:other" : $"new:folder:{i}", rx, ry, cw, rh);
                    c.Round(rx, ry, cw, rh, 18, on ? WithA(Mix(accent, 0x0C0C0E, hov ? 0.4f : 0.32f), k) : Rgba(hov ? 0x1E1E21u : 0x151517u, k));
                    string name, right;
                    if (other)
                    {
                        c.Round(rx + 10, ry + 9, 26, 26, 9, WhiteA(0.14f * k));
                        c.Icon(IcFolder, rx + 23, ry + 22, 12, WhiteA(k));
                        name = _other != null ? ProjectName(_other) : "Other folder…";
                        right = _picking ? "choosing…" : _other != null ? "change" : "Browse";
                    }
                    else
                    {
                        name = ProjectName(_recent[i]);
                        c.Round(rx + 10, ry + 9, 26, 26, 9, Rgba(i < _recentColor.Count ? _recentColor[i] : ProjectColors[i % ProjectColors.Length], k));
                        c.Text(name.Length > 0 ? name[..1].ToUpperInvariant() : "?", rx + 23, ry + 13.5f, 12, new Color4(0, 0, 0, 0.75f * k), FontWeight.ExtraBold, 0.5f);
                        right = Ago(Used(i));
                    }
                    float rw = right.Length > 0 ? c.Measure(right, 11) + 10 : 0;
                    c.Text(name, rx + 46, ry + 13, 13, WhiteA((other && _other == null ? 0.86f : 1) * k), FontWeight.SemiBold, 0, cw - 46 - rw - 12);
                    if (right.Length > 0) c.Text(right, rx + cw - 12, ry + 15, 11, WhiteA(0.38f * k), FontWeight.Regular, 1);
                }
                if (n == 0) c.Text("Run `claude` in a folder once and it shows up here.", x + 2, y + rh + 14, 12, WhiteA(0.4f * k), FontWeight.Regular, 0, iw);
                break;
            }
            case 2:
            {
                // how much it may do on its own: four tiles, each says what it means
                float cw = (iw - 6) / 2, th = 100;
                for (int i = 0; i < Modes.Length; i++)
                {
                    float tx = x + (i % 2) * (cw + 6), ty = y + (i / 2) * (th + 6);
                    bool on = i == _newMode;
                    bool hov = live && m.Hit($"new:mode:{i}", tx, ty, cw, th);
                    c.Round(tx, ty, cw, th, 22, on ? WithA(Mix(accent, 0x0C0C0E, hov ? 0.4f : 0.32f), k) : Rgba(hov ? 0x1E1E21u : 0x151517u, k));
                    c.Icon(ModeIcons[i], tx + 23, ty + 23, 16, WithA(bright, k));
                    c.Text(Modes[i].label, tx + 13, ty + 39, 13.5f, WhiteA(k), FontWeight.Bold);
                    c.Wrapped(ModeSays[i], tx + 13, ty + 60, cw - 26, 11.5f, WhiteA(0.5f * k));
                }
                break;
            }
            case 3:
            {
                // your three answers: tap one to change it
                (char Icon, string Text, string? Side)[] rows =
                [
                    (IcEdit, Task.Text.Trim(), Attached.Count == 0 ? null : Attached.Count == 1 ? "1 file" : $"{Attached.Count} files"),
                    (IcFolder, ProjectName(_newFolder), _newFolder),
                    (ModeIcons[_newMode], Modes[_newMode].label, null),
                ];
                for (int i = 0; i < rows.Length; i++)
                {
                    float ry = y + i * 50;
                    bool hov = live && m.Hit($"new:step:{i}", x, ry, iw, 44);
                    c.Round(x, ry, iw, 44, 18, Rgba(hov ? 0x1E1E21u : 0x151517u, k));
                    c.Icon(rows[i].Icon, x + 23, ry + 22, 14, WithA(bright, k));
                    float nameW = MathF.Min(c.Measure(rows[i].Text, 13, FontWeight.SemiBold), iw - 60);
                    c.Text(rows[i].Text, x + 46, ry + 13, 13, WhiteA(k), FontWeight.SemiBold, 0, iw - 60);
                    if (rows[i].Side is { } side && iw - 60 - nameW > 90)
                        c.Text(i == 1 ? TailPath(c, side, 11, iw - 76 - nameW) : side, x + iw - 14, ry + 15, 11, WhiteA(0.34f * k), FontWeight.Regular, 1);
                }
                break;
            }
            case 5 when _sent is { } a:
            {
                // the session it became: its own Bloub (it dropped in from where the new one left), what it is doing, where and how
                float cx = x0 + w / 2;
                string line = _sentTask, under = a.Status == AgentStatus.Ended ? a.LastError ?? "The agent stopped" : $"{a.Name} · {_sentRule}";
                float tw = MathF.Max(MathF.Min(c.Measure(line, 13.5f, FontWeight.SemiBold), 260), MathF.Min(c.Measure(under, 11.5f), 260));
                float rw = 12 + 34 + 12 + tw + 8 + 23 + 18, rh = 54;
                _sentRowW = rw;
                float ry = a.Y.Value - 27, rx = cx - rw / 2;   // (the row rides with its Bloub)
                bool hov = live && m.Hit("new:open", rx, ry, rw, rh);
                c.Round(rx, ry, rw, rh, 27, Rgba(hov ? 0x1E1E21u : 0x151517u, k));
                c.Text(line, rx + 58, ry + 9, 13.5f, WhiteA(k), FontWeight.SemiBold, 0, 260);
                c.Text(under, rx + 58, ry + 29, 11.5f, a.Status == AgentStatus.Ended ? Rgba(0xFF453A, 0.9f * k) : WhiteA(0.45f * k), FontWeight.Regular, 0, 260);
                if (a.Status != AgentStatus.Ended)
                    for (int i = 0; i < 3; i++)
                    {
                        // (three dots, running: it is working)
                        float ph = (float)((m.Clock - i * 0.15f) % 1f), up = ph < 0.6f ? MathF.Sin(ph / 0.6f * MathF.PI) : 0;
                        c.Circle(rx + rw - 18 - (2 - i) * 9 + 2.5f, ry + rh / 2 - 4 * up, 2.5f, WithA(Rgba(a.Color), (0.45f + 0.55f * up) * k));
                    }
                float settle = Seg(_stepT - 0.17f, 0.25f, 0.7f);
                c.Text(a.Status == AgentStatus.Ended ? "Open it to see what went wrong." : "You'll hear from it when it needs you or when it's done.",
                    cx, y + 30 + rh + 20, 13, WhiteA(0.5f * k * settle), FontWeight.Regular, 0.5f, iw);
                break;
            }
        }
    }
}
