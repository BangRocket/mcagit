using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace McaDiff.Repo;

/// <summary>
/// A packfile: many objects concatenated into one <c>.pack</c> (with a companion
/// <c>.idx</c> mapping hash → offset), where an object may be stored whole or as a
/// delta against an earlier object in the same pack. Built by <see cref="Write"/>
/// during gc; read back through a hash→offset index with positioned reads so a
/// single object can be reconstructed without loading the whole pack.
/// </summary>
public sealed class Packfile : IDisposable
{
    private const byte Version = 1;
    private const int MaxDepth = 50;     // longest delta chain we build
    private const int Window = 10;       // delta-base candidates kept in flight

    private readonly SafeFileHandle _pack;
    private readonly Dictionary<string, long> _index;

    private Packfile(SafeFileHandle pack, Dictionary<string, long> index)
    {
        _pack = pack;
        _index = index;
    }

    public IReadOnlyCollection<string> Hashes => _index.Keys;
    public bool Contains(string hash) => _index.ContainsKey(hash);

    public static Packfile Open(string packPath)
    {
        string idxPath = Path.ChangeExtension(packPath, ".idx");
        var index = ReadIndex(idxPath);
        SafeFileHandle h = File.OpenHandle(packPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new Packfile(h, index);
    }

    /// <summary>All packs present under <c>objects/pack</c>.</summary>
    public static IEnumerable<Packfile> OpenAll(string objectsDir)
    {
        string dir = Path.Combine(objectsDir, "pack");
        if (!Directory.Exists(dir)) yield break;
        foreach (string p in Directory.EnumerateFiles(dir, "*.pack").OrderBy(x => x, StringComparer.Ordinal))
            // Skip a .pack with no .idx — a crash between the two moves leaves an orphan, and
            // without this its missing index would make every object read in the repo fail.
            if (File.Exists(Path.ChangeExtension(p, ".idx")))
                yield return Open(p);
    }

    public byte[]? Read(string hash) => _index.TryGetValue(hash, out long off) ? ReadAt(off) : null;

    public string? ResolvePrefix(string prefix)
    {
        string? match = null;
        foreach (string h in _index.Keys)
            if (h.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (match is not null && match != h) return null; // ambiguous within this pack
                match = h;
            }
        return match;
    }

    private byte[] ReadAt(long off)
    {
        // Read enough to cover the entry header (type + up to two varints), then the payload.
        byte[] head = ReadBytes(off, 24);
        if (head.Length == 0) throw new InvalidDataException("pack entry offset past end of file (truncated pack?)");
        int p = 0;
        byte type = head[p++];
        if (type == 0)
        {
            ulong compLen = ReadVarint(head, ref p);
            return Inflate(ReadPayload(off + p, (int)compLen));
        }
        else
        {
            ulong rel = ReadVarint(head, ref p);
            ulong compLen = ReadVarint(head, ref p);
            byte[] delta = Inflate(ReadPayload(off + p, (int)compLen));
            byte[] baseContent = ReadAt(off - (long)rel);
            return Delta.Apply(baseContent, delta);
        }
    }

    /// <summary>Reads exactly <paramref name="len"/> payload bytes, or throws (truncated pack).</summary>
    private byte[] ReadPayload(long off, int len)
    {
        byte[] buf = ReadBytes(off, len);
        if (buf.Length != len) throw new InvalidDataException("truncated pack object");
        return buf;
    }

    private byte[] ReadBytes(long off, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = RandomAccess.Read(_pack, buf.AsSpan(got), off + got);
            if (r <= 0) { Array.Resize(ref buf, got); break; } // header read may run past EOF — that's fine
            got += r;
        }
        return buf;
    }

    public void Dispose() => _pack.Dispose();

    // ---- writing ----

    /// <summary>
    /// Writes the objects named by <paramref name="orderedHashes"/> (already ordered so
    /// similar objects are adjacent) into a new pack under <c>objects/pack</c>, loading
    /// each object's content on demand via <paramref name="load"/> and delta-compressing
    /// against a recent window. Returns the pack id, or null if there was nothing to pack.
    /// Peak memory is the window, not the whole set.
    /// </summary>
    public static string? Write(string objectsDir, IReadOnlyList<string> orderedHashes, Func<string, byte[]> load)
    {
        if (orderedHashes.Count == 0) return null;
        string packDir = Path.Combine(objectsDir, "pack");
        Directory.CreateDirectory(packDir);

        string id = PackId(orderedHashes);
        string packPath = Path.Combine(packDir, $"pack-{id}.pack");
        if (File.Exists(packPath)) return id; // identical set already packed

        string tmp = packPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var entries = new List<(string Hash, long Offset)>(orderedHashes.Count);

        using (var fs = File.Create(tmp))
        {
            fs.WriteByte((byte)'M'); fs.WriteByte((byte)'C'); fs.WriteByte((byte)'A'); fs.WriteByte((byte)'P');
            fs.WriteByte(Version);

            var window = new List<WindowEntry>(Window);
            foreach (string hash in orderedHashes)
            {
                byte[] content = load(hash);
                long entryOff = fs.Position;
                byte[] compContent = Deflate(content);

                // Try to delta against a recent base of comparable size.
                byte[]? bestDelta = null;
                WindowEntry? bestBase = null;
                foreach (WindowEntry w in window)
                {
                    if (w.Depth >= MaxDepth) continue;
                    if (content.Length > w.Content.Length * 4 || w.Content.Length > content.Length * 4) continue;
                    byte[] d = Delta.Diff(w.Content, content);
                    if (bestDelta is null || d.Length < bestDelta.Length) { bestDelta = d; bestBase = w; }
                }

                int depth = 0;
                if (bestDelta is not null && bestBase is { } baseEntry)
                {
                    byte[] compDelta = Deflate(bestDelta);
                    if (compDelta.Length < compContent.Length)
                    {
                        fs.WriteByte(1);
                        WriteVarint(fs, (ulong)(entryOff - baseEntry.Offset));
                        WriteVarint(fs, (ulong)compDelta.Length);
                        fs.Write(compDelta);
                        depth = baseEntry.Depth + 1;
                    }
                    else bestDelta = null;
                }
                if (bestDelta is null)
                {
                    fs.WriteByte(0);
                    WriteVarint(fs, (ulong)compContent.Length);
                    fs.Write(compContent);
                }

                entries.Add((hash, entryOff));
                window.Add(new WindowEntry(content, entryOff, depth));
                if (window.Count > Window) window.RemoveAt(0);
            }
        }

        WriteIndex(Path.ChangeExtension(tmp, ".idx"), entries);
        File.Move(tmp, packPath);
        File.Move(Path.ChangeExtension(tmp, ".idx"), Path.ChangeExtension(packPath, ".idx"));
        return id;
    }

    private sealed record WindowEntry(byte[] Content, long Offset, int Depth);

    private static string PackId(IEnumerable<string> orderedHashes)
    {
        // Hash the SET (sorted), not the write order — so the same reachable objects always
        // yield the same pack id and a second gc with a different sort order is a no-op (idempotent).
        var sb = new System.Text.StringBuilder();
        foreach (string h in orderedHashes.OrderBy(x => x, StringComparer.Ordinal)) sb.Append(h);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(sb.ToString())))[..40];
    }

    // ---- index IO ----

    private static void WriteIndex(string idxPath, List<(string Hash, long Offset)> entries)
    {
        entries.Sort((a, b) => string.CompareOrdinal(a.Hash, b.Hash));
        using var fs = File.Create(idxPath);
        fs.WriteByte((byte)'M'); fs.WriteByte((byte)'C'); fs.WriteByte((byte)'A'); fs.WriteByte((byte)'I');
        fs.WriteByte(Version);
        WriteInt32(fs, entries.Count);
        foreach ((string hash, long off) in entries)
        {
            fs.Write(Convert.FromHexString(hash));
            WriteInt64(fs, off);
        }
    }

    private static Dictionary<string, long> ReadIndex(string idxPath)
    {
        byte[] b = File.ReadAllBytes(idxPath);
        if (b.Length < 9 || b[0] != 'M' || b[1] != 'C' || b[2] != 'A' || b[3] != 'I')
            throw new InvalidDataException($"not a pack index: {idxPath}");
        if (b[4] != Version)
            throw new InvalidDataException($"pack index version {b[4]} unsupported (this build writes/reads v{Version})");
        int p = 5;
        int count = ReadInt32(b, ref p);
        if (count < 0 || 9L + (long)count * 40 > b.Length) // 40 bytes/entry (32 hash + 8 offset)
            throw new InvalidDataException($"corrupt pack index: bad entry count {count}");
        var map = new Dictionary<string, long>(count);
        for (int i = 0; i < count; i++)
        {
            string hash = Convert.ToHexStringLower(b.AsSpan(p, 32));
            p += 32;
            map[hash] = ReadInt64(b, ref p);
        }
        return map;
    }

    // ---- primitives ----

    private static byte[] Deflate(byte[] content)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] comp)
    {
        using var ms = new MemoryStream(comp);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static ulong ReadVarint(byte[] buf, ref int p)
    {
        ulong v = 0;
        int shift = 0;
        while (true)
        {
            if (p >= buf.Length) throw new InvalidDataException("truncated varint in pack");
            byte b = buf[p++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
    }

    private static void WriteInt32(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void WriteInt64(Stream s, long v)
    {
        for (int sh = 56; sh >= 0; sh -= 8) s.WriteByte((byte)(v >> sh));
    }

    private static int ReadInt32(byte[] b, ref int p)
    {
        int v = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
        p += 4;
        return v;
    }

    private static long ReadInt64(byte[] b, ref int p)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[p + i];
        p += 8;
        return v;
    }
}
