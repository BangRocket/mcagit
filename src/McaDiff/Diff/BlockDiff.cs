using fNbt;

namespace McaDiff.Diff;

/// <summary>
/// Coordinate-level block / biome diff. Matches a chunk's sections by their <c>Y</c> index, decodes
/// each section's paletted <c>block_states</c> / <c>biomes</c> grid on both sides, and reports the
/// cells that actually changed — e.g. <c>sections[-3].block_states[@14,5,7]: minecraft:stone →
/// minecraft:air</c> — instead of an opaque <c>long[N]</c> array delta. A section that changed
/// wholesale (worldgen, <c>/fill</c>) collapses to a one-line summary unless <c>--expand</c> is set.
/// Display-only enrichment: the patch path still records the raw array, so apply is unaffected.
/// </summary>
public static class BlockDiff
{
    // Collapse a section to a summary once more than a quarter of its cells changed (worldgen / fill /
    // mine-out) — a surgical edit of a handful of blocks still lists each. block_states: 1024 of 4096;
    // biomes: 16 of 64.
    private static int SummaryThreshold(int cellCount) => Math.Max(8, cellCount / 4);

    public static List<NbtChange> Diff(NbtCompound rootA, NbtCompound rootB, bool expand)
    {
        var changes = new List<NbtChange>();
        Dictionary<int, NbtCompound> a = SectionsByY(rootA), b = SectionsByY(rootB);
        foreach (int y in a.Keys.Where(b.ContainsKey).OrderBy(v => v))
        {
            DiffContainer(changes, y, "block_states", a[y], b[y], 4096, BlockStateDecoder.BlockMinBits, expand);
            DiffContainer(changes, y, "biomes", a[y], b[y], 64, BlockStateDecoder.BiomeMinBits, expand);
        }
        return changes;
    }

    /// <summary>Removes the paletted containers the coordinate diff owns, so the generic NBT walk
    /// won't also emit their opaque palette / data array deltas. Call after <see cref="Diff"/>.</summary>
    public static void StripPalettedContainers(NbtCompound root)
    {
        if (Sections(root) is not { } sections) return;
        foreach (NbtTag t in sections)
            if (t is NbtCompound sec) { sec.Remove("block_states"); sec.Remove("biomes"); }
    }

    private static void DiffContainer(List<NbtChange> changes, int sectionY, string name,
        NbtCompound secA, NbtCompound secB, int cellCount, int minBits, bool expand)
    {
        if (secA.Get<NbtCompound>(name) is not { } cA || secB.Get<NbtCompound>(name) is not { } cB) return;
        string[]? gA = BlockStateDecoder.Decode(cA, cellCount, minBits);
        string[]? gB = BlockStateDecoder.Decode(cB, cellCount, minBits);
        if (gA is null || gB is null) return; // undecodable → leave it to the generic array diff

        int dim = cellCount == 4096 ? 16 : 4; // 16³ blocks, 4³ biomes
        var diffs = new List<(int X, int Y, int Z, string Old, string New)>();
        for (int i = 0; i < cellCount; i++)
            if (!string.Equals(gA[i], gB[i], StringComparison.Ordinal))
                diffs.Add((i % dim, i / (dim * dim), (i / dim) % dim, gA[i], gB[i])); // i = (y*dim + z)*dim + x

        if (diffs.Count == 0) return;
        if (!expand && diffs.Count > SummaryThreshold(cellCount))
        {
            changes.Add(new NbtChange($"sections[{sectionY}].{name}", ChangeKind.Modified, null, null,
                Note: $"{diffs.Count} of {cellCount} cells changed (use --expand to list each)"));
            return;
        }
        foreach ((int x, int y, int z, string oldK, string newK) in diffs)
            changes.Add(new NbtChange($"sections[{sectionY}].{name}[@{x},{y},{z}]", ChangeKind.Modified, oldK, newK));
    }

    private static Dictionary<int, NbtCompound> SectionsByY(NbtCompound root)
    {
        var map = new Dictionary<int, NbtCompound>();
        if (Sections(root) is not { } sections) return map;
        int idx = 0;
        foreach (NbtTag t in sections)
        {
            if (t is NbtCompound sec)
                map[sec.Get<NbtByte>("Y") is { } yb ? unchecked((sbyte)yb.Value) : idx] = sec; // Y is signed (down to -4)
            idx++;
        }
        return map;
    }

    private static NbtList? Sections(NbtCompound root) =>
        root.Get<NbtList>("sections")                                   // 1.18+ (sections at root)
        ?? root.Get<NbtCompound>("Level")?.Get<NbtList>("Sections")     // legacy
        ?? root.Get<NbtCompound>("Level")?.Get<NbtList>("sections");
}
