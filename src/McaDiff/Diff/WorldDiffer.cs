using McaDiff.Anvil;
using McaDiff.Model;

namespace McaDiff.Diff;

/// <summary>
/// Top-level orchestration: matches files between two worlds, then chunks within
/// region files, then NBT trees within chunks. Two fast paths keep whole-world
/// diffs tractable — identical file bytes skip the file entirely, and identical
/// compressed chunk payloads skip decompression/NBT parsing.
/// </summary>
public static class WorldDiffer
{
    private static readonly IReadOnlyList<NbtChange> NoChanges = Array.Empty<NbtChange>();
    private static readonly IReadOnlyList<ChunkDiff> NoChunks = Array.Empty<ChunkDiff>();

    public static WorldDiff Diff(string pathA, string pathB, DiffRunOptions options)
    {
        bool dirA = WorldSource.IsDirectory(pathA);
        bool dirB = WorldSource.IsDirectory(pathB);

        if (!dirA && !dirB)
            return DiffSingleFiles(pathA, pathB, options);

        if (dirA != dirB)
            throw new ArgumentException("Both inputs must be the same kind: two world folders or two files.");

        Dictionary<string, WorldUnit> unitsA = WorldSource.Enumerate(pathA, options);
        Dictionary<string, WorldUnit> unitsB = WorldSource.Enumerate(pathB, options);

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        keys.UnionWith(unitsA.Keys);
        keys.UnionWith(unitsB.Keys);
        string[] keyArr = keys.ToArray();

        var results = new FileDiff?[keyArr.Length];
        Parallel.For(0, keyArr.Length, i =>
        {
            string k = keyArr[i];
            bool inA = unitsA.TryGetValue(k, out WorldUnit? ua);
            bool inB = unitsB.TryGetValue(k, out WorldUnit? ub);
            results[i] = (inA, inB) switch
            {
                (true, false) => AddedOrRemoved(ua!, DiffStatus.Removed),
                (false, true) => AddedOrRemoved(ub!, DiffStatus.Added),
                _ => DiffUnit(ua!, ub!, options),
            };
        });

        var files = results.Where(r => r is not null).Select(r => r!).ToList();
        return new WorldDiff(pathA, pathB, files);
    }

    private static WorldDiff DiffSingleFiles(string pathA, string pathB, DiffRunOptions options)
    {
        WorldUnit a = WorldSource.ResolveFile(pathA);
        WorldUnit b = WorldSource.ResolveFile(pathB);
        if (a.Kind != b.Kind)
            throw new ArgumentException("Cannot compare a region (.mca) file to a loose NBT (.dat) file.");

        FileDiff? fd = DiffUnit(a, b, options);
        return new WorldDiff(pathA, pathB, fd is null ? Array.Empty<FileDiff>() : new[] { fd });
    }

    private static FileDiff AddedOrRemoved(WorldUnit unit, DiffStatus status)
    {
        int? count = null;
        if (unit.Kind == UnitKind.Region)
        {
            try { count = RegionFile.CountChunks(unit.AbsolutePath); }
            catch { /* corrupt/unreadable — leave count unknown */ }
        }
        return new FileDiff(unit.RelativePath, unit.Category, unit.Kind, status, NoChunks, NoChanges, count);
    }

    /// <summary>Diffs a unit present in both worlds. Returns null when identical.</summary>
    private static FileDiff? DiffUnit(WorldUnit a, WorldUnit b, DiffRunOptions options)
    {
        try
        {
            return a.Kind == UnitKind.Region
                ? DiffRegion(a, b, options)
                : DiffLoose(a, b, options);
        }
        catch (Exception ex)
        {
            return new FileDiff(b.RelativePath, b.Category, b.Kind, DiffStatus.Modified,
                NoChunks, NoChanges, Error: ex.Message);
        }
    }

    private static FileDiff? DiffRegion(WorldUnit a, WorldUnit b, DiffRunOptions options)
    {
        byte[] bytesA = File.ReadAllBytes(a.AbsolutePath);
        byte[] bytesB = File.ReadAllBytes(b.AbsolutePath);
        if (bytesA.AsSpan().SequenceEqual(bytesB))
            return null; // whole-file fast path

        RegionFile regA = RegionFile.Parse(a.AbsolutePath, bytesA);
        RegionFile regB = RegionFile.Parse(b.AbsolutePath, bytesB);

        var positions = new SortedSet<ChunkPos>();
        foreach (RawChunk c in regA.Chunks) positions.Add(c.Pos);
        foreach (RawChunk c in regB.Chunks) positions.Add(c.Pos);

        var chunkDiffs = new List<ChunkDiff>();
        foreach (ChunkPos pos in positions)
        {
            bool inA = regA.TryGet(pos, out RawChunk ca);
            bool inB = regB.TryGet(pos, out RawChunk cb);

            if (inA && !inB)
                chunkDiffs.Add(new ChunkDiff(pos, DiffStatus.Removed, NoChanges));
            else if (!inA && inB)
                chunkDiffs.Add(new ChunkDiff(pos, DiffStatus.Added, NoChanges));
            else
            {
                if (ca.PayloadEquals(cb))
                    continue; // chunk fast path: identical compressed bytes

                try
                {
                    var rootA = ChunkCodec.Decode(ca);
                    var rootB = ChunkCodec.Decode(cb);
                    List<NbtChange> changes = NbtComparer.Compare(rootA, rootB, options.Nbt);
                    if (changes.Count > 0)
                        chunkDiffs.Add(new ChunkDiff(pos, DiffStatus.Modified, changes));
                }
                catch (Exception ex)
                {
                    chunkDiffs.Add(new ChunkDiff(pos, DiffStatus.Modified, NoChanges, ex.Message));
                }
            }
        }

        if (chunkDiffs.Count == 0)
            return null; // bytes differed only in padding/ordering

        return new FileDiff(b.RelativePath, b.Category, UnitKind.Region, DiffStatus.Modified,
            chunkDiffs, NoChanges);
    }

    private static FileDiff? DiffLoose(WorldUnit a, WorldUnit b, DiffRunOptions options)
    {
        byte[] bytesA = File.ReadAllBytes(a.AbsolutePath);
        byte[] bytesB = File.ReadAllBytes(b.AbsolutePath);
        if (bytesA.AsSpan().SequenceEqual(bytesB))
            return null;

        var rootA = ChunkCodec.LoadNbtFile(a.AbsolutePath);
        var rootB = ChunkCodec.LoadNbtFile(b.AbsolutePath);
        List<NbtChange> changes = NbtComparer.Compare(rootA, rootB, options.Nbt);
        if (changes.Count == 0)
            return null; // differ only in compression framing

        return new FileDiff(b.RelativePath, b.Category, UnitKind.Loose, DiffStatus.Modified,
            NoChunks, changes);
    }
}
