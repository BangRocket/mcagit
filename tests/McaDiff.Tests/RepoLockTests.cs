using fNbt;
using McaDiff.Anvil;
using McaDiff.Cli;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>The inter-process repo lock that stops two concurrent commit/push runs from racing
/// branch advancement (issue #2: a backup driver whose run overruns its interval).</summary>
public class RepoLockTests
{
    [Fact]
    public void SecondAcquire_WhileHeld_Throws()
    {
        string dir = TestAnvil.TempDir("lk");
        using RepoLock held = RepoLock.Acquire(dir, "commit");
        Assert.Throws<RepoLockedException>(() => RepoLock.Acquire(dir, "push"));
    }

    [Fact]
    public void Acquire_AfterRelease_Succeeds()
    {
        string dir = TestAnvil.TempDir("lk2");
        RepoLock.Acquire(dir, "commit").Dispose();
        using RepoLock again = RepoLock.Acquire(dir, "commit"); // reuses the leftover lock file
        Assert.NotNull(again);
    }

    [Fact]
    public void Commit_WhileRepoLocked_FailsFast_ThenSucceedsWhenFree()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("lkc"));
        string world = World(1);

        // A concurrent process is mid-commit: the lock is held.
        RepoLock other = RepoLock.Acquire(repo.Dir, "commit");
        Assert.Equal(2, RepoCommands.Commit(repo.Dir, ["-m", "blocked", world]));   // fail-fast, exit 2
        Assert.Null(repo.HeadCommit());                                             // nothing committed

        other.Dispose();
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "ok", world]));        // lock free now
        Assert.NotNull(repo.HeadCommit());
        Assert.Equal("ok", repo.ReadCommit(repo.HeadCommit()!).Message);
    }

    [Fact]
    public void Push_WhileRepoLocked_FailsFast()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("lkp"));
        CommitWorld(repo, World(1), "c0");
        repo.AddRemote("origin", TestAnvil.TempDir("lkpRemote-unused"));

        using RepoLock other = RepoLock.Acquire(repo.Dir, "commit");
        Assert.Equal(2, RepoCommands.Push(repo.Dir, ["origin", "main"]));           // locked before it ever dials the remote
    }

    // ---- helpers ----

    private static string World(int v)
    {
        string dir = TestAnvil.TempDir("lkw");
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
