using System.Runtime.InteropServices;
using Island.Lab.Mascot;

namespace Island.Lab.Island;

/// <summary>
/// The small things a friend does. Bloub notices when you come back to the PC and says hello (what he says
/// depends on the hour, how long you were gone, and what your agents did meanwhile), and when you have been
/// working for an hour and a half without a break he suggests one. Never while something is fullscreen, never
/// over another message, and not more than once in a while.
/// </summary>
public sealed class Friend
{
    const double AwayAfter = 300;            // seconds without touching the mouse or keyboard = you left
    static readonly TimeSpan BreakEvery = TimeSpan.FromMinutes(90);

    double _next, _greetAt = -1, _awayFor;
    bool _away;
    DateTime _lastBreak = DateTime.Now, _lastGreet = DateTime.MinValue;

    public void Update(IslandModel m)
    {
        if (Showcase.On || m.Clock < _next) return;
        _next = m.Clock + 1; // once a second is plenty
        double idle = IdleSeconds();

        if (idle > AwayAfter) { _away = true; _awayFor = idle; return; }
        if (_away && idle < 3)
        {
            _away = false;
            _lastBreak = DateTime.Now;       // that was a break
            _greetAt = m.Clock + 1.2;        // let the screen wake up first
        }
        if (_greetAt > 0 && m.Clock >= _greetAt)
        {
            _greetAt = -1;
            if (CanSpeak(m) && DateTime.Now - _lastGreet > TimeSpan.FromMinutes(10)) Greet(m);
        }
        if (DateTime.Now - _lastBreak >= BreakEvery && CanSpeak(m))
        {
            _lastBreak = DateTime.Now;
            m.Bloub.React(BloubState.Wide, m.Clock, 1.6, BloubExpressionId.Attentif);
            m.ShowInfo("You've been at it for an hour and a half", "Stretch a little? I'll keep an eye on things");
        }
    }

    static bool CanSpeak(IslandModel m) => m.Mode == Mode.Dormant && !m.Hidden && !m.Welcoming;

    void Greet(IslandModel m)
    {
        _lastGreet = DateTime.Now;
        int h = DateTime.Now.Hour;
        string hello = h switch { >= 5 and < 12 => "Good morning!", >= 12 and < 18 => "Welcome back!", >= 18 and < 23 => "Good evening!", _ => "Still up?" };
        int n = m.Team.Count;
        string team = n == 0 ? "all quiet here" : m.Team.AnyNeeds ? m.Team.Summary + " — have a look" : m.Team.Summary;
        string text = h is >= 23 or < 5 ? $"It's late… {team}" : $"You were away {Ago(_awayFor)} · {team}";
        m.Bloub.React(BloubState.Wink, m.Clock, 1.6, BloubExpressionId.Heureux);
        m.Bloub.Squish.Snap(0.8f); m.Bloub.Squish.To(1, Motion.SpringSpec.Bounce); // a happy little hop
        m.ShowInfo(hello, text);
    }

    static string Ago(double seconds) =>
        seconds < 3600 ? $"{Math.Max(1, (int)Math.Round(seconds / 60))} min"
        : seconds < 86400 ? $"{seconds / 3600:0.#} h" : "a long time";

    static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = 8 };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0 : 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
