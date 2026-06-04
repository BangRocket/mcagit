using fNbt;
using McaDiff.Anvil;
using McaDiff.Query;
using Xunit;

namespace McaDiff.Tests;

/// <summary>World-state inspection (#32 Phase 1): block lookup at a coordinate, and finding
/// entities / block entities by id and proximity.</summary>
public class QueryTests
{
    [Fact]
    public void Inspect_ReturnsBlockAndBiome_AtCoordinate()
    {
        int cell = Cell(1, 2, 3);                     // within-section (x,y,z)
        var blockStates = BlockStates(["minecraft:air", "minecraft:stone"], Fill(4096, cell, 1));
        var section = new NbtCompound { new NbtByte("Y", 0), blockStates };
        string world = WorldWithRegion("region", new ChunkPos(0, 0),
            TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtList("sections", NbtTagType.Compound) { section }));

        BlockInspect? at = new WorldQuery(world).BlockAt(1, 2, 3); // chunk (0,0), section Y=0
        Assert.NotNull(at);
        Assert.Equal("minecraft:stone", at!.Block);

        Assert.Equal("minecraft:air", new WorldQuery(world).BlockAt(0, 0, 0)!.Block); // a different cell
        Assert.Null(new WorldQuery(world).BlockAt(9999, 0, 9999));                    // ungenerated → null
    }

    [Fact]
    public void FindBlockEntity_ById_AndProximity()
    {
        var chest = TestAnvil.BlockEntity("minecraft:chest", 5, 64, 5, new NbtList("Items", NbtTagType.Compound) { new NbtCompound() });
        string world = WorldWithRegion("region", new ChunkPos(0, 0),
            TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtList("block_entities", NbtTagType.Compound) { chest }));

        var q = new WorldQuery(world);
        BlockEntityHit hit = Assert.Single(q.BlockEntities("chest"));
        Assert.Equal((5, 64, 5), (hit.X, hit.Y, hit.Z));
        Assert.Equal(1, hit.ItemCount);

        Assert.Empty(q.BlockEntities("chest", near: (500, 64, 500), radius: 10)); // out of range
        Assert.Empty(q.BlockEntities("furnace"));                                  // id doesn't match
    }

    [Fact]
    public void FindEntity_FromEntitiesRegion_WithCustomName()
    {
        var cow = new NbtCompound
        {
            new NbtString("id", "minecraft:cow"),
            new NbtList("Pos", NbtTagType.Double) { new NbtDouble(8.5), new NbtDouble(64.0), new NbtDouble(8.5) },
            new NbtString("CustomName", "Bessie"),
        };
        string world = WorldWithRegion("entities", new ChunkPos(0, 0),
            TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtList("Entities", NbtTagType.Compound) { cow }));

        EntityHit hit = Assert.Single(new WorldQuery(world).Entities("cow"));
        Assert.Equal("minecraft:cow", hit.Id);
        Assert.Equal("Bessie", hit.CustomName);
        Assert.Equal(8.5, hit.X);
    }

    // ---- builders ----

    private static int Cell(int x, int y, int z) => (y * 16 + z) * 16 + x;

    private static int[] Fill(int count, int cell, int value) { var a = new int[count]; a[cell] = value; return a; }

    private static long[] Pack(int[] indices, int paletteCount, int minBits)
    {
        int bits = 1;
        while ((1 << bits) < paletteCount) bits++;
        int bpe = Math.Max(bits, minBits), perLong = 64 / bpe;
        var data = new long[(indices.Length + perLong - 1) / perLong];
        for (int i = 0; i < indices.Length; i++)
            data[i / perLong] = (long)((ulong)data[i / perLong] | ((ulong)indices[i] & ((1UL << bpe) - 1)) << ((i % perLong) * bpe));
        return data;
    }

    private static NbtCompound BlockStates(string[] names, int[] indices)
    {
        var palette = new NbtList("palette", NbtTagType.Compound);
        foreach (string n in names) palette.Add(new NbtCompound { new NbtString("Name", n) });
        return new NbtCompound("block_states") { palette, new NbtLongArray("data", Pack(indices, names.Length, 4)) };
    }

    private static string WorldWithRegion(string category, ChunkPos pos, NbtCompound chunk)
    {
        string dir = TestAnvil.TempDir("q");
        TestAnvil.WriteRegion(Path.Combine(dir, category, "r.0.0.mca"), (pos, chunk));
        return dir;
    }
}
