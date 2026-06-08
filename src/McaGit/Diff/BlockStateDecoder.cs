using System.Text;
using fNbt;

namespace McaGit.Diff;

/// <summary>
/// Unpacks a section's paletted-and-packed <c>block_states</c> / <c>biomes</c> long-array into a
/// per-cell list of palette keys, so the diff can report coordinate-level block changes instead of
/// an opaque <c>long[N]</c> array delta. Post-1.16 packing rules (verified against minecraft.wiki):
/// indices are bit-packed LSB-first with no straddling across longs, <c>bpe = max(ceil(log2(palette)),
/// minBits)</c>, <c>floor(64/bpe)</c> entries per long. A single-entry palette omits <c>data</c>
/// (every cell is palette[0]). Decode-only — never used on the storage path.
/// </summary>
public static class BlockStateDecoder
{
    public const int BlockMinBits = 4; // block_states: 4-bit floor
    public const int BiomeMinBits = 1; // biomes: no floor

    /// <summary>Decodes <paramref name="container"/> (a <c>block_states</c>/<c>biomes</c> compound)
    /// into <paramref name="cellCount"/> palette keys in YZX order, or null if it isn't a decodable
    /// paletted container.</summary>
    public static string[]? Decode(NbtCompound container, int cellCount, int minBits)
    {
        if (container.Get<NbtList>("palette") is not { Count: > 0 } palette) return null;
        string[] keys = new string[palette.Count];
        for (int i = 0; i < palette.Count; i++) keys[i] = PaletteKey(palette[i]);

        var cells = new string[cellCount];
        if (palette.Count == 1)
        {
            Array.Fill(cells, keys[0]); // single block type → data omitted
            return cells;
        }

        if (container.Get<NbtLongArray>("data") is not { } dataTag) return null;
        long[] data = dataTag.Value;

        int bits = 1;
        while ((1 << bits) < palette.Count) bits++;        // ceil(log2(palette.Count)), exact
        int bpe = Math.Max(bits, minBits);
        int perLong = 64 / bpe;
        ulong mask = (1UL << bpe) - 1;
        if (perLong <= 0 || data.Length < (cellCount + perLong - 1) / perLong) return null; // malformed

        for (int i = 0; i < cellCount; i++)
        {
            ulong word = (ulong)data[i / perLong];
            int shift = (i % perLong) * bpe;
            int idx = (int)((word >> shift) & mask);
            cells[i] = idx < keys.Length ? keys[idx] : "?out-of-range";
        }
        return cells;
    }

    /// <summary>A stable, human-readable key for one palette entry: a biome string, or a block
    /// <c>Name</c> with its properties sorted (<c>minecraft:oak_log[axis=y]</c>).</summary>
    private static string PaletteKey(NbtTag entry)
    {
        if (entry is NbtString s) return s.Value;                  // biome palette
        if (entry is not NbtCompound c) return entry.ToString() ?? "?";
        string name = c.Get<NbtString>("Name")?.Value ?? "?";
        if (c.Get<NbtCompound>("Properties") is not { Count: > 0 } props) return name;
        var sb = new StringBuilder(name).Append('[');
        bool first = true;
        foreach (NbtTag p in props.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            sb.Append(p.Name).Append('=').Append((p as NbtString)?.Value ?? "?");
            first = false;
        }
        return sb.Append(']').ToString();
    }
}
