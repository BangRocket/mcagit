using System.IO.Compression;
using System.Security.Cryptography;
using fNbt;
using McaDiff.Anvil;
using McaDiff.Nbt;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Coverage-gap tests from the issue #8 audit (security boundary, corruption,
/// merge conflict variants, stash, compression paths, ResolveRef errors, bisect skip).</summary>
public class TestCoverageAuditTests
{
    // ---- B-3: the poisoned-object defense ----

    [Fact]
    public void ImportRaw_HashMismatch_ThrowsAndDoesNotStore()
    {
        var store = new ObjectStore(TestAnvil.TempDir("ir"));
        byte[] compressed = Zlib([1, 2, 3, 4]);
        string wrong = new('a', 64);
        Assert.Throws<InvalidDataException>(() => store.ImportRaw(wrong, compressed));
        Assert.False(store.Exists(wrong));
    }

    // ---- H-2: packfile corruption ----

    [Fact]
    public void Packfile_TruncatedPack_FailsFsck()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("pk"));
        CommitWorld(repo, World(1), "c0");
        CommitWorld(repo, World(2), "c1");
        Gc.Repack(repo);

        string pack = Directory.EnumerateFiles(Path.Combine(repo.Objects.ObjectsDir, "pack"), "*.pack").Single();
        byte[] b = File.ReadAllBytes(pack);
        repo.Objects.ReloadPacks();
        File.WriteAllBytes(pack, b[..^200]); // lop off the tail
        repo.Objects.ReloadPacks();

        Assert.False(Fsck.Check(repo).Ok); // some object no longer decompresses to its hash
    }

    [Fact]
    public void Packfile_CorruptIndexMagic_ThrowsOnOpen()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("pk2"));
        CommitWorld(repo, World(1), "c0");
        Gc.Repack(repo);
        repo.Objects.ReloadPacks();

        string pack = Directory.EnumerateFiles(Path.Combine(repo.Objects.ObjectsDir, "pack"), "*.pack").Single();
        string idx = Path.ChangeExtension(pack, ".idx");
        byte[] b = File.ReadAllBytes(idx);
        b[0] = 0xFF;
        File.WriteAllBytes(idx, b);
        Assert.Throws<InvalidDataException>(() => Packfile.Open(pack));
    }

    // ---- H-3: SSH signing error path (runs without ssh-keygen) ----

    [Fact]
    public void SshSigner_MissingKey_Throws()
        => Assert.Throws<FileNotFoundException>(() => SshSigner.Sign("payload", Path.Combine(TestAnvil.TempDir("nk"), "absent_key")));

    // ---- H-4: merge conflict variants ----

    [Fact]
    public void Merge_DeleteModifyConflict_KeepsOurs()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("dm"));
        Manifest baseM = RegionManifest("h1"), ours = RegionManifest("h2"), theirs = new(); // theirs deleted the region
        var conflicts = new List<MergeConflict>();
        Manifest merged = Merger.MergeManifests(repo, baseM, ours, theirs, false, conflicts);
        Assert.Contains(conflicts, c => c.Reason.Contains("delete/modify"));
        Assert.True(merged.Regions.ContainsKey("region/r.0.0.mca")); // kept ours
    }

    [Fact]
    public void Merge_BinaryBlobConflict_KeepsOursOrTheirs()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("bb"));
        var baseM = BlobManifest("h1"); var ours = BlobManifest("h2"); var theirs = BlobManifest("h3");
        var conflicts = new List<MergeConflict>();
        Merger.MergeManifests(repo, baseM, ours, theirs, preferTheirs: true, conflicts);
        Assert.Contains(conflicts, c => c.Reason.Contains("binary conflict"));
    }

    // ---- H-5 / H-6: stash drop / clear / apply-with-conflict ----

    [Fact]
    public void Stash_Drop_RemovesNewest_Clear_Empties()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("sd"));
        string world = TestAnvil.TempDir("sdw"); repo.Worktree = world;
        WriteNote(world, "base"); CommitWorld(repo, world, "c0");
        WriteNote(world, "v1"); string s1 = Stash.Push(repo, "one", "t").Commit!;
        WriteNote(world, "v2"); string s2 = Stash.Push(repo, "two", "t").Commit!;
        Assert.Equal([s2, s1], Stash.Stack(repo));

        Assert.True(Stash.Drop(repo, 0));
        Assert.Equal([s1], Stash.Stack(repo));
        Stash.Clear(repo);
        Assert.Empty(Stash.Stack(repo));
    }

    [Fact]
    public void Stash_ApplyWithConflict_RetainsStash()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("sc"));
        string world = TestAnvil.TempDir("scw"); repo.Worktree = world;
        WriteRegionA(world, 1); CommitWorld2(repo, world, "c0");
        WriteRegionA(world, 2); Stash.Push(repo, "wip", "t");  // stash a=2, worktree reset to a=1
        WriteRegionA(world, 3);                                 // diverge: a=3 in worktree

        List<MergeConflict> conflicts = Stash.Apply(repo, 0, pop: true); // a: base 1, ours 3, theirs 2 → conflict
        Assert.NotEmpty(conflicts);
        Assert.Single(Stash.Stack(repo)); // pop retains the stash on conflict
    }

    // ---- M-1 / L-6: every compression path round-trips (None/GZip/ZLib) ----

    [Theory]
    [InlineData(ChunkCompression.None)]
    [InlineData(ChunkCompression.GZip)]
    [InlineData(ChunkCompression.ZLib)]
    public void ChunkCodec_RoundTrips(ChunkCompression comp)
    {
        var root = TestAnvil.Root(new NbtInt("DataVersion", 4000), new NbtString("s", "hi"), new NbtLong("L", 9_000_000_000L));
        var rc = new RawChunk(new ChunkPos(0, 0), comp, ChunkCodec.Encode(root, comp), external: false, timestamp: 0);
        Assert.True(NbtEquality.DeepEquals(root, ChunkCodec.Decode(rc)));
    }

    // ---- H-1: external (.mcc) oversized chunk round-trips ----

    [Fact]
    public void RegionFile_ExternalChunk_RoundTrips()
    {
        var big = new byte[2_000_000];
        new Random(1).NextBytes(big); // incompressible → compressed payload > 255 sectors → spills to .mcc
        var root = TestAnvil.Root(new NbtInt("DataVersion", 4000), new NbtByteArray("data", big));
        string region = Path.Combine(TestAnvil.TempDir("mcc"), "region", "r.0.0.mca");
        Directory.CreateDirectory(Path.GetDirectoryName(region)!);
        RegionWriter.Write(region, [new RawChunk(new ChunkPos(0, 0), ChunkCompression.ZLib, ChunkCodec.Encode(root, ChunkCompression.ZLib), external: false, timestamp: 0)]);

        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(region)!, "c.0.0.mcc")));
        Assert.True(NbtEquality.DeepEquals(root, ChunkCodec.Decode(RegionFile.Open(region).Chunks.Single())));
    }

    // ---- N-4: entities/ and poi/ regions are classified as regions ----

    [Fact]
    public void Snapshot_ClassifiesRegionEntitiesPoiDirs()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("cls"));
        string world = TestAnvil.TempDir("clsw");
        foreach (string dir in new[] { "region", "entities", "poi" })
            TestAnvil.WriteRegion(Path.Combine(world, dir, "r.0.0.mca"),
                (new ChunkPos(0, 0), TestAnvil.Root(new NbtInt("DataVersion", 3953))));
        Manifest m = Snapshotter.Snapshot(repo, world);
        Assert.True(m.Regions.ContainsKey("region/r.0.0.mca"));
        Assert.True(m.Regions.ContainsKey("entities/r.0.0.mca"));
        Assert.True(m.Regions.ContainsKey("poi/r.0.0.mca"));
    }

    // ---- M-3: IgnoreRules forms ----

    [Fact]
    public void IgnoreRules_WildcardAnchoredAndComments()
    {
        string dir = TestAnvil.TempDir("ig");
        File.WriteAllText(Path.Combine(dir, ".mcaignore"), "# a comment\n\n?.dat\nlogs/\n/anchored.txt\n");
        IgnoreRules ig = IgnoreRules.Load(dir);
        Assert.True(ig.IsIgnored("x.dat"));        // ? matches one char
        Assert.False(ig.IsIgnored("xy.dat"));      // ? is exactly one char
        Assert.True(ig.IsIgnored("logs/latest"));  // dir/
        Assert.True(ig.IsIgnored("anchored.txt")); // /anchored
        Assert.False(ig.IsIgnored("sub/anchored.txt"));
    }

    // ---- M-4: ResolveRef error + parent-selector paths ----

    [Fact]
    public void ResolveRef_Errors_And_SecondParent()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rr"));
        string p1 = MakeCommit(repo, []), p2 = MakeCommit(repo, []);
        string m = repo.WriteCommit(new CommitObject { Tree = repo.WriteManifest(new Manifest()), Parents = [p1, p2], Message = "m", Author = "t", Time = "2020" });
        repo.WriteBranch("main", m);

        Assert.Equal(p2, repo.ResolveRef(m + "^2"));
        Assert.Throws<InvalidOperationException>(() => repo.ResolveRef("no-such-ref"));
        Assert.Throws<InvalidOperationException>(() => repo.ResolveRef(m + "^3"));
        Assert.Throws<InvalidOperationException>(() => repo.ResolveRef("HEAD@{99}"));
    }

    // ---- M-7: bisect skip excludes a commit ----

    [Fact]
    public void Bisect_Skip_ExcludesCandidate()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("bis"));
        string world = TestAnvil.TempDir("bisw"); repo.Worktree = world;
        var commits = new List<string>();
        for (int i = 0; i < 5; i++) { WriteRegionA(world, i); commits.Add(CommitWorld2(repo, world, $"c{i}")); }
        repo.BisectStart("main");
        repo.BisectSetBad(commits[4]);
        repo.BisectAddGood(commits[0]);

        string first = Bisect.Compute(repo).Next!;
        repo.BisectAddSkip(first);
        Assert.NotEqual(first, Bisect.Compute(repo).Next); // skipped candidate isn't offered again
    }

    // ---- helpers ----

    private static byte[] Zlib(byte[] content)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(content);
        return ms.ToArray();
    }

    private static Manifest RegionManifest(string chunkHash)
    {
        var m = new Manifest();
        m.Regions["region/r.0.0.mca"] = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["0,0"] = chunkHash };
        return m;
    }

    private static Manifest BlobManifest(string hash) { var m = new Manifest(); m.Blobs["data/x.bin"] = hash; return m; }

    private static NbtCompound AB(int a) => TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a));
    private static void WriteRegionA(string world, int a) =>
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(a)));
    private static void WriteNote(string world, string text)
    {
        Directory.CreateDirectory(Path.Combine(world, "data"));
        File.WriteAllText(Path.Combine(world, "data", "note.txt"), text);
    }

    private static string World(int a)
    {
        string dir = TestAnvil.TempDir("w");
        WriteRegionA(dir, a);
        return dir;
    }

    private static string CommitWorld(Repository repo, string worldDir, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, worldDir);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }

    private static string CommitWorld2(Repository repo, string world, string msg) => CommitWorld(repo, world, msg);

    private static string MakeCommit(Repository repo, string[] parents)
        => repo.WriteCommit(new CommitObject { Tree = repo.WriteManifest(new Manifest()), Parents = [.. parents], Message = "x", Author = "t", Time = "2020" });
}
