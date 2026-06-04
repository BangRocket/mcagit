using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
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

    [Fact]
    public void Players_ReadsSinglePlayer_AndPlayerdata()
    {
        string world = TestAnvil.TempDir("qp");
        var player = new NbtCompound("Player")
        {
            new NbtList("Pos", NbtTagType.Double) { new NbtDouble(1.5), new NbtDouble(64), new NbtDouble(-2.5) },
            new NbtString("Dimension", "minecraft:overworld"),
            new NbtFloat("Health", 18f),
            new NbtInt("playerGameType", 0),
        };
        TestAnvil.WriteLoose(Path.Combine(world, "level.dat"), TestAnvil.Root(new NbtCompound("Data") { player }));
        TestAnvil.WriteLoose(Path.Combine(world, "playerdata", "abc.dat"), TestAnvil.Root(
            new NbtList("Pos", NbtTagType.Double) { new NbtDouble(100), new NbtDouble(70), new NbtDouble(100) },
            new NbtInt("Dimension", -1), new NbtFloat("Health", 20f), new NbtInt("playerGameType", 1)));

        var players = new WorldQuery(world).Players().ToList();
        Assert.Equal(2, players.Count);
        PlayerHit sp = Assert.Single(players, p => p.Source == "singleplayer");
        Assert.Equal(1.5, sp.X);
        Assert.Equal(18, sp.Health);
        Assert.Equal("minecraft:the_nether", Assert.Single(players, p => p.Source == "abc").Dimension); // int -1 → nether
    }

    [Fact]
    public void Poi_FindsRecords_ByTypeAndRange()
    {
        var record = new NbtCompound { new NbtString("type", "minecraft:home"), new NbtIntArray("pos", [10, 64, 20]) };
        var sections = new NbtCompound("Sections") { new NbtCompound("4") { new NbtList("Records", NbtTagType.Compound) { record } } };
        string world = WorldWithRegion("poi", new ChunkPos(0, 0), TestAnvil.Root(new NbtInt("DataVersion", 3953), sections));

        PoiHit hit = Assert.Single(new WorldQuery(world).Poi("home"));
        Assert.Equal((10, 64, 20), (hit.X, hit.Y, hit.Z));
        Assert.Empty(new WorldQuery(world).Poi("home", near: (-500, 64, -500), radius: 10));
    }

    [Fact]
    public void Signs_MatchByText_AcrossFormats()
    {
        var sign = TestAnvil.BlockEntity("minecraft:oak_sign", 3, 70, 4,
            new NbtCompound("front_text") { new NbtList("messages", NbtTagType.String) { new NbtString("{\"text\":\"Welcome home\"}"), new NbtString("\"\"") } });
        string world = WorldWithRegion("region", new ChunkPos(0, 0),
            TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtList("block_entities", NbtTagType.Compound) { sign }));

        SignHit hit = Assert.Single(new WorldQuery(world).Signs("home"));
        Assert.Contains("Welcome home", hit.Lines);          // JSON text component unwrapped
        Assert.Empty(new WorldQuery(world).Signs("nonexistent"));
    }

    [Fact]
    public void WhereChanged_ClassifiesDestructionAndConstruction()
    {
        // A: stone at (1,2,3). B: stone moved to (7,8,9) → (1,2,3) is destroyed, (7,8,9) is built.
        string a = WorldWithRegion("region", new ChunkPos(0, 0), ChunkWithStone(Cell(1, 2, 3)));
        string b = WorldWithRegion("region", new ChunkPos(0, 0), ChunkWithStone(Cell(7, 8, 9)));

        GriefSummary g = GriefReport.Analyze(WorldDiffer.Diff(a, b, new DiffRunOptions(ExpandArrays: true)));
        Assert.Equal(1, g.Destroyed);
        Assert.Equal(1, g.Built);
        Assert.Equal(0, g.Replaced);
        Assert.Contains(g.Events, e => e.Kind == "destroyed" && (e.X, e.Y, e.Z) == (1, 2, 3) && e.Old == "minecraft:stone");
        Assert.Equal((1, 2, 3), g.Center); // single destruction → bbox center is it
    }

    private static NbtCompound ChunkWithStone(int cell)
    {
        var section = new NbtCompound { new NbtByte("Y", 0), BlockStates(["minecraft:air", "minecraft:stone"], Fill(4096, cell, 1)) };
        return TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtList("sections", NbtTagType.Compound) { section });
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
