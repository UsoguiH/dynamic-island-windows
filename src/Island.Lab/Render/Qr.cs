using System.Text;

namespace Island.Lab.Render;

/// <summary>
/// A QR code for a short text (a link): bytes, error correction level M, sizes 1 to 10 (up to 213 bytes).
/// Returns the squares, dark = true, without the white margin around them.
/// </summary>
public static class Qr
{
    // per size (1..10), level M: error-correction bytes per block, then (blocks, data bytes each) twice
    static readonly int[][] Blocks =
    [
        [10, 1, 16, 0, 0], [16, 1, 28, 0, 0], [26, 1, 44, 0, 0], [18, 2, 32, 0, 0], [24, 2, 43, 0, 0],
        [16, 4, 27, 0, 0], [18, 4, 31, 0, 0], [22, 2, 38, 2, 39], [22, 3, 36, 2, 37], [26, 4, 43, 1, 44],
    ];
    static readonly int[][] Align = [[], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50]];

    static readonly Dictionary<string, bool[,]?> _made = new();

    public static bool[,]? Encode(string text)
    {
        lock (_made)
        {
            if (_made.TryGetValue(text, out var have)) return have;
            if (_made.Count > 40) _made.Clear();
            return _made[text] = Make(Encoding.UTF8.GetBytes(text));
        }
    }

    static bool[,]? Make(byte[] data)
    {
        // the smallest size it fits in
        int ver = 0, dataBytes = 0;
        for (int v = 1; v <= 10; v++)
        {
            var b = Blocks[v - 1];
            int cap = b[1] * b[2] + b[3] * b[4], need = 4 + (v < 10 ? 8 : 16) + data.Length * 8;
            if (need <= cap * 8) { ver = v; dataBytes = cap; break; }
        }
        if (ver == 0) return null;

        // ---- the bits: "bytes", how many, the bytes, then padding
        var bits = new List<bool>(dataBytes * 8);
        void Put(int value, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Put(4, 4);
        Put(data.Length, ver < 10 ? 8 : 16);
        foreach (byte d in data) Put(d, 8);
        Put(0, Math.Min(4, dataBytes * 8 - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        for (int pad = 0xEC; bits.Count < dataBytes * 8; pad ^= 0xEC ^ 0x11) Put(pad, 8);
        var words = new byte[dataBytes];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) words[i >> 3] |= (byte)(0x80 >> (i & 7));

        // ---- error correction per block, then the blocks woven together
        var spec = Blocks[ver - 1];
        int ecLen = spec[0], nBlocks = spec[1] + spec[3];
        var div = Divisor(ecLen);
        var blockData = new byte[nBlocks][];
        var blockEc = new byte[nBlocks][];
        for (int i = 0, at = 0; i < nBlocks; i++)
        {
            int len = i < spec[1] ? spec[2] : spec[4];
            blockData[i] = words.AsSpan(at, len).ToArray();
            blockEc[i] = Remainder(blockData[i], div);
            at += len;
        }
        var all = new List<byte>();
        int longest = Math.Max(spec[2], spec[4]);
        for (int i = 0; i < longest; i++) foreach (var b in blockData) if (i < b.Length) all.Add(b[i]);
        for (int i = 0; i < ecLen; i++) foreach (var b in blockEc) all.Add(b[i]);

        // ---- the fixed parts
        int size = ver * 4 + 17;
        var m = new bool[size, size];      // [row, column]
        var fixedPart = new bool[size, size];
        void Set(int x, int y, bool dark) { m[y, x] = dark; fixedPart[y, x] = true; }
        for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy, d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, d != 2 && d != 4);
                }
        }
        Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);
        var al = Align[ver - 1];
        for (int i = 0; i < al.Length; i++)
            for (int j = 0; j < al.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == al.Length - 1) || (i == al.Length - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++) Set(al[i] + dx, al[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        void Format(int mask)
        {
            int d = mask, rem = d;      // (level M is 00)
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int f = ((d << 10) | rem) ^ 0x5412;
            bool Bit(int i) => ((f >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
            Set(8, 7, Bit(6)); Set(8, 8, Bit(7)); Set(7, 8, Bit(8));
            for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
            for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
            for (int i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));
            Set(8, size - 8, true);
        }
        Format(0);
        if (ver >= 7)
        {
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int vb = (ver << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((vb >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(a, b, bit); Set(b, a, bit);
            }
        }

        // ---- the data, zigzagging up and down in columns two wide, from the bottom right
        int n = 0, total = all.Count * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool up = ((right + 1) & 2) == 0;
                    int y = up ? size - 1 - vert : vert;
                    if (fixedPart[y, x] || n >= total) continue;
                    m[y, x] = ((all[n >> 3] >> (7 - (n & 7))) & 1) != 0;
                    n++;
                }
        }

        // ---- the mask that leaves the calmest picture
        static bool Masked(int mask, int x, int y) => mask switch
        {
            0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0,
            4 => (x / 3 + y / 2) % 2 == 0, 5 => x * y % 2 + x * y % 3 == 0, 6 => (x * y % 2 + x * y % 3) % 2 == 0,
            _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
        };
        void Flip(int mask)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++) if (!fixedPart[y, x] && Masked(mask, x, y)) m[y, x] = !m[y, x];
        }
        int best = 0, bestScore = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            Flip(mask); Format(mask);
            int score = Penalty(m, size);
            if (score < bestScore) { bestScore = score; best = mask; }
            Flip(mask);
        }
        Flip(best); Format(best);
        return m;
    }

    /// <summary>Long runs of one colour, blocks of four, and too much of one colour all make a code harder to read.</summary>
    static int Penalty(bool[,] m, int size)
    {
        int score = 0, dark = 0;
        for (int a = 0; a < size; a++)
        {
            int runR = 1, runC = 1;
            for (int b = 0; b < size; b++)
            {
                if (m[a, b]) dark++;
                if (b > 0)
                {
                    if (m[a, b] == m[a, b - 1]) { runR++; if (runR == 5) score += 3; else if (runR > 5) score++; } else runR = 1;
                    if (m[b, a] == m[b - 1, a]) { runC++; if (runC == 5) score += 3; else if (runC > 5) score++; } else runC = 1;
                    if (a > 0 && m[a, b] == m[a, b - 1] && m[a, b] == m[a - 1, b] && m[a, b] == m[a - 1, b - 1]) score += 3;
                }
            }
        }
        return score + Math.Abs(dark * 20 - size * size * 10) / (size * size) * 10;
    }

    // ---- Reed-Solomon over GF(256)

    static int Mul(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
        return z;
    }

    static byte[] Divisor(int degree)
    {
        var r = new byte[degree];
        r[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                r[j] = (byte)Mul(r[j], root);
                if (j + 1 < degree) r[j] ^= r[j + 1];
            }
            root = Mul(root, 2);
        }
        return r;
    }

    static byte[] Remainder(byte[] data, byte[] div)
    {
        var r = new byte[div.Length];
        foreach (byte b in data)
        {
            int factor = b ^ r[0];
            Array.Copy(r, 1, r, 0, r.Length - 1);
            r[^1] = 0;
            for (int i = 0; i < r.Length; i++) r[i] ^= (byte)Mul(div[i], factor);
        }
        return r;
    }
}
