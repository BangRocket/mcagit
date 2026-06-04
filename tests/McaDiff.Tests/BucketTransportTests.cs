using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Cloud bucket transport protocol, exercised against the in-memory bucket fake
/// (S3/Azure adapters are thin and validated separately against real accounts).</summary>
public class BucketTransportTests
{
    [Fact]
    public void Push_Then_Clone_ReproducesRepo()
    {
        Repository origin = Repository.Init(TestAnvil.TempDir("bo"));
        CommitWorld(origin, World(1), "c0");
        string tip = CommitWorld(origin, World(2), "c1");

        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");

        string cloneDir = TestAnvil.TempDir("bc");
        using (var t = new BucketTransport(bucket, "world")) RemoteOps.CloneFrom(t, cloneDir, "azure://acct/c/world");
        Repository clone = Repository.Open(cloneDir);

        Assert.Equal(tip, clone.ReadBranch("main"));
        Assert.True(clone.Objects.Exists(tip));
        Assert.True(clone.Objects.VerifyIntegrity(tip));   // reconstructs to the right hash from the pack
        Assert.Equal("c1", clone.ReadCommit(tip).Message);
        Assert.Equal(origin.ReadCommit(tip).Tree, clone.ReadCommit(tip).Tree);
    }

    [Fact]
    public void Clone_Then_Checkout_ReproducesWorld()
    {
        Repository origin = Repository.Init(TestAnvil.TempDir("bo2"));
        string world = TestAnvil.TempDir("bow");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(1)), (new ChunkPos(1, 0), AB(2)));
        TestAnvil.WriteLoose(Path.Combine(world, "level.dat"), TestAnvil.Root(new NbtCompound("Data") { new NbtLong("Time", 5) }));
        string c = CommitWorld(origin, world, "snap");

        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");

        string cloneDir = TestAnvil.TempDir("bc2");
        using (var t = new BucketTransport(bucket, "world")) RemoteOps.CloneFrom(t, cloneDir, "s3://b/world");
        Repository clone = Repository.Open(cloneDir);

        string outDir = TestAnvil.TempDir("bco");
        Checkout.Materialize(clone, clone.ReadManifest(clone.ReadCommit(c).Tree), outDir);
        Assert.False(WorldDiffer.Diff(outDir, world, new DiffRunOptions()).HasDifferences);
    }

    [Fact]
    public void Push_StoresPacks_NotPerObjectBlobs()
    {
        Repository origin = Repository.Init(TestAnvil.TempDir("bp"));
        CommitWorld(origin, World(1), "c0");
        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");

        Assert.NotEmpty(bucket.List("world/packs/"));                    // one pack + idx + manifest
        Assert.NotEmpty(bucket.List("world/packs/manifest"));
        Assert.Single(bucket.List("world/refs/heads/"));                 // refs/heads/main
        Assert.Empty(bucket.List("world/objects/"));                     // never per-object
    }

    [Fact]
    public void IncrementalPush_AccumulatesPacks_FetchGetsNew()
    {
        Repository origin = Repository.Init(TestAnvil.TempDir("bi"));
        CommitWorld(origin, World(1), "c0");
        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");
        int packsAfterFirst = bucket.List("world/packs/").Count(k => k.EndsWith(".idx"));

        string tip = CommitWorld(origin, World(2), "c1");
        Push(origin, bucket, "main");                                    // second push → second pack
        Assert.Equal(packsAfterFirst + 1, bucket.List("world/packs/").Count(k => k.EndsWith(".idx")));

        // A clone now sees both commits (objects spread across two packs).
        string cloneDir = TestAnvil.TempDir("bif");
        using (var t = new BucketTransport(bucket, "world")) RemoteOps.CloneFrom(t, cloneDir, "azure://a/c/world");
        Repository clone = Repository.Open(cloneDir);
        Assert.Equal(tip, clone.ReadBranch("main"));
        Assert.Equal("c0", clone.ReadCommit(clone.ReadCommit(tip).Parents.Single()).Message); // parent reachable too
    }

    [Fact]
    public void UpdateRef_StalePrecondition_Throws()
    {
        Repository origin = Repository.Init(TestAnvil.TempDir("br"));
        CommitWorld(origin, World(1), "c0");
        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");

        using var t = new BucketTransport(bucket, "world");
        Assert.Throws<InvalidOperationException>(() => t.UpdateRef("main", expectedOld: new string('e', 64), new string('f', 64), force: false));
    }

    [Fact]
    public void NonFastForwardPush_RejectedUnlessForced()
    {
        // origin: c0→c1 ; a diverged repo: c0→c2. Pushing c2 over c1 is non-FF.
        var bucket = new InMemoryBucket();
        Repository a = Repository.Init(TestAnvil.TempDir("ba"));
        string c0 = CommitWorld(a, World(1), "c0");
        CommitWorld(a, World(2), "c1");
        Push(a, bucket, "main");

        Repository b = Repository.Init(TestAnvil.TempDir("bb"));
        // b shares c0 then diverges to c2 (write c0's objects, then a different child)
        ReplayCommit(b, a, c0);
        b.WriteBranch("main", c0); b.SetHeadToBranch("main");
        string c2 = CommitWorld(b, World(9), "c2");

        using (var t = new BucketTransport(bucket, "world"))
            Assert.Throws<InvalidOperationException>(() => RemoteOps.PushTo(b, t, "main", force: false));
        using (var t = new BucketTransport(bucket, "world"))
            RemoteOps.PushTo(b, t, "main", force: true); // forced push succeeds
        Assert.Equal(c2, ReadRemoteBranch(bucket, "main"));
    }

    [Fact]
    public void VerifyRemote_CleanRepo_ReportsOk()
    {
        Repository origin = Repository.Init(TestAnvil.TempDir("bv"));
        CommitWorld(origin, World(1), "c0");
        CommitWorld(origin, World(2), "c1");
        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");

        using var t = new BucketTransport(bucket, "world");
        RemoteOps.VerifyResult shallow = RemoteOps.Verify(t, deep: false);
        Assert.True(shallow.Ok);
        Assert.Equal(1, shallow.Branches);
        Assert.Equal(2, shallow.Commits);
        Assert.Empty(shallow.Missing);
        Assert.Empty(shallow.Corrupt);

        using var t2 = new BucketTransport(bucket, "world");
        Assert.True(RemoteOps.Verify(t2, deep: true).Ok); // hashes every leaf too
    }

    [Fact]
    public void VerifyRemote_MissingPack_DetectedAcrossParentWalk()
    {
        // Two pushes → two packs (parent in pack #1, tip in pack #2). Delete the parent's pack;
        // the tip still verifies, but walking to its parent surfaces the loss.
        Repository origin = Repository.Init(TestAnvil.TempDir("bvm"));
        CommitWorld(origin, World(1), "c0");
        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");
        string parentPack = ManifestIds(bucket).Single();   // pack #1 holds c0
        CommitWorld(origin, World(2), "c1");
        Push(origin, bucket, "main");

        bucket.Delete($"world/packs/{parentPack}");
        bucket.Delete($"world/packs/{parentPack}.idx");

        using var t = new BucketTransport(bucket, "world");
        RemoteOps.VerifyResult r = RemoteOps.Verify(t, deep: false);
        Assert.False(r.Ok);
        Assert.Contains(r.Missing, m => m.Contains("(commit)")); // the parent commit is gone
    }

    [Fact]
    public void MaliciousManifest_PackIdTraversal_Rejected()
    {
        // A hostile bucket advertising a pack id like "../../evil" must not become a local file path.
        Repository origin = Repository.Init(TestAnvil.TempDir("bt23"));
        CommitWorld(origin, World(1), "c0");
        var bucket = new InMemoryBucket();
        Push(origin, bucket, "main");

        (byte[]? manifest, _) = bucket.Get("world/packs/manifest");
        bucket.Put("world/packs/manifest",
            manifest!.Concat(System.Text.Encoding.UTF8.GetBytes("\n../../../tmp/evil\n")).ToArray());

        using var t = new BucketTransport(bucket, "world");
        Assert.Throws<InvalidDataException>(() => RemoteOps.CloneFrom(t, TestAnvil.TempDir("bt23c"), "azure://a/c/world"));
        Assert.False(File.Exists("/tmp/evil.pack")); // nothing written outside the temp dir
    }

    // ---- helpers ----

    private static List<string> ManifestIds(IBucket bucket) =>
        System.Text.Encoding.UTF8.GetString(bucket.Get("world/packs/manifest").Data!)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void Push(Repository repo, IBucket bucket, string branch)
    {
        using var t = new BucketTransport(bucket, "world");
        RemoteOps.PushTo(repo, t, branch, force: false);
    }

    private static string ReadRemoteBranch(IBucket bucket, string branch)
        => System.Text.Encoding.UTF8.GetString(bucket.Get($"world/refs/heads/{branch}").Data!).Trim();

    private static void ReplayCommit(Repository dst, Repository src, string commit)
    {
        // copy the commit, its tree, and the tree's objects from src into dst
        dst.Objects.Import(src.Objects, commit);
        CommitObject c = src.ReadCommit(commit);
        dst.Objects.Import(src.Objects, c.Tree);
        Manifest m = src.ReadManifest(c.Tree);
        foreach (string h in m.Regions.Values.SelectMany(r => r.Values).Concat(m.Nbt.Values).Concat(m.Blobs.Values))
            dst.Objects.Import(src.Objects, h);
    }

    private static NbtCompound AB(int a) => TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a));

    private static string World(int a)
    {
        string dir = TestAnvil.TempDir("w");
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(a)));
        return dir;
    }

    private static string CommitWorld(Repository repo, string worldDir, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, worldDir);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
