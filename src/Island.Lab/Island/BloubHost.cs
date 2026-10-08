using System.Numerics;
using Island.Lab.Mascot;

namespace Island.Lab.Island;

/// <summary>
/// Bloub living in the island: picks the animation state + mood for what the island is doing,
/// drives cursor gaze, and supports short "reaction" overrides (wink on success, burst on done…).
/// </summary>
public sealed class BloubHost
{
    public readonly BloubEngine Engine = new(100, BloubState.Idle, BloubShapeId.Cercle, BloubExpressionId.Neutre);
    readonly BloubCursorTracker _tracker = new();

    BloubState? _reaction;
    BloubExpressionId? _reactionMood;
    double _reactionUntil;
    BloubState _current = BloubState.Idle;
    BloubExpressionId? _mood = BloubExpressionId.Neutre;
    BloubShapeId? _shape = BloubShapeId.Cercle;

    // Gallery
    public static readonly (BloubState State, string Name, string Use)[] Catalogue =
    [
        (BloubState.Idle, "Idle", "Resting — breathes, blinks, follows your cursor"),
        (BloubState.Thinking, "Thinking", "Builds, AI answers, long-running work"),
        (BloubState.Wink, "Wink", "Success — copied, saved, permission allowed"),
        (BloubState.Wide, "Wide", "Attention — you hovered the island"),
        (BloubState.Alert, "Alert", "Something failed — build error, crash"),
        (BloubState.Notify, "Notify", "A new notification arrived"),
        (BloubState.Exclaim, "Exclaim", "Urgent — Claude needs permission, meeting in 1 min"),
        (BloubState.Sleep, "Sleep", "PC idle, Do Not Disturb, late night"),
        (BloubState.Egg, "Egg", "Daily reveal, easter eggs"),
        (BloubState.Hexagon, "Hexagon", "Focus mode, usage gauges"),
        (BloubState.Play, "Play", "Music is playing"),
        (BloubState.Orbit, "Orbit", "Background tasks — downloads, sync, agents"),
        (BloubState.Swirl, "Swirl", "Switching modes"),
        (BloubState.Burst, "Burst", "Celebration — timer done, tests passed"),
        (BloubState.Comet, "Comet", "Something arrived — download done, file on shelf"),
    ];
    public static readonly (BloubExpressionId Id, string Name)[] Moods =
    [
        (BloubExpressionId.Neutre, "Neutral"), (BloubExpressionId.Heureux, "Happy"), (BloubExpressionId.Excite, "Excited"),
        (BloubExpressionId.Curieux, "Curious"), (BloubExpressionId.Fier, "Proud"), (BloubExpressionId.Timide, "Shy"),
        (BloubExpressionId.Colere, "Angry"), (BloubExpressionId.Triste, "Sad"), (BloubExpressionId.Effraye, "Scared"),
        (BloubExpressionId.Confus, "Confused"), (BloubExpressionId.Hilare, "Laughing"), (BloubExpressionId.Somnolent, "Sleepy"),
        (BloubExpressionId.Mefiant, "Suspicious"), (BloubExpressionId.Blase, "Unimpressed"), (BloubExpressionId.Attentif, "Attentive"),
        (BloubExpressionId.Surpris, "Surprised"), (BloubExpressionId.Bof, "Meh"),
    ];
    public static readonly (BloubShapeId Id, string Name)[] Shapes =
    [
        (BloubShapeId.Cercle, "Circle"), (BloubShapeId.Galet, "Pebble"), (BloubShapeId.Squircle, "Squircle"),
        (BloubShapeId.Capsule, "Capsule"), (BloubShapeId.Triangle, "Triangle"), (BloubShapeId.Hexagone, "Hexagon"),
        (BloubShapeId.Nuage, "Cloud"), (BloubShapeId.Goutte, "Drop"),
    ];
    public int GalleryIndex, MoodIndex, ShapeIndex;
    public bool GalleryAuto = true;
    double _galleryNext, _lastStep;

    public string StateName => Catalogue.First(c => c.State == _current).Name;

    /// <summary>Press squish: multiplies Bloub's radius (springs back with a bounce).</summary>
    public readonly Motion.Spring Squish = new(1, Motion.SpringSpec.Bounce, 0.0005f);

    readonly Random _rng = new();
    double _nextFidget = 6;
    int _pokes;
    double _lastPoke = -10;

    static readonly (BloubState s, BloubExpressionId? mood, double secs)[] Fidgets =
    [
        (BloubState.Wink, BloubExpressionId.Heureux, 1.6),
        (BloubState.Wide, BloubExpressionId.Curieux, 1.8),
        (BloubState.Idle, BloubExpressionId.Timide, 2.2),
        (BloubState.Idle, BloubExpressionId.Hilare, 1.6),
        (BloubState.Swirl, null, 1.3),
        (BloubState.Idle, BloubExpressionId.Mefiant, 1.8),
        (BloubState.Idle, BloubExpressionId.Fier, 2.0),
    ];

    /// <summary>Poke reactions escalate with rapid pokes: giggle → surprised → excited → confused → angry.</summary>
    static readonly (BloubState s, BloubExpressionId mood, double secs)[] PokeChain =
    [
        (BloubState.Wink, BloubExpressionId.Hilare, 1.4),
        (BloubState.Wide, BloubExpressionId.Surpris, 1.4),
        (BloubState.Burst, BloubExpressionId.Excite, 2.2),
        (BloubState.Swirl, BloubExpressionId.Confus, 1.5),
        (BloubState.Alert, BloubExpressionId.Colere, 2.4),
    ];

    public string? PokeLine { get; private set; }

    public void Poke(double now)
    {
        _pokes = now - _lastPoke < 3.5 ? _pokes + 1 : 0;
        _lastPoke = now;
        var (st, mood, secs) = PokeChain[Math.Min(_pokes, PokeChain.Length - 1)];
        React(st, now, secs, mood);
        PokeLine = _pokes switch { 0 => "hehe", 1 => "oh!", 2 => "wheee", 3 => "huh?", _ => "stop poking me!" };
        Squish.Snap(0.78f);
        Squish.To(1, Motion.SpringSpec.Bounce);
        _nextFidget = now + 8;
    }

    public void PressDown() => Squish.To(0.86f, Motion.SpringSpec.Press);
    public void PressUp() => Squish.To(1, Motion.SpringSpec.Bounce);

    public BloubHost() => BloubEyeFit.Warm();

    /// <summary>A short reaction (e.g. wink after "Allow") that overrides the mode's state for a moment.</summary>
    public void React(BloubState state, double now, double seconds = 1.6, BloubExpressionId? mood = null)
    {
        _reaction = state;
        _reactionMood = mood;
        _reactionUntil = now + seconds;
    }

    public void GalleryStep(int dir, double now)
    {
        GalleryIndex = (GalleryIndex + dir + Catalogue.Length) % Catalogue.Length;
        _galleryNext = now + 3.2;
    }

    public void Update(double now, IslandModel m, Vector2 center)
    {
        if (m.Mode == Mode.Gallery && GalleryAuto && now >= _galleryNext)
        {
            if (_galleryNext > 0) GalleryIndex = (GalleryIndex + 1) % Catalogue.Length;
            _galleryNext = now + 3.2;
        }

        bool reacting = _reaction != null && now < _reactionUntil;
        if (!reacting) { _reaction = null; _reactionMood = null; PokeLine = null; }

        var (state, mood) = reacting ? (_reaction!.Value, _reactionMood) : Desired(m, now);

        // Hovering Bloub himself: wide-eyed curiosity (unless he is mid-reaction).
        if (!reacting && m.HoverHit == "bloub") (state, mood) = (BloubState.Wide, BloubExpressionId.Curieux);

        // Idle fidgets: every few seconds a small spontaneous gesture, so he never looks frozen.
        if (!reacting && state == BloubState.Idle && m.Mode != Mode.Gallery && now >= _nextFidget)
        {
            var f = Fidgets[_rng.Next(Fidgets.Length)];
            React(f.s, now, f.secs, f.mood);
            _nextFidget = now + f.secs + 4 + _rng.NextDouble() * 6;
        }
        else if (state != BloubState.Idle && _nextFidget < now + 3) _nextFidget = now + 3;
        if (state != _current) { _current = state; Engine.SetState(state, now); }

        var wantMood = m.Mode == Mode.Gallery ? Moods[MoodIndex].Id : mood ?? BloubExpressionId.Neutre;
        if (wantMood != _mood) { _mood = wantMood; Engine.SetExpression(wantMood, now); }

        var wantShape = m.Mode == Mode.Gallery ? Shapes[ShapeIndex].Id : BloubShapeId.Cercle;
        if (wantShape != _shape) { _shape = wantShape; Engine.SetShape(wantShape, now); }

        // Eyes follow the cursor when it is anywhere near the top of the screen.
        Vector2? delta = Vector2.Distance(m.Cursor, center) < 700 ? m.Cursor - center : null;
        _tracker.Update(Engine, now, delta, new Vector2(240, 150));
        Squish.Step((float)Math.Min(0.05, Math.Max(0, now - _lastStep)));
        _lastStep = now;
    }

    (BloubState, BloubExpressionId?) Desired(IslandModel m, double now) => m.Mode switch
    {
        Mode.Gallery => (Catalogue[GalleryIndex].State, null),
        Mode.Alert => m.Alert switch
        {
            AlertKind.ClaudePermission => (BloubState.Exclaim, BloubExpressionId.Surpris),
            AlertKind.Allowed => (BloubState.Wink, BloubExpressionId.Heureux),
            AlertKind.Info => (BloubState.Notify, BloubExpressionId.Curieux),
            AlertKind.Agent => m.AlertAgent?.Status == AgentStatus.NeedsYou ? (BloubState.Exclaim, BloubExpressionId.Surpris) : (BloubState.Wink, BloubExpressionId.Heureux),
            _ => (BloubState.Wide, BloubExpressionId.Heureux),
        },
        Mode.Media or Mode.Split or Mode.Expanded => (m.Playing ? BloubState.Play : BloubState.Idle, BloubExpressionId.Heureux),
        Mode.Focus => (m.FocusPaused ? BloubState.Idle : BloubState.Thinking, BloubExpressionId.Attentif),
        Mode.Command => (BloubState.Thinking, BloubExpressionId.Attentif),
        Mode.Full or Mode.Agents => (BloubState.Idle, BloubExpressionId.Neutre),
        // Peeking out of the folded line: curious about the cursor.
        _ when m.Peeking => (BloubState.Idle, BloubExpressionId.Curieux),
        // Dormant: falls asleep after a while without attention; wakes curious on hover.
        _ => m.Hovered ? (BloubState.Idle, BloubExpressionId.Curieux)
           : m.IdleTime > 45 ? (BloubState.Sleep, BloubExpressionId.Somnolent)
           : (BloubState.Idle, BloubExpressionId.Neutre),
    };
}
