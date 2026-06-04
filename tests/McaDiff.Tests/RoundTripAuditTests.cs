using fNbt;
using McaDiff.Anvil;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Regression tests for the issue #4 round-trip/integrity audit.</summary>
public class RoundTripAuditTests
{
    // ---- BLOCKER-1: PruneStray must never wipe the repo ----

    [Fact]
    public void Materialize_RefusesToPruneTheRepoDirectory()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("blk"));
        string c = CommitWorld(repo, World(1), "c0");
        Manifest m = repo.ReadManifest(repo.ReadCommit(c).Tree);

        // Worktree mis-set to the repo dir itself: a pruning checkout would delete objects/refs/HEAD.
        Assert.Throws<InvalidOperationException>(() => Checkout.Materialize(repo, m, repo.Dir, prune: true));
        Assert.True(File.Exists(Path.Combine(repo.Dir, "HEAD")));        // repo intact
        Assert.True(Directory.Exists(Path.Combine(repo.Dir, "objects")));
        Assert.True(repo.Objects.Exists(c));
    }

    // ---- HIGH-1: an orphan .pack (crash mid-write) must not break reads ----

    [Fact]
    public void OpenAll_SkipsPackWithoutIndex()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("orph"));
        string c = CommitWorld(repo, World(1), "c0");
        Gc.Repack(repo); // one real pack + idx; head now lives in the pack

        string packDir = Path.Combine(repo.Objects.ObjectsDir, "pack");
        File.WriteAllBytes(Path.Combine(packDir, "pack-" + new string('a', 40) + ".pack"), [1, 2, 3]); // orphan, no .idx
        repo.Objects.ReloadPacks();

        Assert.Equal("c0", repo.ReadCommit(c).Message); // still readable (orphan skipped, no FileNotFound)
        Assert.True(Fsck.Check(repo).Ok);
    }

    // ---- HIGH-2: gc is idempotent ----

    [Fact]
    public void Gc_Repack_IsIdempotent()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("idem"));
        CommitWorld(repo, World(1), "c0");
        CommitWorld(repo, World(2), "c1");

        Gc.RepackResult r1 = Gc.Repack(repo);
        Gc.RepackResult r2 = Gc.Repack(repo);

        Assert.Equal(r1.PackId, r2.PackId);     // same set → same pack id
        Assert.Equal(0, r2.Packed);             // nothing repacked the second time
        Assert.Equal(0, r2.Pruned);
        Assert.Equal(0, r2.BytesFreed);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(repo.Objects.ObjectsDir, "pack"), "*.pack"));
        Assert.True(Fsck.Check(repo).Ok);
    }

    // ---- MED-1: malformed delta fails catchably ----

    [Fact]
    public void Delta_Apply_RejectsOutOfRangeCopy()
    {
        byte[] baseBuf = new byte[10];
        // header: baseLen=10, targetLen=100; then a copy of size 255 from offset 0 (> baseBuf) — out of range.
        byte[] delta = [0x0A, 0x64, 0x80 | 0x10, 0xFF];
        Assert.Throws<InvalidDataException>(() => Delta.Apply(baseBuf, delta));
    }

    // ---- MED-2: prune uses the SNAPSHOT's .mcaignore, not the worktree's current one ----

    [Fact]
    public void Checkout_Prune_UsesSnapshotIgnoreRules()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("ign"));
        string world = TestAnvil.TempDir("ignw");
        repo.Worktree = world;
        Directory.CreateDirectory(Path.Combine(world, "data"));
        File.WriteAllText(Path.Combine(world, "data", "keep.txt"), "x");
        File.WriteAllText(Path.Combine(world, ".mcaignore"), "*.log\n");
        string c = CommitWorld(repo, world, "c0");

        // Worktree drifts: remove its .mcaignore, drop an untracked .log file.
        File.Delete(Path.Combine(world, ".mcaignore"));
        File.WriteAllText(Path.Combine(world, "foo.log"), "junk");

        Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(c).Tree), world, prune: true);
        Assert.True(File.Exists(Path.Combine(world, "foo.log"))); // snapshot's .mcaignore (re-written) protects it
        Assert.True(File.Exists(Path.Combine(world, ".mcaignore")));
    }

    // ---- L-1: .mcaignore globs containing '/' match the path ----

    [Fact]
    public void IgnoreRules_GlobWithSlash_MatchesRelativePath()
    {
        string dir = TestAnvil.TempDir("glob");
        File.WriteAllText(Path.Combine(dir, ".mcaignore"), "data/*.dat\nregion/*.mca\n");
        IgnoreRules ig = IgnoreRules.Load(dir);

        Assert.True(ig.IsIgnored("data/foo.dat"));
        Assert.True(ig.IsIgnored("region/r.0.0.mca"));
        Assert.False(ig.IsIgnored("other/foo.dat")); // anchored to data/
        Assert.False(ig.IsIgnored("data/foo.txt"));
    }

    // ---- helpers ----

    private static string World(int a)
    {
        string dir = TestAnvil.TempDir("w");
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a))));
        return dir;
    }

    private static string CommitWorld(Repository repo, string world, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, world);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
