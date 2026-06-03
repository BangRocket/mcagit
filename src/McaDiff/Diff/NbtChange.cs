using fNbt;

namespace McaDiff.Diff;

public enum ChangeKind
{
    Added,       // key/element present only in B
    Removed,     // key/element present only in A
    Modified,    // value differs
    TypeChanged, // same key, different NBT tag type
}

/// <summary>
/// A single leaf-level difference between two NBT trees, addressed by a
/// dotted/bracketed <see cref="Path"/> (e.g. <c>Heightmaps.WORLD_SURFACE</c>,
/// <c>block_entities[@5,63,8].id</c>).
/// </summary>
public sealed record NbtChange(
    string Path,
    ChangeKind Kind,
    string? OldValue,
    string? NewValue,
    NbtTagType? OldType = null,
    NbtTagType? NewType = null,
    string? Note = null);

/// <summary>Tunables for the NBT comparison.</summary>
public sealed record NbtDiffOptions(bool ExpandArrays = false)
{
    public static readonly NbtDiffOptions Default = new();
}
