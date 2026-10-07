namespace Island.Lab;

/// <summary>Tiny diagnostics log (%TEMP%\islandlab.log) for the prototype.</summary>
public static class Diag
{
    static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "islandlab.log");
    static int _count;

    public static void Log(string s)
    {
        if (++_count > 500) return; // don't flood
        try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}"); } catch { }
    }
}
