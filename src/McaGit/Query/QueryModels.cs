namespace McaGit.Query;

/// <summary>A block entity (chest / sign / spawner / …) located in the world, with absolute coords.</summary>
public sealed record BlockEntityHit(string Id, int X, int Y, int Z, string Region, int ItemCount = 0);

/// <summary>A mob / item-frame / armour-stand / … entity, with floating-point position.</summary>
public sealed record EntityHit(string Id, double X, double Y, double Z, string? CustomName, string Region);

/// <summary>What occupies a single block coordinate (and its biome cell).</summary>
public sealed record BlockInspect(int X, int Y, int Z, string Block, string? Biome, string Dimension);

/// <summary>A player's last-saved state (single-player from level.dat, or a <c>playerdata</c> uuid).</summary>
public sealed record PlayerHit(string Source, double X, double Y, double Z, string Dimension, double Health, int GameMode);

/// <summary>A point of interest (bed / workstation / portal …) from a <c>poi/</c> region.</summary>
public sealed record PoiHit(string Type, int X, int Y, int Z, string Region);

/// <summary>A sign with its readable lines (front + back), across the 1.20+ and legacy formats.</summary>
public sealed record SignHit(int X, int Y, int Z, IReadOnlyList<string> Lines, string Region);

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
