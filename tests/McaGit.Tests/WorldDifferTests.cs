using fNbt;
using McaGit.Anvil;
using McaGit.Diff;
using Xunit;

namespace McaGit.Tests;

public class WorldDifferTests
{
    private static readonly DiffRunOptions Opts = new();

    private static NbtCompound SampleChunk() => TestAnvil.Root(
        new NbtInt("DataVersion", 3953),
        new NbtString("Status", "minecraft:full"),
        new NbtLongArray("Heightmap", new long[] { 10, 20, 30, 40 }),
        new NbtList("block_entities", new[]
        {
            TestAnvil.BlockEntity("minecraft:chest", 5, 63, 8),
        }));

    [Fact]
    public void TwoRegionFiles_ReportModifiedChunkWithNbtChanges()
    {
        NbtCompound a = SampleChunk();
        NbtCompound b = SampleChunk();
        ((NbtString)b.Get("Status")!).Value = "minecraft:empty";          // scalar change
        ((NbtLongArray)b.Get("Heightmap")!).Value[2] = 999;               // array change
        var be = (NbtCompound)((NbtList)b.Get("block_entities")!)[0];
        ((NbtString)be.Get("id")!).Value = "minecraft:barrel";

        string fileA = Path.Combine(TestAnvil.TempDir("rgA"), "r.0.0.mca");
        string fileB = Path.Combine(TestAnvil.TempDir("rgB"), "r.0.0.mca");
        TestAnvil.WriteSingleChunkRegion(fileA, new ChunkPos(0, 0), a);
        TestAnvil.WriteSingleChunkRegion(fileB, new ChunkPos(0, 0), b);

        WorldDiff diff = WorldDiffer.Diff(fileA, fileB, Opts);

        FileDiff file = Assert.Single(diff.Files);
        Assert.Equal(UnitKind.Region, file.Kind);
        ChunkDiff chunk = Assert.Single(file.Chunks);
        Assert.Equal(new ChunkPos(0, 0), chunk.Pos);
        Assert.Equal(DiffStatus.Modified, chunk.Status);

        var byPath = chunk.Changes.ToDictionary(c => c.Path);
        Assert.Equal("\"minecraft:empty\"", byPath["Status"].NewValue);
        Assert.Equal("1 of 4 entries differ", byPath["Heightmap"].Note);
        Assert.Equal("\"minecraft:barrel\"", byPath["block_entities[@5,63,8].id"].NewValue);
    }

    [Fact]
    public void IdenticalRegionFiles_ProduceNoDiff()
    {
        NbtCompound a = SampleChunk();
        string fileA = Path.Combine(TestAnvil.TempDir("eqA"), "r.0.0.mca");
        string fileB = Path.Combine(TestAnvil.TempDir("eqB"), "r.0.0.mca");
        TestAnvil.WriteSingleChunkRegion(fileA, new ChunkPos(0, 0), a);
        File.Copy(fileA, fileB);

        WorldDiff diff = WorldDiffer.Diff(fileA, fileB, Opts);
        Assert.False(diff.HasDifferences);
    }

    [Fact]
    public void WorldDirectories_MatchRegionAndLooseFiles()
    {
        string dirA = TestAnvil.TempDir("worldA");
        string dirB = TestAnvil.TempDir("worldB");

        // Region chunk that changes.
        NbtCompound rgA = SampleChunk();
        NbtCompound rgB = SampleChunk();
        ((NbtString)rgB.Get("Status")!).Value = "minecraft:empty";
        TestAnvil.WriteSingleChunkRegion(Path.Combine(dirA, "region", "r.0.0.mca"), new ChunkPos(0, 0), rgA);
        TestAnvil.WriteSingleChunkRegion(Path.Combine(dirB, "region", "r.0.0.mca"), new ChunkPos(0, 0), rgB);

        // Loose level.dat that changes.
        WriteLevelDat(Path.Combine(dirA, "level.dat"), time: 1000);
        WriteLevelDat(Path.Combine(dirB, "level.dat"), time: 2500);

        WorldDiff diff = WorldDiffer.Diff(dirA, dirB, Opts);

        Assert.Equal(2, diff.Files.Count);
        FileDiff region = diff.Files.Single(f => f.Kind == UnitKind.Region);
        FileDiff loose = diff.Files.Single(f => f.Kind == UnitKind.Loose);
        Assert.Equal("region/r.0.0.mca", region.RelativePath);
        Assert.Equal("level.dat", loose.RelativePath);
        Assert.Contains(loose.Changes, c => c.Path == "Data.Time" && c.NewValue == "2500L");
    }

    [Fact]
    public void AddedAndRemovedFiles_AreClassified()
    {
        string dirA = TestAnvil.TempDir("addremA");
        string dirB = TestAnvil.TempDir("addremB");

        // Shared (identical) file — should not appear.
        string sharedA = Path.Combine(dirA, "region", "r.0.0.mca");
        string sharedB = Path.Combine(dirB, "region", "r.0.0.mca");
        TestAnvil.WriteSingleChunkRegion(sharedA, new ChunkPos(0, 0), SampleChunk());
        Directory.CreateDirectory(Path.GetDirectoryName(sharedB)!);
        File.Copy(sharedA, sharedB);

        // Only in A (removed), only in B (added).
        TestAnvil.WriteSingleChunkRegion(Path.Combine(dirA, "region", "r.1.0.mca"), new ChunkPos(32, 0), SampleChunk());
        TestAnvil.WriteSingleChunkRegion(Path.Combine(dirB, "region", "r.2.0.mca"), new ChunkPos(64, 0), SampleChunk());

        WorldDiff diff = WorldDiffer.Diff(dirA, dirB, Opts);

        Assert.Equal(DiffStatus.Removed, diff.Files.Single(f => f.RelativePath == "region/r.1.0.mca").Status);
        Assert.Equal(DiffStatus.Added, diff.Files.Single(f => f.RelativePath == "region/r.2.0.mca").Status);
        Assert.DoesNotContain(diff.Files, f => f.RelativePath == "region/r.0.0.mca");
    }

    private static void WriteLevelDat(string path, long time)
    {
        var root = TestAnvil.Root(new NbtCompound("Data")
        {
            new NbtLong("Time", time),
            new NbtString("LevelName", "Test"),
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new NbtFile(root).SaveToFile(path, NbtCompression.GZip);
    }
}
