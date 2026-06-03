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

        MergeResult r = Merger.Merge(repo, "side", preferTheirs: false, autoResolve: false, "test");
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

            MergeResult r = Merger.Merge(repo, "side", preferTheirs, autoResolve: true, "test");
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

        MergeResult r = Merger.Merge(repo, "side", false, autoResolve: false, "test");
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

    [Fact]
    public void ResolveRef_RevisionSyntaxAndShortHash()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rev"));
        string c0 = CommitWorld(repo, World("a"), "c0");
        string c1 = CommitWorld(repo, World("b"), "c1");
        string c2 = CommitWorld(repo, World("c"), "c2");

        Assert.Equal(c2, repo.ResolveRef("HEAD"));
        Assert.Equal(c2, repo.ResolveRef("main"));
        Assert.Equal(c1, repo.ResolveRef("HEAD~1"));
        Assert.Equal(c1, repo.ResolveRef("HEAD^"));
        Assert.Equal(c0, repo.ResolveRef("HEAD~2"));
        Assert.Equal(c0, repo.ResolveRef("main~2"));
        Assert.Equal(c1, repo.ResolveRef(c1[..8])); // abbreviated hash
    }

    [Fact]
    public void Tags_Resolve_List_Delete()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("tag"));
        string c = CommitWorld(repo, World("a"), "c0");
        repo.WriteTag("v1", c);

        Assert.Equal(c, repo.ResolveRef("v1")); // tag resolves as a revision base
        Assert.Contains("v1", repo.Tags());
        Assert.True(repo.DeleteTag("v1"));
        Assert.DoesNotContain("v1", repo.Tags());
    }

    [Fact]
    public void ObjectStore_ResolvePrefix()
    {
        var store = new ObjectStore(TestAnvil.TempDir("pfx"));
        string h = store.Write([1, 2, 3, 4, 5]);
        Assert.Equal(h, store.ResolvePrefix(h[..10]));
        Assert.Null(store.ResolvePrefix("ffffffff")); // no such object
    }

    [Fact]
    public void Revert_RestoresPreviousTree()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rvt"));
        string c0 = CommitWorld(repo, World("a"), "c0");
        string c1 = CommitWorld(repo, World("b"), "c1");

        // revert c1 = 3-way(base=c1, ours=c1, theirs=c1.parent) → c0's tree
        Manifest baseM = repo.ReadManifest(repo.ReadCommit(c1).Tree);
        Manifest theirsM = repo.ReadManifest(repo.ReadCommit(c0).Tree);
        Manifest merged = Merger.MergeManifests(repo, baseM, baseM, theirsM, false, []);
        Assert.Equal(repo.ReadCommit(c0).Tree, repo.WriteManifest(merged));
    }

    [Fact]
    public void IgnoreRules_MatchForms()
    {
        string dir = TestAnvil.TempDir("ign");
        File.WriteAllText(Path.Combine(dir, ".mcaignore"), "*.sqlite\nlogs/\ndata/foo.dat\n");
        IgnoreRules ig = IgnoreRules.Load(dir);

        Assert.True(ig.IsIgnored("data/DistantHorizons.sqlite")); // *.ext glob
        Assert.True(ig.IsIgnored("logs/latest.log"));             // dir/
        Assert.True(ig.IsIgnored("data/foo.dat"));                // anchored path
        Assert.False(ig.IsIgnored("level.dat"));
        Assert.False(ig.IsIgnored("data/keep.dat"));
    }

    [Fact]
    public void Commit_HonorsMcaIgnore()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("igc"));
        string world = TestAnvil.TempDir("igw");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(1, 1)));
        Directory.CreateDirectory(Path.Combine(world, "data"));
        File.WriteAllText(Path.Combine(world, "data", "skip.sqlite"), "x");
        File.WriteAllText(Path.Combine(world, ".mcaignore"), "*.sqlite\n");

        Manifest m = Snapshotter.Snapshot(repo, world);
        Assert.DoesNotContain(m.Blobs.Keys, k => k.Contains("skip.sqlite"));
    }

    [Fact]
    public void Clone_Then_Push_FastForward()
    {
        Repository a = Repository.Init(TestAnvil.TempDir("rA"));
        CommitWorld(a, World("a"), "c1");
        CommitWorld(a, World("b"), "c2");

        string bDir = TestAnvil.TempDir("rB");
        RemoteOps.Clone(a.Dir, bDir, null);
        Repository b = Repository.Open(bDir);
        Assert.Equal(a.ReadBranch("main"), b.ReadBranch("main")); // clone copied the tip
        Assert.True(b.Objects.Exists(b.ReadBranch("main")!));

        string c3 = CommitWorld(b, World("c"), "c3");            // advance B
        RemoteOps.Push(b, "origin", "main", force: false, null);

        Repository a2 = Repository.Open(a.Dir);
        Assert.Equal(c3, a2.ReadBranch("main"));                 // push fast-forwarded A
        Assert.True(a2.Objects.Exists(c3));
    }

    [Fact]
    public void Fetch_PopulatesRemoteTrackingRef()
    {
        Repository a = Repository.Init(TestAnvil.TempDir("fA"));
        CommitWorld(a, World("a"), "c1");
        string bDir = TestAnvil.TempDir("fB");
        RemoteOps.Clone(a.Dir, bDir, null);
        Repository b = Repository.Open(bDir);

        string c2 = CommitWorld(a, World("b"), "c2");            // A moves ahead
        RemoteOps.Fetch(b, "origin", "main", null);

        Assert.Equal(c2, b.ReadRemoteRef("origin/main"));
        Assert.True(b.Objects.Exists(c2));
        Assert.Equal(c2, b.ResolveRef("origin/main"));          // resolvable as a revision
    }

    [Fact]
    public void Gc_PrunesUnreachableObjects()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("gc"));
        CommitWorld(repo, World("a"), "c1");
        string orphan = repo.Objects.Write([7, 7, 7, 7]); // not referenced by any ref

        Gc.Result r = Gc.Prune(repo);
        Assert.True(r.Pruned >= 1);
        Assert.False(repo.Objects.Exists(orphan));
        Assert.True(repo.Objects.Exists(repo.HeadCommit()!)); // reachable kept
    }

    [Fact]
    public void Reflog_RecordsCommits()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rl"));
        string c1 = CommitWorld(repo, World("a"), "c1");
        List<string> log = repo.Reflog().ToList();
        Assert.NotEmpty(log);
        Assert.Contains(log, l => l.Contains(c1) && l.Contains("commit: c1"));
    }

    [Fact]
    public void Merge_AddsRegionPresentOnlyOnTheirs()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("mr"));
        // base/ours: only region/r.0.0; theirs adds region/r.1.0 (and an empty poi region).
        Manifest baseM = new();
        baseM.Regions["region/r.0.0.mca"] = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["0,0"] = "hashA" };
        Manifest oursM = baseM;
        Manifest theirsM = new();
        theirsM.Regions["region/r.0.0.mca"] = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["0,0"] = "hashA" };
        theirsM.Regions["region/r.1.0.mca"] = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["32,0"] = "hashB" };
        theirsM.Regions["poi/r.0.0.mca"] = new SortedDictionary<string, string>(StringComparer.Ordinal); // empty region

        Manifest merged = Merger.MergeManifests(repo, baseM, oursM, theirsM, false, []);
        Assert.True(merged.Regions.ContainsKey("region/r.1.0.mca")); // added region carried
        Assert.True(merged.Regions.ContainsKey("poi/r.0.0.mca"));    // empty added region carried
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
