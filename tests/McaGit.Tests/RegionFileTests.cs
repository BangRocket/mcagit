using fNbt;
using McaGit.Anvil;
using Xunit;

namespace McaGit.Tests;

public class RegionFileTests
{
    [Fact]
    public void WrittenRegion_RoundTripsThroughParseAndDecode()
    {
        var root = TestAnvil.Root(
            new NbtInt("DataVersion", 3953),
            new NbtString("Status", "minecraft:full"),
            new NbtLongArray("packed", new long[] { 1, 2, 3, 4 }));

        string path = Path.Combine(TestAnvil.TempDir("region-roundtrip"), "r.0.0.mca");
        TestAnvil.WriteSingleChunkRegion(path, new ChunkPos(0, 0), root);

        RegionFile region = RegionFile.Open(path);
        Assert.Equal(1, region.Count);
        Assert.True(region.TryGet(new ChunkPos(0, 0), out RawChunk chunk));
        Assert.Equal(ChunkCompression.ZLib, chunk.Compression);

        NbtCompound decoded = ChunkCodec.Decode(chunk);
        Assert.Equal(3953, decoded.Get("DataVersion")!.IntValue);
        Assert.Equal("minecraft:full", decoded.Get("Status")!.StringValue);
    }

    [Fact]
    public void ChunkPos_RegionIndexAndCoords_AreConsistent()
    {
        // index = (x & 31) + (z & 31) * 32, round-trips with FromRegionIndex.
        var pos = new ChunkPos(-1, -1);
        Assert.Equal(31 + 31 * 32, pos.RegionIndex);
        Assert.Equal((-1, -1), pos.Region);
        Assert.Equal(pos, ChunkPos.FromRegionIndex(-1, -1, pos.RegionIndex));
    }

    [Fact]
    public void RealRegionFile_ParsesAndDecodes_WhenAvailable()
    {
        // Optional: runs only when a real region file is present (this dev box).
        string path = Environment.GetEnvironmentVariable("MCAGIT_TEST_REGION")
            ?? "/Volumes/Storage/Code/minecraft/dobbscraftbackups/region/r.-1.-1.mca";
        if (!File.Exists(path))
            return; // soft skip — no fixture in this environment

        RegionFile region = RegionFile.Open(path);
        Assert.True(region.Count > 0, "expected at least one chunk in a real region file");

        RawChunk first = region.Chunks.First();
        NbtCompound root = ChunkCodec.Decode(first);
        Assert.True(root.Count > 0);
        Assert.NotNull(root.Get("DataVersion")); // present in 1.18+ region & entities chunks
    }
}
