namespace Island.Lab.Island;

/// <summary>
/// Showcase mode (<c>--showcase</c> or <c>ISLAND_SHOWCASE=1</c>): a scripted team of agents, usage and limits,
/// so the island can be tried — and recorded — without Claude Code installed and without your own sessions,
/// prompts or projects ever showing up on screen. Nothing is read from <c>~/.claude</c> and no probe is run.
/// The script loops every <see cref="Loop"/> seconds.
/// </summary>
public static class Showcase
{
    public static readonly bool On =
        Environment.GetEnvironmentVariable("ISLAND_SHOWCASE") is "1" or "true" ||
        Environment.GetCommandLineArgs().Any(a => a.Equals("--showcase", StringComparison.OrdinalIgnoreCase));

    const double Loop = 76;
    static readonly DateTime Start = DateTime.UtcNow;

    /// <summary>One scripted session: its status over the loop and the steps it takes while working.</summary>
    sealed record Script(string Id, string Name, string Title, string Prompt, string Model, double Pace,
        Func<double, string> Status, string[] Steps, string[] Plan, string[] Files, string Reply);

    static readonly Script[] Scripts =
    [
        new("sc-aurora", "aurora-web", "Dark mode for the settings page", "add a dark mode toggle to settings and remember the choice",
            "claude-opus-5-5", 3.1,
            t => t < 40 ? "busy" : "idle",
            ["Reading SettingsPage.tsx", "Searching “useTheme”", "Reading theme.ts", "Editing theme.ts", "Writing ThemeToggle.tsx",
             "Editing SettingsPage.tsx", "Running npm test -- settings", "Editing SettingsPage.test.tsx", "Running npm test", "Running npm run lint",
             "Updating the plan", "Editing CHANGELOG.md"],
            ["Add a theme store", "Build the toggle", "Wire it into Settings", "Persist the choice", "Tests + lint"],
            ["src/theme.ts", "src/components/ThemeToggle.tsx", "src/pages/SettingsPage.tsx", "src/pages/SettingsPage.test.tsx", "CHANGELOG.md"],
            "Dark mode is in. The toggle lives in Settings → Appearance, follows the system theme by default and remembers your choice. 14 tests pass."),
        new("sc-pixel", "pixel-api", "Fix the flaky auth tests", "the login tests fail 1 in 5 runs on CI, find out why and fix it",
            "claude-sonnet-5-5", 2.6,
            _ => "busy",
            ["Running pytest tests/auth -x", "Reading test_login.py", "Searching “freeze_time”", "Reading conftest.py", "Agent: trace token expiry",
             "Editing conftest.py", "Running pytest tests/auth --count 50", "Editing tokens.py", "Running pytest tests/auth --count 200", "Reading ci.yml"],
            ["Reproduce the flake", "Find the race", "Fix the clock fixture", "Run 200× to prove it"],
            ["tests/conftest.py", "app/auth/tokens.py"],
            "Found it: the token clock wasn't frozen in two fixtures, so tokens could expire mid-test. Fixed; 200/200 runs pass."),
        new("sc-notes", "notes-cli", "Publish v2.1 to npm", "bump to 2.1.0, update the changelog and publish",
            "claude-opus-5-5", 4.8,
            t => t is > 26 and < 56 ? "waiting" : "busy",
            ["Reading package.json", "Editing package.json", "Editing CHANGELOG.md", "Running npm run build", "Running npm pack --dry-run"],
            ["Bump version", "Changelog", "Build", "Publish"],
            ["package.json", "CHANGELOG.md"],
            "Ready to publish notes-cli@2.1.0."),
        new("sc-orbit", "orbit-landing", "Hero section animation", "make the hero section feel alive, subtle parallax",
            "claude-sonnet-5-5", 4.0,
            t => t is > 46 and < 68 ? "busy" : "idle",
            ["Reading Hero.astro", "Editing Hero.astro", "Writing parallax.ts", "Running npm run dev", "Reading a web page"],
            ["Parallax layers", "Reduced-motion fallback"],
            ["src/components/Hero.astro", "src/lib/parallax.ts"],
            "The hero now drifts with the cursor (3 layers) and stays still when reduced motion is on."),
    ];

    sealed class State { public AgentData Data = new(); public int Steps = -1; public double TurnStart; public string? LastStatus; public AgentData? Snap; }
    static readonly Dictionary<string, State> States = new();

    /// <summary>The scripted sessions at this moment (called from the sessions thread).</summary>
    public static List<SessionInfo> Sessions()
    {
        double now = (DateTime.UtcNow - Start).TotalSeconds;
        double t = now % Loop;
        int round = (int)(now / Loop);
        var list = new List<SessionInfo>();
        foreach (var s in Scripts)
        {
            if (!States.TryGetValue(s.Id, out var st)) States[s.Id] = st = new State();
            string status = s.Status(t);
            if (status == "busy" && st.LastStatus is "idle" && st.Steps >= 0) { st.Data = new AgentData(); st.Steps = -1; } // a new turn

            if (st.Steps < 0)
            {
                st.Data.AddChat(true, s.Prompt);
                st.Data.Model = s.Model;
                st.Data.Todos = s.Plan.Select(p => new TodoItem(p, "pending", p)).ToList();
                st.Steps = 0;
                st.TurnStart = now;
            }
            // While working, a new step every Pace seconds; an agent that never stops goes round its script again.
            if (status == "busy")
            {
                int want = 1 + (int)((now - st.TurnStart) / s.Pace);
                while (st.Steps < want) Step(s, st, st.Steps++);
            }
            if (status == "idle" && st.LastStatus == "busy")
            {
                while (st.Steps < s.Steps.Length) Step(s, st, st.Steps++);
                st.Data.Todos = s.Plan.Select(p => new TodoItem(p, "completed", p)).ToList();
                st.Data.AddChat(false, s.Reply);
            }
            st.LastStatus = status;
            if (st.Snap == null || st.Snap.Version != st.Data.Version) st.Snap = st.Data.Clone();

            var since = Start.AddSeconds(round * Loop + StatusStart(s, t));
            list.Add(new SessionInfo(s.Id, 0, s.Name, @"C:\dev\" + s.Name, status,
                status == "waiting" ? "permission: run npm publish" : null, s.Title, s.Prompt, st.Snap, since, Start.AddMinutes(-23 - s.Name.Length)));
        }
        list.Sort((a, b) => b.StatusSince.CompareTo(a.StatusSince));
        return list;
    }

    static void Step(Script s, State st, int i)
    {
        i %= s.Steps.Length;
        string line = s.Steps[i];
        string tool = line.Split(' ')[0] switch { "Editing" => "Edit", "Writing" => "Write", "Reading" => "Read", "Running" => "Bash", "Searching" => "Grep", "Agent:" => "Agent", _ => "TodoWrite" };
        st.Data.AddLog(line, tool);
        if (tool is "Edit" or "Write")
        {
            var file = s.Files.FirstOrDefault(f => f.EndsWith(line[(line.IndexOf(' ') + 1)..])) ?? line[(line.IndexOf(' ') + 1)..];
            int k = st.Data.Changes.FindIndex(c => c.Path == file);
            var ch = new FileChange(file, k >= 0 ? st.Data.Changes[k].Edits + 1 : 1);
            if (k >= 0) st.Data.Changes.RemoveAt(k);
            st.Data.Changes.Insert(0, ch);
        }
        // Plan progress follows the steps.
        int done = s.Plan.Length * (i + 1) / Math.Max(1, s.Steps.Length);
        st.Data.Todos = s.Plan.Select((p, j) => new TodoItem(p, j < done ? "completed" : j == done ? "in_progress" : "pending", p)).ToList();
        st.Data.Context = 18_000 + (i + 1) * 6_400;
        st.Data.Cost = 0.04 + (i + 1) * 0.031;
        st.Data.Version++;
    }

    /// <summary>When the current status began within the loop (for "working for 12s" and the sort order).</summary>
    static double StatusStart(Script s, double t)
    {
        string now = s.Status(t);
        double b = t;
        while (b > 0 && s.Status(b - 0.5) == now) b -= 0.5;
        return b;
    }

    public static List<string> RecentProjects() =>
        Scripts.Select(s => @"C:\dev\" + s.Name).Append(@"C:\dev\dotfiles").ToList();

    /// <summary>A believable day of Claude Code use.</summary>
    public static UsageStats Usage()
    {
        var now = DateTime.Now;
        var s = new UsageStats
        {
            Ready = true,
            WindowStart = now.Date.AddHours(now.Hour).AddHours(-2), WindowEnd = now.Date.AddHours(now.Hour).AddHours(3),
            WindowTokens = 1_840_000, WindowRequests = 312,
            TodayTokens = 4_920_000, TodayOutput = 610_000, TodayRequests = 846,
            TodayFiles = 57, TodayCommands = 133, TodaySessions = 6, TodayActive = TimeSpan.FromMinutes(287),
            Projects = [("aurora-web", 1_910_000), ("pixel-api", 1_380_000), ("notes-cli", 820_000), ("orbit-landing", 560_000), ("dotfiles", 250_000)],
            Models = [("Opus", 3_100_000), ("Sonnet", 1_540_000), ("Haiku", 280_000)],
            Streak = 12,
        };
        long[] hours = [0, 0, 0, 0, 0, 0, 0, 40, 160, 420, 610, 540, 180, 260, 700, 820, 640, 380, 120, 60, 290, 430, 210, 60];
        for (int i = 0; i < 24; i++) s.Hours[i] = i <= now.Hour ? hours[i] * 1000 : 0;
        long[] days = [3_100_000, 4_400_000, 2_200_000, 5_600_000, 3_800_000, 6_100_000, 4_920_000];
        days.CopyTo(s.Days, 0);
        return s;
    }

    public static void Limits(ClaudeLimits l)
    {
        var now = DateTime.Now;
        l.FiveHour = 0.47f; l.FiveResets = now.Date.AddHours(now.Hour).AddHours(3).AddMinutes(10);
        l.Week = 0.63f; l.WeekResets = now.Date.AddDays(((int)DayOfWeek.Friday - (int)now.DayOfWeek + 7) % 7).AddHours(18);
        l.UpdatedAt = now;
        l.Error = null;
    }
}
