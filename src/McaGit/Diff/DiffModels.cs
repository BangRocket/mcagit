using McaGit.Anvil;

namespace McaGit.Diff;

public enum UnitKind { Region, Loose }

public enum DiffStatus { Added, Removed, Modified }

/// <summary>One comparable file in a world: a region <c>.mca</c> or a loose NBT <c>.dat</c>.</summary>
public sealed record WorldUnit(string RelativePath, string AbsolutePath, UnitKind Kind, string Category);

/// <summary>A single chunk's diff within a modified region file.</summary>
public sealed record ChunkDiff(
    ChunkPos Pos,
    DiffStatus Status,
    IReadOnlyList<NbtChange> Changes,
    string? Error = null);

/// <summary>A file-level diff: region (with per-chunk detail) or loose (with NBT changes).</summary>
public sealed record FileDiff(
    string RelativePath,
    string Category,
    UnitKind Kind,
    DiffStatus Status,
    IReadOnlyList<ChunkDiff> Chunks,
    IReadOnlyList<NbtChange> Changes,
    int? ItemCount = null,
    string? Error = null);

/// <summary>The complete diff of two worlds (or two single files).</summary>
public sealed record WorldDiff(string PathA, string PathB, IReadOnlyList<FileDiff> Files)
{
    public bool HasDifferences => Files.Count > 0;
}

/// <summary>Options controlling a diff run.</summary>
public sealed record DiffRunOptions(
    bool ExpandArrays = false,
    IReadOnlySet<string>? OnlyCategories = null)
{
    public NbtDiffOptions Nbt => new(ExpandArrays);
    public bool IncludesCategory(string category) => OnlyCategories is null || OnlyCategories.Contains(category);
}
