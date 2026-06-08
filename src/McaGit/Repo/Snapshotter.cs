using System.Collections.Concurrent;
using System.Security.Cryptography;
using fNbt;
using McaGit.Anvil;
using McaGit.Nbt;
using McaGit.Output;

namespace McaGit.Repo;

/// <summary>
/// Turns a world directory into a <see cref="Manifest"/>, storing each unique
/// chunk / loose-NBT / file as a content-addressed object (or, when
/// <c>Store</c> is null, just computing hashes — used by status). Region/entities/
/// poi <c>.mca</c> become per-chunk objects (max dedup); <c>.dat</c> become
/// canonical NBT objects; everything else is a raw blob. A <see cref="ChunkCache"/>
/// lets unchanged chunks skip decoding on re-commit.
/// </summary>
public static class Snapshotter
{
    private static readonly string[] SkipNames = ["session.lock"];

    public static Manifest Snapshot(Repository repo, string worldDir, Progress? progress = null)
        => Build(worldDir, new Ctx(repo.Objects, repo.Cache, WriteCache: true), repo.Dir, progress);

    public static Manifest HashOnly(Repository repo, string worldDir)
        => Build(worldDir, new Ctx(Store: null, repo.Cache, WriteCache: false), repo.Dir, null);

    private readonly record struct Ctx(ObjectStore? Store, ChunkCache? Cache, bool WriteCache);

    private static Manifest Build(string worldDir, Ctx ctx, string? repoDir = null, Progress? progress = null)
    {
        string root = Path.GetFullPath(worldDir);
        IgnoreRules ignore = IgnoreRules.Load(root);
        // If the repo lives inside the world (e.g. `init` run in the world folder), never capture its
        // own metadata — the live mcagit.lock can't even be read while we hold it (issue #26).
        string? repoPrefix = repoDir is null ? null : Path.GetFullPath(repoDir) + Path.DirectorySeparatorChar;
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !SkipNames.Contains(Path.GetFileName(f))
                        && (repoPrefix is null || !Path.GetFullPath(f).StartsWith(repoPrefix, StringComparison.Ordinal))
                        && !ignore.IsIgnored(Path.GetRelativePath(root, f).Replace('\\', '/')))
            .ToArray();

        progress?.Begin("Snapshotting world");
        int total = files.Length, done = 0;
        long chunks = 0;
        var results = new ConcurrentBag<Entry>();
        Parallel.ForEach(files, f =>
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            Entry e = Classify(f, rel, ctx);
            results.Add(e);
            if (progress is not null)
                progress.Update(Interlocked.Increment(ref done), total,
                    $"{Interlocked.Add(ref chunks, e.Chunks?.Count ?? 0)} chunks");
        });
        progress?.Done(total, total, $"{chunks} chunks");

        var manifest = new Manifest();
        foreach (Entry e in results)
        {
            switch (e.Kind)
            {
                case EntryKind.Region: manifest.Regions[e.Rel] = new SortedDictionary<string, string>(e.Chunks!, StringComparer.Ordinal); break;
                case EntryKind.Nbt: manifest.Nbt[e.Rel] = e.Hash!; break;
                default: manifest.Blobs[e.Rel] = e.Hash!; break;
            }
        }

        // Record empty directories so checkout reproduces the layout faithfully.
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (repoPrefix is not null && (Path.GetFullPath(dir) + Path.DirectorySeparatorChar).StartsWith(repoPrefix, StringComparison.Ordinal)) continue;
            string rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            if (!ignore.IsIgnored(rel) && !Directory.EnumerateFileSystemEntries(dir).Any())
                manifest.EmptyDirs.Add(rel);
        }
        manifest.EmptyDirs.Sort(StringComparer.Ordinal);

        if (ctx.WriteCache) ctx.Cache?.Save();
        return manifest;
    }

    private static Entry Classify(string fullPath, string rel, Ctx ctx)
    {
        if (IsRegionFile(rel) && TryChunks(fullPath, ctx) is { } chunks)
            return new Entry(rel, EntryKind.Region, chunks, null);

        if (rel.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && TryNbt(fullPath, ctx) is { } nbtHash)
            return new Entry(rel, EntryKind.Nbt, null, nbtHash);

        return new Entry(rel, EntryKind.Blob, null, Put(File.ReadAllBytes(fullPath), ctx));
    }

    private static Dictionary<string, string>? TryChunks(string fullPath, Ctx ctx)
    {
        try
        {
            RegionFile region = RegionFile.Open(fullPath);
            var map = new Dictionary<string, string>(region.Count);
            foreach (RawChunk rc in region.Chunks)
            {
                string posKey = $"{rc.Pos.X},{rc.Pos.Z}";
                string cacheKey = $"{(int)rc.Compression}:{Convert.ToHexStringLower(SHA256.HashData(rc.Payload))}";

                if (ctx.Cache is not null && ctx.Cache.TryGet(cacheKey, out string hit)
                    && (ctx.Store is null || ctx.Store.Exists(hit)))
                {
                    map[posKey] = hit; // unchanged chunk — no decode/canonicalize
                    continue;
                }

                // Retry so a transient parse hiccup never silently downgrades the whole
                // region to a blob; a genuine UnsupportedChunkException (LZ4) still falls back.
                // Canonicalize straight from the decompressed bytes — no fNbt tree (the per-tag object
                // churn is what makes a cold commit allocation-bound). Byte-identical to the tree path
                // (NbtCanonicalRawTests), so hashes/dedup are unchanged.
                byte[] canon = Retry(() => ChunkCodec.DecodeCanonicalRaw(rc));
                string hash = Put(canon, ctx);
                if (ctx.WriteCache) ctx.Cache?.Set(cacheKey, hash);
                map[posKey] = hash;
            }
            return map; // may be empty (e.g. 0-byte poi region)
        }
        catch (Exception e) when (IsParseFailure(e))
        {
            return null; // genuinely not a region container / undecodable → store as a raw blob
        }
    }

    /// <summary>True for "this file isn't a parseable region/NBT" errors (→ blob fallback). A real
    /// IOException (disk error) or OutOfMemoryException propagates so a commit fails loudly rather
    /// than silently storing a corrupt blob — but a structurally-short file (EndOfStream) is a blob.</summary>
    private static bool IsParseFailure(Exception e)
        => e is EndOfStreamException || e is not (IOException or OutOfMemoryException);

    private static string? TryNbt(string fullPath, Ctx ctx)
    {
        // Retry so a rare transient failure can't misclassify valid NBT as a blob
        // (which would make manifests nondeterministic); genuine non-NBT → blob. Canonicalize from
        // raw bytes (no fNbt tree), byte-identical to the tree path (NbtCanonicalRawTests).
        try { return Put(Retry(() => ChunkCodec.LoadNbtCanonicalRaw(fullPath)), ctx); }
        catch (Exception e) when (IsParseFailure(e)) { return null; }
    }

    /// <summary>Runs <paramref name="action"/>, retrying twice on failure before letting it throw.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (int i = 0; i < 2; i++)
        {
            try { return action(); }
            catch { /* transient — retry */ }
        }
        return action();
    }

    private static string Put(byte[] content, Ctx ctx)
        => ctx.Store is not null ? ctx.Store.Write(content) : Convert.ToHexStringLower(SHA256.HashData(content));

    private static bool IsRegionFile(string rel)
    {
        if (!rel.EndsWith(".mca", StringComparison.OrdinalIgnoreCase)) return false;
        string p = "/" + rel;
        return p.Contains("/region/") || p.Contains("/entities/") || p.Contains("/poi/");
    }

    private enum EntryKind { Region, Nbt, Blob }
    private readonly record struct Entry(string Rel, EntryKind Kind, Dictionary<string, string>? Chunks, string? Hash);
}
