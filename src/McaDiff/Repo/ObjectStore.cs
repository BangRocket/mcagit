using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace McaDiff.Repo;

/// <summary>
/// A content-addressed, deduplicated object store: every object is keyed by the
/// SHA-256 of its content and stored once, zlib-compressed, at
/// <c>objects/&lt;aa&gt;/&lt;rest&gt;</c>. Identical content (e.g. an unchanged
/// chunk in two snapshots) collapses to a single object.
/// </summary>
public sealed class ObjectStore
{
    private readonly string _objectsDir;
    private List<Packfile>? _packs;

    public ObjectStore(string repoDir) => _objectsDir = Path.Combine(repoDir, "objects");

    /// <summary>The <c>objects/</c> directory (loose objects + a <c>pack/</c> subdir).</summary>
    public string ObjectsDir => _objectsDir;

    /// <summary>Lazily-opened packfiles; call <see cref="ReloadPacks"/> after gc changes them.</summary>
    private List<Packfile> Packs => _packs ??= Packfile.OpenAll(_objectsDir).ToList();

    public void ReloadPacks()
    {
        if (_packs is not null) { foreach (Packfile p in _packs) p.Dispose(); _packs = null; }
    }

    // ---- commit staging ----
    // During a commit we stream the commit's *new* objects straight into a single packfile instead of
    // writing one loose file per chunk. The per-loose-file cost (temp-create + rename, hammering the FS
    // catalog/journal) is what makes a cold commit of a 300k-chunk world crawl — brutally so on HFS+.
    // Objects are written to disk as they're produced (not retained in memory), so the live heap stays
    // small and GC doesn't choke the parallel snapshot; only a hash→offset index is kept in memory.
    // Installed by CommitStaging just before the branch ref advances; discarded by AbortStaging on a
    // failed commit — cleaner than leaving orphan loose objects.
    private Packfile.Appender? _stagePack;

    /// <summary>Begins a staging session (see above). Returns a handle that discards the in-progress
    /// pack on Dispose unless <see cref="CommitStaging"/> already installed it — so an aborted commit
    /// leaves nothing behind.</summary>
    public IDisposable BeginStaging()
    {
        _stagePack = new Packfile.Appender(_objectsDir);
        return new StagingScope(this);
    }

    /// <summary>Installs the staged objects as one packfile and ends the session. No-op when no session
    /// is active, so commit-creating paths that didn't stage (merge, rebase, …) keep writing loose
    /// objects exactly as before.</summary>
    public void CommitStaging()
    {
        Packfile.Appender? pack = Interlocked.Exchange(ref _stagePack, null);
        if (pack is null) return;
        pack.Finish();
        ReloadPacks();
    }

    /// <summary>Discards the in-progress staged pack without installing it (a failed/abandoned commit).</summary>
    public void AbortStaging() => Interlocked.Exchange(ref _stagePack, null)?.Dispose();

    private sealed class StagingScope(ObjectStore store) : IDisposable
    {
        public void Dispose() => store.AbortStaging(); // no-op once CommitStaging has cleared it
    }

    /// <summary>Stores <paramref name="content"/> if new; returns its hash either way.</summary>
    public string Write(ReadOnlySpan<byte> content)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (_stagePack is { } pack)
        {
            // Stream a new object into the staged pack; dedup against the store + the staged pack so an
            // unchanged chunk (already in a prior pack) is never re-stored. Add is race-safe.
            if (!Exists(hash)) pack.Add(hash, content);
            return hash;
        }
        string path = PathFor(hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write to a temp file then move, so a concurrent writer never sees a partial object.
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = File.Create(tmp))
            using (var z = new ZLibStream(fs, CompressionLevel.Optimal))
                z.Write(content);
            // Commit the temp file. Two threads can race to write the same hash (e.g. two
            // identical-content files snapshotted in parallel) — whoever loses just drops
            // its temp; the object is already there.
            if (File.Exists(path)) File.Delete(tmp);
            else try { File.Move(tmp, path); }
                catch (IOException) { if (File.Exists(path)) File.Delete(tmp); else throw; }
        }
        return hash;
    }

    public string WriteText(string text) => Write(Encoding.UTF8.GetBytes(text));

    public byte[] Read(string hash)
    {
        if (_stagePack is { } sp && sp.ReadRaw(hash) is { } staged)
            return InflateBounded(staged, MaxObjectBytes);
        string path = PathFor(hash);
        if (File.Exists(path))
        {
            using var fs = File.OpenRead(path);
            using var z = new ZLibStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
        foreach (Packfile pack in Packs)
            if (pack.Read(hash) is { } content) return content;
        throw new FileNotFoundException($"object not found: {hash}");
    }

    public string ReadText(string hash) => Encoding.UTF8.GetString(Read(hash));

    /// <summary>Copies an object from another store (no-op if present). Copies the loose
    /// file when the source has one, otherwise materializes it (the source had it packed).</summary>
    public void Import(ObjectStore src, string hash)
    {
        if (Exists(hash)) return;
        string srcLoose = src.PathFor(hash);
        if (File.Exists(srcLoose))
        {
            string dst = PathFor(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(srcLoose, dst);
        }
        else Write(src.Read(hash)); // packed in src → re-store loose here
    }

    /// <summary>The raw, compressed on-disk bytes of an object (for network transfer);
    /// recompressed from pack content when the object isn't loose.</summary>
    public byte[] ReadRaw(string hash)
    {
        if (_stagePack is { } pack && pack.ReadRaw(hash) is { } staged) return staged;
        string p = PathFor(hash);
        return File.Exists(p) ? File.ReadAllBytes(p) : Compress(Read(hash));
    }

    /// <summary>zlib-compresses content to the loose-object/transport form (shared with BucketTransport).</summary>
    internal static byte[] Compress(ReadOnlySpan<byte> content)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
        return ms.ToArray();
    }

    /// <summary>Decompresses zlib bytes, throwing if the output would exceed <paramref name="max"/>.</summary>
    private static byte[] InflateBounded(byte[] compressed, long max)
    {
        using var ms = new MemoryStream(compressed);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        byte[] buf = new byte[81920];
        long total = 0;
        int r;
        while ((r = z.Read(buf, 0, buf.Length)) > 0)
        {
            total += r;
            if (total > max) throw new InvalidDataException($"object exceeds {max} bytes (decompression bomb?)");
            outMs.Write(buf, 0, r);
        }
        return outMs.ToArray();
    }

    /// <summary>
    /// Stores compressed object bytes received from elsewhere, after verifying they
    /// decompress to content whose SHA-256 equals <paramref name="hash"/> (so a
    /// corrupt or hostile peer can't poison the store). No-op if already present.
    /// </summary>
    /// <summary>Largest object we'll inflate from an untrusted peer (decompression-bomb guard).</summary>
    private const long MaxObjectBytes = 512L * 1024 * 1024;

    public void ImportRaw(string hash, byte[] compressed)
    {
        if (Exists(hash)) return;
        // Inflate with a running byte cap — a few KB of zlib zeros expand ~1000x, so we must
        // bound the output BEFORE materializing it (and before the hash check).
        byte[] content = InflateBounded(compressed, MaxObjectBytes);
        string actual = Convert.ToHexStringLower(SHA256.HashData(content));
        if (actual != hash)
            throw new InvalidDataException($"object hash mismatch: expected {hash}, got {actual}");

        string dst = PathFor(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string tmp = dst + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, compressed);
        if (File.Exists(dst)) File.Delete(tmp); else File.Move(tmp, dst);
    }

    /// <summary>Deletes an object. Returns its on-disk (compressed) size, or 0 if absent.</summary>
    public long Delete(string hash)
    {
        if (!IsValidHash(hash)) return 0;
        string p = PathFor(hash);
        if (!File.Exists(p)) return 0;
        long size = new FileInfo(p).Length;
        File.Delete(p);
        return size;
    }

    public bool Exists(string hash) => IsValidHash(hash)
        && ((_stagePack?.Contains(hash) ?? false) || File.Exists(PathFor(hash)) || Packs.Any(p => p.Contains(hash)));

    /// <summary>A full object id: 64 lowercase hex chars. Validated before any hash reaches
    /// <see cref="PathFor"/>, so a hostile peer can't pass "..config" and traverse out of objects/.</summary>
    public static bool IsValidHash(string hash)
    {
        if (hash.Length != 64) return false;
        foreach (char c in hash)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    /// <summary>True if the object is present and its decompressed content still
    /// hashes to its name (i.e. it isn't truncated or corrupted on disk).</summary>
    public bool VerifyIntegrity(string hash)
    {
        try { return Convert.ToHexStringLower(SHA256.HashData(Read(hash))) == hash; }
        catch { return false; }
    }

    /// <summary>Resolves an abbreviated hash (≥4 hex chars) to a full hash, or null if absent/ambiguous.</summary>
    public string? ResolvePrefix(string prefix)
    {
        if (prefix.Length < 4 || prefix.Length > 64) return null;
        foreach (char c in prefix) // hex-only, so a prefix can never address ".." or a separator
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return null;
        string? match = null;
        string dir = Path.Combine(_objectsDir, prefix[..2]);
        if (Directory.Exists(dir))
        {
            string rest = prefix[2..];
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp") || !name.StartsWith(rest, StringComparison.Ordinal)) continue;
                if (match is not null && match != prefix[..2] + name) return null; // ambiguous
                match = prefix[..2] + name;
            }
        }
        foreach (Packfile pack in Packs)
            if (pack.ResolvePrefix(prefix) is { } h)
            {
                if (match is not null && match != h) return null; // ambiguous across loose/packs
                match = h;
            }
        return match;
    }

    /// <summary>Shortest prefix of <paramref name="hash"/> (≥ <paramref name="minLen"/>) that is
    /// still unambiguous among all stored objects — git's abbreviated-hash behavior. Defaults to
    /// 7 chars and grows only on a real collision.</summary>
    public string Abbreviate(string hash, int minLen = 7)
    {
        if (hash.Length <= minLen) return hash;
        int need = minLen;
        foreach (string other in AllHashes())
        {
            if (other == hash) continue;
            int common = 0, max = Math.Min(hash.Length, other.Length);
            while (common < max && hash[common] == other[common]) common++;
            if (common + 1 > need) need = common + 1;       // need one char past the shared prefix
            if (need >= hash.Length) return hash;
        }
        return hash[..Math.Min(need, hash.Length)];
    }

    /// <summary>Enumerates every stored object hash, loose and packed (used by gc/transfer).</summary>
    public IEnumerable<string> AllHashes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string h in LooseHashes()) if (seen.Add(h)) yield return h;
        foreach (Packfile pack in Packs)
            foreach (string h in pack.Hashes) if (seen.Add(h)) yield return h;
    }

    /// <summary>Just the loose object hashes (gc deletes these once they're packed).</summary>
    public IEnumerable<string> LooseHashes()
    {
        if (!Directory.Exists(_objectsDir)) yield break;
        foreach (string sub in Directory.EnumerateDirectories(_objectsDir))
        {
            if (Path.GetFileName(sub) == "pack") continue;
            foreach (string f in Directory.EnumerateFiles(sub))
            {
                string name = Path.GetFileName(f);
                if (!name.EndsWith(".tmp")) yield return Path.GetFileName(sub) + name;
            }
        }
    }

    /// <summary>A cheap size proxy for delta-grouping during packing (loose file length, else 0).</summary>
    public long StoredSize(string hash)
    {
        string p = PathFor(hash);
        return File.Exists(p) ? new FileInfo(p).Length : 0;
    }

    /// <summary>Existing pack <c>.pack</c> file paths (so gc can delete the old ones after repacking).</summary>
    public IEnumerable<string> PackFilePaths()
    {
        string dir = Path.Combine(_objectsDir, "pack");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.pack") : [];
    }

    /// <summary>Deletes a pack (and its index); returns bytes freed.</summary>
    public long DeletePack(string packPath)
    {
        long freed = 0;
        foreach (string p in new[] { packPath, Path.ChangeExtension(packPath, ".idx") })
            if (File.Exists(p)) { freed += new FileInfo(p).Length; File.Delete(p); }
        return freed;
    }

    /// <summary>Total number of stored objects, loose and packed (used to demonstrate dedup in tests).</summary>
    public int Count() => AllHashes().Count();

    private string PathFor(string hash)
    {
        if (!IsValidHash(hash)) throw new InvalidDataException($"invalid object id: '{hash}'");
        return Path.Combine(_objectsDir, hash[..2], hash[2..]);
    }
}
