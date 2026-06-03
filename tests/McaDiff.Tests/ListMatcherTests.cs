using fNbt;
using McaDiff.Diff;
using Xunit;

namespace McaDiff.Tests;

public class ListMatcherTests
{
    [Fact]
    public void BlockEntityList_KeyedByCoordinates()
    {
        var list = new NbtList("block_entities", new[]
        {
            TestAnvil.BlockEntity("minecraft:chest", 5, 63, 8),
            TestAnvil.BlockEntity("minecraft:torch", 1, 70, 1),
        });
        string[]? keys = ListMatcher.TryGetKeys(list);
        Assert.Equal(new[] { "@5,63,8", "@1,70,1" }, keys);
    }

    [Fact]
    public void EntityList_KeyedByUuidIntArray()
    {
        var e = new NbtCompound { new NbtIntArray("UUID", new[] { 1, 2, 3, 4 }) };
        var list = new NbtList("entities", new[] { e });
        string[]? keys = ListMatcher.TryGetKeys(list);
        Assert.NotNull(keys);
        Assert.StartsWith("uuid:", keys![0]);
    }

    [Fact]
    public void DuplicateKeys_FallBackToIndex()
    {
        var list = new NbtList("block_entities", new[]
        {
            TestAnvil.BlockEntity("a", 1, 1, 1),
            TestAnvil.BlockEntity("b", 1, 1, 1), // collision
        });
        Assert.Null(ListMatcher.TryGetKeys(list));
    }

    [Fact]
    public void NonCompoundList_HasNoIdentity()
    {
        var list = new NbtList("nums", new[] { new NbtInt(1), new NbtInt(2) });
        Assert.Null(ListMatcher.TryGetKeys(list));
    }

    [Fact]
    public void AttributeList_KeyedByStringId()
    {
        var list = new NbtList("attributes", new[]
        {
            new NbtCompound { new NbtString("id", "minecraft:generic.movement_speed"), new NbtDouble("base", 0.1) },
            new NbtCompound { new NbtString("id", "minecraft:player.entity_interaction_range"), new NbtDouble("base", 3) },
        });
        string[]? keys = ListMatcher.TryGetKeys(list);
        Assert.Equal(
            new[] { "id:minecraft:generic.movement_speed", "id:minecraft:player.entity_interaction_range" },
            keys);
    }

    [Fact]
    public void ItemList_KeyedBySlot()
    {
        var list = new NbtList("Items", new[]
        {
            new NbtCompound { new NbtByte("Slot", 0), new NbtString("id", "minecraft:stone") },
            new NbtCompound { new NbtByte("Slot", 3), new NbtString("id", "minecraft:stone") }, // same id, distinct slot
        });
        string[]? keys = ListMatcher.TryGetKeys(list);
        Assert.Equal(new[] { "slot:0", "slot:3" }, keys);
    }
}
