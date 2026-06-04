using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Depth-limited (shallow) clones: history is pruned at a boundary, and every
/// reachability/history walk treats boundary commits as parentless roots (the graft) so
/// nothing faults on the intentionally-absent parents.</summary>
public class ShallowCloneTests
{
    [Fact]
    public void Depth1_KeepsTipOnly_AndGrafts()
    {
        Repository src = ThreeCommits(out string c1, out string c2, out string c3);
        Repository clone = Clone(src, depth: 1);

        Assert.Equal(c3, clone.ReadBranch("main"));
        Assert.True(clone.Objects.Exists(c3));
        Assert.False(clone.Objects.Exists(c2));      // parent history pruned
        Assert.False(clone.Objects.Exists(c1));
        Assert.True(clone.IsShallow);
        Assert.Contains(c3, clone.ShallowBoundary);
        Assert.Empty(clone.ParentsOf(c3));           // grafted: tip looks like a root
    }

    [Fact]
    public void Depth2_KeepsTwoCommits_BoundaryAtSecond()
    {
        Repository src = ThreeCommits(out string c1, out string c2, out string c3);
        Repository clone = Clone(src, depth: 2);

        Assert.True(clone.Objects.Exists(c3));
        Assert.True(clone.Objects.Exists(c2));
        Assert.False(clone.Objects.Exists(c1));      // pruned one deeper
        Assert.Equal([c2], clone.ParentsOf(c3));     // real parent kept
        Assert.Empty(clone.ParentsOf(c2));           // boundary → grafted
        Assert.Contains(c2, clone.ShallowBoundary);
        Assert.DoesNotContain(c3, clone.ShallowBoundary);
    }

    [Fact]
    public void Fsck_OnShallowClone_ReportsNoMissing()
    {
        Repository src = ThreeCommits(out _, out _, out _);
        Repository clone = Clone(src, depth: 1);

        Fsck.Report r = Fsck.Check(clone);
        Assert.Empty(r.Missing);    // the pruned parent must NOT be flagged as missing
        Assert.Empty(r.Corrupt);
    }

    [Fact]
    public void Gc_OnShallowClone_KeepsTip_DoesNotChokeOnBoundary()
    {
        Repository src = ThreeCommits(out _, out _, out string c3);
        Repository clone = Clone(src, depth: 1);

        Gc.Result r = Gc.Prune(clone);   // walks reachability through the boundary graft
        Assert.Equal(0, r.Pruned);       // tip + tree + objects are all reachable
        Assert.True(clone.Objects.Exists(c3));
    }

    [Fact]
    public void ShallowTip_ChecksOutToTheRightWorld()
    {
        string w3 = World("c", 3);
        Repository src = Repository.Init(TestAnvil.TempDir("shS"));
        CommitWorld(src, World("a", 1), "c1");
        CommitWorld(src, World("b", 2), "c2");
        string c3 = CommitWorld(src, w3, "c3");

        Repository clone = Clone(src, depth: 1);
        string outDir = TestAnvil.TempDir("shO");
        Checkout.Materialize(clone, clone.ReadManifest(clone.ReadCommit(c3).Tree), outDir);
        Assert.False(WorldDiffer.Diff(outDir, w3, new DiffRunOptions()).HasDifferences);
    }

    [Fact]
    public void Log_OnShallowClone_StopsAtBoundary()
    {
        Repository src = ThreeCommits(out _, out _, out string c3);
        Repository clone = Clone(src, depth: 1);
        int rc = Cli.RepoCommands.Log(clone.Dir, ["--oneline"]);   // must not throw on the pruned parent
        Assert.Equal(0, rc);
        Assert.Single(LinearFirstParents(clone, c3));              // only the tip is walkable
    }

    // ---- helpers ----

    private static List<string> LinearFirstParents(Repository repo, string start)
    {
        var list = new List<string>();
        for (string? cur = start; cur is not null;)
        {
            list.Add(cur);
            List<string> parents = repo.ParentsOf(cur);
            cur = parents.Count > 0 ? parents[0] : null;
        }
        return list;
    }

    private static Repository ThreeCommits(out string c1, out string c2, out string c3)
    {
        Repository src = Repository.Init(TestAnvil.TempDir("shSrc"));
        c1 = CommitWorld(src, World("a", 1), "c1");
        c2 = CommitWorld(src, World("b", 2), "c2");
        c3 = CommitWorld(src, World("c", 3), "c3");
        return src;
    }

    private static Repository Clone(Repository src, int depth)
    {
        string dst = TestAnvil.TempDir("shClone");
        using var t = new LocalTransport(src.Dir);
        RemoteOps.CloneFrom(t, dst, src.Dir, depth);
        return Repository.Open(dst);
    }

    private static string World(string tag, int v)
    {
        string dir = TestAnvil.TempDir("shw-" + tag);
        var root = TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("v", v));
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), root));
        return dir;
    }

    private static string CommitWorld(Repository repo, string worldDir, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, worldDir);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
