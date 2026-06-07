using fNbt;
using McaGit.Diff;
using Xunit;

namespace McaGit.Tests;

public class NbtComparerTests
{
    private static Dictionary<string, NbtChange> Diff(NbtCompound a, NbtCompound b, bool expand = false)
        => NbtComparer.Compare(a, b, new NbtDiffOptions(expand)).ToDictionary(c => c.Path);

    [Fact]
    public void IdenticalTrees_ProduceNoChanges()
    {
        var a = TestAnvil.Root(new NbtInt("x", 1), new NbtString("name", "steve"));
        var b = TestAnvil.Root(new NbtInt("x", 1), new NbtString("name", "steve"));
        Assert.Empty(NbtComparer.Compare(a, b, NbtDiffOptions.Default));
    }

    [Fact]
    public void ModifiedScalar_IsReported()
    {
        var a = TestAnvil.Root(new NbtInt("x", 1));
        var b = TestAnvil.Root(new NbtInt("x", 2));
        var ch = Diff(a, b)["x"];
        Assert.Equal(ChangeKind.Modified, ch.Kind);
        Assert.Equal("1", ch.OldValue);
        Assert.Equal("2", ch.NewValue);
    }

    [Fact]
    public void AddedAndRemovedKeys_AreReported()
    {
        var a = TestAnvil.Root(new NbtString("gone", "x"));
        var b = TestAnvil.Root(new NbtInt("added", 7));
        var d = Diff(a, b);
        Assert.Equal(ChangeKind.Removed, d["gone"].Kind);
        Assert.Equal(ChangeKind.Added, d["added"].Kind);
        Assert.Equal("7", d["added"].NewValue);
    }

    [Fact]
    public void TypeChange_IsReported()
    {
        var a = TestAnvil.Root(new NbtInt("t", 1));
        var b = TestAnvil.Root(new NbtString("t", "1"));
        var ch = Diff(a, b)["t"];
        Assert.Equal(ChangeKind.TypeChanged, ch.Kind);
        Assert.Equal(NbtTagType.Int, ch.OldType);
        Assert.Equal(NbtTagType.String, ch.NewType);
    }

    [Fact]
    public void NestedCompound_UsesDottedPath()
    {
        var a = TestAnvil.Root(new NbtCompound("sub") { new NbtInt("k", 1) });
        var b = TestAnvil.Root(new NbtCompound("sub") { new NbtInt("k", 2) });
        Assert.Equal(ChangeKind.Modified, Diff(a, b)["sub.k"].Kind);
    }

    [Fact]
    public void IndexAlignedList_ReportsChangedElementAndAppend()
    {
        var a = TestAnvil.Root(new NbtList("l", new[] { new NbtInt(1), new NbtInt(2), new NbtInt(3) }));
        var b = TestAnvil.Root(new NbtList("l", new[] { new NbtInt(1), new NbtInt(9), new NbtInt(3), new NbtInt(4) }));
        var d = Diff(a, b);
        Assert.Equal(ChangeKind.Modified, d["l[1]"].Kind);
        Assert.Equal(ChangeKind.Added, d["l[3]"].Kind);
        Assert.Equal("4", d["l[3]"].NewValue);
    }

    [Fact]
    public void IdentityList_MatchesByCoordinates_IgnoringOrder()
    {
        // Same two block entities, reordered, with one's id changed and a third added.
        var a = TestAnvil.Root(new NbtList("block_entities", new[]
        {
            TestAnvil.BlockEntity("minecraft:chest", 5, 63, 8),
            TestAnvil.BlockEntity("minecraft:torch", 1, 70, 1),
        }));
        var b = TestAnvil.Root(new NbtList("block_entities", new[]
        {
            TestAnvil.BlockEntity("minecraft:torch", 1, 70, 1),          // moved to front, unchanged
            TestAnvil.BlockEntity("minecraft:barrel", 5, 63, 8),         // id changed
            TestAnvil.BlockEntity("minecraft:sign", 2, 64, 2),           // new
        }));
        var d = Diff(a, b);

        // The reordered-but-unchanged torch must NOT appear.
        Assert.DoesNotContain(d.Keys, k => k.Contains("@1,70,1"));
        // The changed chest is matched by coords and only its id differs.
        var idChange = d["block_entities[@5,63,8].id"];
        Assert.Equal(ChangeKind.Modified, idChange.Kind);
        Assert.Equal("\"minecraft:chest\"", idChange.OldValue);
        Assert.Equal("\"minecraft:barrel\"", idChange.NewValue);
        // The new sign is added (its leaves flattened under the keyed path).
        Assert.Contains(d.Keys, k => k.StartsWith("block_entities[@2,64,2].") && d[k].Kind == ChangeKind.Added);
    }

    [Fact]
    public void AttributeList_ReorderedById_ProducesNoChanges()
    {
        // Minecraft reorders the player's attributes between saves; matching by
        // "id" must treat that as a no-op, not 4 spurious value swaps.
        NbtCompound Attr(string id, double @base) =>
            new() { new NbtString("id", id), new NbtDouble("base", @base) };

        var a = TestAnvil.Root(new NbtList("attributes", new[]
        {
            Attr("minecraft:generic.movement_speed", 0.1),
            Attr("minecraft:player.entity_interaction_range", 3),
        }));
        var b = TestAnvil.Root(new NbtList("attributes", new[]
        {
            Attr("minecraft:player.entity_interaction_range", 3), // reordered
            Attr("minecraft:generic.movement_speed", 0.1),
        }));

        Assert.Empty(NbtComparer.Compare(a, b, NbtDiffOptions.Default));
    }

    [Fact]
    public void AttributeList_RealChangeById_IsReportedAtStableKey()
    {
        NbtCompound Attr(string id, double @base) =>
            new() { new NbtString("id", id), new NbtDouble("base", @base) };

        var a = TestAnvil.Root(new NbtList("attributes", new[] { Attr("minecraft:generic.movement_speed", 0.1) }));
        var b = TestAnvil.Root(new NbtList("attributes", new[] { Attr("minecraft:generic.movement_speed", 0.25) }));

        var ch = Diff(a, b)["attributes[id:minecraft:generic.movement_speed].base"];
        Assert.Equal(ChangeKind.Modified, ch.Kind);
    }

    [Fact]
    public void Array_SummarizedByDefault()
    {
        var a = TestAnvil.Root(new NbtLongArray("arr", new long[] { 1, 2, 3 }));
        var b = TestAnvil.Root(new NbtLongArray("arr", new long[] { 1, 9, 3 }));
        var ch = Diff(a, b)["arr"];
        Assert.Equal(ChangeKind.Modified, ch.Kind);
        Assert.Equal("long[3]", ch.OldValue);
        Assert.Equal("1 of 3 entries differ", ch.Note);
    }

    [Fact]
    public void Array_ExpandedWhenRequested()
    {
        var a = TestAnvil.Root(new NbtLongArray("arr", new long[] { 1, 2, 3 }));
        var b = TestAnvil.Root(new NbtLongArray("arr", new long[] { 1, 9, 3 }));
        var d = Diff(a, b, expand: true);
        Assert.False(d.ContainsKey("arr"));
        Assert.Equal("2", d["arr[1]"].OldValue);
        Assert.Equal("9", d["arr[1]"].NewValue);
    }

    [Fact]
    public void Array_LengthChange_IsNotedAndCounted()
    {
        var a = TestAnvil.Root(new NbtIntArray("arr", new[] { 1, 2, 3 }));
        var b = TestAnvil.Root(new NbtIntArray("arr", new[] { 1, 2 }));
        var ch = Diff(a, b)["arr"];
        Assert.Equal("length 3 → 2", ch.Note);
    }
}
