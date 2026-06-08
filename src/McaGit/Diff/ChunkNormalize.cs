using fNbt;

namespace McaGit.Diff;

/// <summary>
/// Diff-path-only normalizations that cancel representation differences Minecraft itself
/// treats as equivalent. <b>Never</b> applied on the canonical/storage path
/// (<see cref="McaGit.Nbt.NbtCanonical"/>) — only to decoded chunk roots right before a
/// semantic compare, so the object hash and on-disk bytes are untouched.
/// </summary>
public static class ChunkNormalize
{
    private const int MaxDepth = 512;

    /// <summary>
    /// Drops the redundant index array from single-entry palettes. When a section's
    /// <c>block_states</c>/<c>biomes</c> palette has exactly one entry, Minecraft omits the
    /// <c>data</c> long-array (wiki: "If only one block state is present … this field is not
    /// required") — every cell trivially indexes palette[0]. Some writers emit it anyway (all
    /// zeros), so a section going all-air/all-stone would otherwise diff as a spurious
    /// <c>data</c> add/remove. Removing it on both sides makes the comparison purely palette-based.
    /// Mutates <paramref name="root"/> in place (safe: diff decodes a fresh tree per chunk).
    /// </summary>
    public static void DropRedundantPaletteData(NbtCompound root) => Walk(root, 0);

    private static void Walk(NbtTag tag, int depth)
    {
        if (depth > MaxDepth) return;
        switch (tag)
        {
            case NbtCompound c:
                // A compound carrying a single-entry palette + a data array: the data is redundant.
                if (c.Get<NbtList>("palette") is { Count: 1 } && c.Contains("data"))
                    c.Remove("data");
                foreach (NbtTag child in c.Tags.ToArray()) // ToArray: we may remove during iteration
                    Walk(child, depth + 1);
                break;
            case NbtList list:
                foreach (NbtTag child in list)
                    Walk(child, depth + 1);
                break;
        }
    }
}
