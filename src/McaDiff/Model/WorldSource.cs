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
    // Dimension sub-roots, relative to the world root ("" = overworld).
    private static readonly string[] DimensionRoots = ["", "DIM-1", "DIM1"];

    // Chunk-bearing categories (each holds r.X.Z.mca files).
    private static readonly string[] RegionCategories = ["region", "entities", "poi"];

    // Loose NBT globs, relative to the world root (covers per-dimension data too).
    private static readonly (string Dir, string Pattern)[] LoosePatterns =
    [
        ("", "level.dat"),
        ("playerdata", "*.dat"),
        ("data", "*.dat"),
        ("DIM-1/data", "*.dat"),
        ("DIM1/data", "*.dat"),
    ];

    public static bool IsDirectory(string path) => Directory.Exists(path);

    /// <summary>Resolves a single file argument into one <see cref="WorldUnit"/>.</summary>
    public static WorldUnit ResolveFile(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        bool isRegion = name.EndsWith(".mca", StringComparison.OrdinalIgnoreCase);
        return new WorldUnit(
            RelativePath: name,
            AbsolutePath: full,
            Kind: isRegion ? UnitKind.Region : UnitKind.Loose,
            Category: isRegion ? "region" : "nbt");
    }

    /// <summary>
    /// Enumerates every comparable unit under <paramref name="root"/>, keyed by a
    /// normalized (forward-slash) relative path so two worlds can be matched.
    /// </summary>
    public static Dictionary<string, WorldUnit> Enumerate(string root, DiffRunOptions options)
    {
        string full = Path.GetFullPath(root);
        var units = new Dictionary<string, WorldUnit>(StringComparer.Ordinal);

        // Region-style categories across dimensions.
        foreach (string dim in DimensionRoots)
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

        // Loose NBT files.
        if (options.IncludesCategory("nbt"))
        {
            foreach ((string dir, string pattern) in LoosePatterns)
            {
                string searchDir = Path.Combine(full, dir);
                if (!Directory.Exists(searchDir)) continue;
                foreach (string file in Directory.EnumerateFiles(searchDir, pattern))
                    Add(units, full, file, UnitKind.Loose, "nbt");
            }
        }

        return units;
    }

    private static void Add(Dictionary<string, WorldUnit> units, string root, string file, UnitKind kind, string category)
    {
        string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        units[rel] = new WorldUnit(rel, file, kind, category);
    }
}
