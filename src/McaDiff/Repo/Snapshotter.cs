using System.Collections.Concurrent;
using System.Security.Cryptography;
using fNbt;
using McaDiff.Anvil;
using McaDiff.Nbt;

namespace McaDiff.Repo;

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

    public static Manifest Snapshot(Repository repo, string worldDir)
        => Build(worldDir, new Ctx(repo.Objects, repo.Cache, WriteCache: true));

    public static Manifest HashOnly(Repository repo, string worldDir)
        => Build(worldDir, new Ctx(Store: null, repo.Cache, WriteCache: false));

    private readonly record struct Ctx(ObjectStore? Store, ChunkCache? Cache, bool WriteCache);

    private static Manifest Build(string worldDir, Ctx ctx)
    {
        string root = Path.GetFullPath(worldDir);
        IgnoreRules ignore = IgnoreRules.Load(root);
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !SkipNames.Contains(Path.GetFileName(f))
                        && !ignore.IsIgnored(Path.GetRelativePath(root, f).Replace('\\', '/')))
            .ToArray();

        var results = new ConcurrentBag<Entry>();
        Parallel.ForEach(files, f =>
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            results.Add(Classify(f, rel, ctx));
        });

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

                NbtCompound root = ChunkCodec.Decode(rc); // throws on LZ4 → fall back to blob
                string hash = Put(NbtCanonical.Serialize(root), ctx);
                if (ctx.WriteCache) ctx.Cache?.Set(cacheKey, hash);
                map[posKey] = hash;
            }
            return map; // may be empty (e.g. 0-byte poi region)
        }
        catch
        {
            return null; // unreadable/LZ4 → store the whole file as a blob instead
        }
    }

    private static string? TryNbt(string fullPath, Ctx ctx)
    {
        try { return Put(NbtCanonical.Serialize(ChunkCodec.LoadNbtFile(fullPath)), ctx); }
        catch { return null; }
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
