using McaDiff.Diff;

namespace McaDiff.Model;

/// <summary>
/// Discovers the set of comparable files in a world directory (or resolves a
/// single file). Knows the standard Anvil layout: <c>region/</c>,
/// <c>entities/</c>, <c>poi/</c> for the overworld plus the <c>DIM-1</c> /
/// <c>DIM1</c> dimension folders, and loose NBT (<c>level.dat</c>,
/// <c>playerdata/*.dat</c>, <c>data/*.dat</c>).
/// </summary>
public static class WorldSource
{
    // Standard dimension sub-roots, relative to the world root ("" = overworld).
    private static readonly string[] DimensionRoots = ["", "DIM-1", "DIM1"];

    // Vanilla dimension paths that, if present under dimensions/, would duplicate the standard roots.
    private static readonly HashSet<string> VanillaDimensionPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "dimensions/minecraft/overworld", "dimensions/minecraft/the_nether", "dimensions/minecraft/the_end",
    };

    // Chunk-bearing categories (each holds r.X.Z.mca files).
    private static readonly string[] RegionCategories = ["region", "entities", "poi"];

    // Loose NBT globs, relative to the world root (covers per-dimension data too). Non-NBT files
    // (advancements/stats JSON) are intentionally NOT enumerated at the world level: the patch format
    // (v1) can't carry a raw blob, so diffing them would break the extract→apply round-trip. They are
    // still byte-comparable via a single-file diff (see ResolveFile). The repo commit path captures
    // them as blobs via its whole-tree walk, so backups are unaffected.
    private static readonly (string Dir, string Pattern, string Category)[] LoosePatterns =
    [
        ("", "level.dat", "nbt"),
        ("playerdata", "*.dat", "nbt"),
        ("data", "*.dat", "nbt"),
        ("data", "*.nbt", "nbt"),          // custom structures
        ("DIM-1/data", "*.dat", "nbt"),
        ("DIM1/data", "*.dat", "nbt"),
    ];

    public static bool IsDirectory(string path) => Directory.Exists(path);

    /// <summary>Resolves a single file argument into one <see cref="WorldUnit"/>.</summary>
    public static WorldUnit ResolveFile(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        bool isRegion = name.EndsWith(".mca", StringComparison.OrdinalIgnoreCase);
        // .mcc (external oversized-chunk payload) and .json are not standalone NBT — byte-compare them.
        bool isBlob = name.EndsWith(".mcc", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        return new WorldUnit(
            RelativePath: name,
            AbsolutePath: full,
            Kind: isRegion ? UnitKind.Region : UnitKind.Loose,
            Category: isRegion ? "region" : isBlob ? "blob" : "nbt");
    }

    /// <summary>
    /// Enumerates every comparable unit under <paramref name="root"/>, keyed by a
    /// normalized (forward-slash) relative path so two worlds can be matched.
    /// </summary>
    public static Dictionary<string, WorldUnit> Enumerate(string root, DiffRunOptions options)
    {
        string full = Path.GetFullPath(root);
        var units = new Dictionary<string, WorldUnit>(StringComparer.Ordinal);

        // Region-style categories across dimensions (standard + data-pack/mod dimensions).
        foreach (string dim in DimensionRootsFor(full))
        {
            foreach (string category in RegionCategories)
            {
                if (!options.IncludesCategory(category)) continue;
                string dir = Path.Combine(full, dim, category);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*.mca"))
                    Add(units, full, file, UnitKind.Region, category);
            }
        }

        // Loose files (NBT + non-NBT blobs).
        if (options.IncludesCategory("nbt"))
        {
            foreach ((string dir, string pattern, string category) in LoosePatterns)
            {
                string searchDir = Path.Combine(full, dir);
                if (!Directory.Exists(searchDir)) continue;
                foreach (string file in Directory.EnumerateFiles(searchDir, pattern))
                    Add(units, full, file, UnitKind.Loose, category);
            }
        }

        return units;
    }

    /// <summary>The standard dimension roots plus any data-pack/mod dimensions found under
    /// <c>dimensions/&lt;namespace&gt;/&lt;path&gt;/</c> (a feature since 1.16).</summary>
    private static IEnumerable<string> DimensionRootsFor(string fullRoot)
    {
        foreach (string d in DimensionRoots) yield return d;

        string dimsDir = Path.Combine(fullRoot, "dimensions");
        if (!Directory.Exists(dimsDir)) yield break;
        foreach (string nsDir in Directory.EnumerateDirectories(dimsDir))
            foreach (string pathDir in Directory.EnumerateDirectories(nsDir))
            {
                string rel = Path.GetRelativePath(fullRoot, pathDir).Replace('\\', '/');
                if (!VanillaDimensionPaths.Contains(rel)) yield return rel; // not a dupe of ""/DIM-1/DIM1
            }
    }

    private static void Add(Dictionary<string, WorldUnit> units, string root, string file, UnitKind kind, string category)
    {
        string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        units[rel] = new WorldUnit(rel, file, kind, category);
    }
}
