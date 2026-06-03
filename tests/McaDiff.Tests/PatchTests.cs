using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Nbt;
using McaDiff.Patch;
using Xunit;

namespace McaDiff.Tests;

public class PatchTests
{
    private static readonly DiffRunOptions Diff = new();

    private static NbtCompound Chunk(string status, long heightmap0 = 1) => TestAnvil.Root(
        new NbtInt("DataVersion", 3953),
        new NbtString("Status", status),
        new NbtLongArray("Heightmap", new[] { heightmap0, 2, 3 }),
        new NbtList("block_entities", new[] { TestAnvil.BlockEntity("minecraft:chest", 5, 63, 8) }));

    private static NbtCompound Level(long time) =>
        TestAnvil.Root(new NbtCompound("Data") { new NbtLong("Time", time), new NbtString("LevelName", "T") });

    private static void BuildWorld(string dir, (ChunkPos Pos, NbtCompound Root)[] chunks, long levelTime)
    {
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), chunks);
        TestAnvil.WriteLoose(Path.Combine(dir, "level.dat"), Level(levelTime));
    }

    private static (string A, string B) BuildAB()
    {
        string a = TestAnvil.TempDir("pA"), b = TestAnvil.TempDir("pB");
        BuildWorld(a, [(new ChunkPos(0, 0), Chunk("a-zero")), (new ChunkPos(1, 0), Chunk("a-one"))], 1000);
        BuildWorld(b, [(new ChunkPos(0, 0), Chunk("b-zero", heightmap0: 99)), (new ChunkPos(2, 0), Chunk("b-two"))], 2000);
        return (a, b); // B vs A: chunk(0,0) modified, (1,0) removed, (2,0) added, level.dat modified
    }

    [Fact]
    public void Forward_ThroughJson_ReproducesTarget_AndLeavesBaseUntouched()
    {
        (string a, string b) = BuildAB();
        WorldPatch patch = WorldPatch.FromJson(PatchExtractor.Extract(a, b, Diff).ToJson()); // exercise serialization

        string outDir = Path.Combine(TestAnvil.TempDir("pOut"), "world");
        ApplyReport report = PatchApplier.Apply(patch, a, outDir, new ApplySettings());

        Assert.False(report.HasConflicts);
        Assert.False(WorldDiffer.Diff(outDir, b, Diff).HasDifferences);   // restored == B
        Assert.True(WorldDiffer.Diff(a, b, Diff).HasDifferences);          // base A untouched
    }

    [Fact]
    public void Reverse_ReproducesBase()
    {
        (string a, string b) = BuildAB();
        WorldPatch patch = PatchExtractor.Extract(a, b, Diff);

        string outDir = Path.Combine(TestAnvil.TempDir("pRev"), "world");
        ApplyReport report = PatchApplier.Apply(patch, b, outDir, new ApplySettings(Reverse: true));

        Assert.False(report.HasConflicts);
        Assert.False(WorldDiffer.Diff(outDir, a, Diff).HasDifferences);   // reverse onto B == A
    }

    [Fact]
    public void WholeChunkMode_RoundTrips()
    {
        (string a, string b) = BuildAB();
        WorldPatch patch = PatchExtractor.Extract(a, b, Diff, wholeChunk: true, wholeFile: true);

        string outDir = Path.Combine(TestAnvil.TempDir("pWhole"), "world");
        PatchApplier.Apply(patch, a, outDir, new ApplySettings());
        Assert.False(WorldDiffer.Diff(outDir, b, Diff).HasDifferences);
    }

    [Fact]
    public void Conflict_IsReported_AndTargetNodeIsNotClobbered()
    {
        // A→B changes only chunk(0,0).Status: alpha → beta.
        string a = TestAnvil.TempDir("cA"), b = TestAnvil.TempDir("cB"), t = TestAnvil.TempDir("cT");
        BuildWorld(a, [(new ChunkPos(0, 0), Chunk("alpha"))], 1000);
        BuildWorld(b, [(new ChunkPos(0, 0), Chunk("beta"))], 1000);
        BuildWorld(t, [(new ChunkPos(0, 0), Chunk("zeta"))], 1000); // target diverged from base

        WorldPatch patch = PatchExtractor.Extract(a, b, Diff);
        string outDir = Path.Combine(TestAnvil.TempDir("cOut"), "world");
        ApplyReport report = PatchApplier.Apply(patch, t, outDir, new ApplySettings());

        Assert.True(report.HasConflicts);
        NbtCompound chunk = ChunkCodec.Decode(RegionFile.Open(Path.Combine(outDir, "region", "r.0.0.mca"))
            .Chunks.First());
        Assert.Equal("zeta", chunk.Get("Status")!.StringValue); // not clobbered to "beta"
    }

    [Fact]
    public void Force_OverridesGuard()
    {
        string a = TestAnvil.TempDir("fA"), b = TestAnvil.TempDir("fB"), t = TestAnvil.TempDir("fT");
        BuildWorld(a, [(new ChunkPos(0, 0), Chunk("alpha"))], 1000);
        BuildWorld(b, [(new ChunkPos(0, 0), Chunk("beta"))], 1000);
        BuildWorld(t, [(new ChunkPos(0, 0), Chunk("zeta"))], 1000);

        WorldPatch patch = PatchExtractor.Extract(a, b, Diff);
        string outDir = Path.Combine(TestAnvil.TempDir("fOut"), "world");
        ApplyReport report = PatchApplier.Apply(patch, t, outDir, new ApplySettings(Force: true));

        Assert.False(report.HasConflicts);
        NbtCompound chunk = ChunkCodec.Decode(RegionFile.Open(Path.Combine(outDir, "region", "r.0.0.mca"))
            .Chunks.First());
        Assert.Equal("beta", chunk.Get("Status")!.StringValue); // forced through
    }

    [Fact]
    public void DryRun_WritesNothing()
    {
        (string a, string b) = BuildAB();
        WorldPatch patch = PatchExtractor.Extract(a, b, Diff);

        string outDir = Path.Combine(TestAnvil.TempDir("dOut"), "world"); // does not exist yet
        ApplyReport report = PatchApplier.Apply(patch, a, outDir, new ApplySettings(DryRun: true));

        Assert.True(report.Applied > 0);
        Assert.False(Directory.Exists(outDir));
    }
}
