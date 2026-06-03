using fNbt;
using McaDiff.Anvil;
using McaDiff.Cli;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>The small CLI contract a live-server commit driver depends on (issue #2):
/// one-shot <c>commit --push</c> and a clear committed-vs-nothing signal.</summary>
public class CommitDriverTests
{
    [Fact]
    public void Commit_Push_UpdatesRemoteBranchInOneShot()
    {
        string originDir = TestAnvil.TempDir("origin");
        Repository.Init(originDir);

        Repository repo = Repository.Init(TestAnvil.TempDir("repo"));
        string world = TestAnvil.TempDir("w");
        repo.Worktree = world;
        repo.AddRemote("origin", originDir);
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(1)));

        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "auto 2026-06-03", "--push", "origin"]));

        string tip = Repository.Open(repo.Dir).HeadCommit()!;
        Repository origin = Repository.Open(originDir);
        Assert.Equal(tip, origin.ReadBranch("main"));          // pushed in the same invocation
        Assert.True(origin.Objects.Exists(tip));               // objects landed too
    }

    [Fact]
    public void Commit_NothingToCommit_ReturnsZero_AndLeavesBranch()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("nc"));
        string world = TestAnvil.TempDir("ncw");
        repo.Worktree = world;
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(1)));

        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "first"]));
        string tip = Repository.Open(repo.Dir).HeadCommit()!;

        // Re-commit an unchanged world: succeeds, but the branch must not move.
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "again", "--json"]));
        Assert.Equal(tip, Repository.Open(repo.Dir).HeadCommit());
    }

    [Fact]
    public void Commit_Json_RunsAndCommits()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cj"));
        string world = TestAnvil.TempDir("cjw");
        repo.Worktree = world;
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(7)));

        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "snap", "--json"]));
        Assert.NotNull(Repository.Open(repo.Dir).HeadCommit()); // committed despite --json output
    }

    private static NbtCompound AB(int a) =>
        TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a));
}
