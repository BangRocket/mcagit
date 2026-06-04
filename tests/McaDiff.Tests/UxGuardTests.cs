using fNbt;
using McaDiff.Anvil;
using McaDiff.Cli;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>First-time-user crash guards (#26): init must not scatter repo data into a world,
/// the snapshot must ignore repo metadata if the repo lives inside the world, and checkout/reset
/// must refuse a world that's open in Minecraft instead of crashing half-written.</summary>
public class UxGuardTests
{
    private static NbtCompound Chunk() => TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", 1));

    [Fact]
    public void Init_InsideWorldFolder_Refused()
    {
        string world = TestAnvil.TempDir("uxi");
        TestAnvil.WriteLoose(Path.Combine(world, "level.dat"), TestAnvil.Root(new NbtCompound("Data")));

        Assert.Equal(2, RepoCommands.Init(world, []));            // refuses: would scatter repo files into the world
        Assert.False(Repository.IsRepository(world));            // nothing created
        Assert.Equal(0, RepoCommands.Init(TestAnvil.TempDir("uxi2"), [])); // a non-world folder is fine
    }

    [Fact]
    public void Snapshot_SkipsRepoMetadata_WhenRepoInsideWorld()
    {
        string world = TestAnvil.TempDir("uxw");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), Chunk()));
        Repository repo = Repository.Init(Path.Combine(world, ".mcagit")); // repo lives inside the world

        Manifest m = Snapshotter.Snapshot(repo, world);          // must not choke on its own objects/HEAD/lock
        Assert.True(m.Regions.ContainsKey("region/r.0.0.mca"));  // real content captured
        Assert.DoesNotContain(m.Blobs.Keys.Concat(m.Nbt.Keys), k => k.StartsWith(".mcagit/", StringComparison.Ordinal));
    }

    [Fact]
    public void Checkout_RefusesWhenWorldIsOpen_ThenSucceeds()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("uxr"));
        string world = TestAnvil.TempDir("uxrw");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), Chunk()));
        repo.Worktree = world;
        string c = repo.CreateCommit(repo.WriteManifest(Snapshotter.Snapshot(repo, world)), [], "c0", "t");
        repo.WriteBranch("main", c);
        repo.SetHeadToBranch("main");

        string lockFile = Path.Combine(world, "session.lock");
        File.WriteAllText(lockFile, "");
        using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // server holds it
            Assert.Equal(2, RepoCommands.Checkout(repo.Dir, ["main", world, "--force"]));

        Assert.Equal(0, RepoCommands.Checkout(repo.Dir, ["main", world, "--force"])); // released → proceeds
    }
}
