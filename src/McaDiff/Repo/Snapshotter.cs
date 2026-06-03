using System.Collections.Concurrent;
using System.Security.Cryptography;
using fNbt;
using McaDiff.Anvil;
using McaDiff.Nbt;

namespace McaDiff.Repo;

/// <summary>
/// Turns a world directory into a <see cref="Manifest"/>, storing each unique
/// chunk / loose-NBT / file as a content-addressed object (or, when
/// <c>store</c> is null, just computing hashes — used by status). Region/entities/
/// poi <c>.mca</c> become per-chunk objects (max dedup); <c>.dat</c> become
/// canonical NBT objects; everything else is a raw blob.
/// </summary>
public static class Snapshotter
{
    private static readonly string[] SkipNames = ["session.lock"];

    public static Manifest Snapshot(Repository repo, string worldDir) => Build(worldDir, repo.Objects);

    public static Manifest HashOnly(string worldDir) => Build(worldDir, store: null);

    private static Manifest Build(string worldDir, ObjectStore? store)
    {
        string root = Path.GetFullPath(worldDir);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !SkipNames.Contains(Path.GetFileName(f)))
            .ToArray();

        var results = new ConcurrentBag<Entry>();
        Parallel.ForEach(files, f =>
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            results.Add(Classify(f, rel, store));
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
        return manifest;
    }

    private static Entry Classify(string fullPath, string rel, ObjectStore? store)
    {
        if (IsRegionFile(rel) && TryChunks(fullPath, store) is { } chunks)
            return new Entry(rel, EntryKind.Region, chunks, null);

        if (rel.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && TryNbt(fullPath, store) is { } nbtHash)
            return new Entry(rel, EntryKind.Nbt, null, nbtHash);

        return new Entry(rel, EntryKind.Blob, null, Put(File.ReadAllBytes(fullPath), store));
    }

    private static Dictionary<string, string>? TryChunks(string fullPath, ObjectStore? store)
    {
        try
        {
            RegionFile region = RegionFile.Open(fullPath);
            var map = new Dictionary<string, string>(region.Count);
            foreach (RawChunk rc in region.Chunks)
            {
                NbtCompound root = ChunkCodec.Decode(rc); // throws on LZ4 → fall back to blob
                map[$"{rc.Pos.X},{rc.Pos.Z}"] = Put(NbtCanonical.Serialize(root), store);
            }
            return map; // may be empty (e.g. 0-byte poi region)
        }
        catch
        {
            return null; // unreadable/LZ4 → store the whole file as a blob instead
        }
    }

    private static string? TryNbt(string fullPath, ObjectStore? store)
    {
        try { return Put(NbtCanonical.Serialize(ChunkCodec.LoadNbtFile(fullPath)), store); }
        catch { return null; }
    }

    private static string Put(byte[] content, ObjectStore? store)
        => store is not null ? store.Write(content) : Convert.ToHexStringLower(SHA256.HashData(content));

    private static bool IsRegionFile(string rel)
    {
        if (!rel.EndsWith(".mca", StringComparison.OrdinalIgnoreCase)) return false;
        string p = "/" + rel;
        return p.Contains("/region/") || p.Contains("/entities/") || p.Contains("/poi/");
    }

    private enum EntryKind { Region, Nbt, Blob }
    private readonly record struct Entry(string Rel, EntryKind Kind, Dictionary<string, string>? Chunks, string? Hash);
}
