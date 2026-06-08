using fNbt;
using McaGit.Anvil;
using McaGit.Diff;
using McaGit.Nbt;

namespace McaGit.Repo;

/// <summary>
/// Diffs two world snapshots described by <see cref="Manifest"/>s, pulling chunk /
/// NBT content from a <see cref="IContentSource"/> (a commit's object store or a
/// working world). Chunk hashes from the manifest let it skip unchanged chunks
/// instantly and decode only the ones that actually differ — then it reuses the
/// same <see cref="WorldDiff"/> model and formatters as the file-based diff.
/// Non-NBT blobs are not diffed (parity with the file-based <c>diff</c>).
/// </summary>
public static class RepoDiffer
{
    public interface IContentSource
    {
        NbtCompound Chunk(string regionRel, string posKey);
        NbtCompound Nbt(string rel);
    }

    public static WorldDiff Diff(string labelA, Manifest a, IContentSource srcA,
                                 string labelB, Manifest b, IContentSource srcB, DiffRunOptions opt)
    {
        var files = new List<FileDiff>();

        foreach (string rel in Union(a.Regions.Keys, b.Regions.Keys))
        {
            if (!opt.IncludesCategory(CategoryOf(rel))) continue;
            bool inA = a.Regions.TryGetValue(rel, out var ca);
            bool inB = b.Regions.TryGetValue(rel, out var cb);

            if (inA && !inB) { files.Add(FileLevel(rel, DiffStatus.Removed, ca!.Count)); continue; }
            if (!inA && inB) { files.Add(FileLevel(rel, DiffStatus.Added, cb!.Count)); continue; }

            var chunks = new List<ChunkDiff>();
            foreach (string posKey in Union(ca!.Keys, cb!.Keys).OrderBy(ParsePos))
            {
                ca.TryGetValue(posKey, out string? hA);
                cb.TryGetValue(posKey, out string? hB);
                if (hA == hB) continue; // hash match → unchanged, no decode

                ChunkPos pos = ParsePos(posKey);
                if (hA is not null && hB is null) chunks.Add(new ChunkDiff(pos, DiffStatus.Removed, []));
                else if (hA is null) chunks.Add(new ChunkDiff(pos, DiffStatus.Added, []));
                else
                {
                    try
                    {
                        NbtCompound chunkA = srcA.Chunk(rel, posKey), chunkB = srcB.Chunk(rel, posKey);
                        ChunkNormalize.DropRedundantPaletteData(chunkA);
                        ChunkNormalize.DropRedundantPaletteData(chunkB);
                        List<NbtChange> blockChanges = BlockDiff.Diff(chunkA, chunkB, opt.ExpandArrays);
                        BlockDiff.StripPalettedContainers(chunkA);
                        BlockDiff.StripPalettedContainers(chunkB);
                        List<NbtChange> ch = NbtComparer.Compare(chunkA, chunkB, opt.Nbt);
                        ch.AddRange(blockChanges);
                        if (ch.Count > 0) chunks.Add(new ChunkDiff(pos, DiffStatus.Modified, ch));
                    }
                    catch (Exception ex) { chunks.Add(new ChunkDiff(pos, DiffStatus.Modified, [], ex.Message)); }
                }
            }
            if (chunks.Count > 0)
                files.Add(new FileDiff(rel, CategoryOf(rel), UnitKind.Region, DiffStatus.Modified, chunks, []));
        }

        if (opt.IncludesCategory("nbt"))
        {
            foreach (string rel in Union(a.Nbt.Keys, b.Nbt.Keys))
            {
                bool inA = a.Nbt.TryGetValue(rel, out string? hA);
                bool inB = b.Nbt.TryGetValue(rel, out string? hB);
                if (inA && inB && hA == hB) continue;

                if (inA && !inB) files.Add(new FileDiff(rel, "nbt", UnitKind.Loose, DiffStatus.Removed, [], []));
                else if (!inA && inB) files.Add(new FileDiff(rel, "nbt", UnitKind.Loose, DiffStatus.Added, [], []));
                else
                {
                    try
                    {
                        List<NbtChange> ch = NbtComparer.Compare(srcA.Nbt(rel), srcB.Nbt(rel), opt.Nbt);
                        if (ch.Count > 0) files.Add(new FileDiff(rel, "nbt", UnitKind.Loose, DiffStatus.Modified, [], ch));
                    }
                    catch (Exception ex) { files.Add(new FileDiff(rel, "nbt", UnitKind.Loose, DiffStatus.Modified, [], [], Error: ex.Message)); }
                }
            }
        }

        files.Sort((x, y) => string.CompareOrdinal(x.RelativePath, y.RelativePath));
        return new WorldDiff(labelA, labelB, files);
    }

    private static FileDiff FileLevel(string rel, DiffStatus status, int chunkCount)
        => new(rel, CategoryOf(rel), UnitKind.Region, status, [], [], chunkCount);

    private static string CategoryOf(string rel)
    {
        string p = "/" + rel;
        if (p.Contains("/entities/")) return "entities";
        if (p.Contains("/poi/")) return "poi";
        return "region";
    }

    private static ChunkPos ParsePos(string key)
    {
        int comma = key.IndexOf(',');
        return new ChunkPos(int.Parse(key[..comma]), int.Parse(key[(comma + 1)..]));
    }

    private static IEnumerable<string> Union(IEnumerable<string> a, IEnumerable<string> b)
    {
        var set = new HashSet<string>(a, StringComparer.Ordinal);
        set.UnionWith(b);
        return set;
    }

    /// <summary>Content from a committed snapshot's object store.</summary>
    public sealed class CommitSource(Repository repo, Manifest manifest) : IContentSource
    {
        public NbtCompound Chunk(string regionRel, string posKey)
            => NbtCanonical.Deserialize(repo.Objects.Read(manifest.Regions[regionRel][posKey]));
        public NbtCompound Nbt(string rel)
            => NbtCanonical.Deserialize(repo.Objects.Read(manifest.Nbt[rel]));
    }

    /// <summary>Content read live from a working world directory (region files cached).</summary>
    public sealed class WorldContentSource(string worldDir) : IContentSource
    {
        private readonly Dictionary<string, RegionFile> _regions = new(StringComparer.Ordinal);

        public NbtCompound Chunk(string regionRel, string posKey)
        {
            if (!_regions.TryGetValue(regionRel, out RegionFile? rf))
                _regions[regionRel] = rf = RegionFile.Open(Path.Combine(worldDir, regionRel));
            int comma = posKey.IndexOf(',');
            var pos = new ChunkPos(int.Parse(posKey[..comma]), int.Parse(posKey[(comma + 1)..]));
            if (!rf.TryGet(pos, out RawChunk rc))
                throw new InvalidOperationException($"chunk {posKey} not found in {regionRel}");
            return ChunkCodec.Decode(rc);
        }

        public NbtCompound Nbt(string rel) => ChunkCodec.LoadNbtFile(Path.Combine(worldDir, rel));
    }
}
