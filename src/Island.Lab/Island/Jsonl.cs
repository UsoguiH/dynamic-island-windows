namespace Island.Lab.Island;

/// <summary>
/// Reads the whole lines of an append-only <c>.jsonl</c> file as bytes, through one buffer the caller keeps
/// and reuses. Claude Code transcripts run to hundreds of megabytes (pasted screenshots are single lines of
/// several megabytes): reading them as strings allocated so much that the garbage collector stalled the
/// island, so nothing here allocates per line.
/// </summary>
public static class Jsonl
{
    /// <summary>The longest line that is kept whole.</summary>
    const int MaxLine = 24 << 20;

    /// <summary>
    /// Calls <paramref name="line"/> for each complete line after <paramref name="offset"/> (the memory is only
    /// valid during the call). Returns where the next read should start: after the last complete line, so an
    /// unfinished one is read again once the rest of it has been written.
    /// </summary>
    /// <param name="buffer">Reused between calls; grows for long lines.</param>
    /// <param name="keepLongWith">
    /// A line longer than the buffer is kept (the buffer grows) only if it contains these bytes near its start;
    /// otherwise it is skipped without being read into memory. Empty: every line is kept.
    /// </param>
    /// <param name="skipFirst">The read starts in the middle of a line: drop what is left of it.</param>
    public static long Read(string path, long offset, ref byte[] buffer, ReadOnlySpan<byte> keepLongWith,
        Action<ReadOnlyMemory<byte>> line, Func<bool>? stop = null, bool skipFirst = false)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        fs.Seek(offset, SeekOrigin.Begin);
        int have = 0;             // bytes in the buffer: [0, have)
        long consumed = offset;   // file position of buffer[0]
        bool skipping = skipFirst;
        while (true)
        {
            if (have == buffer.Length)
            {
                // a line longer than the buffer
                if (buffer.Length >= MaxLine || buffer.AsSpan(0, have).IndexOf(keepLongWith) < 0) { skipping = true; consumed += have; have = 0; }
                else Array.Resize(ref buffer, buffer.Length * 2);
            }
            int n = fs.Read(buffer, have, buffer.Length - have);
            if (n <= 0) break;
            have += n;
            int start = 0;
            while (true)
            {
                int nl = buffer.AsSpan(start, have - start).IndexOf((byte)'\n');
                if (nl < 0) break;
                if (skipping) skipping = false;
                else line(new ReadOnlyMemory<byte>(buffer, start, nl));
                start += nl + 1;
            }
            if (skipping) { consumed += have; have = 0; } // still inside the long line: drop what was read
            else if (start > 0)
            {
                buffer.AsSpan(start, have - start).CopyTo(buffer);
                have -= start; consumed += start;
            }
            if (stop != null && stop()) break;
        }
        return consumed;
    }
}
