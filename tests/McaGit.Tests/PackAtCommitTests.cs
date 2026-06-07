using fNbt;
using McaGit.Anvil;
using McaGit.Cli;
using McaGit.Repo;
using Xunit;

namespace McaGit.Tests;

/// <summary>
/// Pack-at-commit: a commit buffers its new objects and flushes them as a single packfile instead of
/// writing one loose file per chunk (the per-file FS cost that makes a cold commit crawl). These
/// assert the objects land packed (not loose), reproduce the world faithfully, dedup unchanged chunks
/// across packs, and that an unchanged re-commit writes nothing.
/// </summary>
public class PackAtCommitTests
{
    private static NbtCompound Chunk(int a) =>
        TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a));

    private static Repository BoundRepo(string label, out string world)
    {
        Repository repo = Repository.Init(TestAnvil.TempDir(label));
        world = TestAnvil.TempDir(label + "w");
        repo.Worktree = world;
        return repo;
    }

    [Fact]
    public void Commit_WritesObjectsToPack_NotLooseFiles()
    {
        Repository repo = BoundRepo("pac", out string world);
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), Chunk(1)), (new ChunkPos(1, 0), Chunk(2)));

        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "first"]));

        Repository r = Repository.Open(repo.Dir);
        Assert.NotEmpty(r.Objects.PackFilePaths());                 // a pack was written
        Assert.Empty(r.Objects.LooseHashes());                      // and nothing was left loose
        Assert.True(r.Objects.Exists(r.HeadCommit()!));             // the commit reads back from the pack
        Assert.True(r.Objects.Count() >= 4);                        // 2 chunks + tree + commit, all packed
    }

    [Fact]
    public void Commit_ThenCheckout_ReproducesWorldFromPack()
    {
        Repository repo = BoundRepo("pacc", out string world);
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), Chunk(42)));
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "c"]));

        Repository r = Repository.Open(repo.Dir);
        string tree = r.ReadCommit(r.HeadCommit()!).Tree;
        string outDir = TestAnvil.TempDir("pacc-out");
        Checkout.Materialize(r, r.ReadManifest(tree), outDir);      // reads every object back out of the pack

        // Re-snapshotting the checked-out world yields the same tree hash ⇒ a faithful reproduction.
        Repository verify = Repository.Init(TestAnvil.TempDir("pacc-v"));
        Assert.Equal(tree, verify.WriteManifest(Snapshotter.Snapshot(verify, outDir)));
    }

    [Fact]
    public void SecondCommit_DedupsUnchangedChunksAcrossPacks()
    {
        Repository repo = BoundRepo("pacd", out string world);
        string region = Path.Combine(world, "region", "r.0.0.mca");
        TestAnvil.WriteRegion(region, (new ChunkPos(0, 0), Chunk(1)), (new ChunkPos(1, 0), Chunk(2)));
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "first"]));
        int after1 = Repository.Open(repo.Dir).Objects.Count();     // 2 chunks + tree + commit = 4

        // Change only chunk (0,0); chunk (1,0) is byte-identical to the first commit's.
        TestAnvil.WriteRegion(region, (new ChunkPos(0, 0), Chunk(99)), (new ChunkPos(1, 0), Chunk(2)));
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "second"]));

        Repository r = Repository.Open(repo.Dir);
        // Only the changed chunk + new tree + new commit are added; the unchanged chunk is found in the
        // first pack (Exists checks packs) and never re-stored.
        Assert.Equal(after1 + 3, r.Objects.Count());
        Assert.Equal(2, r.Objects.PackFilePaths().Count());         // one pack per commit
    }

    [Fact]
    public void RecommitUnchanged_WritesNoNewPack()
    {
        Repository repo = BoundRepo("pacu", out string world);
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), Chunk(7)));
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "first"]));
        int packs = Repository.Open(repo.Dir).Objects.PackFilePaths().Count();
        string tip = Repository.Open(repo.Dir).HeadCommit()!;

        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "again"])); // nothing changed

        Repository r = Repository.Open(repo.Dir);
        Assert.Equal(tip, r.HeadCommit());                          // branch didn't move
        Assert.Equal(packs, r.Objects.PackFilePaths().Count());     // and no empty pack was written
    }

    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    public void Gc_WithThreads_KeepsWorldReproducible(string threads)
    {
        Repository repo = BoundRepo("gct" + threads, out string world);
        var chunks = new (ChunkPos, NbtCompound)[64];
        for (int i = 0; i < chunks.Length; i++) chunks[i] = (new ChunkPos(i % 8, i / 8), Chunk(i));
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), chunks);
        Assert.Equal(0, RepoCommands.Commit(repo.Dir, ["-m", "first"]));
        string head = Repository.Open(repo.Dir).HeadCommit()!;

        Assert.Equal(0, RepoCommands.GcCmd(repo.Dir, ["--threads", threads]));

        Repository r = Repository.Open(repo.Dir);
        Assert.NotEmpty(r.Objects.PackFilePaths());
        Assert.Empty(r.Objects.LooseHashes());
        Assert.True(r.Objects.Exists(head));                 // HEAD survived the repack
        // Re-committing the unchanged world is a no-op iff every chunk object survived gc intact.
        Assert.Equal(0, RepoCommands.Commit(r.Dir, ["-m", "again"]));
        Assert.Equal(head, Repository.Open(repo.Dir).HeadCommit());
    }

    [Fact]
    public void Gc_RejectsInvalidThreads()
    {
        Repository repo = BoundRepo("gcbad", out _);
        Assert.NotEqual(0, RepoCommands.GcCmd(repo.Dir, ["--threads", "nope"]));
    }
}
