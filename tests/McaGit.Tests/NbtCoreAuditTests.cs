using System.Text.Json.Nodes;
using fNbt;
using McaGit.Diff;
using McaGit.Nbt;
using McaGit.Patch;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>Regression tests for the issue #5 NBT/Diff/Patch + Merger audit.</summary>
public class NbtCoreAuditTests
{
    // ---- B1: float/double special values round-trip through the patch encoding ----

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(1.5f)]
    [InlineData(0f)]
    [InlineData(float.MaxValue)]
    public void NbtJson_Float_RoundTripsSpecialValues(float v)
    {
        string json = NbtJson.ToJson(new NbtFloat("f", v)).ToJsonString(); // old code threw here on NaN/Inf
        var back = (NbtFloat)NbtJson.FromJson(JsonNode.Parse(json)!, "f");
        Assert.True(back.FloatValue.Equals(v));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(3.141592653589793)]
    public void NbtJson_Double_RoundTripsSpecialValues(double v)
    {
        string json = NbtJson.ToJson(new NbtDouble("d", v)).ToJsonString();
        var back = (NbtDouble)NbtJson.FromJson(JsonNode.Parse(json)!, "d");
        Assert.True(back.DoubleValue.Equals(v));
    }

    // ---- M1 / D7 / M2: loud failures instead of silent corruption ----

    [Fact]
    public void NbtJson_ByteOutOfRange_Throws()
        => Assert.Throws<FormatException>(() => NbtJson.FromJson(JsonNode.Parse("{\"byte\":300}")!));

    [Fact]
    public void NbtJson_UnknownListType_Throws()
        => Assert.Throws<NotSupportedException>(() =>
            NbtJson.FromJson(JsonNode.Parse("{\"list\":{\"type\":\"Banana\",\"items\":[]}}")!));

    [Fact]
    public void NbtJson_EmptyList_IsAllowed()
        => Assert.Equal(NbtTagType.List, NbtJson.FromJson(JsonNode.Parse("{\"list\":{\"type\":\"Unknown\",\"items\":[]}}")!).TagType);

    [Theory]
    [InlineData("list[]")]
    [InlineData("a.list[]")]
    public void NbtPath_EmptyBracket_Throws(string path)
        => Assert.Throws<FormatException>(() => NbtPath.Get(TestAnvil.Root(new NbtList("list")), path));

    // ---- M3: byte display is signed, like every Minecraft tool ----

    [Fact]
    public void ValueRepr_Byte_IsSigned()
    {
        Assert.Equal("-56b", ValueRepr.Scalar(new NbtByte("x", 0xC8))); // 200 unsigned → -56 signed
        Assert.Equal("127b", ValueRepr.Scalar(new NbtByte("x", 127)));
    }

    // ---- N3: a type change stops recursion (one event, no descent) ----

    [Fact]
    public void Comparer_TypeChange_StopsRecursion()
    {
        var a = TestAnvil.Root(new NbtCompound("x") { new NbtInt("inner", 1) });
        var b = TestAnvil.Root(new NbtInt("x", 5));
        var sink = new RecordingSink();
        NbtComparer.Walk(a, b, sink);
        Assert.Single(sink.Events);
        Assert.Equal(("TypeChanged", "x"), sink.Events[0]);
    }

    // ---- N1: the two sinks cover the same change set (the core invariant's backstop) ----

    [Fact]
    public void SinkParity_DisplayAndPatchCoverSamePaths()
    {
        var a = TestAnvil.Root(new NbtInt("mod", 1), new NbtInt("rem", 9), new NbtInt("typ", 3),
            new NbtIntArray("arr", [1, 2, 3]), new NbtInt("same", 0));
        var b = TestAnvil.Root(new NbtInt("mod", 2), new NbtCompound("add") { new NbtInt("x", 1), new NbtInt("y", 2) },
            new NbtString("typ", "hi"), new NbtIntArray("arr", [1, 9, 3]), new NbtInt("same", 0));

        List<NbtChange> display = NbtComparer.Compare(a, b, NbtDiffOptions.Default);
        var patch = new PatchOpSink();
        NbtComparer.Walk(a, b, patch);

        Assert.NotEmpty(display);
        Assert.NotEmpty(patch.Ops);
        static bool Under(string d, string op) =>
            d == op || d.StartsWith(op + ".", StringComparison.Ordinal) || d.StartsWith(op + "[", StringComparison.Ordinal);

        // Every patch op covers at least one display row, and every display row sits under a patch op.
        Assert.All(patch.Ops, op => Assert.Contains(display, c => Under(c.Path, op.Path)));
        Assert.All(display, c => Assert.Contains(patch.Ops, op => Under(c.Path, op.Path)));
    }

    // ---- ADD-1: a merge op that can't apply is a conflict, not a silent drop ----

    [Fact]
    public void Merge_ParentTypeChanged_ReportsConflict_NotSilentDrop()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("mc"));
        // base: { X: {} } ; ours: X→int 5 ; theirs: X.Y=1 added
        Manifest b = ChunkManifest(repo, TestAnvil.Root(new NbtCompound("X")));
        Manifest o = ChunkManifest(repo, TestAnvil.Root(new NbtInt("X", 5)));
        Manifest t = ChunkManifest(repo, TestAnvil.Root(new NbtCompound("X") { new NbtInt("Y", 1) }));

        var conflicts = new List<MergeConflict>();
        Merger.MergeManifests(repo, b, o, t, preferTheirs: false, conflicts);
        Assert.Contains(conflicts, c => c.Reason.Contains("parent path missing")); // X became int → X.Y can't apply
    }

    // ---- helpers ----

    private static Manifest ChunkManifest(Repository repo, NbtCompound chunk)
    {
        string hash = repo.Objects.Write(NbtCanonical.Serialize(chunk));
        var m = new Manifest();
        m.Regions["region/r.0.0.mca"] = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["0,0"] = hash };
        return m;
    }

    private sealed class RecordingSink : IDiffSink
    {
        public List<(string Kind, string Path)> Events { get; } = [];
        public void Added(string p, NbtTag v) => Events.Add(("Added", p));
        public void Removed(string p, NbtTag v) => Events.Add(("Removed", p));
        public void Modified(string p, NbtTag a, NbtTag b) => Events.Add(("Modified", p));
        public void TypeChanged(string p, NbtTag a, NbtTag b) => Events.Add(("TypeChanged", p));
        public void ArrayChanged(string p, NbtTag a, NbtTag b) => Events.Add(("ArrayChanged", p));
    }
}
