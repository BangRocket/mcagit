using fNbt;
using McaDiff.Anvil;
using McaDiff.Nbt;

namespace McaDiff.Repo;

/// <summary>Materializes a <see cref="Manifest"/> back into a playable world directory.</summary>
public static class Checkout
{
    /// <param name="prune">When true (a full checkout/reset/bisect), worktree files not in
    /// the snapshot are removed so it reproduces the snapshot exactly — ignored files
    /// (.mcaignore) and session.lock are preserved. Left false for a partial restore.</param>
    public static void Materialize(Repository repo, Manifest manifest, string worldOut, bool prune = false)
    {
        Directory.CreateDirectory(worldOut);
        if (prune) PruneStray(manifest, worldOut);

        foreach ((string rel, SortedDictionary<string, string> chunks) in manifest.Regions)
        {
            var raws = new List<RawChunk>(chunks.Count);
            foreach ((string posKey, string hash) in chunks)
            {
                ChunkPos pos = ParsePos(posKey);
                NbtCompound root = NbtCanonical.Deserialize(repo.Objects.Read(hash));
                raws.Add(new RawChunk(pos, ChunkCompression.ZLib,
                    ChunkCodec.Encode(root, ChunkCompression.ZLib), external: false, timestamp: 0));
            }
            RegionWriter.Write(Path.Combine(worldOut, rel), raws); // empty list → valid empty region
        }

        foreach ((string rel, string hash) in manifest.Nbt)
        {
            string outFile = Path.Combine(worldOut, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            ChunkCodec.SaveNbtFile(outFile, NbtCanonical.Deserialize(repo.Objects.Read(hash)));
        }

        foreach ((string rel, string hash) in manifest.Blobs)
        {
            string outFile = Path.Combine(worldOut, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, repo.Objects.Read(hash));
        }

        foreach (string rel in manifest.EmptyDirs)
            Directory.CreateDirectory(Path.Combine(worldOut, rel));
    }

    /// <summary>Deletes worktree files absent from the snapshot, so a full checkout is an
    /// exact reproduction. Ignored files and session.lock are kept; empty dirs left behind
    /// (and not recorded in the manifest) are removed.</summary>
    private static void PruneStray(Manifest manifest, string worldOut)
    {
        if (!Directory.Exists(worldOut)) return;
        IgnoreRules ignore = IgnoreRules.Load(worldOut);

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rel in manifest.Regions.Keys) keep.Add(rel);
        foreach (string rel in manifest.Nbt.Keys) keep.Add(rel);
        foreach (string rel in manifest.Blobs.Keys) keep.Add(rel);

        foreach (string file in Directory.EnumerateFiles(worldOut, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(worldOut, file).Replace('\\', '/');
            if (rel == "session.lock" || keep.Contains(rel) || ignore.IsIgnored(rel)) continue;
            File.Delete(file);
        }

        var keepDirs = new HashSet<string>(manifest.EmptyDirs, StringComparer.Ordinal);
        foreach (string dir in Directory.EnumerateDirectories(worldOut, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length)) // deepest first
        {
            string rel = Path.GetRelativePath(worldOut, dir).Replace('\\', '/');
            if (keepDirs.Contains(rel) || ignore.IsIgnored(rel)) continue;
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
    }

    private static ChunkPos ParsePos(string key)
    {
        int comma = key.IndexOf(',');
        return new ChunkPos(int.Parse(key[..comma]), int.Parse(key[(comma + 1)..]));
    }
}
