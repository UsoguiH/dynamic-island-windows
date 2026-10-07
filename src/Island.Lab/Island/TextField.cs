namespace Island.Lab.Island;

/// <summary>A one-line text input drawn inside the island (message box, new-agent task…).</summary>
public sealed class TextField
{
    public string Text = "";
    public int Caret;
    public string Placeholder;
    public double ChangedAt;

    public TextField(string placeholder) => Placeholder = placeholder;

    public void Set(string text) { Text = text; Caret = text.Length; }
    public void Clear() => Set("");

    public void Insert(string s)
    {
        s = s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        if (Text.Length + s.Length > 4000) s = s[..Math.Max(0, 4000 - Text.Length)];
        Text = Text.Insert(Caret, s);
        Caret += s.Length;
    }

    public void Back(bool word = false)
    {
        if (Caret == 0) return;
        int from = word ? WordStart(Caret) : Caret - 1;
        Text = Text.Remove(from, Caret - from);
        Caret = from;
    }

    public void Delete() { if (Caret < Text.Length) Text = Text.Remove(Caret, 1); }
    public void Left(bool word = false) => Caret = word ? WordStart(Caret) : Math.Max(0, Caret - 1);
    public void Right(bool word = false) => Caret = word ? WordEnd(Caret) : Math.Min(Text.Length, Caret + 1);
    public void Home() => Caret = 0;
    public void End() => Caret = Text.Length;

    int WordStart(int i) { while (i > 0 && Text[i - 1] == ' ') i--; while (i > 0 && Text[i - 1] != ' ') i--; return i; }
    int WordEnd(int i) { while (i < Text.Length && Text[i] == ' ') i++; while (i < Text.Length && Text[i] != ' ') i++; return i; }
}
