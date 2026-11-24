using System.Diagnostics;
using System.Text.Json;

namespace Island.Lab.Island;

/// <summary>
/// Your real Claude plan limits — the 5-hour, weekly and Fable windows that <c>/usage</c> shows. Every Claude API
/// response carries them, and Claude Code reports them as a <c>rate_limit_event</c> in stream-json output.
/// We read them from agents the island runs, and otherwise from a tiny probe (Haiku, no tools, no MCP,
/// no saved session ≈ 650 tokens, ~$0.001) at start, every 10 minutes, and when you open the dashboard.
/// The Fable limit (<c>seven_day_overage_included</c>, "Fable limit" in Claude Code) only comes back on a Fable
/// reply, so the probe asks Fable instead (~$0.01) when that number is stale: once at start, every 15 minutes while
/// the dashboard is open, every 2 hours otherwise, and when you click the card. Agents on Fable keep it fresh for free.
/// </summary>
public sealed class ClaudeLimits
{
    public float? FiveHour, Week, Fable;                // 0..1 used
    public DateTime? FiveResets, WeekResets, FableResets; // local time
    public DateTime UpdatedAt = DateTime.MinValue;
    public string? Error;
    public bool Probing => _probing != 0;

    int _probing;
    DateTime _lastProbe = DateTime.MinValue, _fableAt = DateTime.MinValue, _fableTried = DateTime.MinValue;
    bool _askFable;
    readonly object _lock = new();

    public bool Known => FiveHour != null || Week != null || Fable != null;

    /// <summary>Applies a stream-json <c>rate_limit_event</c> (from a probe or any island agent).</summary>
    public void Apply(JsonElement e)
    {
        if (!e.TryGetProperty("rate_limit_info", out var info)) return;
        bool windows = info.TryGetProperty("unifiedWindows", out var win);
        // a Fable request refused because the Fable allowance is used up names it, without a window
        bool fableOut = info.TryGetProperty("rateLimitType", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("seven_day_overage_included")
                        && info.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.ValueEquals("rejected");
        if (!windows && !fableOut) return;
        lock (_lock)
        {
            if (windows)
            {
                if (Read(win, "five_hour") is var (f, fr)) { FiveHour = f; FiveResets = fr; }
                if (Read(win, "seven_day") is var (w, wr)) { Week = w; WeekResets = wr; }
                if (Read(win, "seven_day_overage_included") is var (fb, fbr)) { Fable = fb; FableResets = fbr; _fableAt = DateTime.Now; }
            }
            if (fableOut && !(windows && win.TryGetProperty("seven_day_overage_included", out _)))
            {
                Fable = 1; _fableAt = DateTime.Now;
                if (info.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var t)) FableResets = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;
            }
            UpdatedAt = DateTime.Now;
            Error = null;
        }
    }

    static (float, DateTime?)? Read(JsonElement win, string key)
    {
        if (!win.TryGetProperty(key, out var o) || !o.TryGetProperty("utilization", out var u) || !u.TryGetDouble(out var used)) return null;
        DateTime? resets = o.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var t) ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null;
        return ((float)used, resets);
    }

    /// <summary>
    /// Refreshes in the background if the numbers are older than <paramref name="maxAge"/>, asking Fable (for the Fable
    /// limit too) if that one is older than <paramref name="fableAge"/> and Fable was not already asked that recently.
    /// </summary>
    public void Refresh(TimeSpan maxAge, TimeSpan fableAge)
    {
        if (Showcase.On) { Showcase.Limits(this); return; }
        var now = DateTime.Now;
        bool fable = now - _fableAt > fableAge && now - _fableTried > fableAge;
        // a check that failed (offline, signed out, Claude Code missing) is tried again later and later: 45 s, 90 s,
        // 3 min… up to 15 min, instead of starting a new Claude process every 45 seconds for as long as it fails
        var wait = TimeSpan.FromSeconds(Math.Min(900, 45 * Math.Pow(2, Math.Min(_failures, 6))));
        if (now - UpdatedAt < maxAge && !fable || now - _lastProbe < wait) return;
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;
        _lastProbe = now;
        _askFable = fable;
        if (fable) _fableTried = now;
        new Thread(Probe) { IsBackground = true, Name = "ClaudeLimits", Priority = ThreadPriority.BelowNormal }.Start();
    }

    int _failures;

    void Probe()
    {
        try
        {
            var psi = new ProcessStartInfo(AgentRunner.ClaudeExe())
            {
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var a in new[] { "-p", "ok", "--model", _askFable ? "fable" : "haiku", "--system-prompt", "Reply with: ok", "--tools", "",
                                      "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands",
                                      "--no-session-persistence", "--output-format", "stream-json", "--verbose" })
                psi.ArgumentList.Add(a);
            foreach (var k in psi.Environment.Keys.ToList())
                if (k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) || k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                    psi.Environment.Remove(k);
            var before = UpdatedAt;
            Diag.Log("limits probe: start" + (_askFable ? " (fable)" : ""));
            using var p = Process.Start(psi)!;
            // it is a background errand: it must never take the processor from what you are doing (or from the island)
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            p.StandardInput.Close();
            _ = p.StandardError.ReadToEndAsync();
            var read = Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    // the result line closes the reply: every limit it carried has been read by now
                    if (line.Contains("\"type\":\"result\"")) break;
                    if (!line.Contains("rate_limit_event")) continue;
                    try { using var doc = JsonDocument.Parse(line); Apply(doc.RootElement); } catch { }
                }
            });
            // the numbers are all we came for: Claude shutting down afterwards (0.7 s on a good day, 30 s on a bad one)
            // is not worth waiting for, and "Checking your limits..." stays up for as long as this runs
            if (!read.Wait(30000)) Error = "Limits check timed out";
            try { if (!p.HasExited) p.Kill(true); } catch { }
            if (DateTime.Now - UpdatedAt > TimeSpan.FromMinutes(1) && Error == null) Error = "Couldn't read your limits";
            _failures = UpdatedAt > before ? 0 : _failures + 1;
            Diag.Log("limits probe: " + (UpdatedAt > before ? "ok" : Error ?? "nothing"));
        }
        catch (Exception ex) { Error = "Couldn't run claude"; _failures++; Diag.Log("limits probe: " + ex.Message); }
        finally { Interlocked.Exchange(ref _probing, 0); }
    }
}
