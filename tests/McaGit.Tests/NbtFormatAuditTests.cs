using fNbt;
using McaGit.Anvil;
using McaGit.Diff;
using McaGit.Model;
using McaGit.Nbt;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>Regression tests for the issue #6 Anvil/NBT format-fidelity audit.</summary>
public class NbtFormatAuditTests
{
    // ---- BLOCKER-1: LZ4 chunk codec ----

    [Fact]
    public void ChunkCodec_Lz4_RoundTrips()
    {
        var root = TestAnvil.Root(new NbtInt("DataVersion", 4790), new NbtString("Status", "minecraft:full"),
            new NbtLongArray("Heightmap", [1, 2, 3, 4, 5]), new NbtFloat("nan", float.NaN));
        byte[] payload = ChunkCodec.Encode(root, ChunkCompression.Lz4);
        var rc = new RawChunk(new ChunkPos(0, 0), ChunkCompression.Lz4, payload, external: false, timestamp: 0);
        Assert.True(NbtEquality.DeepEquals(root, ChunkCodec.Decode(rc)));
    }

    [Fact]
    public void Snapshot_Lz4Region_StoresPerChunk_NotBlob()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("lz4"));
        string world = TestAnvil.TempDir("lz4w");
        WriteLz4Region(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(1)), (new ChunkPos(1, 0), AB(2)));

        Manifest m = Snapshotter.Snapshot(repo, world);
        Assert.True(m.Regions.ContainsKey("region/r.0.0.mca")); // decoded per-chunk (not the whole-region blob fallback)
        Assert.False(m.Blobs.ContainsKey("region/r.0.0.mca"));
        Assert.Equal(2, m.Regions["region/r.0.0.mca"].Count);
    }

    // ---- HIGH-3: POI records matched by pos ----

    [Fact]
    public void NbtIdentity_PoiPos_ProducesPositionKey()
    {
        var rec = new NbtCompound
        {
            new NbtString("type", "minecraft:home"),
            new NbtIntArray("pos", [5, 63, 8]),
            new NbtInt("free_tickets", 1),
        };
        Assert.Equal("@5,63,8", NbtIdentity.KeyOf(rec));
    }

    // ---- HIGH-1: custom dimensions enumerated by the diff path ----

    [Fact]
    public void WorldSource_EnumeratesCustomDimensions()
    {
        string world = TestAnvil.TempDir("dim");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(1)));
        TestAnvil.WriteRegion(Path.Combine(world, "dimensions", "twilightforest", "twilight_forest", "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), AB(2)));

        var units = WorldSource.Enumerate(world, new DiffRunOptions());
        Assert.Contains("region/r.0.0.mca", units.Keys);
        Assert.Contains("dimensions/twilightforest/twilight_forest/region/r.0.0.mca", units.Keys);
    }

    // ---- MED-1: region-coord parsing ----

    [Fact]
    public void ParseRegionCoords_ParsesStandard_ThrowsOnMalformed()
    {
        Assert.Equal((1, -2), RegionFile.ParseRegionCoords("/x/region/r.1.-2.mca"));
        Assert.Throws<FormatException>(() => RegionFile.ParseRegionCoords("/x/region/notaregion.mca"));
    }

    // ---- helpers ----

    private static NbtCompound AB(int a) => TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a));

    private static void WriteLz4Region(string path, params (ChunkPos Pos, NbtCompound Root)[] chunks)
    {
        var raw = chunks
            .Select(c => new RawChunk(c.Pos, ChunkCompression.Lz4,
                ChunkCodec.Encode(c.Root, ChunkCompression.Lz4), external: false, timestamp: 100))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RegionWriter.Write(path, raw);
    }
}
