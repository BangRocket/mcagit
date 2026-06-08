using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using McaGit.Output;
using Microsoft.Win32.SafeHandles;

namespace McaGit.Repo;

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
    private const int MaxDepth = 50;     // longest delta chain we build (and accept on read)
    private const int Window = 10;       // delta-base candidates kept in flight
    private const long MaxObjectBytes = 512L * 1024 * 1024; // per-object inflate cap (untrusted packs)

    private readonly SafeFileHandle _pack;
    private readonly long _packLen;
    private readonly Dictionary<string, long> _index;

    private Packfile(SafeFileHandle pack, Dictionary<string, long> index)
    {
        _pack = pack;
        _packLen = RandomAccess.GetLength(pack);
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

    public byte[]? Read(string hash) => _index.TryGetValue(hash, out long off) ? ReadAt(off, 0) : null;

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

    // Hardened against a hostile pack (a /pack push is untrusted): the delta chain is depth-capped
    // and must strictly walk backward (no cycles / stack overflow), and every inflate is size-bounded
    // (no decompression bomb) before ObjectStore re-verifies the hash.
    private byte[] ReadAt(long off, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("pack delta chain too deep");
        if (off < 0 || off >= _packLen) throw new InvalidDataException("pack entry offset out of range");

        // Read enough to cover the entry header (type + up to two varints), then the payload.
        byte[] head = ReadBytes(off, 24);
        if (head.Length == 0) throw new InvalidDataException("pack entry offset past end of file (truncated pack?)");
        int p = 0;
        byte type = head[p++];
        if (type == 0)
        {
            ulong compLen = ReadVarint(head, ref p);
            return Inflate(ReadPayload(off + p, compLen));
        }
        else
        {
            ulong rel = ReadVarint(head, ref p);
            ulong compLen = ReadVarint(head, ref p);
            if (rel == 0 || (long)rel > off) throw new InvalidDataException("pack delta base out of range"); // must point strictly earlier
            byte[] delta = Inflate(ReadPayload(off + p, compLen));
            byte[] baseContent = ReadAt(off - (long)rel, depth + 1);
            return Delta.Apply(baseContent, delta);
        }
    }

    /// <summary>Reads exactly <paramref name="len"/> payload bytes, or throws (truncated pack /
    /// out-of-range length from an untrusted pack).</summary>
    private byte[] ReadPayload(long off, ulong len)
    {
        if (off < 0 || len > MaxObjectBytes || off + (long)len > _packLen)
            throw new InvalidDataException("pack object length out of range");
        byte[] buf = ReadBytes(off, (int)len);
        if (buf.Length != (int)len) throw new InvalidDataException("truncated pack object");
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
    public static string? Write(string objectsDir, IReadOnlyList<string> orderedHashes,
        Func<string, byte[]> load, Action? onObject = null)
    {
        if (orderedHashes.Count == 0) return null;
        string packDir = Path.Combine(objectsDir, "pack");
        Directory.CreateDirectory(packDir);

        string id = PackId(orderedHashes);
        string packPath = Path.Combine(packDir, $"pack-{id}.pack");
        if (File.Exists(packPath)) return id; // identical set already packed

        string tmp = packPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        List<(string Hash, long Offset)> entries;
        using (var fs = File.Create(tmp))
        {
            fs.WriteByte((byte)'M'); fs.WriteByte((byte)'C'); fs.WriteByte((byte)'A'); fs.WriteByte((byte)'P');
            fs.WriteByte(Version);
            entries = WriteSegment(fs, orderedHashes, load, onObject);
        }

        WriteIndex(Path.ChangeExtension(tmp, ".idx"), entries);
        File.Move(tmp, packPath);
        File.Move(Path.ChangeExtension(tmp, ".idx"), Path.ChangeExtension(packPath, ".idx"));
        return id;
    }

    /// <summary>
    /// Writes the given objects as pack entries into <paramref name="body"/> — type-0 (whole, zlib) or
    /// type-1 (delta vs an earlier object in THIS call, via a relative back-offset) — returning each
    /// entry's offset within <paramref name="body"/>. No MCAP header is written (the caller owns it).
    /// Because delta bases are confined to this call and back-offsets are relative, the produced bytes
    /// are position-independent: appending them at any file offset preserves every back-reference.
    /// Peak memory is the window, not the whole set.
    /// </summary>
    private static List<(string Hash, long Offset)> WriteSegment(
        Stream body, IReadOnlyList<string> hashes, Func<string, byte[]> load, Action? onObject)
    {
        var entries = new List<(string Hash, long Offset)>(hashes.Count);
        var window = new List<WindowEntry>(Window);
        foreach (string hash in hashes)
        {
            byte[] content = load(hash);
            long entryOff = body.Position;
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
                    body.WriteByte(1);
                    WriteVarint(body, (ulong)(entryOff - baseEntry.Offset));
                    WriteVarint(body, (ulong)compDelta.Length);
                    body.Write(compDelta);
                    depth = baseEntry.Depth + 1;
                }
                else bestDelta = null;
            }
            if (bestDelta is null)
            {
                body.WriteByte(0);
                WriteVarint(body, (ulong)compContent.Length);
                body.Write(compContent);
            }

            entries.Add((hash, entryOff));
            window.Add(new WindowEntry(content, entryOff, depth));
            if (window.Count > Window) window.RemoveAt(0);
            onObject?.Invoke();
        }
        return entries;
    }

    /// <summary>
    /// Like <see cref="Write"/>, but delta-compresses the objects across <paramref name="threads"/>
    /// CPU cores. The size-sorted set is split into byte-balanced contiguous segments (so adjacency —
    /// hence delta quality — is preserved and no segment is a straggler); each segment is compressed in
    /// parallel to its own <c>*.pack.tmp</c> with delta bases confined to that segment, then the segment
    /// bodies are concatenated serially into the final pack. Within-segment delta back-offsets are
    /// relative, so concatenation needs no offset fixup; only the index records global offsets.
    /// Falls back to the serial <see cref="Write"/> when there is nothing to parallelize.
    /// Peak memory is the window per worker. Drives <paramref name="progress"/> if supplied.
    /// </summary>
    public static string? WriteParallel(string objectsDir, IReadOnlyList<string> orderedHashes,
        Func<string, byte[]> load, int threads, Func<string, long> sizeOf, Progress? progress)
    {
        if (orderedHashes.Count == 0) return null;

        long total = orderedHashes.Count;
        long done = 0;
        progress?.Begin("gc: packing");
        Action onObject = () => progress?.Update(Interlocked.Increment(ref done), total);

        // Don't oversplit: past the core count, more (smaller) segments only cost delta quality and temp
        // files with no extra throughput — and this defangs an absurd --threads (one near the object count
        // would otherwise create ~one segment temp file per object). Keep a few objects per segment.
        const int MinObjectsPerSegment = 4;
        int effectiveThreads = Math.Min(threads, Math.Max(1, orderedHashes.Count / MinObjectsPerSegment));
        if (effectiveThreads <= 1)
        {
            string? sid = Write(objectsDir, orderedHashes, load, onObject);
            progress?.Done(total, total, $"{orderedHashes.Count} objects");
            return sid;
        }

        string packDir = Path.Combine(objectsDir, "pack");
        Directory.CreateDirectory(packDir);

        string id = PackId(orderedHashes);
        string packPath = Path.Combine(packDir, $"pack-{id}.pack");
        if (File.Exists(packPath)) { progress?.Done(total, total, "already packed"); return id; }

        List<(int Start, int Count)> segments = PartitionByBytes(orderedHashes, sizeOf, effectiveThreads);

        // Parallel phase: each segment -> its own temp body file (peak memory = window per worker).
        var segFiles = new string[segments.Count];
        var segEntries = new List<(string Hash, long Offset)>[segments.Count];
        Parallel.For(0, segments.Count, new ParallelOptions { MaxDegreeOfParallelism = effectiveThreads }, k =>
        {
            (int start, int count) = segments[k];
            var slice = new List<string>(count);
            for (int i = 0; i < count; i++) slice.Add(orderedHashes[start + i]);
            string segTmp = Path.Combine(packDir, $"incoming-seg-{k}-{Guid.NewGuid():N}.pack.tmp");
            segFiles[k] = segTmp;
            using var seg = File.Create(segTmp);
            segEntries[k] = WriteSegment(seg, slice, load, onObject);
        });

        // Serial concat: header, then append each segment body and shift its offsets by where the
        // segment lands in the final file. Within-segment relative deltas survive untouched.
        string tmp = packPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var entries = new List<(string Hash, long Offset)>(orderedHashes.Count);
        using (var fs = File.Create(tmp))
        {
            fs.WriteByte((byte)'M'); fs.WriteByte((byte)'C'); fs.WriteByte((byte)'A'); fs.WriteByte((byte)'P');
            fs.WriteByte(Version);
            for (int k = 0; k < segments.Count; k++)
            {
                long pk = fs.Position;
                using (var seg = File.OpenRead(segFiles[k])) seg.CopyTo(fs);
                foreach ((string hash, long off) in segEntries[k]) entries.Add((hash, pk + off));
                File.Delete(segFiles[k]);
            }
        }

        WriteIndex(Path.ChangeExtension(tmp, ".idx"), entries);
        File.Move(tmp, packPath);
        File.Move(Path.ChangeExtension(tmp, ".idx"), Path.ChangeExtension(packPath, ".idx"));
        progress?.Done(total, total, $"{orderedHashes.Count} objects");
        return id;
    }

    /// <summary>
    /// Splits <paramref name="ordered"/> into at most <paramref name="segments"/> contiguous chunks
    /// balanced by cumulative stored size, each non-empty. Contiguous keeps size-adjacency (delta
    /// quality); byte-balancing keeps the large-object chunk from being a straggler.
    /// </summary>
    private static List<(int Start, int Count)> PartitionByBytes(
        IReadOnlyList<string> ordered, Func<string, long> sizeOf, int segments)
    {
        var sizes = new long[ordered.Count];
        long total = 0;
        for (int i = 0; i < ordered.Count; i++) { sizes[i] = Math.Max(1, sizeOf(ordered[i])); total += sizes[i]; }
        long target = Math.Max(1, total / segments);

        var result = new List<(int Start, int Count)>(segments);
        int start = 0;
        long running = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            running += sizes[i];
            int remainingObjs = ordered.Count - (i + 1);
            int remainingSegs = segments - result.Count - 1;   // segments still to open after this cut
            // Cut when over target, but only while more segments remain and enough objects are left to
            // give each remaining segment at least one.
            if (result.Count < segments - 1 && running >= target && remainingObjs > remainingSegs)
            {
                result.Add((start, i - start + 1));
                start = i + 1;
                running = 0;
            }
        }
        result.Add((start, ordered.Count - start));            // last segment takes the remainder
        return result;
    }

    /// <summary>
    /// Streams whole (non-delta) objects into a new pack as they are produced, so a commit's new
    /// objects land in ONE file instead of hundreds of thousands of loose ones — without holding them
    /// in memory. (Buffering them in a dictionary instead keeps the whole commit's objects live, and
    /// GC marking the growing retained set starves the parallel snapshot — measured on a 300k-chunk
    /// world.) Each blob is stored as a type-0 entry (zlib, no delta); <c>gc</c> deltifies later.
    /// <see cref="Add"/> is thread-safe — compression runs outside the lock; only the sequential append
    /// is serialized — and <see cref="Finish"/> writes the index and atomically installs the pack.
    /// </summary>
    public sealed class Appender : IDisposable
    {
        private readonly string _objectsDir;
        private readonly string _tmp;
        private readonly FileStream _fs;
        private readonly ConcurrentDictionary<string, long> _offsets = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private bool _closed;

        public Appender(string objectsDir)
        {
            _objectsDir = objectsDir;
            string packDir = Path.Combine(objectsDir, "pack");
            Directory.CreateDirectory(packDir);
            // A "*.pack.tmp" name is ignored by OpenAll ("*.pack") and swept by gc if a crash orphans it.
            _tmp = Path.Combine(packDir, "incoming-" + Guid.NewGuid().ToString("N") + ".pack.tmp");
            _fs = File.Create(_tmp);
            _fs.WriteByte((byte)'M'); _fs.WriteByte((byte)'C'); _fs.WriteByte((byte)'A'); _fs.WriteByte((byte)'P');
            _fs.WriteByte(Version);
        }

        public bool Contains(string hash) => _offsets.ContainsKey(hash);

        /// <summary>Appends <paramref name="content"/> as a whole object unless it's already staged.</summary>
        public void Add(string hash, ReadOnlySpan<byte> content)
        {
            if (_offsets.ContainsKey(hash)) return;
            byte[] comp = Deflate(content.ToArray());
            lock (_lock)
            {
                if (_closed || _offsets.ContainsKey(hash)) return; // lost a race / already finished
                long off = _fs.Position;
                _fs.WriteByte(0);                 // type 0 = whole object
                WriteVarint(_fs, (ulong)comp.Length);
                _fs.Write(comp);
                _offsets[hash] = off;
            }
        }

        /// <summary>The raw zlib bytes of a staged object (positioned read from the open temp pack),
        /// or null if it isn't staged here.</summary>
        public byte[]? ReadRaw(string hash)
        {
            if (!_offsets.TryGetValue(hash, out long off)) return null;
            lock (_lock)
            {
                if (_closed) return null;
                long save = _fs.Position;
                try
                {
                    _fs.Position = off + 1;       // skip the type byte
                    ulong len = ReadVarintFromStream(_fs);
                    byte[] comp = new byte[(int)len];
                    _fs.ReadExactly(comp);
                    return comp;
                }
                finally { _fs.Position = save; }
            }
        }

        /// <summary>Writes the index and atomically installs the pack; returns its id (null if empty).</summary>
        public string? Finish()
        {
            lock (_lock)
            {
                if (_closed) return null;
                _closed = true;
                _fs.Flush(flushToDisk: true);
                _fs.Dispose();
                if (_offsets.IsEmpty) { TryDelete(_tmp); return null; }

                var entries = _offsets.Select(kv => (kv.Key, kv.Value)).ToList();
                string id = PackId(entries.Select(e => e.Key).ToList());
                string packPath = Path.Combine(_objectsDir, "pack", $"pack-{id}.pack");
                if (File.Exists(packPath)) { TryDelete(_tmp); return id; } // identical set already packed
                WriteIndex(Path.ChangeExtension(_tmp, ".idx"), entries);
                File.Move(_tmp, packPath);
                File.Move(Path.ChangeExtension(_tmp, ".idx"), Path.ChangeExtension(packPath, ".idx"));
                return id;
            }
        }

        /// <summary>Discards the in-progress pack (a failed/abandoned commit) — leaves nothing behind.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_closed) return;
                _closed = true;
                try { _fs.Dispose(); } catch { /* best effort */ }
                TryDelete(_tmp);
            }
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
    }

    private static ulong ReadVarintFromStream(Stream s)
    {
        ulong v = 0;
        int shift = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) throw new InvalidDataException("truncated varint in pack");
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
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

    private static Dictionary<string, long> ReadIndex(string idxPath) => ParseIndex(File.ReadAllBytes(idxPath));

    /// <summary>The hash → offset map encoded in <paramref name="b"/> (a <c>.idx</c> file's bytes).</summary>
    internal static Dictionary<string, long> ParseIndex(byte[] b)
    {
        if (b.Length < 9 || b[0] != 'M' || b[1] != 'C' || b[2] != 'A' || b[3] != 'I')
            throw new InvalidDataException("not a pack index");
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

    /// <summary>The object hashes listed in a <c>.idx</c>'s bytes — lets a bucket build its object
    /// index from the small index blobs without downloading the pack data.</summary>
    public static IReadOnlyCollection<string> IndexHashes(byte[] idxBytes) => ParseIndex(idxBytes).Keys;

    // ---- primitives ----

    private static byte[] Deflate(byte[] content)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
        return ms.ToArray();
    }

    /// <summary>Inflates with an output cap so a crafted pack object can't decompression-bomb the
    /// server: the expansion is bounded here, before <see cref="ObjectStore.ImportRaw"/> re-checks.</summary>
    private static byte[] Inflate(byte[] comp)
    {
        using var ms = new MemoryStream(comp);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        var buf = new byte[81920];
        long total = 0;
        int r;
        while ((r = z.Read(buf, 0, buf.Length)) > 0)
        {
            total += r;
            if (total > MaxObjectBytes) throw new InvalidDataException("pack object inflates past the size cap (decompression bomb?)");
            outMs.Write(buf, 0, r);
        }
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
