using System.Diagnostics;
using System.Text.Json;

namespace Island.Lab.Island;

/// <summary>
/// Your real Claude plan limits — the 5-hour and weekly windows that <c>/usage</c> shows. Every Claude API
/// response carries them, and Claude Code reports them as a <c>rate_limit_event</c> in stream-json output.
/// We read them from agents the island runs, and otherwise from a tiny probe (Haiku, no tools, no MCP,
/// no saved session ≈ 650 tokens, ~$0.001) at start, every 10 minutes, and when you open the dashboard.
/// </summary>
public sealed class ClaudeLimits
{
    public float? FiveHour, Week;          // 0..1 used
    public DateTime? FiveResets, WeekResets; // local time
    public DateTime UpdatedAt = DateTime.MinValue;
    public string? Error;
    public bool Probing => _probing != 0;

    int _probing;
    DateTime _lastProbe = DateTime.MinValue;
    readonly object _lock = new();

    public bool Known => FiveHour != null || Week != null;

    /// <summary>Applies a stream-json <c>rate_limit_event</c> (from a probe or any island agent).</summary>
    public void Apply(JsonElement e)
    {
        if (!e.TryGetProperty("rate_limit_info", out var info) || !info.TryGetProperty("unifiedWindows", out var win)) return;
        lock (_lock)
        {
            if (Read(win, "five_hour") is var (f, fr)) { FiveHour = f; FiveResets = fr; }
            if (Read(win, "seven_day") is var (w, wr)) { Week = w; WeekResets = wr; }
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

    /// <summary>Refreshes in the background if the numbers are older than <paramref name="maxAge"/>.</summary>
    public void Refresh(TimeSpan maxAge)
    {
        if (Showcase.On) { Showcase.Limits(this); return; }
        // a check that failed (offline, signed out, Claude Code missing) is tried again later and later: 45 s, 90 s,
        // 3 min… up to 15 min, instead of starting a new Claude process every 45 seconds for as long as it fails
        var wait = TimeSpan.FromSeconds(Math.Min(900, 45 * Math.Pow(2, Math.Min(_failures, 6))));
        if (DateTime.Now - UpdatedAt < maxAge || DateTime.Now - _lastProbe < wait) return;
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;
        _lastProbe = DateTime.Now;
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
            foreach (var a in new[] { "-p", "ok", "--model", "haiku", "--system-prompt", "Reply with: ok", "--tools", "",
                                      "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands",
                                      "--no-session-persistence", "--output-format", "stream-json", "--verbose" })
                psi.ArgumentList.Add(a);
            foreach (var k in psi.Environment.Keys.ToList())
                if (k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) || k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                    psi.Environment.Remove(k);
            var before = UpdatedAt;
            Diag.Log("limits probe: start");
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
                    if (!line.Contains("rate_limit_event")) continue;
                    try { using var doc = JsonDocument.Parse(line); Apply(doc.RootElement); } catch { }
                }
            });
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } Error = "Limits check timed out"; }
            read.Wait(2000);
            if (DateTime.Now - UpdatedAt > TimeSpan.FromMinutes(1) && Error == null) Error = "Couldn't read your limits";
            _failures = UpdatedAt > before ? 0 : _failures + 1;
            Diag.Log("limits probe: " + (UpdatedAt > before ? "ok" : Error ?? "nothing"));
        }
        catch (Exception ex) { Error = "Couldn't run claude"; _failures++; Diag.Log("limits probe: " + ex.Message); }
        finally { Interlocked.Exchange(ref _probing, 0); }
    }
}
