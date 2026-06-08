using fNbt;
using McaGit.Anvil;
using McaGit.Cli;
using McaGit.Nbt;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>Regression tests for the issue #7 git-fidelity audit: cherry-pick/revert/rebase
/// stop-on-conflict, and the smaller reset/branch/init/tag fixes.</summary>
public class GitFidelityTests
{
    // ---- B-1: cherry-pick stops on conflict, then continue / abort ----

    [Fact]
    public void CherryPick_Conflict_StopsWithoutCommitting()
    {
        var (repo, world, m1, f1) = ConflictSetup();
        int rc = RepoCommands.CherryPick(repo.Dir, [f1]);

        Assert.NotEqual(0, rc);
        Repository r = Repository.Open(repo.Dir);
        Assert.True(r.InCherryPick);
        Assert.Equal(m1, r.HeadCommit());            // NO commit was created
        Assert.Equal(m1, r.ReadBranch("main"));
    }

    [Fact]
    public void CherryPick_Continue_CommitsResolution()
    {
        var (repo, world, m1, f1) = ConflictSetup();
        RepoCommands.CherryPick(repo.Dir, [f1]);     // stop
        WriteA(world, 5);                            // resolve by hand
        Assert.Equal(0, RepoCommands.CherryPick(repo.Dir, ["--continue"]));

        Repository r = Repository.Open(repo.Dir);
        Assert.False(r.InCherryPick);
        string tip = r.HeadCommit()!;
        Assert.Equal(m1, r.ReadCommit(tip).Parents.Single());
        Assert.Equal(5, ChunkA(r, tip));
    }

    [Fact]
    public void CherryPick_Abort_RestoresWorktreeAndHead()
    {
        var (repo, world, m1, f1) = ConflictSetup();
        RepoCommands.CherryPick(repo.Dir, [f1]);     // stop (worktree now holds the partial result)
        Assert.Equal(0, RepoCommands.CherryPick(repo.Dir, ["--abort"]));

        Repository r = Repository.Open(repo.Dir);
        Assert.False(r.InCherryPick);
        Assert.Equal(m1, r.HeadCommit());
        Assert.Equal(3, WorldChunkA(world));         // restored to main (a=3)
    }

    // ---- B-3: rebase stops on first conflict, then continue / abort ----

    [Fact]
    public void Rebase_Conflict_StopsWithoutMovingBranch()
    {
        var (repo, world, c1, fA) = RebaseConflictSetup();
        int rc = RepoCommands.RebaseCmd(repo.Dir, ["main"]);

        Assert.NotEqual(0, rc);
        Repository r = Repository.Open(repo.Dir);
        Assert.True(r.InRebase);
        Assert.Equal(fA, r.ReadBranch("feature"));   // branch only moves on success
    }

    [Fact]
    public void Rebase_Continue_Completes()
    {
        var (repo, world, c1, fA) = RebaseConflictSetup();
        RepoCommands.RebaseCmd(repo.Dir, ["main"]);  // stop
        WriteA(world, 7);                            // resolve
        Assert.Equal(0, RepoCommands.RebaseCmd(repo.Dir, ["--continue"]));

        Repository r = Repository.Open(repo.Dir);
        Assert.False(r.InRebase);
        string tip = r.ReadBranch("feature")!;
        Assert.Equal(c1, r.ReadCommit(tip).Parents.Single()); // replayed onto main's tip
        Assert.Equal(7, ChunkA(r, tip));
    }

    [Fact]
    public void Rebase_Abort_RestoresBranch()
    {
        var (repo, world, c1, fA) = RebaseConflictSetup();
        RepoCommands.RebaseCmd(repo.Dir, ["main"]);
        Assert.Equal(0, RepoCommands.RebaseCmd(repo.Dir, ["--abort"]));

        Repository r = Repository.Open(repo.Dir);
        Assert.False(r.InRebase);
        Assert.Equal(fA, r.ReadBranch("feature"));
    }

    // ---- smaller fidelity fixes ----

    [Fact]
    public void Reset_OnDetachedHead_MovesHead()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rst"));
        string world = TestAnvil.TempDir("rstw"); repo.Worktree = world;
        string c0 = CommitA(repo, world, 1, "c0");
        string c1 = CommitA(repo, world, 2, "c1");
        repo.SetHeadDetached(c1);

        Assert.Equal(0, RepoCommands.Reset(repo.Dir, [c0])); // git moves HEAD; old code errored "requires a branch"
        Assert.Equal(c0, Repository.Open(repo.Dir).HeadCommit());
    }

    [Fact]
    public void Branch_StartPoint_NoClobber_Delete()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("br"));
        string world = TestAnvil.TempDir("brw"); repo.Worktree = world;
        string c0 = CommitA(repo, world, 1, "c0");
        CommitA(repo, world, 2, "c1");

        Assert.Equal(0, RepoCommands.Branch(repo.Dir, ["old", c0[..10]])); // create at a start-point
        Assert.Equal(c0, Repository.Open(repo.Dir).ReadBranch("old"));
        Assert.NotEqual(0, RepoCommands.Branch(repo.Dir, ["old"]));        // refuse to clobber
        Assert.Equal(0, RepoCommands.Branch(repo.Dir, ["-d", "old"]));     // delete
        Assert.Null(Repository.Open(repo.Dir).ReadBranch("old"));
    }

    [Fact]
    public void Init_IsIdempotent()
    {
        string dir = TestAnvil.TempDir("init");
        Assert.Equal(0, RepoCommands.Init(dir, []));
        Assert.Equal(0, RepoCommands.Init(dir, [])); // re-init exits 0, not an error
    }

    [Fact]
    public void Tag_RefusesOverwriteWithoutForce()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("tg"));
        string world = TestAnvil.TempDir("tgw"); repo.Worktree = world;
        CommitA(repo, world, 1, "c0");
        Assert.Equal(0, RepoCommands.Tag(repo.Dir, ["v1"]));
        Assert.NotEqual(0, RepoCommands.Tag(repo.Dir, ["v1"]));       // exists → refuse
        Assert.Equal(0, RepoCommands.Tag(repo.Dir, ["v1", "-f"]));    // -f overwrites
    }

    // ---- helpers ----

    /// <summary>main: c0(a=1)→m1(a=3); plus an off-branch f1(a=2) so cherry-picking f1 conflicts.</summary>
    private static (Repository repo, string world, string m1, string f1) ConflictSetup()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cp"));
        string world = TestAnvil.TempDir("cpw"); repo.Worktree = world;
        string c0 = CommitA(repo, world, 1, "c0");
        string m1 = CommitA(repo, world, 3, "m1");
        string f1 = MakeCommit(repo, 2, [c0], "f1");
        return (repo, world, m1, f1);
    }

    /// <summary>main: c0(a=1)→c1(a=3); feature: c0→fA(a=2) — rebasing feature onto main conflicts.</summary>
    private static (Repository repo, string world, string c1, string fA) RebaseConflictSetup()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rb"));
        string world = TestAnvil.TempDir("rbw"); repo.Worktree = world;
        string c0 = CommitA(repo, world, 1, "c0");
        repo.WriteBranch("feature", c0);
        string c1 = CommitA(repo, world, 3, "c1");      // main advances
        repo.SetHeadToBranch("feature");
        string fA = CommitA(repo, world, 2, "fA");      // feature advances
        return (repo, world, c1, fA);
    }

    private static NbtCompound AB(int a) => TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a));

    private static void WriteA(string world, int a) =>
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(a)));

    private static string CommitA(Repository repo, string world, int a, string msg)
    {
        WriteA(world, a);
        Manifest m = Snapshotter.Snapshot(repo, world);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }

    private static string MakeCommit(Repository repo, int a, string[] parents, string msg)
    {
        string w = TestAnvil.TempDir("mk");
        WriteA(w, a);
        string tree = repo.WriteManifest(Snapshotter.Snapshot(repo, w));
        return repo.WriteCommit(new CommitObject { Tree = tree, Parents = [.. parents], Message = msg, Author = "t", Time = "2020" });
    }

    private static int ChunkA(Repository repo, string commit)
    {
        Manifest m = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        return NbtCanonical.Deserialize(repo.Objects.Read(m.Regions["region/r.0.0.mca"]["0,0"])).Get("a")!.IntValue;
    }

    private static int WorldChunkA(string world)
        => ChunkCodec.Decode(RegionFile.Open(Path.Combine(world, "region", "r.0.0.mca")).Chunks.Single()).Get("a")!.IntValue;
}
