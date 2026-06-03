using fNbt;
using McaDiff.Anvil;
using McaDiff.Nbt;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Tier 3 git-likeness: recursive merge base + the conflict stop/continue/abort workflow.</summary>
public class GitLikeTier3Tests
{
    // ---- merge base ----

    [Fact]
    public void FindAll_LinearHistory_HasOneBase()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("mb1"));
        string c0 = Commit(repo, 1, []);
        string c1 = Commit(repo, 2, [c0]);
        string c2 = Commit(repo, 3, [c1]);
        Assert.Equal([c0], MergeBase.FindAll(repo, c0, c2));
        Assert.Equal([c1], MergeBase.FindAll(repo, c1, c2));
    }

    [Fact]
    public void FindAll_CrissCross_HasTwoBases()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("mb2"));
        string c0 = Commit(repo, 1, []);
        string cL = Commit(repo, 2, [c0]);
        string cR = Commit(repo, 3, [c0]);
        string cP = Commit(repo, 2, [cL, cR]); // merge keeping L, on the "main" line
        string cQ = Commit(repo, 3, [cR, cL]); // merge keeping R, on the "side" line

        var bases = MergeBase.FindAll(repo, cP, cQ);
        Assert.Equal(2, bases.Count);
        Assert.Contains(cL, bases);
        Assert.Contains(cR, bases);
    }

    [Fact]
    public void Merge_OverCrissCross_CompletesDeterministically()
    {
        string Run()
        {
            Repository repo = Repository.Init(TestAnvil.TempDir("mb3"));
            string c0 = Commit(repo, 1, []);
            string cL = Commit(repo, 2, [c0]);
            string cR = Commit(repo, 3, [c0]);
            string cP = Commit(repo, 2, [cL, cR]);
            string cQ = Commit(repo, 3, [cR, cL]);
            repo.WriteBranch("main", cP);
            repo.WriteBranch("side", cQ);
            repo.SetHeadToBranch("main");

            MergeResult r = Merger.Merge(repo, "side", preferTheirs: false, autoResolve: false, "test");
            Assert.False(r.Stopped);                                  // recursive base resolves it cleanly
            Assert.NotNull(r.CommitHash);
            return repo.ReadCommit(r.CommitHash!).Tree;               // resulting tree
        }
        Assert.Equal(Run(), Run()); // virtual base uses a fixed timestamp → fully deterministic
    }

    // ---- conflict workflow ----

    [Fact]
    public void Merge_Conflict_StopsWithoutCommitting_AndRecordsState()
    {
        var (repo, _, cMain, cSide) = SetUpConflict();
        MergeResult r = Merger.Merge(repo, "side", preferTheirs: false, autoResolve: false, "test");

        Assert.True(r.Stopped);
        Assert.Null(r.CommitHash);
        Assert.NotEmpty(r.Conflicts);
        Assert.True(repo.InMerge);
        Assert.Equal(cSide, repo.ReadMergeHead());
        Assert.Equal(cMain, repo.ReadOrigHead());
        Assert.Equal(cMain, repo.ReadBranch("main"));   // branch did NOT move
        Assert.NotEmpty(repo.ReadMergeConflicts());
    }

    [Fact]
    public void Merge_Abort_RestoresPreMergeState()
    {
        var (repo, world, cMain, _) = SetUpConflict();
        Merger.Merge(repo, "side", preferTheirs: false, autoResolve: false, "test");

        Merger.Abort(repo);
        Assert.False(repo.InMerge);
        Assert.Equal(cMain, repo.ReadBranch("main"));
        Assert.Equal(2, WorldChunkA(world));            // worktree restored to ours (a=2)
    }

    [Fact]
    public void Merge_Continue_CommitsResolvedWorktree()
    {
        var (repo, world, cMain, cSide) = SetUpConflict();
        Merger.Merge(repo, "side", preferTheirs: false, autoResolve: false, "test");

        // Resolve by hand: set a=5 in the worktree, then continue.
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(5, 10)));
        MergeResult r = Merger.Continue(repo, "test");

        Assert.False(repo.InMerge);
        Assert.NotNull(r.CommitHash);
        CommitObject mc = repo.ReadCommit(r.CommitHash!);
        Assert.Equal([cMain, cSide], mc.Parents);       // a real merge commit
        Assert.Equal(5, ChunkA(repo, r.CommitHash!));   // the resolution was captured
        Assert.Equal(r.CommitHash, repo.ReadBranch("main"));
    }

    [Fact]
    public void Merge_AutoResolve_StillCommitsInOneShot()
    {
        var (repo, _, cMain, cSide) = SetUpConflict();
        MergeResult r = Merger.Merge(repo, "side", preferTheirs: true, autoResolve: true, "test");

        Assert.False(repo.InMerge);
        Assert.True(r.HasConflicts);
        Assert.NotNull(r.CommitHash);
        Assert.Equal(3, ChunkA(repo, r.CommitHash!));   // kept theirs (a=3)
    }

    // ---- helpers ----

    /// <summary>main: c0→cMain(a=2); side: c0→cSide(a=3) — a single-node conflict on `a`,
    /// with a worktree bound so continue/abort can act.</summary>
    private static (Repository repo, string world, string cMain, string cSide) SetUpConflict()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cf"));
        string world = TestAnvil.TempDir("cfw");
        repo.Worktree = world;

        WriteA(world, 1);
        string c0 = CommitWorld(repo, world, "base");        // main = c0
        repo.WriteBranch("side", c0);

        WriteA(world, 2);
        string cMain = CommitWorld(repo, world, "main a=2");  // main advances

        repo.SetHeadToBranch("side");
        WriteA(world, 3);
        string cSide = CommitWorld(repo, world, "side a=3");  // side advances
        repo.SetHeadToBranch("main");

        return (repo, world, cMain, cSide);
    }

    private static NbtCompound AB(int a, int b) =>
        TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a), new NbtInt("b", b));

    private static void WriteA(string world, int a) =>
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(a, 10)));

    /// <summary>A commit whose only chunk has the given `a`, with explicit parents (graph building).</summary>
    private static string Commit(Repository repo, int a, string[] parents)
    {
        string world = TestAnvil.TempDir("gc");
        WriteA(world, a);
        string tree = repo.WriteManifest(Snapshotter.Snapshot(repo, world));
        return repo.WriteCommit(new CommitObject { Tree = tree, Parents = [.. parents], Message = $"a={a}", Author = "t", Time = "2020" });
    }

    private static string CommitWorld(Repository repo, string world, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, world);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }

    private static int ChunkA(Repository repo, string commit)
    {
        Manifest m = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        return NbtCanonical.Deserialize(repo.Objects.Read(m.Regions["region/r.0.0.mca"]["0,0"])).Get("a")!.IntValue;
    }

    private static int WorldChunkA(string world)
    {
        RegionFile rf = RegionFile.Open(Path.Combine(world, "region", "r.0.0.mca"));
        RawChunk rc = rf.Chunks.Single();
        return ChunkCodec.Decode(rc).Get("a")!.IntValue;
    }
}
