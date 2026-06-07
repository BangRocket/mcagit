using fNbt;
using McaGit.Anvil;
using McaGit.Diff;
using Xunit;

namespace McaGit.Tests;

/// <summary>Coordinate-level block diff (#15/#17 HIGH): decode paletted block_states and report which
/// cells changed (sections[Y].block_states[@x,y,z]: stone → air) instead of an opaque long[] delta.</summary>
public class BlockDiffTests
{
    [Fact]
    public void Decoder_UnpacksPackedIndices()
    {
        int cell = Cell(1, 2, 3); // (x,y,z) within a section, YZX order
        NbtCompound bs = BlockStates(["minecraft:air", "minecraft:stone"], Fill(4096, cell, 1));

        string[]? grid = BlockStateDecoder.Decode(bs, 4096, BlockStateDecoder.BlockMinBits);
        Assert.NotNull(grid);
        Assert.Equal("minecraft:stone", grid![cell]);
        Assert.Equal("minecraft:air", grid[0]);
    }

    [Fact]
    public void Decoder_SinglePalette_IsAllOneBlock_NoData()
    {
        var bs = new NbtCompound("block_states")
        {
            new NbtList("palette", NbtTagType.Compound) { new NbtCompound { new NbtString("Name", "minecraft:bedrock") } },
        };
        string[]? grid = BlockStateDecoder.Decode(bs, 4096, BlockStateDecoder.BlockMinBits);
        Assert.NotNull(grid);
        Assert.All(grid!, b => Assert.Equal("minecraft:bedrock", b));
    }

    [Fact]
    public void Diff_ReportsTheChangedBlock_ByCoordinate()
    {
        int cell = Cell(1, 2, 3);
        string a = World(Chunk(0, BlockStates(["minecraft:air"], null)));                       // all air (single palette)
        string b = World(Chunk(0, BlockStates(["minecraft:air", "minecraft:stone"], Fill(4096, cell, 1))));

        WorldDiff diff = WorldDiffer.Diff(a, b, new DiffRunOptions());
        Assert.True(diff.HasDifferences);
        NbtChange ch = Assert.Single(diff.Files[0].Chunks[0].Changes);
        Assert.Equal("sections[0].block_states[@1,2,3]", ch.Path);
        Assert.Equal("minecraft:air", ch.OldValue);
        Assert.Equal("minecraft:stone", ch.NewValue);
    }

    [Fact]
    public void Diff_WholeSectionChange_CollapsesToSummary_UnlessExpand()
    {
        // A section mined out: every cell stone → air. Without --expand this is one summary line.
        string a = World(Chunk(0, BlockStates(["minecraft:stone"], null)));
        string b = World(Chunk(0, BlockStates(["minecraft:air"], null)));

        NbtChange summary = Assert.Single(WorldDiffer.Diff(a, b, new DiffRunOptions()).Files[0].Chunks[0].Changes);
        Assert.Equal("sections[0].block_states", summary.Path);
        Assert.Contains("4096 of 4096", summary.Note);

        // With --expand, every changed cell is listed.
        WorldDiff expanded = WorldDiffer.Diff(a, b, new DiffRunOptions(ExpandArrays: true));
        Assert.Equal(4096, expanded.Files[0].Chunks[0].Changes.Count);
    }

    [Fact]
    public void Diff_IdenticalSections_NoBlockChanges()
    {
        int cell = Cell(5, 6, 7);
        string a = World(Chunk(0, BlockStates(["minecraft:air", "minecraft:stone"], Fill(4096, cell, 1))));
        string b = World(Chunk(0, BlockStates(["minecraft:air", "minecraft:stone"], Fill(4096, cell, 1))));
        Assert.False(WorldDiffer.Diff(a, b, new DiffRunOptions()).HasDifferences);
    }

    // ---- builders ----

    private static int Cell(int x, int y, int z) => (y * 16 + z) * 16 + x;

    private static int[] Fill(int count, int cell, int value)
    {
        var idx = new int[count];
        idx[cell] = value;
        return idx;
    }

    private static long[] Pack(int[] indices, int paletteCount, int minBits)
    {
        int bits = 1;
        while ((1 << bits) < paletteCount) bits++;
        int bpe = Math.Max(bits, minBits);
        int perLong = 64 / bpe;
        var data = new long[(indices.Length + perLong - 1) / perLong];
        for (int i = 0; i < indices.Length; i++)
        {
            ulong word = (ulong)data[i / perLong];
            word |= ((ulong)indices[i] & ((1UL << bpe) - 1)) << ((i % perLong) * bpe);
            data[i / perLong] = (long)word;
        }
        return data;
    }

    private static NbtCompound BlockStates(string[] names, int[]? indices)
    {
        var palette = new NbtList("palette", NbtTagType.Compound);
        foreach (string n in names) palette.Add(new NbtCompound { new NbtString("Name", n) });
        var bs = new NbtCompound("block_states") { palette };
        if (indices is not null) bs.Add(new NbtLongArray("data", Pack(indices, names.Length, BlockStateDecoder.BlockMinBits)));
        return bs;
    }

    private static NbtCompound Chunk(int sectionY, NbtCompound blockStates)
    {
        var section = new NbtCompound { new NbtByte("Y", unchecked((byte)(sbyte)sectionY)), blockStates };
        return TestAnvil.Root(new NbtInt("DataVersion", 3953),
            new NbtList("sections", NbtTagType.Compound) { section });
    }

    private static string World(NbtCompound chunk)
    {
        string dir = TestAnvil.TempDir("bd");
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), chunk));
        return dir;
    }
}
