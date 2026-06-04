using fNbt;
using McaDiff.Anvil;
using McaDiff.Nbt;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Pre-parse NBT depth guard (#22): deeply-nested NBT must throw a catchable
/// InvalidDataException before fNbt's recursive parser overflows the native stack (an uncatchable
/// process kill). The guard runs on the raw bytes, so it covers the actual parse path.</summary>
public class NbtDepthGuardTests
{
    private static NbtCompound Nest(int depth)
    {
        var root = new NbtCompound("");
        NbtCompound cur = root;
        for (int i = 0; i < depth; i++)
        {
            var child = new NbtCompound("c");
            cur.Add(child);
            cur = child;
        }
        return root;
    }

    private static byte[] ToBytes(NbtCompound root) =>
        new NbtFile(root) { BigEndian = true }.SaveToBuffer(NbtCompression.None);

    [Fact]
    public void Check_DeepNesting_ThrowsCatchable()
    {
        byte[] deep = ToBytes(Nest(NbtDepthGuard.MaxDepth + 50));
        Assert.Throws<InvalidDataException>(() => NbtDepthGuard.Check(deep)); // not a StackOverflow
    }

    [Fact]
    public void Check_NormalNesting_Passes()
    {
        NbtDepthGuard.Check(ToBytes(Nest(20)));                 // realistic Minecraft depth
        NbtDepthGuard.Check(ToBytes(TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtString("Status", "full"))));
    }

    [Fact]
    public void ChunkCodec_DecodeDeeplyNestedChunk_ThrowsCatchable()
    {
        string dir = TestAnvil.TempDir("deep");
        string path = Path.Combine(dir, "region", "r.0.0.mca");
        TestAnvil.WriteRegion(path, (new ChunkPos(0, 0), Nest(NbtDepthGuard.MaxDepth + 50)));

        RawChunk rc = RegionFile.Open(path).Chunks.First();
        Assert.Throws<InvalidDataException>(() => ChunkCodec.Decode(rc)); // caught, process survives
    }
}
