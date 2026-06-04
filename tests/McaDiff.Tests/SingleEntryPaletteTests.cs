using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Regression guard for the single-entry-palette false-positive diff (#17 BLOCKER):
/// Minecraft omits <c>block_states.data</c> when a section has one block type, so a section going
/// all-stone must not diff as a spurious <c>data</c> add/remove — while a genuine palette change
/// must still be reported.</summary>
public class SingleEntryPaletteTests
{
    [Fact]
    public void SectionDataPresentVsAbsent_SinglePalette_IsNoDiff()
    {
        string a = WorldWith(Chunk(palette: "minecraft:stone", withData: true));
        string b = WorldWith(Chunk(palette: "minecraft:stone", withData: false));

        // Region bytes differ (one carries the redundant data array), so this exercises the
        // decode+normalize path, not the whole-file fast path.
        Assert.False(WorldDiffer.Diff(a, b, new DiffRunOptions()).HasDifferences);
    }

    [Fact]
    public void GenuinePaletteChange_StillReported()
    {
        string a = WorldWith(Chunk(palette: "minecraft:stone", withData: false));
        string b = WorldWith(Chunk(palette: "minecraft:air", withData: false));
        Assert.True(WorldDiffer.Diff(a, b, new DiffRunOptions()).HasDifferences); // palette[0].Name changed
    }

    [Fact]
    public void BiomesSingleEntryPalette_DataPresentVsAbsent_IsNoDiff()
    {
        string a = WorldWith(ChunkBiomes(withData: true));
        string b = WorldWith(ChunkBiomes(withData: false));
        Assert.False(WorldDiffer.Diff(a, b, new DiffRunOptions()).HasDifferences);
    }

    // ---- builders ----

    private static NbtCompound Chunk(string palette, bool withData)
    {
        var blockStates = new NbtCompound("block_states")
        {
            new NbtList("palette", NbtTagType.Compound) { new NbtCompound { new NbtString("Name", palette) } },
        };
        if (withData) blockStates.Add(new NbtLongArray("data", new long[256])); // all-zero → every cell = palette[0]
        var section = new NbtCompound { new NbtByte("Y", 0), blockStates };     // unnamed: a list element
        return TestAnvil.Root(new NbtInt("DataVersion", 3953),
            new NbtList("sections", NbtTagType.Compound) { section });
    }

    private static NbtCompound ChunkBiomes(bool withData)
    {
        var biomes = new NbtCompound("biomes")
        {
            new NbtList("palette", NbtTagType.String) { new NbtString("minecraft:plains") },
        };
        if (withData) biomes.Add(new NbtLongArray("data", new long[1]));
        var section = new NbtCompound { new NbtByte("Y", 0), biomes };
        return TestAnvil.Root(new NbtInt("DataVersion", 3953),
            new NbtList("sections", NbtTagType.Compound) { section });
    }

    private static string WorldWith(NbtCompound chunk)
    {
        string dir = TestAnvil.TempDir("sep");
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), chunk));
        return dir;
    }
}
