using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Island.Lab.Mascot;
using Island.Lab.Motion;
using Island.Lab.Platform;

namespace Island.Lab.Island;

/// <summary>
/// "New tab · Describe it", in the tab library. Bloub stretches out of his orb into the white line you type in
/// (his eyes stay at its left end, looking at you); living previews to start from sit below. Enter: your words
/// fly up as the title, the line pulls back into Bloub, and he builds the tab in front of you (Claude Code finds
/// a free live source and writes the recipe; the island checks it against the real data). Done: a leap with a
/// flip, the ✓ stamp biting its notch, two proud hops. "Add to island": the tab glides into your bar as a chip
/// and Bloub jumps home with a twirl.
/// All positions are relative to the island's top-left (the dashboard is 724 × 404).
/// </summary>
public sealed class TabMaker
{
    public enum Stage { Off, Line, Build, Adding }
    public Stage Now { get; private set; }
    public bool Active => Now != Stage.Off;
    /// <summary>The island stays open while a tab is being built or added (Cancel / Esc leave).</summary>
    public bool KeepsOpen => Now is Stage.Build or Stage.Adding;
    public readonly TextField Field = new("");
    float _clock, _at;   // now, and when the stage began
    float Age => _clock - _at;

    // ================================================================ the four kinds the line understands on sight
    // (anything else works too: Claude finds a source for it; these just light up the chip and the previews)

    public sealed record Kind(string Id, Regex Test, string Name, char Icon, uint Color, string Starter, Func<Read, string> Means);
    public static readonly Kind[] Kinds =
    [
        new("football", new("(match|score|football|soccer|game|league|goal|fixture)"), "Matches", '\uE7C1', 0x30D158, "Live scores of today’s matches",
            r => $"<b>live scores</b>{(r.When ? " for today" : "")}, by league"),
        new("crypto", new("(bitcoin|crypto|btc|eth|price|stock|coin)"), "Price", '\uEAFC', 0xFF9F0A, "Bitcoin price with a chart",
            _ => "<b>the price</b>, with a 24-hour chart"),
        new("weather", new("(weather|temperature|rain|forecast|sun)"), "Weather", '\uE706', 0x64D2FF, "Weather in Riyadh this week",
            r => $"<b>a 5-day forecast</b>{(r.Where != null ? $" for {r.Where}" : "")}"),
        new("news", new("(news|stories|story|hacker|headline|reddit|posts)"), "News", '\uE8A5', 0xBF5AF2, "Top stories on Hacker News",
            _ => "<b>the top stories</b>, as a list"),
    ];
    public const string Question = "What should I keep an eye on?";
    public const string Hint = "Press <k>Tab</k> for the suggestion, or start from one below";
    static readonly string[] Suggest = Kinds.Select(k => k.Starter.ToLowerInvariant()).ToArray();
    static readonly Regex When = new("^(today|tonight|now|tomorrow|week|weekly|weekend|daily|hourly|live|morning|evening)$");
    static readonly Regex Who = new("^(my|mine|me|our)$");
    static readonly Regex Stop = new("^(the|a|an|of|in|at|for|this|with|and|on|to|show|keep|eye|me|my)$");

    public sealed record Token(int At, string Word, bool Hit);
    public sealed record Read(Kind? Kind, List<Token> Tokens, string? Where, bool When);

    /// <summary>What Bloub understands in a sentence: which kind of tab, and the words that told him.</summary>
    public static Read Understand(string text)
    {
        var toks = Regex.Matches(text, @"\S+").Select(m => (m.Index, W: m.Value, N: Regex.Replace(Regex.Replace(m.Value.ToLowerInvariant(), "[’']s$", ""), "[^a-z0-9]", ""))).ToList();
        string? where = null; bool when = false;
        var outT = new List<Token>();
        for (int j = 0; j < toks.Count; j++)
        {
            var t = toks[j];
            bool hit = false;
            if (When.IsMatch(t.N)) { hit = true; when = true; }
            else if (Who.IsMatch(t.N)) hit = true;
            else if (t.N.Length > 0 && Kinds.Any(k => k.Test.IsMatch(t.N))) hit = true;
            else if (j > 0 && toks[j - 1].N is "in" or "at" or "for" && t.N.Length > 2 && !Stop.IsMatch(t.N))
            {
                hit = true;
                where ??= char.ToUpperInvariant(t.N[0]) + t.N[1..];
            }
            outT.Add(new Token(t.Index, t.W, hit));
        }
        var kind = Kinds.FirstOrDefault(k => k.Test.IsMatch(text.ToLowerInvariant()));
        return new Read(kind, outT, where, when);
    }

    /// <summary>"bitcoin · price": the words that told him, for the first build step.</summary>
    static string About(string text)
    {
        var r = Understand(text);
        var words = r.Tokens.Where(t => t.Hit).Select(t => Regex.Replace(t.Word.ToLowerInvariant(), "[^a-z0-9’' ]", "")).Where(w => w.Length > 0).Distinct().Take(3).ToList();
        if (words.Count == 0) words = r.Tokens.Select(t => t.Word.ToLowerInvariant()).Where(w => !Stop.IsMatch(w)).Take(3).ToList();
        return words.Count > 0 ? string.Join(" · ", words) : text;
    }

    // ================================================================ easing (the design's "calm" curve, and a few cubic-béziers)

    static readonly float[] CalmTable = [0, 0.021f, 0.076f, 0.151f, 0.238f, 0.329f, 0.419f, 0.506f, 0.586f, 0.659f, 0.724f, 0.780f, 0.828f, 0.868f, 0.902f, 0.929f, 0.951f, 0.968f, 0.982f, 0.992f, 0.999f, 1.004f, 1.008f, 1.010f, 1.011f, 1.011f, 1.011f, 1.010f, 1.009f, 1.008f, 1.007f, 1.006f, 1.005f, 1.005f, 1.004f, 1.003f, 1.002f, 1.002f, 1.001f, 1.001f, 1];
    public static float Calm(float u)
    {
        u = Math.Clamp(u, 0, 1) * (CalmTable.Length - 1);
        int i = Math.Min((int)u, CalmTable.Length - 2);
        return CalmTable[i] + (CalmTable[i + 1] - CalmTable[i]) * (u - i);
    }
    public static float Bezier(float u, float x1, float y1, float x2, float y2)
    {
        u = Math.Clamp(u, 0, 1);
        float t = u;
        for (int i = 0; i < 8; i++)
        {
            float x = 3 * (1 - t) * (1 - t) * t * x1 + 3 * (1 - t) * t * t * x2 + t * t * t - u;
            float dx = 3 * (1 - t) * (1 - t) * x1 + 6 * (1 - t) * t * (x2 - x1) + 3 * t * t * (1 - x2);
            if (MathF.Abs(dx) < 1e-5f) break;
            t = Math.Clamp(t - x / dx, 0, 1);
        }
        return 3 * (1 - t) * (1 - t) * t * y1 + 3 * (1 - t) * t * t * y2 + t * t * t;
    }
    public static float EaseIn(float u) => Bezier(u, 0.42f, 0, 1, 1);
    public static float EaseOut(float u) => Bezier(u, 0, 0, 0.58f, 1);
    /// <summary>Progress of something that started at <paramref name="at"/> and lasts <paramref name="dur"/> seconds (0 before, 1 after).</summary>
    public float Since(float at, float dur) => Math.Clamp((_clock - at) / dur, 0, 1);
    public float Clock => _clock;

    // ================================================================ the line: Bloub's own body, stretched

    public static readonly (float X, float Cy, float W, float H) Pill = (72.4f, 132, 584, 64);
    public const float BallR = 45.6f;                        // his body at the line's left end
    public static readonly Vector3 LeftBall = new(Pill.X + BallR, Pill.Cy, BallR);
    public static Vector3 OrbBall => new(Scenes.LibOrbX, Scenes.LibOrbY, Scenes.LibOrbR);

    enum Morph { None, Stretch, Back }
    Morph _morph;
    Vector3 _from, _to;                                       // ball (cx, cy, r) it comes from / goes to
    float _morphAt, _backDur;
    (float L, float Cy, float W, float H) _backFrom;
    readonly Spring _mw = new(0, new SpringSpec(0.6f, 0.58f), 0.0005f), _mh = new(0, new SpringSpec(0.44f, 0.38f), 0.0005f), _mp = new(0, new SpringSpec(0.42f, 0.8f), 0.0005f);
    readonly Spring _bs = new(0, new SpringSpec(0.4f, 0.74f), 0.0005f), _bp = new(0, new SpringSpec(0.38f, 0.86f), 0.0005f);
    Action? _backThen;
    /// <summary>His eyes inside the line: the ball they belong to (its radius), the hop, tilt and scale of a reaction.</summary>
    public readonly Spring EyeR = new(BallR, new SpringSpec(0.26f, 0.74f), 0.01f);
    /// <summary>In the line his eyes sit a little left of his ball's centre, clear of your words.</summary>
    public readonly Spring EyeDx = new(0, new SpringSpec(0.26f, 0.74f), 0.01f);
    public readonly Spring Hop = new(0, new SpringSpec(0.32f, 0.4f), 0.05f), Tilt = new(0, new SpringSpec(0.42f, 0.5f), 0.05f),
        EyeS = new(1, new SpringSpec(0.3f, 0.45f), 0.001f), Ps = new(1, new SpringSpec(0.34f, 0.42f), 0.0005f), Sx = new(0, new SpringSpec(0.3f, 0.2f), 0.05f);
    public bool LineShown => _morph != Morph.None;
    /// <summary>Hides the real Bloub: the line is drawn in his place.</summary>
    public bool HidesBloub => _morph != Morph.None;

    /// <summary>The line right now: left edge, centre line, width, height (island coordinates), and how ball-like it still is (shade).</summary>
    public (float L, float Cy, float W, float H, float Shade) Line()
    {
        float d0 = _from.Z * 2;
        if (_morph == Morph.Stretch)
        {
            float l0 = _from.X - _from.Z, p = _mp.Value;
            float w = d0 + (Pill.W - d0) * _mw.Value, h = d0 + (Pill.H - d0) * _mh.Value;
            return (l0 + (Pill.X - l0) * p, _from.Y + (Pill.Cy - _from.Y) * p, w, h, 1 - Since(_morphAt, 0.2f));
        }
        if (_morph == Morph.Back)
        {
            var (l0, c0, w0, h0) = _backFrom;
            float d1 = _to.Z * 2, l1 = _to.X - _to.Z, s = _bs.Value, p = _bp.Value;
            float sh = Since(_morphAt + _backDur * 0.7f, _backDur * 0.3f);
            return (l0 + (l1 - l0) * p, c0 + (_to.Y - c0) * p, w0 + (d1 - w0) * s, h0 + (d1 - h0) * s, sh);
        }
        return (Pill.X, Pill.Cy, Pill.W, Pill.H, 0);
    }

    void Stretch(Vector3 from)
    {
        _from = from; _morph = Morph.Stretch; _morphAt = _clock;
        _mw.Snap(0); _mw.To(1); _mh.Snap(0); _mh.To(1); _mp.Snap(0); _mp.To(1);
        EyeR.Snap(from.Z); EyeR.To(BallR); EyeDx.Snap(0); EyeDx.To(-12); EyeS.Snap(1); Hop.Snap(0); Tilt.Snap(0); Ps.Snap(1); Sx.Snap(0);
        Pose = LeftBall;
    }

    /// <summary>The line pulls back into a ball (at its left end, or into his orb) and becomes Bloub again.</summary>
    void BallBack(float dur, Vector3 to, Action? then)
    {
        if (_morph == Morph.None) { then?.Invoke(); return; }
        var (l, cy, w, h, _) = Line();
        _backFrom = (l, cy, w, h); _to = to; _morph = Morph.Back; _morphAt = _clock; _backDur = dur;
        _bs.Snap(0); _bs.To(1); _bp.Snap(0); _bp.To(1);
        EyeR.To(to.Z); EyeDx.To(0); Tilt.To(0); EyeS.To(1);
        _backThen = then;
        Armed = false; Ready = false;
    }

    // ================================================================ Bloub's body while he works here: a pose (where), and his rig (squash, jumps, flips)

    /// <summary>Where Bloub is (cx, cy, ball radius), when this flow moves him; null = the island places him.</summary>
    public Vector3? Pose { get; private set; }
    Vector3 _goFrom, _goTo; float _goAt = -1, _goDur, _goLift; Func<float, float> _goEase = Calm;
    void Go(Vector3 to, float dur, float lift, Func<float, float>? ease = null)
    {
        _goFrom = Pose ?? to; _goTo = to; _goAt = _clock; _goDur = dur; _goLift = lift; _goEase = ease ?? Calm;
    }

    readonly Spring _sq = new(1, new SpringSpec(0.38f, 0.45f), 0.0005f);
    readonly Spring _tilt = new(0, new SpringSpec(0.5f, 0.55f), 0.01f);
    float _jy, _vy; bool _air; Action? _land;
    float _flipAt = -10, _flipDur, _flipDir = 1; Func<float, float> _flipEase = Calm;
    float _wiggleAt = -10, _crouchAt = -10, _landAt = -10;
    const float Gravity = 2600;      // his own pixels (a 100 px Bloub), like the design
    void Leap(float v, Action? land = null) { _vy = v; _air = true; _land = land; _jy = MathF.Min(_jy, -0.01f); }

    /// <summary>His body's transform for the renderer: squash, tilt, jump and flip (identity when at rest).</summary>
    public Matrix3x2 BloubXf(IslandModel m)
    {
        if (m.Mode != Mode.Full || Now is not (Stage.Build or Stage.Adding) && !_air && MathF.Abs(_sq.Value - 1) < 0.001f && MathF.Abs(_tilt.Value) < 0.05f) return Matrix3x2.Identity;
        var c = new Vector2(m.Face.X, m.Face.Y + m.BloubLift);
        float r = 11 * m.Face.Size.Value * m.Bloub.Squish.Value, s = r * 2 / 0.95f, k = s / 100;
        float st = _air ? MathF.Min(0.14f, MathF.Abs(_vy) / 4200) : 0;
        float sq = _sq.Value, sy = sq * (1 + st), sx = (1 + (1 - sq) * 0.85f) * (1 - st * 0.7f);
        var (kx, ky) = Keyed();
        sx *= kx; sy *= ky;
        var foot = c + new Vector2(0, 0.46f * s);
        float spin = Flip() + Wiggle();
        return Matrix3x2.CreateRotation(spin, c) * Matrix3x2.CreateTranslation(-foot) * Matrix3x2.CreateScale(sx, sy)
             * Matrix3x2.CreateRotation(_tilt.Value * MathF.PI / 180) * Matrix3x2.CreateTranslation(foot + new Vector2(0, _jy * k));
    }

    float Flip()
    {
        float u = (_clock - _flipAt) / _flipDur;
        return u is > 0 and < 1 ? _flipDir * MathF.Tau * _flipEase(u) : 0;
    }

    /// <summary>The happy wiggle when he lands home: −12°, +10°, −6°, 0.</summary>
    float Wiggle()
    {
        float u = (_clock - _wiggleAt) / 0.7f;
        if (u is <= 0 or >= 1) return 0;
        float[] at = [0, 0.2f, 0.45f, 0.7f, 1], deg = [0, -12, 10, -6, 0];
        int i = 0; while (i < 3 && u > at[i + 1]) i++;
        float t = (u - at[i]) / (at[i + 1] - at[i]); t = t * t * (3 - 2 * t);
        return (deg[i] + (deg[i + 1] - deg[i]) * t) * MathF.PI / 180;
    }

    /// <summary>Keyframed squashes: the crouch before he jumps home, the springy push-off, the landing.</summary>
    (float, float) Keyed()
    {
        float u = (_clock - _crouchAt) / 0.26f;
        if (u is > 0 and < 1) { float e = EaseOut(u); return (1 + 0.12f * e, 1 - 0.16f * e); }
        u = (_clock - _crouchAt - 0.52f) / 0.42f;              // push-off: (1.12, .84) → (.92, 1.1) → (1, 1)
        if (u is > 0 and < 1)
        {
            float e = EaseOut(u);
            return e < 0.25f ? (1.12f - 0.8f * e, 0.84f + 1.04f * e) : (0.92f + 0.08f * (e - 0.25f) / 0.75f, 1.1f - 0.1f * (e - 0.25f) / 0.75f);
        }
        if (_clock - _crouchAt is > 0 and < 0.52f) return (1.12f, 0.84f);    // holds the crouch
        u = (_clock - _landAt) / 0.46f;                         // landing: (1.16, .82) → (.96, 1.05) → (1, 1)
        if (u is > 0 and < 1)
        {
            float e = EaseOut(u);
            return e < 0.3f ? (1 + 0.16f * e / 0.3f, 1 - 0.18f * e / 0.3f)
                 : e < 0.65f ? (1.16f - 0.2f * (e - 0.3f) / 0.35f, 0.82f + 0.23f * (e - 0.3f) / 0.35f)
                 : (0.96f + 0.04f * (e - 0.65f) / 0.35f, 1.05f - 0.05f * (e - 0.65f) / 0.35f);
        }
        return (1, 1);
    }

    // the ✓ stamp: it slams on, bites a notch out of him, the tick draws itself; later it lets go
    public float StampAt { get; private set; } = -10;
    public float UnstampAt { get; private set; } = -10;
    public bool Badged => StampAt > 0;

    // touchdown: a ring on the floor, a few speed lines kicked up; and the confetti when he gets home
    public readonly List<(Vector2 At, float T0, float Scale)> Rings = new();
    public readonly List<(Vector2 At, float Deg, float R0, float W, float T0)> Zips = new();
    public readonly List<(Vector2 From, Vector2 To, float Size, uint Color, float T0, float Dur)> Confetti = new();

    // ================================================================ the describe screen

    public bool Armed { get; private set; }
    public bool Ready { get; private set; }                  // there's something to build (the button turns dark)
    public Read Reading { get; private set; } = Understand("");
    public float ArmedAt { get; private set; } = -10;
    /// <summary>The grey line in the empty field (the question, then suggestions) and the one rolling out.</summary>
    public (string Text, float At) Ph, PhOld;
    /// <summary>The line under the field: a hint, or what Bloub understood.</summary>
    public (string Text, float At) Voice, VoiceOld;
    public readonly Spring Fs = new(23, new SpringSpec(0.45f, 1f), 0.01f);
    public readonly Spring ChipT = new(0, new SpringSpec(0.6f, 0.9f), 0.002f);
    public readonly Spring GoDark = new(0, new SpringSpec(0.35f, 1f), 0.002f);
    /// <summary>The go button easing bigger under the mouse.</summary>
    public readonly Spring GoHover = new(0, new SpringSpec(0.3f, 0.7f), 0.002f);
    /// <summary>How far a long sentence is slid left so the caret stays in view (DIPs).</summary>
    public readonly Spring Pan = new(0, new SpringSpec(0.22f, 1f), 0.05f);
    /// <summary>Where your words start and their size (island coordinates, set while drawing): a click puts the caret there.</summary>
    public float TextX, TextFs = 23;
    public const int MaxText = 280;
    public readonly Dictionary<int, float> HitAt = new();
    /// <summary>Per preview: the one he thinks you mean steps forward (Pick), any of them is picked (HasPick), hover.</summary>
    public readonly Spring[] Pick = Enumerable.Range(0, 4).Select(_ => new Spring(0, new SpringSpec(0.6f, 0.9f), 0.002f)).ToArray();
    public readonly Spring[] Hov = Enumerable.Range(0, 4).Select(_ => new Spring(0, new SpringSpec(0.4f, 1f), 0.002f)).ToArray();
    public readonly Spring HasPick = new(0, new SpringSpec(0.5f, 1f), 0.002f);     // when each understood word went dark (it fades in)
    int _sug;
    float _phNext, _idleAt = -1, _hoverTile = -1, _hoverSince;
    string _lastText = "";
    HashSet<int> _lastHits = new();
    Kind? _lastKind;
    string _typing = ""; float _typeNext, _typeEvery; Action? _typeThen; float _thenAt = -1; Action? _then;
    public bool Typing => _typing.Length > 0;
    public float OpenedAt { get; private set; } = -10;
    public float LibBackAt { get; private set; } = -10;

    // the living previews: tiny real tabs ticking along
    public sealed class Mini
    {
        public string[] Home = ["ARS", "HIL", "LIV"], Away = ["CHE", "NAS", "MCI"];
        public int[] Hs = [2, 1, 2], As = [1, 1, 2];
        public bool[] Live = [true, true, false];
        public float[] GoalAt = [-10, -10, -10], RollAt = [-10, -10, -10]; public bool[] RollAway = new bool[3];
        public int Price = 67582; public float PriceAt = -10; public bool PriceUp = true;
        public int[] Points = [843, 507, 212]; public float PointsAt = -10;
        public float BarsAt = -10;
        public int N;
    }
    public Mini Minis { get; private set; } = new();
    float _tileNext;

    public void Open(IslandModel m, string text = "")
    {
        if (m.Mode != Mode.Full || !m.Tabs.Editing || Now != Stage.Off && Now != Stage.Build) return;
        bool fromBuild = Now == Stage.Build;
        Builder?.Cancel(); Builder = null;
        Now = Stage.Line; _at = _clock; OpenedAt = _clock;
        Diag.Log("maker: line");
        Armed = false; Ready = false; ArmedAt = -10;
        Field.Clear(); _lastText = ""; _lastHits.Clear(); HitAt.Clear(); _lastKind = null; Reading = Understand("");
        Ph = ("", -10); PhOld = ("", -10); Voice = ("", -10); VoiceOld = ("", -10);
        Fs.Snap(23); ChipT.Snap(0); GoDark.Snap(0); HasPick.Snap(0); Pan.Snap(0); GoHover.Snap(0);
        foreach (var s in Pick) s.Snap(0);
        foreach (var s in Hov) s.Snap(0);
        _typing = ""; _then = null; _hoverTile = -1; _idleAt = -1;
        Minis = new(); _tileNext = _clock + 1; _sug = 0;
        MoonShow.To(0); MoonPull.To(1);
        Unstamp(true);
        // in one motion, he pours himself out of wherever he is into the line
        var from = BloubBall(m);
        Pose = null; _goAt = -1;
        Stretch(from);
        m.FocusField(Field);
        _then = () =>
        {
            Armed = true; ArmedAt = _clock;
            if (Ph.Text.Length == 0) PutPh(Question);
            SayVoice(Hint);
            _phNext = _clock + 3;
            if (text.Length > 0) TypeOut(text, 0.012f, null);
        };
        _thenAt = _clock + (fromBuild ? 0.3f : 0.38f);
    }

    /// <summary>Bloub's ball right now, in island coordinates.</summary>
    static Vector3 BloubBall(IslandModel m)
    {
        float left = IslandModel.CX - m.Live.W / 2;
        return new(m.Face.X - left, m.Face.Y + m.BloubLift - m.Top, 11 * m.Face.Size.Value * m.Bloub.Squish.Value);
    }

    void PutPh(string text)
    {
        if (text == Ph.Text) return;
        // the one showing rolls out (anything still rolling out goes at once)
        PhOld = (Ph.Text, _clock);
        Ph = (text, _clock);
    }
    void SayVoice(string text) { if (text == Voice.Text) return; VoiceOld = (Voice.Text, _clock); Voice = (text, _clock); }

    void TypeOut(string text, float every, Action? then)
    {
        Field.Clear(); _lastText = ""; OnEdited(false);
        _typing = text; _typeEvery = every; _typeNext = _clock; _typeThen = then;
    }

    /// <summary>Every change of the text: the understood words go dark, the chip and button follow, his eyes react.</summary>
    void OnEdited(bool fromTyping)
    {
        string text = Field.Text;
        var r = Understand(text);
        Reading = r;
        var hits = r.Tokens.Where(t => t.Hit).Select(t => t.At).ToHashSet();
        bool fresh = false;
        foreach (var h in hits) if (!_lastHits.Contains(h)) { fresh = true; HitAt[h] = _clock; }
        _lastHits = hits;
        Ready = text.Trim().Length >= 3;
        ChipT.To(r.Kind != null ? 1 : 0);
        for (int i = 0; i < Pick.Length; i++) Pick[i].To(r.Kind == Kinds[i] ? 1 : 0);
        HasPick.To(r.Kind != null ? 1 : 0);
        GoDark.To(Ready ? 1 : 0);
        Fit(r);
        if (r.Kind != null) SayVoice($"<ok>Got it · {r.Kind.Means(r)}");
        else if (text.Trim().Length == 0 || !Voice.Text.StartsWith("Press")) SayVoice(text.Trim().Length >= 3 ? "Press <k>Enter</k> and I’ll find a live source for it" : Hint);
        _idleAt = -1; Tilt.To(0);
        if (fromTyping) { Ps.Velocity -= 0.14f; Hop.Velocity -= 45; }
        if (r.Kind != null && r.Kind != _lastKind) { Hop.Velocity -= 330; EyeS.Velocity += 6; Ps.Velocity -= 0.5f; }
        else if (fromTyping && fresh) { Hop.Velocity -= 160; Ps.Velocity -= 0.2f; }
        if (r.Kind == null && text.Trim().Length > 0) _idleAt = _clock + 1.6f;
        _lastKind = r.Kind;
    }

    /// <summary>Long sentences shrink to fit the line.</summary>
    void Fit(Read r)
    {
        if (Measure == null) return;
        float avail = Pill.W - 69 - 10 - (r.Kind != null ? 176 : 62);
        float w23 = Measure(Field.Text, 23);
        // (down to a size that still reads well; past that the line slides to follow the caret)
        float want = w23 <= 0 ? 23 : Math.Clamp(23 * avail / w23, 17, 23);
        if (MathF.Abs(Fs.Target - want) > 0.3f) Fs.To(want);
    }
    /// <summary>Text measuring (set by the scene, which owns the fonts).</summary>
    public static Func<string, float, float>? Measure;

    /// <summary>"no": the line shakes its head.</summary>
    void Shake() { Sx.Snap(0); Sx.Velocity = 420; Tilt.To(0); }

    // ================================================================ building

    public TabBuilder? Builder { get; private set; }
    public string Title { get; private set; } = "";
    public float TitleFs { get; private set; } = 23;
    public Vector2 TitleFrom { get; private set; }
    public float BuildAt { get; private set; } = -10;
    public float DotsAt { get; private set; } = -10;
    public float DotsOffAt { get; private set; } = -10;
    public (TabBuilder.Step Step, float At)? Step, StepOld;
    float _stepNext;
    bool _filling;
    public CustomTab? Preview { get; private set; }
    public float SolidAt { get; private set; } = -10;
    public float BarAt { get; private set; } = -10;
    public bool Done { get; private set; }
    public bool Failed { get; private set; }
    public readonly List<(string Label, JsonObject Patch, float GoneAt)> Tweaks = new();
    public float AdjustAt { get; private set; } = -10;
    bool _tweaking;
    float _doneAt = -1, _celebrateAt = -1;

    void Submit(IslandModel m)
    {
        if (Now != Stage.Line || Typing || !Armed) return;
        if (Field.Text.Trim().Length == 0) { TypeOut(Suggest[_sug], 0.018f, () => After(0.28f, () => Submit(m))); return; }
        if (Field.Text.Trim().Length < 3) { Shake(); SayVoice("Tell me a little more"); return; }
        Ps.Velocity -= 0.6f;
        Build(m, Field.Text.Trim());
    }

    void After(float secs, Action a) { _then = a; _thenAt = _clock + secs; }

    void Build(IslandModel m, string text)
    {
        Now = Stage.Build; _at = _clock; BuildAt = _clock;
        Diag.Log($"maker: build \"{text}\"");
        Title = char.ToUpperInvariant(text[0]) + text[1..];
        TitleFs = MathF.Round(Fs.Value * 2) / 2;
        var (l, cy, _, _, _) = Line();
        TitleFrom = new Vector2(l + 69, cy - TitleFs * 0.7f);
        Step = null; StepOld = null; _stepNext = _clock + 0.56f; _filling = false;
        Preview = null; SolidAt = -10; BarAt = -10; Done = false; Failed = false; Tweaks.Clear(); _tweaking = false;
        _doneAt = -1; _celebrateAt = -1; DotsOffAt = -10;
        DotsAt = _clock;
        // the line pulls back into Bloub, and he springs up beside the title
        BallBack(0.42f, LeftBall, () =>
        {
            _sq.Snap(0.8f); _sq.To(1);
            Go(new Vector3(42, 92, 20.9f), 0.76f, 22);
        });
        Builder = new TabBuilder(text);
        Builder.Start(About(text));
        m.Bloub.React(BloubState.Thinking, _clock, 600, BloubExpressionId.Attentif);
    }

    void Retry(IslandModel m)
    {
        if (Builder == null) return;
        string text = Builder.Request;
        Step = null; StepOld = null; _stepNext = _clock + 0.3f; _filling = false;
        Preview = null; SolidAt = -10; BarAt = -10; Done = false; Failed = false; Tweaks.Clear();
        _doneAt = -1; _celebrateAt = -1; DotsAt = _clock; DotsOffAt = -10;
        Unstamp(false);
        Builder = new TabBuilder(text);
        Builder.Start(About(text));
        m.Bloub.React(BloubState.Thinking, _clock, 600, BloubExpressionId.Attentif);
    }

    void ShowStep(TabBuilder.Step s)
    {
        Diag.Log($"maker: step {s.Lead} · {s.Bold}");
        StepOld = Step;
        Step = (s, _clock);
        _stepNext = _clock + (s.Lead == "Understanding" ? 0.9f : 1.25f);
    }

    void StepBuild(IslandModel m)
    {
        var b = Builder;
        if (b == null || Done || Failed) return;
        if (_clock < _stepNext) return;
        if (b.Next(out var s))
        {
            ShowStep(s);
            if (s.Bad) { Fail(m); return; }
            if (s.Lead == "Filling in") _filling = true;
            return;
        }
        if (!b.Finished) return;
        if (b.Recipe == null) { Fail(m); return; }
        if (!_filling) { ShowStep(new TabBuilder.Step("Filling in", "real data")); _filling = true; return; }
        if (SolidAt < 0)
        {
            // the shapes turn solid where the dots were; the data decodes in
            Preview = new CustomTab(b.Recipe);
            if (b.Data != null) { b.Data.Host = Preview.Data.Host; Preview.Data = b.Data; }
            SolidAt = _clock + 0.3f;
            DotsOffAt = SolidAt;
            foreach (var (label, patch) in b.Tweaks) Tweaks.Add((label, patch, -1));
            _doneAt = SolidAt + 0.3f + 0.5f + 0.07f * 4;
            return;
        }
        if (_doneAt > 0 && _clock >= _doneAt && !Done)
        {
            Done = true;
            ShowStep(new TabBuilder.Step("Ready", TabBuilder.Summary(b.Recipe, Preview?.Data), Done: true));
            Celebrate(m);
            BarAt = _clock + 1.15f;
        }
    }

    void Fail(IslandModel m)
    {
        Failed = true;
        DotsOffAt = _clock;
        BarAt = _clock + 0.5f;
        _tilt.Snap(0); _tilt.Velocity = 320;   // he shakes his head
        m.Bloub.React(BloubState.Idle, _clock, 2.4, BloubExpressionId.Triste);
    }

    /// <summary>"I made it!": he gathers himself, leaps with a full flip, lands (the floor rings), the ✓ stamps on, two proud hops.</summary>
    void Celebrate(IslandModel m)
    {
        m.Bloub.React(BloubState.Wide, _clock, 3.2, BloubExpressionId.Excite);
        _sq.Configure(new SpringSpec(0.14f, 1f)); _sq.To(0.72f);
        _celebrateAt = _clock;
        After(0.2f, () =>
        {
            _sq.Configure(new SpringSpec(0.38f, 0.45f)); _sq.Snap(1.16f); _sq.To(1);
            _flipAt = _clock + 0.04f; _flipDur = 0.47f; _flipDir = 1; _flipEase = u => Bezier(u, 0.45f, 0.05f, 0.35f, 1);
            Leap(-680, () =>
            {
                Impact(m);
                After(0.12f, () =>
                {
                    StampAt = _clock; UnstampAt = -10;
                    After(0.24f, () => { _sq.Snap(0.84f); _sq.To(1); After(0.52f, () =>
                    {
                        _tilt.To(-7);
                        Leap(-340, () => { _tilt.To(6); Leap(-250, () => _tilt.To(0)); });
                    }); });
                });
            });
        });
    }

    void Impact(IslandModel m)
    {
        var p = Pose ?? new Vector3(42, 92, 20.9f);
        float s = p.Z * 2 / 0.95f, w = s / 44;
        var at = new Vector2(p.X, p.Y + s * 0.47f);
        Rings.Add((at, _clock, w));
        for (int side = -1; side <= 1; side += 2)
            for (int j = 0; j < 3; j++)
                Zips.Add((at + new Vector2(0, -4), side < 0 ? 202 + j * 24 : -22 - j * 24, s * 0.42f, w, _clock + j * 0.025f));
    }

    void Unstamp(bool instant)
    {
        if (!Badged) return;
        if (instant) { StampAt = -10; UnstampAt = -10; return; }
        UnstampAt = _clock;
    }

    void Tweak(IslandModel m, int i)
    {
        if (_tweaking || i < 0 || i >= Tweaks.Count || Tweaks[i].GoneAt > 0 || Builder?.Json is not { } json) return;
        _tweaking = true;
        var (label, patch, _) = Tweaks[i];
        Tweaks[i] = (label, patch, _clock);
        Task.Run(async () =>
        {
            var res = await TabBuilder.Apply(json, patch);
            lock (_tweakLock) _tweakResult = res;
        });
    }
    readonly object _tweakLock = new();
    (Recipe? r, JsonObject? json, CustomData? data, string? problem)? _tweakResult;

    // ================================================================ adding it to your island

    public string? AddingId { get; private set; }
    public float ChipAt { get; private set; } = -10;
    public float FlightAt { get; private set; } = -10;
    public Vector2 ChipFrom => new(362, 210);
    public string ChipName { get; private set; } = "";
    public char ChipIcon { get; private set; }
    public float AddAt { get; private set; } = -10;

    void Add(IslandModel m)
    {
        if (Now != Stage.Build || !Done || Builder?.Json is not { } json || Preview == null) return;
        Now = Stage.Adding; _at = _clock; AddAt = _clock;
        Diag.Log("maker: add");
        var recipe = Preview.Recipe;
        ChipName = recipe.Name; ChipIcon = recipe.Icon;
        ChipAt = _clock + 0.12f; FlightAt = -10;
        _crouchAt = _clock;                                    // Bloub gets ready: a little crouch
        var tab = CustomTabs.Install(TabBuilder.Strip(json), Preview.Data);
        AddingId = tab.Id;
        After(0.52f, () =>
        {
            var tb = m.Tabs;
            TabBar.RefreshAll();
            if (tb.Enabled.Count >= TabBar.MaxTabs) tb.Enabled.RemoveAt(tb.Enabled.Count - 1);
            tb.Enabled.Add(tab.Id);
            var s = tb[tab.Id];
            s.Placed = false; s.A.Snap(1); s.On.Snap(1); s.Fill.Snap(1);
            tb.HiddenTab = tab.Id;
            tb.Selected = tab.Id;
            tb.Save();
            tb.SetEditing(m, false);
            m.FocusField(null);
            FlightAt = _clock;
            // …and Bloub jumps home with a twirl, popping confetti at the top
            var home = new Vector3(40, 32, 14.3f);
            Go(home, 0.98f, 70, u => Bezier(u, 0.3f, 0, 0.25f, 1));
            _flipAt = _clock + 0.08f; _flipDur = 0.76f; _flipDir = 1; _flipEase = u => Bezier(u, 0.45f, 0, 0.25f, 1);
            After(0.43f, () =>
            {
                var p = Pose ?? home;
                Burst(new Vector2(p.X, p.Y));
                After(0.33f, () =>
                {
                    // the chip lands: the tab takes its place
                    tb.HiddenTab = null;
                    s.Pop.Snap(1.16f); s.Pop.To(1);
                    m.TabFade.Snap(0); m.TabFade.To(1);
                    After(0.23f, () =>
                    {
                        _landAt = _clock; _wiggleAt = _clock + 0.46f;
                        m.Bloub.React(BloubState.Wink, _clock, 1.4, BloubExpressionId.Heureux);
                        After(0.9f, () =>
                        {
                            Unstamp(false);
                            After(0.5f, () => Finish(m, $"“{ChipName}” is on your island"));
                        });
                    });
                });
            });
        });
    }

    void Burst(Vector2 at)
    {
        uint[] cols = [0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0x30D158, 0x2D8CFF, 0xF0479B, 0xFFD60A, 0xFFFFFF, 0xE5374C];
        var rng = new Random();
        for (int i = 0; i < 18; i++)
        {
            float a = i / 18f * MathF.Tau + (float)rng.NextDouble() * 0.3f, r = 34 + (float)rng.NextDouble() * 30;
            var to = at + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r * 0.8f);
            Confetti.Add((at, to, 3 + (float)rng.NextDouble() * 3.5f, cols[i % cols.Length], _clock, 0.9f + (float)rng.NextDouble() * 0.3f));
        }
    }

    /// <summary>Back to normal: the island places Bloub again.</summary>
    void Finish(IslandModel m, string? toast)
    {
        Diag.Log($"maker: finish ({toast ?? "reset"})");
        Now = Stage.Off;
        Pose = null; _goAt = -1;
        _morph = Morph.None;
        Builder?.Cancel(); Builder = null; Preview = null;
        StampAt = -10; UnstampAt = -10;
        m.Tabs.HiddenTab = null;
        if (m.Focused == Field) m.FocusField(null);
        m.Face.Opacity.Snap(1);
        if (toast != null) m.ShowToast(toast);
        AddingId = null;
    }

    // ================================================================ leaving: back into the orb, the library comes back

    public readonly Spring MoonShow = new(1, new SpringSpec(0.3f, 1f), 0.002f), MoonPull = new(0, new SpringSpec(0.42f, 0.9f), 0.002f);

    public void Cancel(IslandModel m)
    {
        if (Now is Stage.Off or Stage.Adding) return;
        Builder?.Cancel(); Builder = null;
        _then = null; _typing = "";
        Unstamp(false);
        bool line = _morph != Morph.None;
        Now = Stage.Off; LibBackAt = _clock;
        if (m.Focused == Field) m.FocusField(null);
        m.Bloub.React(BloubState.Idle, _clock, 0.1);
        if (line)
        {
            BallBack(0.44f, OrbBall, () => Pose = null);
            MoonsOutAt(0.42f);
        }
        else
        {
            Go(OrbBall, 0.76f, 34, u => Bezier(u, 0.4f, 0, 0.3f, 1));
            _releaseAt = _clock + 0.76f;
            MoonsOutAt(0.74f);
        }
    }
    float _releaseAt = -1, _moonsAt = -1;
    string? _libHover;
    void MoonsOutAt(float secs) => _moonsAt = _clock + secs;

    /// <summary>The island left the dashboard: drop everything at once.</summary>
    public void Reset(IslandModel m)
    {
        if (Now == Stage.Off && _morph == Morph.None && Pose == null) return;
        Finish(m, null);
        MoonShow.Snap(1); MoonPull.Snap(0);
        _then = null; _typing = ""; _air = false; _jy = 0; _vy = 0; _sq.Snap(1); _tilt.Snap(0); _flipAt = -10; _releaseAt = -1; _moonsAt = -1;
    }

    // ================================================================ input

    public void Act(IslandModel m, string[] p)
    {
        switch (p.ElementAtOrDefault(1))
        {
            case "new" or "orb": Open(m); break;
            case "field":
                if (Now != Stage.Line) break;
                m.FocusField(Field);
                // the caret goes where you clicked, between the nearest two letters
                if (!Typing && Measure != null && Field.Text.Length > 0)
                {
                    float x = m.Cursor.X - TextX + Pan.Value;
                    int best = Field.Text.Length; float bestD = float.MaxValue;
                    for (int i = 0; i <= Field.Text.Length; i++)
                    {
                        float d = MathF.Abs(Measure(Field.Text[..i], TextFs) - x);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    Field.Caret = best;
                    Field.ChangedAt = m.Clock;
                }
                break;
            case "go": Submit(m); break;
            case "tile" when Now == Stage.Line && Armed && !Typing && int.TryParse(p.ElementAtOrDefault(2), out int ti) && ti < Kinds.Length:
                _hoverTile = -1;
                TypeOut(Kinds[ti].Starter.ToLowerInvariant(), 0.022f, () => After(0.34f, () => Submit(m)));
                break;
            case "add": Add(m); break;
            case "tw" when int.TryParse(p.ElementAtOrDefault(2), out int wi): Tweak(m, wi); break;
            case "retry": Retry(m); break;
            case "del" when p.Length > 2 && Now == Stage.Off: m.Tabs.Delete(m, p[2]); break;
            case "edit": if (Builder?.Request is { } req) Open(m, req); break;
        }
    }

    /// <summary>Dev demo: he types the sentence himself, then builds it.</summary>
    public void Demo(IslandModel m, string text)
    {
        if (Now == Stage.Line && Armed) TypeOut(text, 0.04f, () => After(0.5f, () => Submit(m)));
    }

    /// <summary>Keys while the field is focused. Returns true when handled.</summary>
    public bool Key(IslandModel m, char ch)
    {
        switch (ch)
        {
            case (char)13:
                if (Now == Stage.Line) Submit(m);
                else if (Now == Stage.Build && Done) Add(m);
                else if (Now == Stage.Build && Failed) Retry(m);
                return true;
            case (char)27: Cancel(m); return true;
            case (char)9:
                if (Now == Stage.Line && Armed && Field.Text.Length == 0 && !Typing) TypeOut(Suggest[_sug], 0.018f, null);
                return true;
        }
        return Now != Stage.Line || Typing;   // while he types or builds, your keys don't edit the line
    }

    // ================================================================ every frame

    public void Update(IslandModel m, float dt)
    {
        _clock = m.Clock;
        if (Active && m.Mode != Mode.Full) Reset(m);

        foreach (var s in (Spring[])[_mw, _mh, _mp, _bs, _bp, EyeR, EyeDx, Hop, Tilt, EyeS, Ps, Sx, Fs, ChipT, GoDark, GoHover, Pan, _sq, _tilt, MoonShow, MoonPull, HasPick]) s.Step(dt);
        foreach (var s in Pick) s.Step(dt);
        foreach (var s in Hov) s.Step(dt);

        // gravity: his jumps, and every jump lands
        if (_air)
        {
            const int n = 8;
            for (int i = 0; i < n && _air; i++)
            {
                float h = dt / n;
                _vy += Gravity * h; _jy += _vy * h;
                if (_jy >= 0)
                {
                    float v = _vy;
                    _jy = 0; _vy = 0; _air = false;
                    _sq.Snap(1 - MathF.Min(0.3f, v / 2400)); _sq.To(1);
                    var land = _land; _land = null;
                    land?.Invoke();
                }
            }
        }

        // where he is: a calm arc between two places
        if (_goAt >= 0)
        {
            float u = Math.Clamp((_clock - _goAt) / _goDur, 0, 1), e = _goEase(u);
            var p = Vector3.Lerp(_goFrom, _goTo, e);
            p.Y -= MathF.Sin(u * MathF.PI) * _goLift;
            Pose = p;
            if (u >= 1) _goAt = -1;
        }
        if (_releaseAt > 0 && _clock >= _releaseAt) { _releaseAt = -1; Pose = null; }
        if (_moonsAt > 0 && _clock >= _moonsAt)
        {
            _moonsAt = -1;
            MoonPull.Snap(1); MoonPull.To(0); MoonShow.To(1);
            _sq.Snap(0.8f); _sq.To(1);
        }

        // the line's motion ends: back to Bloub
        if (_morph == Morph.Back && _clock - _morphAt >= _backDur)
        {
            _morph = Morph.None;
            Pose = _to;
            m.Face.X.Snap(IslandModel.CX - m.Live.W / 2 + _to.X); m.Face.Y.Snap(m.Top + _to.Y); m.Face.Size.Snap(_to.Z / 11);
            m.Face.Opacity.Snap(1);
            _sq.Snap(0.86f); _sq.To(1);
            var then = _backThen; _backThen = null;
            then?.Invoke();
        }

        // queued beats
        if (_then != null && _clock >= _thenAt) { var t = _then; _then = null; t(); }

        // effects run out
        Rings.RemoveAll(r => _clock - r.T0 > 0.64f);
        Zips.RemoveAll(z => _clock - z.T0 > 0.5f);
        Confetti.RemoveAll(c => _clock - c.T0 > c.Dur);

        // in the library: his orb and the New tile say what they do
        if (Now == Stage.Off && m.Mode == Mode.Full && m.Tabs.Editing)
        {
            string? h = m.HoverHit is "mk:new" or "mk:orb" ? m.HoverHit : null;
            if (h != _libHover)
            {
                _libHover = h;
                if (h == "mk:orb") { m.Tabs.Hint("Tap me to describe a new tab", '\uE945'); _sq.Velocity -= 1.3f; }
                else if (h == "mk:new") m.Tabs.Hint("Describe any tab you want", '\uE945');
                else m.Tabs.Hint(null, ' ');
            }
        }

        switch (Now)
        {
            case Stage.Line: UpdateLine(m); break;
            case Stage.Build:
                StepBuild(m);
                (Recipe? r, JsonObject? json, CustomData? data, string? problem)? got;
                lock (_tweakLock) { got = _tweakResult; _tweakResult = null; }
                if (got is { } tw)
                {
                    _tweaking = false;
                    if (tw.r != null && tw.json != null)
                    {
                        Builder?.Adopt(tw.r, tw.json, tw.data);
                        Preview = new CustomTab(tw.r);
                        if (tw.data != null) { tw.data.Host = Preview.Data.Host; Preview.Data = tw.data; }
                        AdjustAt = _clock;
                        ShowStep(new TabBuilder.Step("Ready", TabBuilder.Summary(tw.r, tw.data), Done: true));
                        _sq.Snap(0.86f); _sq.To(1); _tilt.Velocity += 90;
                    }
                    else { _tilt.Snap(0); _tilt.Velocity = 320; m.ShowToast("That change didn’t work with this source"); }
                }
                break;
        }
    }

    void UpdateLine(IslandModel m)
    {
        // he types a sentence himself (a suggestion, a preview you picked)
        if (_typing.Length > 0 && _clock >= _typeNext)
        {
            char ch = _typing[0];
            _typing = _typing[1..];
            Field.Insert(ch.ToString());
            _lastText = Field.Text;
            OnEdited(true);
            _typeNext = _clock + _typeEvery + (ch == ' ' ? 0.03f : 0);
            if (_typing.Length == 0) { var t = _typeThen; _typeThen = null; t?.Invoke(); }
        }
        if (Field.Text.Length > MaxText) Field.Set(Field.Text[..MaxText]);
        GoHover.To(m.HoverHit == "mk:go" ? 1 : 0);
        if (Field.Text != _lastText) { _lastText = Field.Text; OnEdited(true); }

        // a puzzled tilt when he doesn't recognise it (Claude will still look for it)
        if (_idleAt > 0 && _clock >= _idleAt) { _idleAt = -1; Tilt.To(-12); }

        // hovering a preview shows its sentence in the empty line
        int hover = m.HoverHit is { } h && h.StartsWith("mk:tile:") && int.TryParse(h[8..], out int hi) ? hi : -1;
        if (hover != _hoverTile) { _hoverTile = hover; _hoverSince = _clock; }
        bool held = hover >= 0 && _clock - _hoverSince > 0.14f;
        if (Armed && held && Field.Text.Length == 0 && !Typing && Ph.Text != Suggest[hover]) { _sug = hover; PutPh(Suggest[hover]); _phNext = _clock + 3; }

        // the grey suggestion rolls on now and then
        if (Armed && _clock >= _phNext)
        {
            _phNext = _clock + 3;
            if (Field.Text.Length == 0 && hover < 0 && !Typing)
            {
                if (Ph.Text != Question) _sug = (_sug + 1) % Suggest.Length;
                PutPh(Suggest[_sug]);
            }
        }

        // the previews tick along
        if (Armed && _clock >= _tileNext) { _tileNext = _clock + 1; TickMinis(); }
    }

    readonly Random _rng = new();
    void TickMinis()
    {
        var t = Minis;
        int n = ++t.N;
        if (n % 4 == 2)
        {
            int i = (n >> 2) % 2; bool away = (n >> 3) % 2 == 1;
            if (away) t.As[i]++; else t.Hs[i]++;
            t.RollAt[i] = _clock; t.RollAway[i] = away; t.GoalAt[i] = _clock;
        }
        if (n % 2 == 0)
        {
            int d = (int)MathF.Round(((float)_rng.NextDouble() - 0.42f) * 90);
            t.Price += d; t.PriceUp = d >= 0; t.PriceAt = _clock;
        }
        if (n % 5 == 3) { t.Points[2] += 3 + _rng.Next(20); t.PointsAt = _clock; }
        if (n % 6 == 5) t.BarsAt = _clock;
    }
}
