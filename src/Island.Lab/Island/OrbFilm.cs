using System.Numerics;
using System.Text.Json;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Island.Lab.Render;

namespace Island.Lab.Island;

/// <summary>
/// Bloub's states from the reference film (orb_aniamtionState.mp4), replayed frame for frame: the Greeting (he
/// dips, two hands pop out, one waves), the file (he becomes a box, opens a slot, a sheet drops in) and Finished
/// (the green light, the blue draining out, his eyes rolling back in). The shapes were traced from the film in
/// every frame (design/show-bloub-once.html) and baked into Assets/orbfilm.json: thousandths of his radius,
/// 60 frames a second. While a state plays it is drawn in place of the island's own Bloub, who is never drawn under
/// it (two faces at once looked wrong): only the ball's paint eases between his look and the film's at both ends.
/// </summary>
public sealed class OrbFilm
{
    public enum Clip { None, Greet, Slot, File, Done }

    sealed class Frame
    {
        public int[] B = [], W = [0, 0, 0];   // body; blue wash, flat shading, dimming
        public int[][] E = [], H = [];        // eyes; hands (centre, then outline)
        public int[][] E2 = [];               // eyes that are smooth outlines even while E is plain polygons (Finished's eyes coming up as the last ones go down)
        public int[]? S, P, KP, KM, C, G;     // slot; paper outline; its curl (outline, shading); pointer; green light
        public bool Poly;                     // eyes are plain polygons (the join between two takes), not smooth outlines
    }

    // ---- Finished, smooth. The film moves on twos (each pose is held for two frames, and a blink is only four
    // poses), so replaying its frames looks like ten frames a second on a big Bloub. Each channel's poses become
    // keys instead, joined by a curve that passes through every one of them and never overshoots: the same motion,
    // at whatever rate the screen draws. (The same method as FINS in design/new-agent.html.)

    /// <summary>One channel of the film: its pose changes as keys, joined by a monotone cubic.</summary>
    sealed class Track
    {
        readonly float[] _f; readonly float[][] _v, _m;
        public int Width => _v[0].Length;
        public float First => _f[0];

        public Track(List<(float F, float[] V)> keys)
        {
            int n = keys.Count, w = keys[0].V.Length;
            _f = keys.Select(k => k.F).ToArray(); _v = keys.Select(k => k.V).ToArray(); _m = new float[n][];
            for (int i = 0; i < n; i++) _m[i] = new float[w];
            if (n < 2) return;
            var d = new float[n - 1];
            for (int c = 0; c < w; c++)
            {
                for (int i = 0; i < n - 1; i++) d[i] = (_v[i + 1][c] - _v[i][c]) / (_f[i + 1] - _f[i]);
                _m[0][c] = d[0];   // (it comes in moving, and it settles at the end)
                for (int i = 1; i < n - 1; i++)
                    if (d[i - 1] * d[i] > 0)
                    {
                        float h0 = _f[i] - _f[i - 1], h1 = _f[i + 1] - _f[i], w1 = 2 * h1 + h0, w2 = h1 + 2 * h0;
                        _m[i][c] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
                    }
            }
        }

        /// <summary>The keys of <paramref name="rows"/> (one per frame from <paramref name="from"/>): a new one wherever a value moves more than <paramref name="tol"/> from the last.</summary>
        public static List<(float F, float[] V)> Keys(IReadOnlyList<int[]?> rows, int from, float tol)
        {
            var keys = new List<(float, float[])>(); int[]? last = null;
            for (int f = from; f < rows.Count; f++)
            {
                if (rows[f] is not { } v || last != null && v.Length != last.Length) continue;
                bool moved = last == null;
                for (int j = 0; !moved && j < v.Length; j++) moved = MathF.Abs(v[j] - last![j]) > tol;
                if (moved) { keys.Add((f, v.Select(x => (float)x).ToArray())); last = v; }
            }
            return keys;
        }

        /// <summary>
        /// The channel at frame <paramref name="f"/>. With <paramref name="soft"/> (in frames) it is the curve seen through a
        /// short bell: the film's own timing wavers (a pose a frame early, then held for three), and this takes that
        /// waver out of his speed without moving where he ends up.
        /// </summary>
        public int[] At(float f, float soft = 0)
        {
            int w = Width; var acc = new float[w]; var o = new int[w];
            if (soft <= 0) Add(f, 1, acc);
            else { Add(f - 2 * soft, 0.0545f, acc); Add(f - soft, 0.2442f, acc); Add(f, 0.4026f, acc); Add(f + soft, 0.2442f, acc); Add(f + 2 * soft, 0.0545f, acc); }
            for (int c = 0; c < w; c++) o[c] = (int)MathF.Round(acc[c]);
            return o;
        }

        void Add(float f, float weight, float[] acc)
        {
            int n = _f.Length, w = Width;
            if (f <= _f[0] || n == 1) { for (int c = 0; c < w; c++) acc[c] += weight * _v[0][c]; return; }
            if (f >= _f[n - 1]) { for (int c = 0; c < w; c++) acc[c] += weight * _v[n - 1][c]; return; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (_f[mid] <= f) lo = mid; else hi = mid; }
            float h = _f[hi] - _f[lo], u = (f - _f[lo]) / h, u2 = u * u, u3 = u2 * u;
            float a = 2 * u3 - 3 * u2 + 1, b = (u3 - 2 * u2 + u) * h, cc = 3 * u2 - 2 * u3, e = (u3 - u2) * h;
            for (int c = 0; c < w; c++) acc[c] += weight * (a * _v[lo][c] + b * _m[lo][c] + cc * _v[hi][c] + e * _m[hi][c]);
        }
    }

    static Track? _doneBody, _doneLight;
    static Track[] _doneEyes = [];
    static int _doneFilm = -1;            // the first frame of the film's own Finished (before it: the join from the file)
    static Vector2 _lightAt;              // where the green light sits from his centre (it rides his shoulder)
    /// <summary>The green light: 2.3 times the film's (a quarter of his own radius: the size the user settled on, having tried 3.2); it sits a little further out on his rim, and the gap cut round it is wider to match.</summary>
    const float LightScale = 2.3f, LightOut = 30, LightGap = 65;
    const int EyesUnder = 3, EyesDrop = 640;
    const float Soft = 1.3f;              // (frames: see Track.At)   // (his eyes come up from under him: one pose more, three frames early, out of sight below)

    static void SmoothDone()
    {
        var fr = _done;
        _doneFilm = Array.FindIndex(fr, f => f.E.Length > 0 && !f.Poly);
        if (_doneFilm < 0) return;
        _doneBody = new Track(Track.Keys(fr.Select(f => (int[]?)f.B).ToArray(), 0, 1.5f));
        int eyes = fr[_doneFilm].E.Length;
        var tracks = new List<Track>();
        for (int e = 0; e < eyes; e++)
        {
            int ee = e;
            var keys = Track.Keys(fr.Select((f, i) => i >= _doneFilm && !f.Poly && f.E.Length == eyes ? f.E[ee] : (int[]?)null).ToArray(), _doneFilm, 2f);
            if (keys.Count == 0) { _doneFilm = -1; return; }
            var under = (float[])keys[0].V.Clone();
            for (int j = 1; j < under.Length; j += 2) under[j] += EyesDrop;
            keys.Insert(0, (keys[0].F - EyesUnder, under));
            tracks.Add(new Track(keys));
        }
        _doneEyes = tracks.ToArray();
        // the light: it grows in as the film has it, then keeps one size (the film's wavers by a few thousandths from frame to frame) at one place on his shoulder
        var lit = fr.Select((f, i) => (f, i)).Where(x => x.f.G != null).ToList();
        if (lit.Count == 0) return;
        float Median(IEnumerable<float> v) { var s = v.OrderBy(x => x).ToArray(); return s[s.Length / 2]; }
        var late = lit.Where(x => x.i >= _doneFilm).DefaultIfEmpty(lit[^1]).ToList();
        _lightAt = new Vector2(Median(late.Select(x => (float)(x.f.G![0] - x.f.B[0]))), Median(late.Select(x => (float)(x.f.G![1] - x.f.B[1]))));
        var grow = Track.Keys(fr.Select((f, i) => i < _doneFilm && f.G != null ? new[] { f.G[2] } : (int[]?)null).ToArray(), 0, 1.5f);
        if (grow.Count > 0 && grow[0].F > 0) grow.Insert(0, (grow[0].F - 1, new float[] { 0f }));
        grow.Add((MathF.Max(_doneFilm, grow.Count > 0 ? grow[^1].F + 1 : 0), new float[] { Median(late.Select(x => (float)x.f.G![2])) }));
        _doneLight = new Track(grow);
    }

    /// <summary>Finished at playback frame <paramref name="tf"/>, between the film's poses.</summary>
    static Frame DonePose(float tf)
    {
        var fr = _done;
        tf = Math.Clamp(tf, 0, fr.Length - 1);
        int i0 = (int)MathF.Floor(tf), i1 = Math.Min(fr.Length - 1, i0 + 1); float u = tf - i0;
        var near = fr[u < 0.5f ? i0 : i1];
        if (_doneFilm < 0 || _doneBody == null) return near;
        int[] a = fr[i0].W, b = fr[i1].W, w = new int[a.Length];
        for (int j = 0; j < w.Length; j++) w[j] = (int)MathF.Round(a[j] + (b[j] - a[j]) * u);
        var f = new Frame { B = _doneBody.At(tf, Soft), W = w };
        // his eyes: the join's go down and out; the film's come up from under him and carry on
        bool join = near.Poly;
        if (join)
        {
            f.Poly = true;
            var x = fr[i0].E; var y = fr[i1].E;
            f.E = fr[i0].Poly && fr[i1].Poly && x.Length == y.Length && x.Zip(y).All(p => p.First.Length == p.Second.Length)
                ? x.Select((v, k) => { var o = new int[v.Length]; for (int j = 0; j < o.Length; j++) o[j] = (int)MathF.Round(v[j] + (y[k][j] - v[j]) * u); return o; }).ToArray()
                : near.E;
        }
        if (tf >= _doneFilm - EyesUnder)
        {
            var film = new int[_doneEyes.Length][];
            for (int e = 0; e < film.Length; e++) film[e] = _doneEyes[e].At(tf, Soft);
            if (join) f.E2 = film; else f.E = film;
        }
        if (_doneLight != null && tf > _doneLight.First)
        {
            float len = _lightAt.Length(), k = len > 1 ? 1 + LightOut / len : 1;
            f.G = [(int)MathF.Round(f.B[0] + _lightAt.X * k), (int)MathF.Round(f.B[1] + _lightAt.Y * k), _doneLight.At(tf)[0]];
        }
        return f;
    }

    static Frame? _doneRest;
    /// <summary>Finished with its eyes' moves made bigger: how far each eye's middle is (from his centre) from where it ends up, times <paramref name="amp"/>, coming in over frames 34 to 44 (before, they are still rolling up from under him). Each eye moves whole: its shape stays the film's.</summary>
    static Frame Stronger(Frame f, float tf, float amp)
    {
        _doneRest ??= DonePose(_done.Length - 1);
        var rest = _doneRest;
        if (rest.E.Length != f.E.Length) return f;
        float u = Math.Clamp((tf - 34) / 10f, 0, 1), k = (amp - 1) * u * u * (3 - 2 * u);
        if (k <= 0) return f;
        var e = new int[f.E.Length][];
        for (int i = 0; i < e.Length; i++)
        {
            var a = f.E[i]; var b = rest.E[i];
            if (a.Length != b.Length) return f;
            float ax = 0, ay = 0, bx = 0, by = 0; int m = a.Length / 2;
            for (int j = 0; j + 1 < a.Length; j += 2) { ax += a[j]; ay += a[j + 1]; bx += b[j]; by += b[j + 1]; }
            if (m == 0) return f;
            float dx = (ax / m - f.B[0] - (bx / m - rest.B[0])) * k, dy = (ay / m - f.B[1] - (by / m - rest.B[1])) * k;
            e[i] = new int[a.Length];
            for (int j = 0; j + 1 < a.Length; j += 2) { e[i][j] = (int)MathF.Round(a[j] + dx); e[i][j + 1] = (int)MathF.Round(a[j + 1] + dy); }
        }
        return new Frame { B = f.B, W = f.W, E = e, E2 = f.E2, H = f.H, S = f.S, P = f.P, KP = f.KP, KM = f.KM, C = f.C, G = f.G, Poly = f.Poly };
    }

    /// <summary>Dev: Finished sampled <paramref name="perSecond"/> times a second, one line each (his centre, the first eye's centre, the light), to show no pose is held.</summary>
    public static string DoneCheck(int perSecond)
    {
        Load();
        var sb = new System.Text.StringBuilder();
        int held = 0, n = 0; (int, int)? last = null;
        for (float t = 0; t <= (_done.Length - 1) / Fps; t += 1f / perSecond, n++)
        {
            var f = DonePose(t * Fps);
            var e = f.E2.Length > 0 ? f.E2[0] : f.E.Length > 0 ? f.E[0] : null;
            int ex = 0, ey = 0;
            if (e != null) { for (int j = 0; j + 1 < e.Length; j += 2) { ex += e[j]; ey += e[j + 1]; } ex /= e.Length / 2; ey /= e.Length / 2; }
            var raw = _done[Math.Clamp((int)MathF.Round(t * Fps), 0, _done.Length - 1)];
            int ry = 0; if (raw.E.Length > 0) { for (int j = 1; j < raw.E[0].Length; j += 2) ry += raw.E[0][j]; ry /= raw.E[0].Length / 2; }
            if (last == (ex, ey)) held++;
            last = (ex, ey);
            sb.AppendLine($"{t * Fps,7:0.00}  body {f.B[0],5} {f.B[1],5} {f.B[2],5}  eye {ex,5} {ey,5}  (film's frame: {ry,5})  light {(f.G is { } g ? $"{g[0],5} {g[1],5} {g[2],4}" : "-")}");
        }
        sb.AppendLine($"samples {n}, of which the eye did not move from the one before: {held}");
        return sb.ToString();
    }

    static Frame[] _greet = [], _file = [], _done = [];
    static int _paperFrom;
    static bool _loaded;
    const float Fps = 60;

    static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            using var s = typeof(OrbFilm).Assembly.GetManifestResourceStream("orbfilm.json");
            if (s == null) { Diag.Log("orbfilm.json is missing"); return; }
            using var doc = JsonDocument.Parse(s);
            _greet = Frames(doc.RootElement.GetProperty("greet"));
            _file = Frames(doc.RootElement.GetProperty("file"));
            _done = Frames(doc.RootElement.GetProperty("done"));
            _paperFrom = Math.Max(0, Array.FindIndex(_file, f => f.P != null));
            foreach (var f in _greet) Array.Sort(f.H, (a, b) => a[0].CompareTo(b[0]));   // left hand first, so a hand blends with itself from frame to frame
            SmoothDone();
        }
        catch (Exception e) { Diag.Log("orbfilm: " + e.Message); _greet = _file = _done = []; }
    }

    static Frame[] Frames(JsonElement a) => a.EnumerateArray().Select(e => new Frame
    {
        B = Ints(e, "b") ?? [0, 0, 1000], W = Ints(e, "w") ?? [0, 0, 0], E = Lists(e, "e"), H = Lists(e, "h"),
        S = Ints(e, "s"), P = Ints(e, "p"), KP = Ints(e, "kp"), KM = Ints(e, "km"), C = Ints(e, "c"), G = Ints(e, "g"),
        Poly = e.TryGetProperty("poly", out _),
    }).ToArray();

    static int[]? Ints(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null;
    static int[][] Lists(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.EnumerateArray().Select(a => a.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray() : [];

    public Clip Now { get; private set; }
    bool _stay;

    // ---- A session finished: he goes out to your pointer (DoneFlight moves him; here is how he looks at it). His eyes
    // roll in on the side the pointer is on (the film's Finished, mirrored and turned about his centre), and he keeps
    // watching it: when it crosses him, a blink, and his eyes are on the other side. The green light keeps clear of his eyes.
    /// <summary>Where your pointer is from his centre, in his radii (the host sets it as he sets off, the stage while he is out).</summary>
    public Vector2 Toward;
    /// <summary>Out there, his eyes are also turned a little toward your pointer (false: only mirrored to its side, so Finished plays exactly as in the film).</summary>
    public bool Turn = true;
    float _sx = 1, _sy = 1, _wx = 1, _wy = 1, _rot, _fl = -1, _ba;
    bool _aimed;
    /// <summary>He is out of the island, at your pointer: the stage draws him (<see cref="DrawOut"/>), the island does not.</summary>
    public bool Out;
    /// <summary>Out there, on his way home: 0 the film's grey with the light on, 1 the island Bloub's own paint, the light out.</summary>
    public float Homing;
    /// <summary>Finished's eyes are in and the light is on: he can set off.</summary>
    public bool EyesIn => Now == Clip.Done && _stay && !_ending && _hold <= 0 && _t * Fps > _doneFilm + 54;
    const float Dir0 = -1.03038f;   // (Dir0: the way the film's eyes roll in, up and to the right)

    void Follow(float dt)
    {
        float tf = _t * Fps, len = Toward.Length();
        bool has = len > 0.35f;
        float dx = has ? Toward.X : 0.354f, dy = has ? Toward.Y : -0.59f;
        float wsx = dx < 0 ? -1 : 1, wsy = dy > 0 ? -1 : 1;
        float wrot = Turn ? Math.Clamp(MathF.Atan2(-MathF.Abs(dy), MathF.Abs(dx)) - Dir0, -0.42f, 0.42f) : 0;
        float wba = MathF.Atan2(-0.635f, dx < 0 && dy <= 0 ? 0.86f : -0.86f);
        if (!_aimed) { _aimed = true; _sx = wsx; _sy = wsy; _rot = wrot; _ba = wba; }      // his first look is decided as he starts
        else if (tf > _doneFilm + 42)                                                      // (while his eyes are rolling in, the aim holds)
        {
            float k = MathF.Min(1, dt * 6), da = wba - _ba;
            while (da > MathF.PI) da -= 2 * MathF.PI; while (da < -MathF.PI) da += 2 * MathF.PI;
            _rot += (wrot - _rot) * k; _ba += da * k;
            if (_fl < 0 && (wsx != _sx || wsy != _sy)) { _fl = 0; _wx = wsx; _wy = wsy; }
        }
        if (_fl >= 0) { float was = _fl; _fl += dt; if (was < 0.09f && _fl >= 0.09f) { _sx = _wx; _sy = _wy; } if (_fl > 0.2f) _fl = -1; }
    }

    /// <summary>The frame with his eyes mirrored and turned about his centre toward the pointer, and the light where it keeps clear of them.</summary>
    Frame Turned(Frame f)
    {
        float cr = MathF.Cos(_rot), sr = MathF.Sin(_rot), cx = f.B[0], cy = f.B[1];
        int[] T(int[] e)
        {
            var o = new int[e.Length];
            for (int j = 0; j + 1 < e.Length; j += 2)
            {
                float x = e[j] - cx, y = e[j + 1] - cy;
                o[j] = (int)MathF.Round(cx + (x * cr - y * sr) * _sx); o[j + 1] = (int)MathF.Round(cy + (x * sr + y * cr) * _sy);
            }
            return o;
        }
        var g = new Frame { B = f.B, W = f.W, Poly = f.Poly, E = f.E.Select(T).ToArray(), E2 = f.E2.Select(T).ToArray(), H = f.H, S = f.S, P = f.P, KP = f.KP, KM = f.KM, C = f.C, G = f.G };
        if (f.G is { } l) { float len = _lightAt.Length() + LightOut; g.G = [(int)MathF.Round(cx + MathF.Cos(_ba) * len), (int)MathF.Round(cy + MathF.Sin(_ba) * len), l[2]]; }
        return g;
    }
    float _t, _hold, _look = 1, _lid = 1, _settle;   // _look: 1 the island Bloub's paint, 0 the film's; _lid: 1 eyes open, 0 shut
    bool _ending;

    /// <summary>The Greeting plays at the film's own speed (blended between its frames, so it is smooth on any display).</summary>
    const float GreetSpeed = 1f;
    // It lasts a little longer than the film's: one beat of the wave (frames 49 to 72, where his pose comes back
    // to within a seventh of his radius of where it was) plays three times instead of once, cross-faded at the seam.
    const int LoopA = 49, LoopB = 72, Loops = 2, LoopFade = 5;
    static float GreetFrames => _greet.Length - 1 + Loops * (LoopB - LoopA);

    /// <summary>The Greeting at playback frame <paramref name="p"/> (which runs past the film's length: the wave repeats).</summary>
    static Frame Greeting(float p)
    {
        int len = LoopB - LoopA, pass = 0;
        float q = p;
        if (p > LoopB)
        {
            float x = p - LoopB; int k = (int)(x / len);
            if (k < Loops) { q = LoopA + (x - k * len); pass = k + 1; } else { q = LoopB + (x - Loops * len); pass = Loops + 1; }
        }
        var f = Blend(_greet, q);
        // this pass ends by going back to the start of the beat: ease into that pose over its last frames
        if (pass < Loops && q > LoopB - LoopFade)
        {
            float w = Math.Clamp((q - (LoopB - LoopFade)) / LoopFade, 0, 1); w = w * w * (3 - 2 * w);
            f = Lerp(f, Blend(_greet, q - len), w);
        }
        return f;
    }

    static Frame Lerp(Frame a, Frame b, float w)
    {
        static int[] Mix(int[] x, int[] y, float w) { var o = new int[x.Length]; for (int i = 0; i < o.Length; i++) o[i] = (int)MathF.Round(x[i] + (y[i] - x[i]) * w); return o; }
        static bool Same(int[][] x, int[][] y) { if (x.Length != y.Length) return false; for (int i = 0; i < x.Length; i++) if (x[i].Length != y[i].Length) return false; return true; }
        static int[][] All(int[][] x, int[][] y, float w) => Same(x, y) ? x.Select((v, i) => Mix(v, y[i], w)).ToArray() : w < 0.5f ? x : y;
        return new Frame { B = Mix(a.B, b.B, w), W = a.W, E = All(a.E, b.E, w), H = All(a.H, b.H, w), S = a.S != null && b.S != null ? Mix(a.S, b.S, w) : w < 0.5f ? a.S : b.S, Poly = a.Poly };
    }

    // The file, as something you really do: while a file hangs over the island he is the box with its slot open
    // (the film's frames 0 to 18, held); when you let go the sheet flies from your cursor to the film's pose above
    // the slot (frame 21) and the film carries on from there; if you take the file away he closes again.
    const int SlotOpen = 21, SheetOver = 21;
    // Waiting, he looks at you with the slot open: the film's frame 50 (the sheet has just gone in, his eyes are
    // open, the slot still is too). He gets there through its first 15 frames (the box forming, the slot opening)
    // and a short ease; frames 16 to 21, where he squints as the sheet arrives, only play when one really does.
    const int SlotFilm = 15, SlotHold = 50;

    /// <summary>The waiting box at playback frame <paramref name="p"/> (0: still a ball, <see cref="SlotOpen"/>: open and looking).</summary>
    static Frame SlotPose(float p)
    {
        Frame f;
        if (p <= SlotFilm) { var a = _file[Math.Clamp((int)MathF.Round(p), 0, SlotFilm)]; f = new Frame { B = a.B, W = a.W, E = a.E, S = a.S, Poly = a.Poly }; }
        else { float w = Math.Clamp((p - SlotFilm) / (SlotOpen - SlotFilm), 0, 1); f = Lerp(_file[SlotFilm], _file[SlotHold], w * w * (3 - 2 * w)); }
        return f;
    }

    Frame? _poseFrom;    // the pose he was in when the sheet was let go (the film's frames ease in from it)
    float _poseT = 1;
    const float FlySeconds = 0.3f;
    float _fly = -1, _age;
    Vector2 _from;       // where the sheet starts, in thousandths of his radius from where the film has it above the slot
    bool _closing;

    /// <summary>His slot is open and waiting.</summary>
    public bool Waiting => Now == Clip.Slot && !_closing;

    /// <summary>
    /// The sheet lands: it flies in from <paramref name="from"/> (an offset from his centre, in his radii; null: from
    /// the lower right, as in the film), drops into the slot, and Finished follows.
    /// </summary>
    public void Drop(Vector2? from)
    {
        Load();
        if (_file.Length == 0 || _done.Length == 0) return;
        if (Now != Clip.Slot || _ending) { _t = 0; _look = 1; _lid = Active ? _lid : 0; _poseFrom = null; _poseT = 1; }
        else
        {
            // from the waiting pose into the film where the sheet arrives (he squints at it: frames 16 to 21)
            _poseFrom = SlotPose(_t * Fps); _poseT = 0;
            _t = MathF.Min(_t, (SlotFilm + 1) / Fps);
        }
        Now = Clip.File; _hold = 0; _settle = 0; _ending = _closing = false; _fly = 0; _stay = false;
        var rest = SheetCentre(_file[SheetOver]);
        _from = from is { } f ? f * 1000 - rest : new Vector2(5200, 1500);
    }

    /// <summary>The file was taken away: the slot closes and he is himself again.</summary>
    public void CloseSlot() { if (Now == Clip.Slot) _closing = true; else if (Now == Clip.File && _fly < 0) Stop(); }

    static Vector2 SheetCentre(Frame f)
    {
        if (f.P is not { Length: >= 2 } p) return Vector2.Zero;
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        for (int i = 0; i + 1 < p.Length; i += 2) { x0 = MathF.Min(x0, p[i]); x1 = MathF.Max(x1, p[i]); y0 = MathF.Min(y0, p[i + 1]); y1 = MathF.Max(y1, p[i + 1]); }
        return new Vector2((x0 + x1) / 2, (y0 + y1) / 2);
    }

    public bool Active => Now != Clip.None;
    /// <summary>While a film state plays, the island's own Bloub is not drawn at all: one face, never two.</summary>
    public bool Covers => Active;
    /// <summary>Seconds since the Greeting played its last frame (0 until then).</summary>
    public float GreetedFor => Now == Clip.Greet ? MathF.Max(0, _t - GreetFrames / Fps) / GreetSpeed : 0;
    /// <summary>The words beside him: hidden while the sheet is on its way, shown once he has it.</summary>
    public float TextIn => Now == Clip.File ? Math.Clamp((_t * Fps - 44) / 16f, 0, 1) : 1;

    /// <summary>
    /// Plays the Greeting, or the file followed by Finished. With a <paramref name="hold"/> he sits in its first pose
    /// that long before it starts moving (he has just landed: no hand-over needed). Without one he takes over from the
    /// island's Bloub in the middle of a blink: the caller shuts that Bloub's eyes first, and these open.
    /// </summary>
    public void Play(Clip clip, float hold = 0, bool stay = false, float from = 0)
    {
        Load();
        if (_greet.Length == 0 || _file.Length == 0 || _done.Length == 0) return;
        Speed = 1;
        Now = clip; _t = from / Fps; _hold = hold; _settle = 0; _ending = _closing = false; _look = 1; _lid = hold > 0 || from > 0 ? 1 : 0; _fly = -1; _stay = stay; _sx = _sy = 1; _rot = 0; _fl = -1; _aimed = false; Out = false; Homing = 0;
        if (stay && clip == Clip.Done) Follow(0);   // (aimed at once: he may be drawn before the next step)
    }

    /// <summary>
    /// Out of the island, where Finished's green light will sit for a pointer the way of <paramref name="toward"/>, so a
    /// light drawn before Finished starts can be in the same place: its angle from his centre, its distance and radius,
    /// and the radius of the gap round it (lengths in the radius <see cref="DrawOut"/> is given); and his own radius in
    /// that unit (the film's ball is a little bigger than its unit).
    /// </summary>
    public static (float Angle, float Dist, float R, float Gap, float Body) OutLight(Vector2 toward)
    {
        Load();
        bool has = toward.Length() > 0.35f;
        float dx = has ? toward.X : 0.354f, dy = has ? toward.Y : -0.59f;
        float r = (_doneLight?.At((_done.Length - 1))[0] ?? 120) / 1000f * LightScale;
        float body = _doneBody?.At(_done.Length - 1)[2] / 1000f ?? 1.032f;
        return (MathF.Atan2(-0.635f, dx < 0 && dy <= 0 ? 0.86f : -0.86f), (_lightAt.Length() + LightOut) / 1000f, r, r + LightGap / 1000f, body);
    }

    /// <summary>How fast Finished plays (1: the film's own speed). Reset by <see cref="Play"/>.</summary>
    public float Speed = 1;

    /// <summary>Hands back to the island's own Bloub: his eyes shut, and the island's Bloub opens his.</summary>
    public void Stop() { if (Active) _ending = true; }

    public void Step(float dt)
    {
        if (!Active) return;
        if (_ending)
        {
            _lid -= dt / 0.1f;
            if (_lid <= 0) { _lid = 0; Now = Clip.None; }
            return;
        }
        _lid = MathF.Min(1, _lid + dt / 0.12f);
        _age += dt;
        _poseT = MathF.Min(1, _poseT + dt / 0.11f);
        // the Greeting keeps the island Bloub's own white paint (it is him saying hello); the file eases into the film's grey
        if (Now == Clip.Greet) _look = 1; else if (_closing) _look = MathF.Min(1, _look + dt / 0.22f); else if (_settle <= 0) _look = MathF.Max(0, _look - dt / 0.3f);
        if (_hold > 0) { _hold -= dt; return; }
        if (Now == Clip.Slot)
        {
            // opening, then held open; closing runs the same frames back
            if (_closing) { _t -= dt * 1.25f; if (_t <= 0) { _t = 0; _ending = true; } }
            else _t = MathF.Min(_t + dt, SlotOpen / Fps);
            return;
        }
        _t += dt * (Now == Clip.Greet ? GreetSpeed : Now == Clip.Done ? Speed : 1);
        if (Now == Clip.File && _fly >= 0)
        {
            // the film waits with the slot open until the sheet has arrived above it
            _fly += dt;
            if (_fly < FlySeconds) _t = MathF.Min(_t, SheetOver / Fps); else { _fly = -1; _t = MathF.Max(_t, SheetOver / Fps); }
        }
        switch (Now)
        {
            case Clip.Greet:
                break;   // he keeps the Greeting's last pose until the welcome closes (the island's Bloub takes over as it folds away)
            case Clip.File:
                if (_t >= (_file.Length - 1) / Fps) { Now = Clip.Done; _t = 0; }
                break;
            case Clip.Done:
                // Finished ends with him a little above his seat: he settles back into his own paint, then the island's Bloub takes over
                float end = (_done.Length - 1) / Fps;
                if (_stay) { _t = MathF.Min(_t, end); Follow(dt); }   // (a finished session's card: he keeps Finished's last pose, the green light on, until the card closes)
                else if (_t >= end) { _settle = MathF.Min(1, (_t - end) / 0.4f); _look = _settle; if (_settle >= 1) _ending = true; }
                break;
        }
    }

    /// <summary>
    /// The Greeting between two frames. The film holds each pose for two frames; averaging over one frame's width
    /// turns those steps into continuous motion, which is what lets it play slower without stuttering.
    /// </summary>
    static Frame Blend(Frame[] fr, float tf)
    {
        Span<int> ix = stackalloc int[4]; Span<float> wt = stackalloc float[4]; int n = 0;
        void Tap(float t, float w, Span<int> ix, Span<float> wt)
        {
            int i0 = (int)MathF.Floor(t); float u = t - i0;
            Add(i0, w * (1 - u), ix, wt); Add(i0 + 1, w * u, ix, wt);
        }
        void Add(int i, float w, Span<int> ix, Span<float> wt)
        {
            if (w <= 0.0005f) return;
            i = Math.Clamp(i, 0, fr.Length - 1);
            for (int k = 0; k < n; k++) if (ix[k] == i) { wt[k] += w; return; }
            ix[n] = i; wt[n++] = w;
        }
        Tap(tf - 0.5f, 0.5f, ix, wt); Tap(tf + 0.5f, 0.5f, ix, wt);
        int best = 0; for (int k = 1; k < n; k++) if (wt[k] > wt[best]) best = k;
        var near = fr[ix[best]];
        if (n == 1) return near;
        float total = 0; for (int k = 0; k < n; k++) total += wt[k];

        int[] Mix(Func<Frame, int[]> get, Span<int> ix, Span<float> wt)
        {
            var a = get(near); var sum = new float[a.Length];
            for (int k = 0; k < n; k++) { var v = get(fr[ix[k]]); for (int j = 0; j < sum.Length; j++) sum[j] += v[j] * wt[k]; }
            var o = new int[a.Length]; for (int j = 0; j < o.Length; j++) o[j] = (int)MathF.Round(sum[j] / total);
            return o;
        }
        // shapes blend only while every frame involved has the same ones (an eye or a hand that appears or goes takes the nearest frame)
        bool Same(Func<Frame, int[][]> get, Span<int> ix)
        {
            var a = get(near);
            for (int k = 0; k < n; k++) { var v = get(fr[ix[k]]); if (v.Length != a.Length) return false; for (int j = 0; j < a.Length; j++) if (v[j].Length != a[j].Length) return false; }
            return true;
        }
        int[][] MixAll(Func<Frame, int[][]> get, Span<int> ix, Span<float> wt)
        {
            var a = get(near); if (a.Length == 0 || !Same(get, ix)) return a;
            var o = new int[a.Length][];
            for (int j = 0; j < o.Length; j++) { int jj = j; o[j] = Mix(f => get(f)[jj], ix, wt); }
            return o;
        }
        return new Frame { B = Mix(f => f.B, ix, wt), W = near.W, E = MixAll(f => f.E, ix, wt), H = MixAll(f => f.H, ix, wt) };
    }

    /// <summary>Draws the current frame with his centre at <paramref name="at"/> and his radius <paramref name="r"/> (DIPs).</summary>
    (float, float, float, float, float, float)? _seen; int _seenN;

    public void Draw(Canvas c, Vector2 at, float r, float opacity, ID2D1Geometry? island, Area islandBox, ulong islandKey)
    {
        if (!Active || r <= 0 || Out) return;
        float op = opacity, look = _look * _look * (3 - 2 * _look);
        if (op <= 0.004f) return;
        var frames = Now == Clip.Greet ? _greet : Now == Clip.Done ? _done : _file;
        int i = Math.Clamp((int)MathF.Round(_t * Fps), 0, frames.Length - 1);
        var clip = Now;
        float tf = _t * Fps;
        var f = clip == Clip.Greet ? Greeting(MathF.Min(tf, GreetFrames)) : clip == Clip.Done ? DonePose(tf) : frames[i];
        float lid = _lid, home = 0;
        if (clip == Clip.Done && _stay)
        {
            home = Homing;   // (just back from your pointer: already in his own paint, the light out)
            // just landed from your pointer: he sits in his seat (not where the film put him)
            f = Turned(f);
            at -= new Vector2(f.B[0], f.B[1]) * (r / 1000f);
            if (_fl >= 0) lid *= MathF.Min(1, MathF.Abs(_fl - 0.09f) / 0.09f);
        }
        if (clip == Clip.Done && _settle > 0)
        {
            float e = 1 - MathF.Pow(1 - _settle, 3);
            at -= new Vector2(f.B[0], f.B[1]) * (r / 1000f) * e;
        }
        float paper = 1, l = 3f, rt = 3f, up = 3.4f, dn = 3.4f, fly = -1;
        if (clip == Clip.Slot)
        {
            // waiting with the slot open: no sheet yet, and he breathes a little so the wait is alive
            f = SlotPose(tf);
            float open = Math.Clamp(_t * Fps / SlotOpen, 0, 1);
            r *= 1 + 0.022f * open * MathF.Sin(_age * 4.4f);
        }
        else if (clip == Clip.File && i <= SheetOver)
        {
            // the sheet on its way from the cursor: the film's sheet above the slot, moved back along its path
            // (a little arc, and it grows as it comes), with nothing of the box in front of it yet
            float u = _fly < 0 ? 1 : Math.Clamp(_fly / FlySeconds, 0, 1), e = 1 - MathF.Pow(1 - u, 3);
            var rest = _file[SheetOver].P!;
            var c0 = SheetCentre(_file[SheetOver]);
            var off = _from * (1 - e) + new Vector2(0, -260 * MathF.Sin(u * MathF.PI) * MathF.Min(1, _from.Length() / 3000));
            float sc = 0.62f + 0.38f * e;
            var flown = new int[rest.Length];
            float fx0 = float.MaxValue, fx1 = float.MinValue, fy0 = float.MaxValue, fy1 = float.MinValue;
            for (int j = 0; j + 1 < rest.Length; j += 2)
            {
                float px = c0.X + (rest[j] - c0.X) * sc + off.X, py = c0.Y + (rest[j + 1] - c0.Y) * sc + off.Y;
                flown[j] = (int)MathF.Round(px); flown[j + 1] = (int)MathF.Round(py);
                fx0 = MathF.Min(fx0, px); fx1 = MathF.Max(fx1, px); fy0 = MathF.Min(fy0, py); fy1 = MathF.Max(fy1, py);
            }
            var body = _poseFrom != null && _poseT < 1 ? Lerp(_poseFrom, f, _poseT * _poseT * (3 - 2 * _poseT)) : f;
            f = new Frame { B = body.B, W = f.W, E = body.E, S = body.S, P = flown, Poly = f.Poly };
            paper = Math.Clamp(u * 5, 0, 1); fly = u;
            l = MathF.Max(l, -fx0 / 1000 + 0.3f); rt = MathF.Max(rt, fx1 / 1000 + 0.3f); up = MathF.Max(up, -fy0 / 1000 + 0.3f); dn = MathF.Max(dn, fy1 / 1000 + 0.3f);
        }
        else if (clip == Clip.File) rt = 3.6f;
        // the Greeting stays inside the island (his hands never poke out of it); the sheet comes in over its edge
        if (Render.FrameCheck.On)
        {
            // (the checker draws each frame twice: he must be asked for at the same place both times)
            var seen = (_t, _age, at.X, at.Y, r, op);
            if (_seen is { } p && p.Item1 == _t && p.Item2 == _age && p != seen && _seenN++ < 20)
                Diag.Log($"film drawn differently within one tick: at {p.Item3:R},{p.Item4:R} -> {at.X:R},{at.Y:R}  r {p.Item5:R} -> {r:R}  opacity {p.Item6:R} -> {op:R}");
            _seen = seen;
        }
        bool inside = clip is Clip.Greet or Clip.Slot;
        c.PushLayer(Math.Clamp(op, 0, 1), inside ? island : null, inside ? islandBox : null, inside ? islandKey : 0);
        ulong hash = new FrameHash(77).I((int)clip).I(i).A(clip is Clip.Greet or Clip.Slot or Clip.Done ? tf : 0).A(at.X).A(at.Y).A(r).A(look).A(lid).A(_stay ? _rot + 10 * _ba + 100 * _sx + 1000 * _sy : 0).A(home).A(fly).A(_poseT).V;   // (he is big in the file card: a sixteenth of a pixel of drift shows on his eyes' edge)
        bool plain = _stay;   // (a finished session's card has no blue step before it: colour only in the light)
        c.Custom(at.X - l * r, at.Y - up * r, (l + rt) * r, (up + dn) * r, hash, (ctx, brush) => Paint(ctx, c.Factory, brush, f, at, r, clip, paper, MathF.Max(look, home), lid, fly >= 0, plain, light: 1 - home));
        c.PopLayer();
    }

    /// <summary>
    /// Out of the island (the stage): Finished's pose with his centre at <paramref name="at"/>, looking the way of
    /// <see cref="Toward"/>. One eye can wink (<paramref name="wink"/>: 1 open, 0 shut). The gap round the light is a real
    /// bite out of him there, since nothing black is behind him.
    /// </summary>
    /// <remarks>
    /// <paramref name="skin"/> (top, middle, bottom of a ball lit from the upper left) paints him in one colour throughout
    /// instead of the film's grey; <paramref name="light"/> (0 to 1, or -1: by <see cref="Homing"/>) sizes the green
    /// light; <paramref name="joinEyes"/> false leaves out the eyes of the join from the file (he comes from somewhere
    /// else: Finished starts with no eyes, then the film's come up from under him). <paramref name="amp"/> makes the
    /// film's moves bigger once its eyes are in (how far they are from where they end up, times this): a stronger nod.
    /// <paramref name="wash"/> keeps the film's blue that fills him from below before the light comes on (as after a
    /// file); without it the colour is only in the light (a finished session's card).
    /// </remarks>
    public void DrawOut(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, Vector2 at, float r, float wink, float light = -1, Color4[]? skin = null, bool joinEyes = true, float amp = 1, bool wash = false)
    {
        if (Now != Clip.Done || r <= 0) return;
        float tf = _t * Fps;
        var pose = DonePose(tf);
        if (amp != 1 && !pose.Poly && pose.E.Length > 0) pose = Stronger(pose, tf, amp);
        var f = Turned(pose);
        if (!joinEyes && f.Poly) f.E = [];
        at -= new Vector2(f.B[0], f.B[1]) * (r / 1000f);
        float shut = _lid;
        if (_fl >= 0) shut *= MathF.Min(1, MathF.Abs(_fl - 0.09f) / 0.09f);
        float look = skin != null ? 1 : _look * _look * (3 - 2 * _look);
        Paint(ctx, fac, brush, f, at, r, Clip.Done, 1, MathF.Max(look, Homing), shut, false, !wash, cut: true, wink: wink, light: light >= 0 ? light : 1 - Homing, skin: skin);
    }

    // ---- painting (the film's look: a grey ball lit from the upper right, ink-black eyes)

    static readonly Color4 Ink = new(0, 0, 0, 1);
    static readonly Color4 Lo = Canvas.Rgba(0x949496), Hi = Canvas.Rgba(0xE5E4E6), Flat = Canvas.Rgba(0xABABAB), Wash = Canvas.Rgba(0x64A3CC), GreenLight = Canvas.Rgba(0x00FE12);

    static void Paint(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, Frame f, Vector2 o, float r, Clip clip, float paper, float look, float lid, bool flying = false, bool plain = false, bool cut = false, float wink = 1, float light = 1, Color4[]? skin = null)
    {
        float k = r / 1000f;
        Vector2 P(float x, float y) => new(o.X + x * k, o.Y + y * k);

        if (clip == Clip.Greet)
        {
            // in the island he waves on the spot: the film's dip, without its drift to the left
            float bx = 0, by = f.B[1], br = f.B[2];
            foreach (var h in f.H)
            {
                using var hand = Outline(fac, h, 2, bx, by, o, k, smooth: true);
                Body(ctx, brush, hand, P(bx + h[0], by + h[1]), 339 * k, 0, 0, 0, look);
            }
            using var ball = fac.CreateEllipseGeometry(new Ellipse(P(bx, by), br * k, br * k));
            Body(ctx, brush, ball, P(bx, by), br * k, 0, 0, 0, look);
            Eyes(ctx, fac, brush, f, ball, bx, by, o, k, lid);
            return;
        }

        ID2D1Geometry body;
        Vector2 centre; float size;
        if (clip is Clip.File or Clip.Slot)
        {
            float cx = f.B[0], cy = f.B[1], w = f.B[2], h = f.B[3], rad = f.B[4];
            centre = P(cx, cy); size = MathF.Max(w, h) / 2 * k;
            body = f.B[5] != 0
                ? fac.CreateEllipseGeometry(new Ellipse(centre, w / 2 * k, h / 2 * k))
                : fac.CreateRoundedRectangleGeometry(new RoundedRectangle(new System.Drawing.RectangleF(o.X + (cx - w / 2) * k, o.Y + (cy - h / 2) * k, w * k, h * k), rad * k, rad * k));
        }
        else
        {
            centre = P(f.B[0], f.B[1]); size = f.B[2] * k;
            body = fac.CreateEllipseGeometry(new Ellipse(centre, size, size));
        }
        using (body)
        {
            Body(ctx, brush, body, centre, size, plain ? 0 : f.W[0] / 1000f, f.W[1] / 1000f, f.W[2] / 1000f, look, skin);
            if (f.S is { } s)
            {
                float sh = (s[3] - s[1]) * k, sr = MathF.Min(24f / 115 * 1000 * k, sh / 2);
                brush.Color = Ink;
                ctx.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(o.X + s[0] * k, o.Y + s[1] * k, (s[2] - s[0]) * k, sh), sr, sr), brush);
            }
            Eyes(ctx, fac, brush, f, body, 0, 0, o, k, lid, wink);
        }

        if (f.P is { } pp && paper > 0.01f) Paper(ctx, fac, brush, f, pp, o, k, paper, flying);

        if (f.G is { } g && g[2] > 4 && light > 0.02f)
        {
            // the green light on his shoulder, with the gap the film cuts around it (the island is black behind him)
            float gr = g[2] * k * LightScale * light;
            float gap = gr + LightGap * k * MathF.Min(1, g[2] / 100f) * light;
            if (cut)
            {
                // out of the island nothing black is behind him: the gap is a real bite out of him
                ctx.PrimitiveBlend = PrimitiveBlend.Copy;
                brush.Color = new Color4(0, 0, 0, 0);
                ctx.FillEllipse(new Ellipse(P(g[0], g[1]), gap, gap), brush);
                ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
            }
            else { brush.Color = Ink; ctx.FillEllipse(new Ellipse(P(g[0], g[1]), gap, gap), brush); }
            brush.Color = GreenLight;
            ctx.FillEllipse(new Ellipse(P(g[0], g[1]), gr, gr), brush);
        }
    }

    /// <summary>The film's ball: grey 148 to 229 from lower left to upper right, then the blue rising from below, the flat grey, the dimming.</summary>
    static void Body(ID2D1DeviceContext ctx, ID2D1SolidColorBrush brush, ID2D1Geometry shape, Vector2 c, float r, float wash, float flat, float dim, float look = 0, Color4[]? skin = null)
    {
        bool own = !(skin is { Length: 3 } && look >= 0.999f);
        if (own)
        using (var stops = ctx.CreateGradientStopCollection([new GradientStop(0, Lo), new GradientStop(1, Hi)]))
        using (var g = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(c.X - 0.749f * r, c.Y + 0.663f * r), new Vector2(c.X + 0.749f * r, c.Y - 0.663f * r)), stops))
            ctx.FillGeometry(shape, g);
        if (own && flat > 0.01f) { brush.Color = Canvas.WithA(Flat, MathF.Min(1, flat)); ctx.FillGeometry(shape, brush); }
        if (own && wash > 0.01f)
        {
            using var stops = ctx.CreateGradientStopCollection([new GradientStop(0, Canvas.WithA(Wash, 0)), new GradientStop(1, Canvas.WithA(Wash, MathF.Min(1, wash)))]);
            using var g = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(c.X, c.Y - 0.05f * r), new Vector2(c.X, c.Y + r)), stops);
            ctx.FillGeometry(shape, g);
        }
        if (own && dim > 0.01f) { brush.Color = new Color4(0, 0, 0, MathF.Min(1, dim)); ctx.FillGeometry(shape, brush); }
        if (look > 0.01f)
        {
            // handing over: the island Bloub's own paint (a white ball lit from the upper left, see BloubRenderer.InkBrush) eases in over the film's
            float a = MathF.Min(1, look);
            using var stops = skin is { Length: 3 }
                ? ctx.CreateGradientStopCollection([new GradientStop(0, Canvas.WithA(skin[0], a)), new GradientStop(0.3f, Canvas.WithA(skin[1], a)), new GradientStop(1, Canvas.WithA(skin[2], a))])
                : ctx.CreateGradientStopCollection([new GradientStop(0, new Color4(0.992f, 0.992f, 0.995f, a)), new GradientStop(0.3f, Canvas.WithA(Canvas.Rgba(0xF2F2F7), a)), new GradientStop(1, new Color4(0.55f, 0.55f, 0.562f, a))]);
            using var g = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(new Vector2(c.X - 0.30f * r, c.Y - 0.42f * r), Vector2.Zero, 1.65f * r, 1.65f * r), stops);
            ctx.FillGeometry(shape, g);
        }
    }

    static void Eyes(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, Frame f, ID2D1Geometry body, float bx, float by, Vector2 o, float k, float lid, float wink = 1)
    {
        if (f.E.Length == 0 && f.E2.Length == 0) return;
        Mask(ctx, body);
        brush.Color = Ink;
        int winker = -1;
        if (wink < 0.999f)
        {
            // a wink is one eye only: the one nearer his middle (the bigger of the two as he looks aside)
            float best = float.MaxValue;
            for (int n = 0; n < f.E.Length + f.E2.Length; n++)
            {
                var e = n >= f.E.Length ? f.E2[n - f.E.Length] : f.E[n];
                if (e.Length < 6) continue;
                float mx = 0, my = 0;
                for (int j = 0; j + 1 < e.Length; j += 2) { mx += e[j]; my += e[j + 1]; }
                mx = mx / (e.Length / 2) - f.B[0]; my = my / (e.Length / 2) - f.B[1];
                if (mx * mx + my * my < best) { best = mx * mx + my * my; winker = n; }
            }
        }
        for (int n = 0; n < f.E.Length + f.E2.Length; n++)
        {
            bool extra = n >= f.E.Length;
            var e = extra ? f.E2[n - f.E.Length] : f.E[n];
            if (e.Length < 6) continue;
            var shape = e;
            float ld = n == winker ? lid * wink : lid;
            if (ld < 0.999f)
            {
                // a blink: the eye squashes towards its own middle (the hand-over to and from the island's Bloub happens while it is shut)
                float cy = 0; for (int j = 1; j < e.Length; j += 2) cy += e[j]; cy /= e.Length / 2;
                float s = 0.06f + 0.94f * Math.Clamp(ld, 0, 1);
                shape = (int[])e.Clone(); for (int j = 1; j < shape.Length; j += 2) shape[j] = (int)MathF.Round(cy + (e[j] - cy) * s);
            }
            using var eye = Outline(fac, shape, 0, bx, by, o, k, smooth: extra || !f.Poly);
            ctx.FillGeometry(eye, brush);
        }
        ctx.PopLayer();
    }

    static void Paper(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, Frame f, int[] pp, Vector2 o, float k, float alpha, bool flying)
    {
        Vector2 P(float x, float y) => new(o.X + x * k, o.Y + y * k);
        using var sheet = Outline(fac, pp, 0, 0, 0, o, k, smooth: false);
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        for (int i = 0; i + 1 < pp.Length; i += 2) { x0 = MathF.Min(x0, pp[i]); x1 = MathF.Max(x1, pp[i]); y0 = MathF.Min(y0, pp[i + 1]); y1 = MathF.Max(y1, pp[i + 1]); }

        // it goes in behind the front of the box: hidden below the slot, but only where the box is
        ID2D1PathGeometry? region = null;
        if (f.S is { } s && !flying)
        {
            float sb = s[3], left = f.B[0] - f.B[2] / 2f, right = f.B[0] + f.B[2] / 2f, far = 40000;
            region = fac.CreatePathGeometry();
            using var sink = region.Open();
            sink.SetFillMode(FillMode.Winding);
            void Box(float l, float t, float r, float b)
            {
                sink.BeginFigure(P(l, t), FigureBegin.Filled);
                sink.AddLine(P(r, t)); sink.AddLine(P(r, b)); sink.AddLine(P(l, b));
                sink.EndFigure(FigureEnd.Closed);
            }
            Box(-far, -far, far, sb); Box(-far, sb, left, far); Box(right, sb, far, far);
            sink.Close();
        }
        ctx.PushLayer(new LayerParameters1
        {
            ContentBounds = new Rect(-1e6f, -1e6f, 2e6f, 2e6f), GeometricMask = region!, MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity, Opacity = alpha, LayerOptions = LayerOptions1.None,
        }, null!);
        using (var stops = ctx.CreateGradientStopCollection([new GradientStop(0, Canvas.Rgba(0xF9F7FA)), new GradientStop(0.55f, Canvas.Rgba(0xF3F1F4)), new GradientStop(1, Canvas.Rgba(0xE4E2E5))]))
        using (var g = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(P(x1, y0), P(x0, y1)), stops))
            ctx.FillGeometry(sheet, g);
        if (f.S is { } sl && y1 > sl[1] && !flying)
        {
            // the part that is down inside the slot goes dark
            ctx.PushAxisAlignedClip(new Rect(o.X + sl[0] * k, o.Y + sl[1] * k, (sl[2] - sl[0]) * k, (sl[3] - sl[1]) * k), AntialiasMode.PerPrimitive);
            using var stops = ctx.CreateGradientStopCollection([new GradientStop(0, new Color4(0, 0, 0, 0)), new GradientStop(1, new Color4(0, 0, 0, 0.82f))]);
            using var g = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(P(0, sl[1] + 40), P(0, sl[3] + 120)), stops);
            ctx.FillGeometry(sheet, g);
            ctx.PopAxisAlignedClip();
        }
        if (f.KP is { } kp && f.KM is { } km)
        {
            // its curled corner, traced from the film, shaded from its crease to its lip
            using var curl = Outline(fac, kp, 0, 0, 0, o, k, smooth: true);
            float ux = km[2] / 1000f * 220, uy = km[3] / 1000f * 220;
            using var stops = ctx.CreateGradientStopCollection([new GradientStop(0, Grey(km[4])), new GradientStop(1, Grey(km[5]))]);
            using var g = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(P(km[0] - ux, km[1] - uy), P(km[0] + ux, km[1] + uy)), stops);
            ctx.FillGeometry(curl, g);
        }
        ctx.PopLayer();
        region?.Dispose();
    }

    static Color4 Grey(int v) => new(v / 255f, v / 255f, v / 255f, 1);

    static void Mask(ID2D1DeviceContext ctx, ID2D1Geometry shape) => ctx.PushLayer(new LayerParameters1
    {
        ContentBounds = new Rect(-1e6f, -1e6f, 2e6f, 2e6f), GeometricMask = shape, MaskAntialiasMode = AntialiasMode.PerPrimitive,
        MaskTransform = Matrix3x2.Identity, Opacity = 1, LayerOptions = LayerOptions1.None,
    }, null!);

    /// <summary>A closed outline from x, y pairs (starting at <paramref name="from"/>), offset by (bx, by): smooth through its points, or straight between them.</summary>
    static ID2D1PathGeometry Outline(ID2D1Factory1 fac, int[] v, int from, float bx, float by, Vector2 o, float k, bool smooth)
    {
        int n = (v.Length - from) / 2;
        Vector2 At(int i) { i = (i % n + n) % n; return new(o.X + (bx + v[from + 2 * i]) * k, o.Y + (by + v[from + 2 * i + 1]) * k); }
        var geo = fac.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(At(0), FigureBegin.Filled);
        for (int i = 0; i < n; i++)
        {
            if (!smooth) { if (i > 0) sink.AddLine(At(i)); continue; }
            Vector2 p0 = At(i - 1), p1 = At(i), p2 = At(i + 1), p3 = At(i + 2);
            sink.AddBezier(new BezierSegment { Point1 = p1 + (p2 - p0) / 6, Point2 = p2 - (p3 - p1) / 6, Point3 = p2 });
        }
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }
}
