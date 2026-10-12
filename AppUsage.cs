// Чтение 5-часового лимита из локального кэша приложения Claude (Local Storage = LevelDB со сжатием Snappy).
// Только чтение; ключи входа не используются. Запись вида {"resetsAt":<unix>,"utilization":0.8,...}.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

static class AppUsage
{
    static readonly Regex re = new Regex("\"resetsAt\":(\\d{9,11}),\"utilization\":([0-9.]+)");

    // возвращает (resetsAt, utilization 0..1) самого свежего окна или null
    public static double[] Read()
    {
        var dirs = new List<string>();
        string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(pk))
            foreach (var p in Directory.GetDirectories(pk, "Claude_*"))
                dirs.Add(Path.Combine(p, "LocalCache", "Roaming", "Claude", "Local Storage", "leveldb"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "Local Storage", "leveldb"));
        long bestReset = 0; double bestUtil = -1;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".ldb" && ext != ".log") continue;
                string text;
                try
                {
                    byte[] b = ReadShared(f);
                    text = ext == ".ldb" ? TableText(b) : Latin1(b);
                }
                catch { continue; }
                foreach (Match m in re.Matches(text))
                {
                    long r = long.Parse(m.Groups[1].Value);
                    double u = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    // в пределах одного окна использование только растёт — берём новейшее окно и максимум в нём
                    if (r > bestReset || (r == bestReset && u > bestUtil)) { bestReset = r; bestUtil = u; }
                }
            }
        }
        return bestReset > 0 ? new double[] { bestReset, bestUtil } : null;
    }

    static byte[] ReadShared(string p)
    {
        using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream()) { fs.CopyTo(ms); return ms.ToArray(); }
    }

    static string Latin1(byte[] b) { return Encoding.GetEncoding(28591).GetString(b); }

    static ulong Varint(byte[] b, ref int p)
    {
        ulong v = 0; int s = 0;
        while (p < b.Length) { byte x = b[p++]; v |= (ulong)(x & 0x7f) << s; if ((x & 0x80) == 0) break; s += 7; }
        return v;
    }

    // таблица LevelDB: футер → индексный блок → блоки данных (сжатые Snappy или нет)
    static string TableText(byte[] f)
    {
        if (f.Length < 48) return "";
        int p = f.Length - 48;
        Varint(f, ref p); Varint(f, ref p);                       // metaindex handle
        int io = (int)Varint(f, ref p), isz = (int)Varint(f, ref p);
        var sb = new StringBuilder();
        byte[] index = Block(f, io, isz);
        foreach (var h in BlockValues(index))
        {
            try { int q = 0; int off = (int)Varint(h, ref q), sz = (int)Varint(h, ref q); sb.Append(Latin1(Block(f, off, sz))).Append('\n'); }
            catch { }
        }
        return sb.ToString();
    }

    static byte[] Block(byte[] f, int off, int size)
    {
        var raw = new byte[size]; Array.Copy(f, off, raw, 0, size);
        return f[off + size] == 1 ? Snappy(raw) : raw;
    }

    static IEnumerable<byte[]> BlockValues(byte[] blk)
    {
        int n = BitConverter.ToInt32(blk, blk.Length - 4);
        int end = blk.Length - 4 - 4 * n, p = 0;
        while (p < end)
        {
            Varint(blk, ref p); int nonShared = (int)Varint(blk, ref p), vlen = (int)Varint(blk, ref p);
            p += nonShared;
            if (p + vlen > blk.Length) yield break;
            var v = new byte[vlen]; Array.Copy(blk, p, v, 0, vlen); p += vlen;
            yield return v;
        }
    }

    static byte[] Snappy(byte[] s)
    {
        int p = 0; int n = (int)Varint(s, ref p);
        var o = new byte[n]; int op = 0;
        while (p < s.Length && op < n)
        {
            int tag = s[p++], t = tag & 3;
            if (t == 0)
            {
                int len = tag >> 2;
                if (len >= 60) { int nb = len - 59; len = 0; for (int i = 0; i < nb; i++) len |= s[p++] << (8 * i); }
                len += 1;
                Array.Copy(s, p, o, op, len); p += len; op += len;
            }
            else
            {
                int len, off;
                if (t == 1) { len = ((tag >> 2) & 7) + 4; off = ((tag >> 5) << 8) | s[p++]; }
                else if (t == 2) { len = (tag >> 2) + 1; off = s[p] | (s[p + 1] << 8); p += 2; }
                else { len = (tag >> 2) + 1; off = s[p] | (s[p + 1] << 8) | (s[p + 2] << 16) | (s[p + 3] << 24); p += 4; }
                for (int i = 0; i < len; i++) { o[op] = o[op - off]; op++; }
            }
        }
        return o;
    }
}
