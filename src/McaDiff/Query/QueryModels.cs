namespace McaDiff.Query;

/// <summary>A block entity (chest / sign / spawner / …) located in the world, with absolute coords.</summary>
public sealed record BlockEntityHit(string Id, int X, int Y, int Z, string Region, int ItemCount = 0);

/// <summary>A mob / item-frame / armour-stand / … entity, with floating-point position.</summary>
public sealed record EntityHit(string Id, double X, double Y, double Z, string? CustomName, string Region);

/// <summary>What occupies a single block coordinate (and its biome cell).</summary>
public sealed record BlockInspect(int X, int Y, int Z, string Block, string? Biome, string Dimension);

/// <summary>The standard dimensions, with their on-disk sub-roots ("" = overworld).</summary>
public static class Dimensions
{
    public static string SubDir(string dim) => dim.ToLowerInvariant() switch
    {
        "" or "overworld" or "0" or "minecraft:overworld" => "",
        "nether" or "the_nether" or "-1" or "minecraft:the_nether" => "DIM-1",
        "end" or "the_end" or "1" or "minecraft:the_end" => "DIM1",
        _ => dim, // a custom dimension path, used as-is
    };
}
