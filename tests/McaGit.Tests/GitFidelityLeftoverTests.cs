using fNbt;
using McaGit.Anvil;
using McaGit.Cli;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>The #7 leftovers confirmed by the #16 audit: config --global without a repo,
/// clean -d, remote remove/rename/set-url/get-url, push --all exit code, abbreviated hashes.</summary>
public class GitFidelityLeftoverTests
{
    [Fact]
    public void Config_Global_OutsideRepo_DoesNotRequireRepo()
    {
        string notARepo = TestAnvil.TempDir("nrepo");
        // Reading an (almost certainly) unset global key must report "unset" (exit 1),
        // NOT "not a repository" (exit 2). Read-only — never writes ~/.mcaconfig.
        int rc = RepoCommands.Config(notARepo, ["--global", "mcagit.audit.almostcertainlyunset"]);
        Assert.NotEqual(2, rc); // 2 == NoRepo; the fix makes --global repo-independent
    }

    [Fact]
    public void Clean_RemovesUntrackedDir_OnlyWithDashD()
    {
        Repository repo = InitWithCommit(out string world);
        string strayDir = Path.Combine(world, "newdim", "region");
        Directory.CreateDirectory(strayDir);
        File.WriteAllText(Path.Combine(strayDir, "stray.txt"), "x");

        // clean -f (no -d): the untracked file goes, the now-empty dir stays (git semantics).
        Assert.Equal(0, RepoCommands.Clean(repo.Dir, ["-f", "--world", world]));
        Assert.False(File.Exists(Path.Combine(strayDir, "stray.txt")));
        Assert.True(Directory.Exists(Path.Combine(world, "newdim")));

        // clean -df: the untracked directory tree is removed.
        Directory.CreateDirectory(strayDir);
        File.WriteAllText(Path.Combine(strayDir, "stray.txt"), "x");
        Assert.Equal(0, RepoCommands.Clean(repo.Dir, ["-f", "-d", "--world", world]));
        Assert.False(Directory.Exists(Path.Combine(world, "newdim")));
    }

    [Fact]
    public void Remote_Remove_Rename_SetUrl_GetUrl()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rem"));
        Assert.Equal(0, RepoCommands.Remote(repo.Dir, ["add", "origin", "https://example.com/a"]));
        Assert.Equal("https://example.com/a", repo.GetRemote("origin"));

        // add again → rejected
        Assert.Equal(2, RepoCommands.Remote(repo.Dir, ["add", "origin", "https://example.com/b"]));

        Assert.Equal(0, RepoCommands.Remote(repo.Dir, ["set-url", "origin", "https://example.com/c"]));
        Assert.Equal("https://example.com/c", repo.GetRemote("origin"));

        Assert.Equal(0, RepoCommands.Remote(repo.Dir, ["rename", "origin", "upstream"]));
        Assert.Null(repo.GetRemote("origin"));
        Assert.Equal("https://example.com/c", repo.GetRemote("upstream"));

        Assert.Equal(0, RepoCommands.Remote(repo.Dir, ["get-url", "upstream"]));
        Assert.Equal(2, RepoCommands.Remote(repo.Dir, ["get-url", "origin"])); // gone

        Assert.Equal(0, RepoCommands.Remote(repo.Dir, ["remove", "upstream"]));
        Assert.Null(repo.GetRemote("upstream"));
        Assert.Equal(2, RepoCommands.Remote(repo.Dir, ["remove", "upstream"])); // already gone
    }

    [Fact]
    public void PushAll_ReturnsNonZero_WhenABranchFails()
    {
        Repository repo = InitWithCommit(out _);
        repo.AddRemote("origin", TestAnvil.TempDir("not-a-repo")); // empty dir → LocalTransport ctor throws
        Assert.Equal(1, RepoCommands.Push(repo.Dir, ["origin", "--all"])); // push fails → non-zero
    }

    [Fact]
    public void Abbreviate_IsSevenCharsWhenUnambiguous_AndAPrefix()
    {
        Repository repo = InitWithCommit(out _);
        string head = repo.HeadCommit()!;
        string abbr = repo.Objects.Abbreviate(head);
        Assert.Equal(7, abbr.Length);              // git default, grows only on collision
        Assert.StartsWith(abbr, head);
        Assert.Equal(head, repo.Objects.ResolvePrefix(abbr)); // round-trips back to the full hash
    }

    // ---- helpers ----

    private static Repository InitWithCommit(out string world)
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("gfl"));
        world = TestAnvil.TempDir("gflw");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", 1))));
        Manifest m = Snapshotter.Snapshot(repo, world);
        repo.WriteBranch("main", repo.CreateCommit(repo.WriteManifest(m), [], "c0", "test"));
        repo.SetHeadToBranch("main");
        return repo;
    }
}
