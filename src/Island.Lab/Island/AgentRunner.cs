using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Island.Lab.Island;

/// <summary>A tool call waiting for your Allow / Deny.</summary>
public sealed record PermissionAsk(string RequestId, string Tool, string Summary, JsonNode? Input);

/// <summary>
/// One Claude agent launched by the island: a headless <c>claude -p</c> process speaking stream-json both
/// ways (the Agent SDK protocol). You can message it at any time, interrupt it, and it asks the island —
/// not a terminal — for permission (<c>--permission-prompt-tool stdio</c>).
/// Output is read on a background thread; <see cref="Drain"/> hands events to the UI thread.
/// </summary>
public sealed class AgentRunner : IDisposable
{
    public readonly string Cwd;
    public string? SessionId { get; private set; }
    public bool Exited { get; private set; }
    public string? Error { get; private set; }

    readonly Process _p;
    readonly ConcurrentQueue<JsonElement> _events = new();
    readonly StringBuilder _stderr = new();
    readonly object _write = new();

    /// <param name="cwd">Project folder the agent works in.</param>
    /// <param name="resume">Session to continue (with <paramref name="fork"/>: a copy, the original is untouched).</param>
    /// <param name="permissionMode">null = your default from settings; "plan" = read-only; "acceptEdits"…</param>
    public AgentRunner(string cwd, string? resume = null, bool fork = false, string? permissionMode = null, string? model = null)
    {
        Cwd = cwd;
        var psi = new ProcessStartInfo(ClaudeExe())
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompt-tool", "stdio" })
            psi.ArgumentList.Add(a);
        if (resume != null) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(resume); if (fork) psi.ArgumentList.Add("--fork-session"); }
        if (permissionMode != null) { psi.ArgumentList.Add("--permission-mode"); psi.ArgumentList.Add(permissionMode); }
        if (model != null) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
        // Don't let the child think it is nested inside another Claude Code session.
        foreach (var k in psi.Environment.Keys.ToList())
            if (k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) || k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(k);

        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _p.Exited += (_, _) => Exited = true;
        _p.Start();
        new Thread(ReadOut) { IsBackground = true, Name = "agent-out" }.Start();
        new Thread(ReadErr) { IsBackground = true, Name = "agent-err" }.Start();
        Diag.Log($"agent started pid={_p.Id} cwd={cwd} resume={resume} fork={fork} mode={permissionMode}");
    }

    public static string ClaudeExe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(p)) return p; } catch { }
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] known =
        [
            Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
        ];
        return known.FirstOrDefault(File.Exists) ?? "claude.exe";
    }

    void ReadOut()
    {
        try
        {
            string? line;
            while ((line = _p.StandardOutput.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var o = doc.RootElement.Clone();
                    if (o.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String) SessionId = sid.GetString();
                    _events.Enqueue(o);
                }
                catch { }
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        Exited = true;
    }

    void ReadErr()
    {
        try
        {
            string? line;
            while ((line = _p.StandardError.ReadLine()) != null)
                lock (_stderr) { if (_stderr.Length < 4000) _stderr.AppendLine(line); }
        }
        catch { }
    }

    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }

    /// <summary>Hands queued output events to the UI thread.</summary>
    public void Drain(Action<JsonElement> handle)
    {
        while (_events.TryDequeue(out var e)) handle(e);
    }

    void Write(JsonNode o)
    {
        if (Exited) return;
        try
        {
            lock (_write)
            {
                _p.StandardInput.WriteLine(o.ToJsonString());
                _p.StandardInput.Flush();
            }
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>Sends you a message to the agent (queued by Claude if it is busy).</summary>
    public void Send(string text) => Write(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        ["parent_tool_use_id"] = null,
        ["session_id"] = SessionId ?? "",
    });

    /// <summary>Stops the current turn (like Esc in the terminal); the agent stays alive.</summary>
    public void Interrupt() => Write(new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = "isl_" + Guid.NewGuid().ToString("N")[..12],
        ["request"] = new JsonObject { ["subtype"] = "interrupt" },
    });

    /// <summary>Answers a permission request.</summary>
    public void Answer(PermissionAsk ask, bool allow, string? why = null, JsonNode? input = null) => Write(new JsonObject
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"] = "success",
            ["request_id"] = ask.RequestId,
            ["response"] = allow
                ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input ?? ask.Input?.DeepClone() ?? new JsonObject() }
                : new JsonObject { ["behavior"] = "deny", ["message"] = why ?? "The user denied this from the island." },
        },
    });

    public void Kill()
    {
        try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
        Exited = true;
    }

    public void Dispose() { Kill(); _p.Dispose(); }
}
