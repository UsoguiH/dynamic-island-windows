using System.Text.Json;

namespace Island.Lab.Island;

public sealed record ChatItem(bool User, string Text);
public sealed record TodoItem(string Text, string Status, string? Active); // pending | in_progress | completed
public sealed record FileChange(string Path, int Edits);
public enum FeedKind { Step, Reply, You }
/// <summary>One line of an agent's activity feed: a tool step, something it said, or something you said.</summary>
public sealed record FeedItem(FeedKind Kind, string Text, string? Tool = null);

/// <summary>
/// What an agent has been doing, built from Claude messages. The same shapes come from a session's
/// transcript (.jsonl) and from a headless agent's stream-json output, so one parser serves both.
/// </summary>
public sealed class AgentData
{
    public readonly List<string> Log = new();
    public readonly List<ChatItem> Chat = new();
    public List<TodoItem> Todos = new();
    public readonly List<FileChange> Changes = new();
    /// <summary>Steps, replies and your messages in the order they happened.</summary>
    public readonly List<FeedItem> Feed = new();
    public long Version;       // bumps on any change
    public long LogVersion;    // bumps when a log line is added
    public string? Model;
    public long Context;       // tokens in the context window at the last turn
    public double Cost;

    public string? LastReply => Chat.LastOrDefault(c => !c.User)?.Text;
    /// <summary>Takes back one recorded edit of <paramref name="path"/> (the edit was denied).</summary>
    public void Unchange(string path)
    {
        int i = Changes.FindIndex(ch => string.Equals(ch.Path, path, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        if (Changes[i].Edits > 1) Changes[i] = Changes[i] with { Edits = Changes[i].Edits - 1 };
        else Changes.RemoveAt(i);
    }

    public int TodosDone => Todos.Count(t => t.Status == "completed");

    public AgentData Clone()
    {
        var d = new AgentData { Todos = Todos.ToList(), Version = Version, LogVersion = LogVersion, Model = Model, Context = Context, Cost = Cost };
        d.Log.AddRange(Log); d.Chat.AddRange(Chat); d.Changes.AddRange(Changes); d.Feed.AddRange(Feed);
        return d;
    }

    void AddFeed(FeedItem f)
    {
        Feed.Add(f);
        if (Feed.Count > 80) Feed.RemoveRange(0, Feed.Count - 80);
    }

    public void AddLog(string line, string? tool = null)
    {
        Log.Add(line); LogVersion++; Version++;
        AddFeed(new FeedItem(FeedKind.Step, line, tool));
        if (Log.Count > 14) Log.RemoveRange(0, Log.Count - 14);
    }

    public void AddChat(bool user, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 6000) text = text[..6000] + "…";
        if (Chat.Count > 0 && Chat[^1].User == user && Chat[^1].Text == text) return;
        Chat.Add(new ChatItem(user, text)); Version++;
        AddFeed(new FeedItem(user ? FeedKind.You : FeedKind.Reply, text));
        if (Chat.Count > 40) Chat.RemoveRange(0, Chat.Count - 40);
    }

    static string? Str(JsonElement o, string p) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Applies one "assistant" or "user" message object (transcript line or stream-json event).</summary>
    public void Apply(JsonElement o)
    {
        string? type = Str(o, "type");
        if (type is not ("assistant" or "user")) return;
        if (o.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return;
        if (o.TryGetProperty("parent_tool_use_id", out var pt) && pt.ValueKind == JsonValueKind.String) return; // a subagent's inner turn
        if (!o.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        msg.TryGetProperty("content", out var content);

        if (type == "assistant")
        {
            if (Str(msg, "model") is { } model && !model.StartsWith("<")) Model = model;
            if (msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                long Tok(string p) => u.TryGetProperty(p, out var v) && v.TryGetInt64(out var n) ? n : 0;
                long ctx = Tok("input_tokens") + Tok("cache_read_input_tokens") + Tok("cache_creation_input_tokens");
                if (ctx > 0) Context = ctx;
            }
            if (content.ValueKind != JsonValueKind.Array) return;
            foreach (var c in content.EnumerateArray())
            {
                switch (Str(c, "type"))
                {
                    case "text":
                        AddChat(false, Str(c, "text") ?? "");
                        break;
                    case "tool_use":
                    {
                        string name = Str(c, "name") ?? "tool";
                        var input = c.TryGetProperty("input", out var inp) ? inp : default;
                        AddLog(ClaudeSessions.Describe(name, input), name);
                        if (name == "TodoWrite" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
                        {
                            Todos = todos.EnumerateArray().Select(t => new TodoItem(Str(t, "content") ?? "", Str(t, "status") ?? "pending", Str(t, "activeForm"))).ToList();
                            Version++;
                        }
                        if (name is "Edit" or "MultiEdit" or "Write" or "NotebookEdit" && (Str(input, "file_path") ?? Str(input, "notebook_path")) is { } path)
                        {
                            int i = Changes.FindIndex(ch => string.Equals(ch.Path, path, StringComparison.OrdinalIgnoreCase));
                            var ch = new FileChange(path, i >= 0 ? Changes[i].Edits + 1 : 1);
                            if (i >= 0) Changes.RemoveAt(i);
                            Changes.Insert(0, ch);
                            if (Changes.Count > 30) Changes.RemoveAt(Changes.Count - 1);
                            Version++;
                        }
                        break;
                    }
                }
            }
            return;
        }

        // user: only what you actually typed (not tool results, not system/meta messages)
        if (o.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
        if (o.TryGetProperty("toolUseResult", out _)) return;
        string text = "";
        if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in content.EnumerateArray())
            {
                var t = Str(c, "type");
                if (t == "tool_result") return;
                if (t == "text") text += (text.Length > 0 ? "\n" : "") + Str(c, "text");
            }
        }
        text = text.Trim();
        if (text.Length == 0 || text.StartsWith('<') || text.StartsWith("[Request interrupted") || text.StartsWith("Caveat:")) return;
        AddChat(true, text);
    }
}
