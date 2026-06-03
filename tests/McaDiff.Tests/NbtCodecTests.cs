using System.Text.Json.Nodes;
using fNbt;
using McaDiff.Nbt;
using Xunit;

namespace McaDiff.Tests;

public class NbtCodecTests
{
    private static NbtTag RoundTrip(NbtTag tag)
    {
        JsonObject json = NbtJson.ToJson(tag);
        // also exercise text (de)serialization the patch file goes through
        JsonNode reparsed = JsonNode.Parse(json.ToJsonString())!;
        return NbtJson.FromJson(reparsed, tag.Name);
    }

    [Theory]
    [InlineData(NbtTagType.Byte)]
    [InlineData(NbtTagType.Short)]
    [InlineData(NbtTagType.Int)]
    [InlineData(NbtTagType.Float)]
    [InlineData(NbtTagType.Double)]
    [InlineData(NbtTagType.String)]
    public void Scalars_RoundTrip(NbtTagType type)
    {
        NbtTag tag = type switch
        {
            NbtTagType.Byte => new NbtByte("v", 200),       // > 127 to catch signedness
            NbtTagType.Short => new NbtShort("v", -1234),
            NbtTagType.Int => new NbtInt("v", -7),
            NbtTagType.Float => new NbtFloat("v", 0.085f),
            NbtTagType.Double => new NbtDouble("v", -30.899999976158142),
            _ => new NbtString("v", "minecraft:chest \"quoted\""),
        };
        Assert.True(NbtEquality.DeepEquals(tag, RoundTrip(tag)));
    }

    [Fact]
    public void Long_BeyondDoublePrecision_RoundTripsExactly()
    {
        var tag = new NbtLong("Time", 9_007_199_254_740_993L); // 2^53 + 1
        var back = (NbtLong)RoundTrip(tag);
        Assert.Equal(9_007_199_254_740_993L, back.Value);
    }

    [Fact]
    public void LongArray_RoundTripsExactly()
    {
        var tag = new NbtLongArray("packed", new[] { 1L, -1L, long.MaxValue, long.MinValue, 9_007_199_254_740_993L });
        Assert.True(NbtEquality.DeepEquals(tag, RoundTrip(tag)));
    }

    [Fact]
    public void NestedCompoundAndList_RoundTrip()
    {
        var tag = TestAnvil.Root(
            new NbtInt("DataVersion", 3953),
            new NbtList("attributes", new[]
            {
                new NbtCompound { new NbtString("id", "minecraft:generic.movement_speed"), new NbtDouble("base", 0.1) },
            }),
            new NbtCompound("Brain") { new NbtCompound("memories") },
            new NbtIntArray("UUID", new[] { 1, 2, 3, 4 }),
            new NbtByteArray("blob", new byte[] { 0, 255, 1, 254 }));
        Assert.True(NbtEquality.DeepEquals(tag, RoundTrip(tag)));
    }

    [Fact]
    public void EmptyList_RoundTrips()
    {
        var tag = TestAnvil.Root(new NbtList("empties", NbtTagType.Compound));
        Assert.True(NbtEquality.DeepEquals(tag, RoundTrip(tag)));
    }

    [Fact]
    public void DeepEquals_DetectsDifferences()
    {
        var a = TestAnvil.Root(new NbtInt("x", 1));
        var b = TestAnvil.Root(new NbtInt("x", 2));
        Assert.False(NbtEquality.DeepEquals(a, b));
        Assert.True(NbtEquality.DeepEquals(a, TestAnvil.Root(new NbtInt("x", 1))));
    }
}
