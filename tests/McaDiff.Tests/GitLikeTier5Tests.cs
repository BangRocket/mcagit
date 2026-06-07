using fNbt;
using McaDiff.Anvil;
using McaDiff.Cli;
using McaDiff.Nbt;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Tier 5 git-likeness: HEAD@{n}, stash, rebase, clean, and hooks.</summary>
public class GitLikeTier5Tests
{
    // ---- reflog revision syntax ----

    [Fact]
    public void ResolveRef_HeadReflogSyntax()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rl"));
        string world = TestAnvil.TempDir("rlw");
        repo.Worktree = world;
        string c0 = CommitWorld(repo, world, 1, "c0");
        string c1 = CommitWorld(repo, world, 2, "c1");
        string c2 = CommitWorld(repo, world, 3, "c2");

        Assert.Equal(c2, repo.ResolveRef("HEAD@{0}"));
        Assert.Equal(c1, repo.ResolveRef("HEAD@{1}"));
        Assert.Equal(c0, repo.ResolveRef("HEAD@{2}"));
    }

    // ---- stash ----

    [Fact]
    public void Stash_ShelvesAndRestoresWorktree()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("st"));
        string world = TestAnvil.TempDir("stw");
        repo.Worktree = world;
        WriteNote(world, "base");
        CommitWorld(repo, world, 1, "c0");

        WriteNote(world, "changed");
        Stash.PushResult r = Stash.Push(repo, "wip", "t");
        Assert.True(r.Created);
        Assert.Equal("base", ReadNote(world));          // worktree reset to HEAD
        Assert.Single(Stash.Stack(repo));

        List<MergeConflict> conflicts = Stash.Apply(repo, 0, pop: true);
        Assert.Empty(conflicts);
        Assert.Equal("changed", ReadNote(world));        // stashed change restored
        Assert.Empty(Stash.Stack(repo));
    }

    [Fact]
    public void Stash_SurvivesGc()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("stg"));
        string world = TestAnvil.TempDir("stgw");
        repo.Worktree = world;
        WriteNote(world, "base");
        CommitWorld(repo, world, 1, "c0");
        WriteNote(world, "changed");
        string stash = Stash.Push(repo, "wip", "t").Commit!;

        Gc.Repack(repo);
        Assert.True(repo.Objects.Exists(stash));         // stash commit kept reachable
        Assert.Equal("changed", ReadObjectNote(repo, stash)); // its snapshot is intact in the pack
    }

    // ---- rebase ----

    [Fact]
    public void Rebase_ReplaysCommitsOntoUpstream()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rb"));
        string c0 = Commit(repo, 1, 1, []);
        repo.WriteBranch("main", c0);
        string c1 = Commit(repo, 2, 1, [c0]);   // main: a 1→2
        repo.WriteBranch("main", c1);
        repo.WriteBranch("feature", c0);
        repo.SetHeadToBranch("feature");
        string fA = Commit(repo, 1, 9, [c0]);   // feature: b 1→9
        repo.WriteBranch("feature", fA);

        Rebase.Result r = Rebase.Start(repo, "main", null, "t");
        Assert.False(r.UpToDate);
        Assert.Equal(1, r.Replayed);
        Assert.Equal(c1, repo.ReadCommit(r.NewTip!).Parents.Single()); // replayed onto main tip
        Assert.Equal((2, 9), ChunkAB(repo, r.NewTip!));                // both changes present
        Assert.Equal(r.NewTip, repo.ReadBranch("feature"));            // branch moved
    }

    [Fact]
    public void Rebase_Onto_RelocatesToNewBase()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rbo"));
        string c0 = Commit(repo, 1, 1, []);
        repo.WriteBranch("main", c0);
        string c1 = Commit(repo, 2, 1, [c0]);
        repo.WriteBranch("main", c1);
        repo.WriteBranch("feature", c0);
        repo.SetHeadToBranch("feature");
        string fA = Commit(repo, 1, 9, [c0]);
        repo.WriteBranch("feature", fA);

        Rebase.Result r = Rebase.Start(repo, "main", ontoRef: c0, "t"); // replay onto c0 instead of main tip
        Assert.Equal(c0, repo.ReadCommit(r.NewTip!).Parents.Single());
        Assert.Equal((1, 9), ChunkAB(repo, r.NewTip!));               // just feature's change over c0
    }

    // ---- clean ----

    [Fact]
    public void Clean_RemovesUntrackedKeepsTracked()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cl"));
        string world = TestAnvil.TempDir("clw");
        repo.Worktree = world;
        WriteNote(world, "tracked");
        CommitWorld(repo, world, 1, "c0");
        File.WriteAllText(Path.Combine(world, "data", "untracked.txt"), "junk");

        Assert.Equal(0, RepoCommands.Clean(repo.Dir, ["-f"]));
        Assert.False(File.Exists(Path.Combine(world, "data", "untracked.txt"))); // removed
        Assert.True(File.Exists(Path.Combine(world, "data", "note.txt")));        // kept (tracked)
    }

    [Fact]
    public void Clean_DryRun_RemovesNothing()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cld"));
        string world = TestAnvil.TempDir("cldw");
        repo.Worktree = world;
        WriteNote(world, "tracked");
        CommitWorld(repo, world, 1, "c0");
        string untracked = Path.Combine(world, "data", "untracked.txt");
        File.WriteAllText(untracked, "junk");

        Assert.Equal(0, RepoCommands.Clean(repo.Dir, ["-n"]));
        Assert.True(File.Exists(untracked)); // dry run leaves it
    }

    // ---- hooks ----

    [Fact]
    public void PreCommitHook_NonZero_AbortsCommit()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("hk"));
        string world = TestAnvil.TempDir("hkw");
        repo.Worktree = world;
        WriteNote(world, "x");
        WriteHook(repo, "pre-commit", "#!/bin/sh\nexit 1\n");

        int rc = RepoCommands.Commit(repo.Dir, ["-m", "blocked"]);
        Assert.NotEqual(0, rc);
        Assert.Null(repo.HeadCommit()); // nothing committed
    }

    [Fact]
    public void PostCommitHook_RunsAfterCommit()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("hk2"));
        string world = TestAnvil.TempDir("hk2w");
        repo.Worktree = world;
        WriteNote(world, "x");
        WriteHook(repo, "post-commit", "#!/bin/sh\n: > \"$MCAGIT_DIR/post-ran\"\n");

        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "ok"]));
        Assert.NotNull(repo.HeadCommit());
        Assert.True(File.Exists(Path.Combine(repo.Dir, "post-ran"))); // post-commit fired
    }

    // ---- helpers ----

    private static void WriteHook(Repository repo, string name, string script)
    {
        string dir = Path.Combine(repo.Dir, "hooks");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void WriteNote(string world, string text)
    {
        Directory.CreateDirectory(Path.Combine(world, "data"));
        File.WriteAllText(Path.Combine(world, "data", "note.txt"), text);
    }

    private static string ReadNote(string world) => File.ReadAllText(Path.Combine(world, "data", "note.txt"));

    private static string ReadObjectNote(Repository repo, string commit)
    {
        Manifest m = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        return System.Text.Encoding.UTF8.GetString(repo.Objects.Read(m.Blobs["data/note.txt"]));
    }

    private static NbtCompound AB(int a, int b) =>
        TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a), new NbtInt("b", b));

    private static string Commit(Repository repo, int a, int b, string[] parents)
    {
        string world = TestAnvil.TempDir("rbw");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(a, b)));
        string tree = repo.WriteManifest(Snapshotter.Snapshot(repo, world));
        return repo.WriteCommit(new CommitObject { Tree = tree, Parents = [.. parents], Message = $"a{a}b{b}", Author = "t", Time = "2020" });
    }

    private static (int, int) ChunkAB(Repository repo, string commit)
    {
        Manifest m = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        NbtCompound c = NbtCanonical.Deserialize(repo.Objects.Read(m.Regions["region/r.0.0.mca"]["0,0"]));
        return (c.Get("a")!.IntValue, c.Get("b")!.IntValue);
    }

    private static string CommitWorld(Repository repo, string world, int a, string msg)
    {
        // a blob-only world (data/note.txt) — `a` just varies the note so commits differ
        Manifest m = Snapshotter.Snapshot(repo, world);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
