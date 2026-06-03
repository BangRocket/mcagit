using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Nbt;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

public class RepoTests
{
    // ---- object store & canonical form ----

    [Fact]
    public void ObjectStore_Dedups_IdenticalContent()
    {
        var store = new ObjectStore(TestAnvil.TempDir("os"));
        byte[] data = [1, 2, 3, 4];
        string h1 = store.Write(data);
        string h2 = store.Write([1, 2, 3, 4]);
        Assert.Equal(h1, h2);
        Assert.Equal(1, store.Count());
        Assert.Equal(data, store.Read(h1));
    }

    [Fact]
    public void NbtCanonical_IsDeterministic_AndRoundTrips()
    {
        var root = TestAnvil.Root(new NbtInt("a", 1), new NbtString("b", "x"),
            new NbtList("l", new[] { new NbtInt(1), new NbtInt(2) }));
        byte[] s1 = NbtCanonical.Serialize(root);
        byte[] s2 = NbtCanonical.Serialize(root);
        Assert.Equal(s1, s2);
        Assert.True(NbtEquality.DeepEquals(root, NbtCanonical.Deserialize(s1)));
    }

    // ---- history round-trip ----

    [Fact]
    public void Commit_Then_Checkout_ReproducesWorld()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("repo"));
        string world = TestAnvil.TempDir("w");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), Chunk("full", 1)), (new ChunkPos(1, 0), Chunk("full", 2)));
        TestAnvil.WriteLoose(Path.Combine(world, "level.dat"), Level(1000));
        Directory.CreateDirectory(Path.Combine(world, "stats"));
        File.WriteAllText(Path.Combine(world, "stats", "x.json"), "{\"hello\":1}");

        string c = CommitWorld(repo, world, "init");
        string outDir = TestAnvil.TempDir("co");
        Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(c).Tree), outDir);

        Assert.False(McaDiff.Diff.WorldDiffer.Diff(outDir, world, new McaDiff.Diff.DiffRunOptions()).HasDifferences);
        Assert.Equal("{\"hello\":1}", File.ReadAllText(Path.Combine(outDir, "stats", "x.json"))); // blob preserved
    }

    // ---- merge ----

    [Fact]
    public void MergeBase_FindsCommonAncestor()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("mb"));
        string c0 = CommitWorld(repo, World("base"), "c0");
        string c1 = CommitWorld(repo, World("ours"), "c1");      // main: c0 <- c1
        repo.WriteBranch("side", c0);
        repo.SetHeadToBranch("side");
        string c2 = CommitWorld(repo, World("theirs"), "c2");    // side: c0 <- c2
        Assert.Equal(c0, MergeBase.Find(repo, c1, c2));
    }

    [Fact]
    public void Merge_CombinesNonOverlappingNodeChanges()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("m1"));
        CommitWorld(repo, ChunkWorld(a: 1, b: 10), "base");
        repo.WriteBranch("side", repo.HeadCommit()!);

        CommitWorld(repo, ChunkWorld(a: 2, b: 10), "ours change A");   // main
        repo.SetHeadToBranch("side");
        CommitWorld(repo, ChunkWorld(a: 1, b: 20), "their change B");  // side
        repo.SetHeadToBranch("main");

        MergeResult r = Merger.Merge(repo, "side", preferTheirs: false, "test");
        Assert.False(r.HasConflicts);
        NbtCompound merged = MergedChunk(repo, r.CommitHash!);
        Assert.Equal(2, merged.Get("a")!.IntValue);   // ours' change kept
        Assert.Equal(20, merged.Get("b")!.IntValue);  // theirs' change kept
        Assert.Equal(2, repo.ReadCommit(r.CommitHash!).Parents.Count); // merge commit
    }

    [Fact]
    public void Merge_SameNodeChangedBothWays_ConflictsKeepingOurs_ThenTheirs()
    {
        foreach (bool preferTheirs in new[] { false, true })
        {
            Repository repo = Repository.Init(TestAnvil.TempDir("m2"));
            CommitWorld(repo, ChunkWorld(a: 1, b: 10), "base");
            repo.WriteBranch("side", repo.HeadCommit()!);
            CommitWorld(repo, ChunkWorld(a: 2, b: 10), "ours");   // main: a -> 2
            repo.SetHeadToBranch("side");
            CommitWorld(repo, ChunkWorld(a: 3, b: 10), "theirs"); // side: a -> 3
            repo.SetHeadToBranch("main");

            MergeResult r = Merger.Merge(repo, "side", preferTheirs, "test");
            Assert.True(r.HasConflicts);
            Assert.Equal(preferTheirs ? 3 : 2, MergedChunk(repo, r.CommitHash!).Get("a")!.IntValue);
        }
    }

    [Fact]
    public void Merge_FastForward_WhenOursIsAncestor()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("ff"));
        string c0 = CommitWorld(repo, ChunkWorld(1, 10), "c0");
        repo.WriteBranch("side", c0);
        repo.SetHeadToBranch("side");
        string c1 = CommitWorld(repo, ChunkWorld(2, 10), "c1");  // side ahead
        repo.SetHeadToBranch("main");                            // main still at c0

        MergeResult r = Merger.Merge(repo, "side", false, "test");
        Assert.True(r.FastForward);
        Assert.Equal(c1, repo.ReadBranch("main"));
    }

    [Fact]
    public void RepoDiff_TwoCommits_ShowsChunkChange()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rd"));
        string cA = CommitWorld(repo, ChunkWorld(1, 10), "a");
        string cB = CommitWorld(repo, ChunkWorld(2, 10), "b");
        Manifest mA = repo.ReadManifest(repo.ReadCommit(cA).Tree);
        Manifest mB = repo.ReadManifest(repo.ReadCommit(cB).Tree);

        WorldDiff diff = RepoDiffer.Diff(
            "a", mA, new RepoDiffer.CommitSource(repo, mA),
            "b", mB, new RepoDiffer.CommitSource(repo, mB), new DiffRunOptions());

        ChunkDiff chunk = diff.Files.Single(f => f.RelativePath == "region/r.0.0.mca").Chunks.Single();
        var change = chunk.Changes.Single(c => c.Path == "a");
        Assert.Equal("1", change.OldValue);
        Assert.Equal("2", change.NewValue);
    }

    [Fact]
    public void RepoDiff_CommitVsWorkingTree()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rdw"));
        string world = TestAnvil.TempDir("wt");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(1, 10)));
        string c = CommitWorld(repo, world, "snap");

        // Edit the working world on disk.
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(2, 10)));

        Manifest head = repo.ReadManifest(repo.ReadCommit(c).Tree);
        Manifest work = Snapshotter.HashOnly(repo, world);
        WorldDiff diff = RepoDiffer.Diff(
            "HEAD", head, new RepoDiffer.CommitSource(repo, head),
            "working", work, new RepoDiffer.WorldContentSource(world), new DiffRunOptions());

        var change = diff.Files.Single().Chunks.Single().Changes.Single(c => c.Path == "a");
        Assert.Equal("2", change.NewValue);
    }

    [Fact]
    public void Checkout_RecreatesEmptyDirectories()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("ed"));
        string world = World("w");
        Directory.CreateDirectory(Path.Combine(world, "datapacks")); // empty dir, like a real save

        string c = CommitWorld(repo, world, "init");
        string outDir = TestAnvil.TempDir("edco");
        Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(c).Tree), outDir);

        Assert.True(Directory.Exists(Path.Combine(outDir, "datapacks")));
    }

    [Fact]
    public void DetachedHead_Commit_DoesNotMoveBranch()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("det"));
        string c0 = CommitWorld(repo, World("base"), "c0");   // main = c0
        repo.SetHeadDetached(c0);
        string c1 = CommitWorld(repo, World("other"), "c1");  // commit while detached

        Assert.Equal(c0, repo.ReadBranch("main"));            // main NOT clobbered
        Assert.Null(repo.CurrentBranch());                    // still detached
        Assert.Equal(c1, repo.HeadCommit());                  // HEAD advanced
        Assert.Equal(c0, repo.ReadCommit(c1).Parents[0]);     // parented on the detached commit
    }

    [Fact]
    public void Canonical_ReorderedCompoundKeys_HashIdentically()
    {
        var a = TestAnvil.Root(new NbtInt("x", 1), new NbtString("y", "hi"), new NbtLong("z", 5));
        var b = TestAnvil.Root(new NbtLong("z", 5), new NbtString("y", "hi"), new NbtInt("x", 1));
        Assert.Equal(NbtCanonical.Serialize(a), NbtCanonical.Serialize(b)); // dedup despite key order
    }

    [Fact]
    public void ReCommit_SameWorld_AddsNoObjects_AndMatchesHead()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cache"));
        string world = World("w");
        CommitWorld(repo, world, "first");
        int before = repo.Objects.Count();

        Manifest again = Snapshotter.Snapshot(repo, world); // re-snapshot uses the chunk cache
        Assert.Equal(before, repo.Objects.Count());          // dedup + cache: no new objects
        Assert.Equal(repo.ReadCommit(repo.HeadCommit()!).Tree, repo.WriteManifest(again));
    }

    // ---- helpers ----

    private static NbtCompound Chunk(string status, long heightmap0) => TestAnvil.Root(
        new NbtInt("DataVersion", 3953), new NbtString("Status", status),
        new NbtLongArray("Heightmap", new[] { heightmap0, 2, 3 }));

    private static NbtCompound Level(long time) =>
        TestAnvil.Root(new NbtCompound("Data") { new NbtLong("Time", time) });

    private static string World(string tag)
    {
        string dir = TestAnvil.TempDir("w-" + tag);
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), Chunk(tag, 1)));
        return dir;
    }

    private static NbtCompound AB(int a, int b) =>
        TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a), new NbtInt("b", b));

    private static string ChunkWorld(int a, int b)
    {
        string dir = TestAnvil.TempDir("cw");
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(a, b)));
        return dir;
    }

    private static string CommitWorld(Repository repo, string worldDir, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, worldDir);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }

    private static NbtCompound MergedChunk(Repository repo, string commit)
    {
        Manifest m = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        string hash = m.Regions["region/r.0.0.mca"]["0,0"];
        return NbtCanonical.Deserialize(repo.Objects.Read(hash));
    }
}
