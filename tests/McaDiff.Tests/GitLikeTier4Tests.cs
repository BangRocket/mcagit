using System.Security.Cryptography;
using System.Text;
using McaDiff.Cli;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Tier 4 git-likeness: bisect binary search + the staging index.</summary>
public class GitLikeTier4Tests
{
    // ---- bisect ----

    [Fact]
    public void Bisect_ConvergesOnFirstBadCommit()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("bis"));
        string world = TestAnvil.TempDir("bisw");
        repo.Worktree = world;

        var commits = new List<string>();
        for (int i = 0; i <= 6; i++) { WriteNote(world, $"v{i}"); commits.Add(CommitWorld(repo, world, $"c{i}")); }

        const int firstBad = 3; // the bug appears at c3 and persists
        repo.BisectStart("main");
        repo.BisectSetBad(commits[6]);
        repo.BisectAddGood(commits[0]);

        Bisect.State s;
        int guard = 0;
        while (true)
        {
            s = Bisect.Compute(repo);
            Assert.False(s.NeedMarks);
            if (s.Done) break;
            Assert.True(++guard < 20, "bisect did not converge");
            int idx = commits.IndexOf(s.Next!);
            if (idx >= firstBad) repo.BisectSetBad(s.Next!); else repo.BisectAddGood(s.Next!);
        }
        Assert.Equal(commits[firstBad], s.FirstBad);
    }

    [Fact]
    public void Bisect_StartClearsPriorMarks_AndTracksOriginal()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("bis2"));
        string world = TestAnvil.TempDir("bis2w");
        repo.Worktree = world;
        WriteNote(world, "a"); CommitWorld(repo, world, "c0");

        repo.BisectStart("main");
        repo.BisectSetBad(repo.HeadCommit()!);
        repo.BisectAddGood(repo.HeadCommit()!);
        Assert.True(repo.InBisect);
        Assert.Equal("main", repo.BisectOriginal());

        repo.BisectStart("main"); // restart wipes marks
        Assert.Null(repo.BisectBad());
        Assert.Empty(repo.BisectGood());

        repo.BisectClear();
        Assert.False(repo.InBisect);
    }

    // ---- staging index ----

    [Fact]
    public void Add_StagesOnlyNamedPath_CommitKeepsRestFromHead()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("idx"));
        string world = TestAnvil.TempDir("idxw");
        repo.Worktree = world;

        WriteBlobs(world, "1", "1");
        CommitWorld(repo, world, "c0");
        WriteBlobs(world, "2", "2"); // both files changed on disk

        Staging.Add(repo, world, ["data/a.txt"]);
        Assert.True(StagingIndex.Exists(repo));
        Manifest idx = StagingIndex.Load(repo);
        Assert.Equal(HashOf("2"), idx.Blobs["data/a.txt"]); // a staged
        Assert.Equal(HashOf("1"), idx.Blobs["data/b.txt"]); // b carried from HEAD

        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "staged a"]));
        Assert.False(StagingIndex.Exists(repo)); // index cleared on commit

        Repository r2 = Repository.Open(repo.Dir);
        Manifest committed = r2.ReadManifest(r2.ReadCommit(r2.HeadCommit()!).Tree);
        Assert.Equal(HashOf("2"), committed.Blobs["data/a.txt"]); // a committed
        Assert.Equal(HashOf("1"), committed.Blobs["data/b.txt"]); // b NOT committed
    }

    [Fact]
    public void Add_Dot_StagesEverything()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("idxall"));
        string world = TestAnvil.TempDir("idxallw");
        repo.Worktree = world;
        WriteBlobs(world, "1", "1");
        CommitWorld(repo, world, "c0");
        WriteBlobs(world, "2", "2");

        Staging.Add(repo, world, ["."]);
        Manifest idx = StagingIndex.Load(repo);
        Assert.Equal(HashOf("2"), idx.Blobs["data/a.txt"]);
        Assert.Equal(HashOf("2"), idx.Blobs["data/b.txt"]);
    }

    [Fact]
    public void Unstage_RevertsPathToHead_AndClearsWhenEmpty()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("unst"));
        string world = TestAnvil.TempDir("unstw");
        repo.Worktree = world;
        WriteBlobs(world, "1", "1");
        CommitWorld(repo, world, "c0");
        WriteBlobs(world, "2", "2");
        Staging.Add(repo, world, ["."]); // index: a=2, b=2

        Staging.Unstage(repo, ["data/a.txt"]);
        Manifest idx = StagingIndex.Load(repo);
        Assert.Equal(HashOf("1"), idx.Blobs["data/a.txt"]); // a back to HEAD
        Assert.Equal(HashOf("2"), idx.Blobs["data/b.txt"]);

        Staging.Unstage(repo, ["data/b.txt"]);              // now index == HEAD
        Assert.False(StagingIndex.Exists(repo));             // empty index removed
    }

    // ---- helpers ----

    private static string HashOf(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static void WriteNote(string world, string text)
    {
        Directory.CreateDirectory(Path.Combine(world, "data"));
        File.WriteAllText(Path.Combine(world, "data", "note.txt"), text);
    }

    private static void WriteBlobs(string world, string a, string b)
    {
        Directory.CreateDirectory(Path.Combine(world, "data"));
        File.WriteAllText(Path.Combine(world, "data", "a.txt"), a);
        File.WriteAllText(Path.Combine(world, "data", "b.txt"), b);
    }

    private static string CommitWorld(Repository repo, string world, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, world);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
