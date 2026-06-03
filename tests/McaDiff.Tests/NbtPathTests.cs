using fNbt;
using McaDiff.Nbt;
using Xunit;

namespace McaDiff.Tests;

public class NbtPathTests
{
    private static NbtCompound Sample() => TestAnvil.Root(
        new NbtCompound("Data") { new NbtLong("Time", 5), new NbtList("Pos", new[] { new NbtDouble(1), new NbtDouble(2), new NbtDouble(3) }) },
        new NbtList("block_entities", new[]
        {
            TestAnvil.BlockEntity("minecraft:chest", 5, 63, 8),
            TestAnvil.BlockEntity("minecraft:torch", 1, 70, 1),
        }),
        new NbtList("Items", new[]
        {
            new NbtCompound { new NbtByte("Slot", 0), new NbtString("id", "minecraft:stone") },
        }));

    [Fact]
    public void Get_ByKey_Index_AndIdentity()
    {
        var root = Sample();
        Assert.Equal(5, NbtPath.Get(root, "Data.Time")!.LongValue);
        Assert.Equal(2d, NbtPath.Get(root, "Data.Pos[1]")!.DoubleValue);
        Assert.Equal("minecraft:chest", NbtPath.Get(root, "block_entities[@5,63,8].id")!.StringValue);
        Assert.Equal("minecraft:stone", NbtPath.Get(root, "Items[slot:0].id")!.StringValue);
        Assert.Null(NbtPath.Get(root, "Data.Missing"));
    }

    [Fact]
    public void Set_ReplacesCompoundKey()
    {
        var root = Sample();
        Assert.True(NbtPath.Set(root, "Data.Time", new NbtLong("Time", 99)));
        Assert.Equal(99, NbtPath.Get(root, "Data.Time")!.LongValue);
    }

    [Fact]
    public void Set_Null_RemovesNode()
    {
        var root = Sample();
        Assert.True(NbtPath.Set(root, "block_entities[@5,63,8]", null));
        Assert.Null(NbtPath.Get(root, "block_entities[@5,63,8]"));
        Assert.NotNull(NbtPath.Get(root, "block_entities[@1,70,1]")); // sibling untouched
    }

    [Fact]
    public void Set_AppendsIdentityElementWhenAbsent()
    {
        var root = Sample();
        var sign = TestAnvil.BlockEntity("minecraft:sign", 2, 64, 2);
        Assert.True(NbtPath.Set(root, "block_entities[@2,64,2]", sign));
        Assert.Equal("minecraft:sign", NbtPath.Get(root, "block_entities[@2,64,2].id")!.StringValue);
    }

    [Fact]
    public void TerminalName_DistinguishesKeyFromElement()
    {
        Assert.Equal("Time", NbtPath.TerminalName("Data.Time"));
        Assert.Null(NbtPath.TerminalName("block_entities[@5,63,8]"));
        Assert.Null(NbtPath.TerminalName(""));
    }

    [Fact]
    public void Set_ReturnsFalse_WhenParentMissing()
    {
        var root = Sample();
        Assert.False(NbtPath.Set(root, "Nope.deeper.key", new NbtInt("key", 1)));
    }
}
