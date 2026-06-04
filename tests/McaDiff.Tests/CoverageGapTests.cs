using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Nbt;
using McaDiff.Patch;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Coverage gaps the #19 audit flagged: NbtJson round-trips across all tag types, ambiguous
/// prefix resolution, fetch-all, apply --only filtering, fast-forward / no-op rebase, and multi-region
/// diff parallelism.</summary>
public class CoverageGapTests
{
    public static IEnumerable<object[]> AllTagTypes() => new List<object[]>
    {
        new object[] { new NbtByte("b", 127) },
        new object[] { new NbtShort("s", -12345) },
        new object[] { new NbtInt("i", int.MinValue) },
        new object[] { new NbtLong("l", 9223372036854775807L) },        // beyond 2^53
        new object[] { new NbtFloat("f", float.NaN) },                   // NaN must survive
        new object[] { new NbtDouble("d", double.NegativeInfinity) },
        new object[] { new NbtString("str", "héllo \"world\"") },
        new object[] { new NbtByteArray("ba", new byte[] { 1, 2, 3 }) },
        new object[] { new NbtIntArray("ia", new[] { 256, -7, 0 }) },
        new object[] { new NbtLongArray("la", new[] { 9223372036854775807L, -1L }) },
        new object[] { new NbtList("li", NbtTagType.Int) { new NbtInt(1), new NbtInt(2) } },
        new object[] { new NbtCompound("c") { new NbtInt("x", 5), new NbtString("y", "z") } },
    };

    [Theory]
    [MemberData(nameof(AllTagTypes))]
    public void NbtJson_RoundTrips_EveryTagType(NbtTag tag)
    {
        // Through a JSON string, exactly as a .mcapatch is serialized + reloaded.
        var reparsed = System.Text.Json.Nodes.JsonNode.Parse(NbtJson.ToJson(tag).ToJsonString())!;
        NbtTag back = NbtJson.FromJson(reparsed, tag.Name);
        Assert.True(NbtEquality.DeepEquals(tag, back), $"{tag.TagType} did not round-trip");
    }

    [Fact]
    public void ResolvePrefix_Ambiguous_ReturnsNull()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rp"));
        string a = repo.Objects.WriteText("alpha"), b = repo.Objects.WriteText("bravo");

        // A full hash resolves to itself; a 1-char prefix is (almost surely) shared → ambiguous → null.
        Assert.Equal(a, repo.Objects.ResolvePrefix(a));
        string shared = a[..1];
        if (b.StartsWith(shared, StringComparison.Ordinal))
            Assert.Null(repo.Objects.ResolvePrefix(shared)); // two objects share it → ambiguous
        Assert.Null(repo.Objects.ResolvePrefix("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")); // no match
    }

    [Fact]
    public void Fetch_AllBranches_WhenBranchIsNull()
    {
        Repository origin = Repository.Init(TestAnvil.TempDir("fo"));
        string c = origin.CreateCommit(origin.WriteManifest(new Manifest()), [], "c0", "t");
        origin.WriteBranch("main", c);
        origin.WriteBranch("dev", c);
        origin.SetHeadToBranch("main");

        Repository clone = Repository.Init(TestAnvil.TempDir("fc"));
        using var t = new LocalTransport(origin.Dir);
        RemoteOps.FetchInto(clone, t, branch: null, "origin"); // null → all branches

        Assert.Equal(c, clone.ReadRemoteRef("origin/main"));
        Assert.Equal(c, clone.ReadRemoteRef("origin/dev"));
    }

    [Fact]
    public void Apply_OnlyCategory_FiltersOtherFiles()
    {
        string a = World(1, withNbt: true);
        string b = World(2, withNbt: true); // both the region chunk and level.dat differ
        WorldPatch patch = PatchExtractor.Extract(a, b, new DiffRunOptions());

        string outDir = TestAnvil.TempDir("ao");
        PatchApplier.Apply(patch, a, outDir, new ApplySettings(OnlyCategories: new HashSet<string> { "nbt" }));

        // Only the loose-NBT change applied: level.dat matches b, the region still matches a.
        Assert.False(WorldDiffer.Diff(Path.Combine(outDir, "level.dat"), Path.Combine(b, "level.dat"), new DiffRunOptions()).HasDifferences);
        Assert.True(WorldDiffer.Diff(Path.Combine(outDir, "region", "r.0.0.mca"), Path.Combine(b, "region", "r.0.0.mca"), new DiffRunOptions()).HasDifferences);
    }

    [Fact]
    public void Rebase_FastForward_And_NoOp()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rb"));
        string c0 = repo.CreateCommit(repo.WriteManifest(new Manifest()), [], "c0", "t");
        string c1 = repo.CreateCommit(repo.WriteManifest(M(new NbtInt("v", 1))), [c0], "c1", "t");
        repo.WriteBranch("main", c1);
        repo.WriteBranch("feature", c0); // feature is behind main
        repo.SetHeadToBranch("feature");

        Rebase.Result ff = Rebase.Start(repo, "main", null, "t"); // feature onto main → fast-forward
        Assert.True(ff.FastForward);
        Assert.Equal(c1, repo.ReadBranch("feature"));

        // no-op: main already contains everything in an ancestor branch.
        repo.WriteBranch("base", c0);
        repo.SetHeadToBranch("main");
        Rebase.Result noop = Rebase.Start(repo, "base", null, "t");
        Assert.True(noop.UpToDate);
    }

    [Fact]
    public void WorldDiffer_ParallelAcrossManyRegions()
    {
        string a = TestAnvil.TempDir("wpA"), b = TestAnvil.TempDir("wpB");
        for (int r = 0; r < 6; r++) // 6 region files → exercises the per-region parallelism
        {
            TestAnvil.WriteRegion(Path.Combine(a, "region", $"r.{r}.0.mca"), (new ChunkPos(r * 32, 0), AB(r)));
            TestAnvil.WriteRegion(Path.Combine(b, "region", $"r.{r}.0.mca"), (new ChunkPos(r * 32, 0), AB(r + 100)));
        }
        WorldDiff diff = WorldDiffer.Diff(a, b, new DiffRunOptions());
        Assert.Equal(6, diff.Files.Count); // every region's change detected, none dropped/duplicated
    }

    [Fact]
    public void Extract_RecordsDataVersions_ForCrossVersionWarning()
    {
        string a = WorldV(3953), b = WorldV(4189);
        WorldPatch p = PatchExtractor.Extract(a, b, new DiffRunOptions());
        Assert.Equal(3953, p.BaseDataVersion);   // from base level.dat → Data.DataVersion
        Assert.Equal(4189, p.TargetDataVersion);
    }

    private static string WorldV(int dv)
    {
        string dir = TestAnvil.TempDir("dvw");
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(dv)));
        TestAnvil.WriteLoose(Path.Combine(dir, "level.dat"), TestAnvil.Root(new NbtCompound("Data") { new NbtInt("DataVersion", dv) }));
        return dir;
    }

    // ---- helpers ----

    private static Manifest M(params NbtTag[] _) => new();

    private static NbtCompound AB(int v) => TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", v));

    private static string World(int v, bool withNbt)
    {
        string dir = TestAnvil.TempDir("cgw");
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(v)));
        if (withNbt)
            TestAnvil.WriteLoose(Path.Combine(dir, "level.dat"), TestAnvil.Root(new NbtCompound("Data") { new NbtLong("Time", v) }));
        return dir;
    }
}
