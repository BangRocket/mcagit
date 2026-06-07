namespace McaGit.Anvil;

/// <summary>Absolute chunk coordinates (in chunks, not blocks).</summary>
public readonly record struct ChunkPos(int X, int Z) : IComparable<ChunkPos>
{
    /// <summary>Index 0..1023 of this chunk within its region's location table.</summary>
    public int RegionIndex => (X & 31) + (Z & 31) * 32;

    /// <summary>Region file coordinate that contains this chunk.</summary>
    public (int RegionX, int RegionZ) Region => (X >> 5, Z >> 5);

    public static ChunkPos FromRegionIndex(int regionX, int regionZ, int index)
        => new(regionX * 32 + (index % 32), regionZ * 32 + (index / 32));

    public int CompareTo(ChunkPos other)
    {
        int byZ = Z.CompareTo(other.Z);
        return byZ != 0 ? byZ : X.CompareTo(other.X);
    }

    public override string ToString() => $"({X}, {Z})";
}
