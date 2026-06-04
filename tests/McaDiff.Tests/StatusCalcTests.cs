using fNbt;
using McaDiff.Anvil;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Tests for StatusCalc — the backbone of `mcadiff status`, previously uncovered (issue #8 B-1).</summary>
public class StatusCalcTests
{
    [Fact]
    public void Status_DetectsAddedModifiedRemoved()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("st"));
        string world = TestAnvil.TempDir("stw");
        repo.Worktree = world;
        WriteRegion(world, 1);
        Directory.CreateDirectory(Path.Combine(world, "data"));
        File.WriteAllText(Path.Combine(world, "data", "keep.txt"), "x");
        File.WriteAllText(Path.Combine(world, "data", "gone.txt"), "y");
        Commit(repo, world);

        WriteRegion(world, 2);                                              // modify the chunk
        File.WriteAllText(Path.Combine(world, "data", "added.txt"), "z");   // add
        File.Delete(Path.Combine(world, "data", "gone.txt"));              // remove

        List<StatusEntry> e = StatusCalc.Compute(repo, world);
        Assert.Contains(e, x => x.Path == "region/r.0.0.mca" && x.Change == "modified");
        Assert.Contains(e, x => x.Path == "data/added.txt" && x.Change == "added");
        Assert.Contains(e, x => x.Path == "data/gone.txt" && x.Change == "removed");
        Assert.DoesNotContain(e, x => x.Path == "data/keep.txt"); // unchanged → not reported
    }

    [Fact]
    public void Status_UnmodifiedWorld_IsEmpty()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("st2"));
        string world = TestAnvil.TempDir("st2w");
        repo.Worktree = world;
        WriteRegion(world, 1);
        Commit(repo, world);
        Assert.Empty(StatusCalc.Compute(repo, world));
    }

    [Fact]
    public void StatusCalc_ManifestPair_AddedRemovedAndChunkCount()
    {
        var from = new Manifest();
        from.Regions["region/r.0.0.mca"] = Chunks(("0,0", "a"), ("1,0", "b"));
        from.Blobs["old.txt"] = "h1";
        var to = new Manifest();
        to.Regions["region/r.0.0.mca"] = Chunks(("0,0", "CHANGED"), ("1,0", "b")); // 1 of 2 chunks differ
        to.Blobs["new.txt"] = "h2";

        List<StatusEntry> e = StatusCalc.Compute(from, to);
        Assert.Contains(e, x => x.Path == "region/r.0.0.mca" && x.Change == "modified" && x.Detail!.Contains("1 chunks"));
        Assert.Contains(e, x => x.Path == "new.txt" && x.Change == "added");
        Assert.Contains(e, x => x.Path == "old.txt" && x.Change == "removed");
    }

    private static SortedDictionary<string, string> Chunks(params (string Pos, string Hash)[] kv)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, h) in kv) d[p] = h;
        return d;
    }

    private static void WriteRegion(string world, int a) =>
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a))));

    private static void Commit(Repository repo, string world)
    {
        Manifest m = Snapshotter.Snapshot(repo, world);
        string? head = repo.HeadCommit();
        repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], "c", "test");
    }
}
