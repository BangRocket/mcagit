using System.Security.Cryptography;
using fNbt;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Tier 2 git-likeness: binary delta codec, packfiles, and gc repacking.</summary>
public class GitLikeTier2Tests
{
    // ---- delta ----

    [Theory]
    [InlineData("", "")]
    [InlineData("", "hello")]
    [InlineData("hello", "")]
    [InlineData("identical", "identical")]
    [InlineData("the quick brown fox", "the quick red fox jumps")]
    public void Delta_RoundTrips(string baseStr, string targetStr)
    {
        byte[] b = System.Text.Encoding.UTF8.GetBytes(baseStr);
        byte[] t = System.Text.Encoding.UTF8.GetBytes(targetStr);
        Assert.Equal(t, Delta.Apply(b, Delta.Diff(b, t)));
    }

    [Fact]
    public void Delta_RoundTrips_LargeRandom_AndBinary()
    {
        var rng = new Random(12345);
        for (int trial = 0; trial < 20; trial++)
        {
            byte[] b = new byte[rng.Next(0, 8000)];
            rng.NextBytes(b);
            byte[] t = (byte[])b.Clone();
            // mutate a few spots + splice
            for (int k = 0; k < 5 && t.Length > 0; k++) t[rng.Next(t.Length)] ^= 0xFF;
            if (t.Length > 100) t = t[..(t.Length - 50)];
            Assert.Equal(t, Delta.Apply(b, Delta.Diff(b, t)));
        }
    }

    [Fact]
    public void Delta_OfSimilarBuffers_IsTiny()
    {
        var rng = new Random(7);
        byte[] b = new byte[20_000];
        rng.NextBytes(b);
        byte[] t = (byte[])b.Clone();
        t[10_000] ^= 0xFF; // a single changed byte deep inside
        byte[] delta = Delta.Diff(b, t);
        Assert.True(delta.Length < 200, $"delta was {delta.Length} bytes for a 1-byte change");
        Assert.Equal(t, Delta.Apply(b, delta));
    }

    // ---- packfile ----

    [Fact]
    public void Packfile_RoundTripsAllObjects()
    {
        string objDir = TestAnvil.TempDir("pf");
        var rng = new Random(3);
        byte[] baseBuf = new byte[4000];
        rng.NextBytes(baseBuf);

        var objs = new Dictionary<string, byte[]>();
        for (int i = 0; i < 30; i++)
        {
            byte[] c = (byte[])baseBuf.Clone();
            c[i] ^= 0xAA; // near-identical, same length
            objs[Hash(c)] = c;
        }
        objs[Hash([1, 2, 3])] = [1, 2, 3]; // a tiny outlier

        List<string> ordered = objs.Keys.OrderBy(h => h, StringComparer.Ordinal).ToList();
        string? id = Packfile.Write(objDir, ordered, h => objs[h]);
        Assert.NotNull(id);

        Packfile pack = Packfile.OpenAll(objDir).Single();
        foreach ((string h, byte[] content) in objs)
        {
            Assert.True(pack.Contains(h));
            Assert.Equal(content, pack.Read(h));
        }
        Assert.Equal(objs.Count, pack.Hashes.Count);
        pack.Dispose();
    }

    [Fact]
    public void Packfile_DeltaCompressesSimilarObjects()
    {
        string objDir = TestAnvil.TempDir("pfc");
        var rng = new Random(99);
        byte[] baseBuf = new byte[6000];
        rng.NextBytes(baseBuf);

        var objs = new Dictionary<string, byte[]>();
        for (int i = 0; i < 25; i++)
        {
            byte[] c = (byte[])baseBuf.Clone();
            c[i] ^= 0x5A;
            objs[Hash(c)] = c;
        }
        List<string> ordered = objs.Keys.OrderBy(h => h, StringComparer.Ordinal).ToList();
        string id = Packfile.Write(objDir, ordered, h => objs[h])!;

        long packSize = new FileInfo(Path.Combine(objDir, "pack", $"pack-{id}.pack")).Length;
        long naive = objs.Values.Sum(c => (long)Deflate(c).Length); // if each stored whole
        Assert.True(packSize < naive / 2, $"pack {packSize} not < half of naive {naive}");
    }

    // ---- gc repack ----

    [Fact]
    public void Gc_Repack_PacksReachable_PrunesUnreachable_AndStaysReadable()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rp"));
        string c0 = CommitWorld(repo, World("a", 1), "c0");
        string c1 = CommitWorld(repo, World("a", 2), "c1");
        string orphan = repo.Objects.Write([42, 42, 42, 42]); // unreachable

        Gc.RepackResult r = Gc.Repack(repo);
        Assert.True(r.Packed > 0);
        Assert.NotNull(r.PackId);
        Assert.Empty(repo.Objects.LooseHashes());            // everything moved into the pack
        Assert.False(repo.Objects.Exists(orphan));            // unreachable dropped

        // Reachable history is intact and readable from the pack.
        Assert.True(repo.Objects.Exists(c0));
        Assert.True(repo.Objects.Exists(c1));
        Assert.Equal(c1, repo.HeadCommit());
        Assert.Equal("c1", repo.ReadCommit(c1).Message);
        Assert.True(repo.Objects.VerifyIntegrity(c1));
        Assert.True(Fsck.Check(repo).Ok);
    }

    [Fact]
    public void Gc_Repack_Then_Checkout_ReproducesWorld()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rpc"));
        string world = TestAnvil.TempDir("w");
        TestAnvil.WriteRegion(Path.Combine(world, "region", "r.0.0.mca"),
            (new ChunkPos(0, 0), AB(1, 10)), (new ChunkPos(1, 0), AB(2, 20)));
        TestAnvil.WriteLoose(Path.Combine(world, "level.dat"), TestAnvil.Root(new NbtCompound("Data") { new NbtLong("Time", 5) }));
        string c = CommitWorld(repo, world, "init");

        Gc.Repack(repo);

        string outDir = TestAnvil.TempDir("co");
        Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(c).Tree), outDir);
        Assert.False(WorldDiffer.Diff(outDir, world, new DiffRunOptions()).HasDifferences);
    }

    [Fact]
    public void Gc_Repack_PreservesAnnotatedTags()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rpt"));
        string c = CommitWorld(repo, World("a", 1), "c0");
        repo.WriteAnnotatedTag(new TagObject { Object = c, Tag = "v1", Tagger = "t", Message = "release" });

        Gc.Repack(repo);

        Assert.Equal(c, repo.ResolveRef("v1"));               // tag object survived in the pack
        Assert.Equal("release", repo.ReadAnnotatedTag("v1")!.Message);
        Assert.True(Fsck.Check(repo).Ok);
    }

    [Fact]
    public void Clone_FromPackedRepo_CopiesObjects()
    {
        Repository a = Repository.Init(TestAnvil.TempDir("pa"));
        CommitWorld(a, World("a", 1), "c1");
        string c2 = CommitWorld(a, World("a", 2), "c2");
        Gc.Repack(a); // source now stores everything packed

        string bDir = TestAnvil.TempDir("pb");
        RemoteOps.Clone(a.Dir, bDir, null);
        Repository b = Repository.Open(bDir);
        Assert.Equal(c2, b.ReadBranch("main"));
        Assert.True(b.Objects.Exists(c2));
        Assert.Equal("c2", b.ReadCommit(c2).Message); // readable end-to-end after packed transfer
    }

    [Fact]
    public void ReadRaw_FromPacked_RoundTripsThroughImportRaw()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("raw"));
        string c = CommitWorld(repo, World("a", 1), "c0");
        Gc.Repack(repo);

        byte[] raw = repo.Objects.ReadRaw(c);                 // recompressed from the pack
        var dst = new ObjectStore(TestAnvil.TempDir("rawdst"));
        dst.ImportRaw(c, raw);                                 // verifies hash on import
        Assert.Equal(repo.Objects.Read(c), dst.Read(c));
    }

    // ---- helpers ----

    private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] Deflate(byte[] content)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(content);
        return ms.ToArray();
    }

    private static NbtCompound AB(int a, int b) =>
        TestAnvil.Root(new NbtInt("DataVersion", 3953), new NbtInt("a", a), new NbtInt("b", b));

    private static string World(string tag, int v)
    {
        string dir = TestAnvil.TempDir("w-" + tag);
        TestAnvil.WriteRegion(Path.Combine(dir, "region", "r.0.0.mca"), (new ChunkPos(0, 0), AB(v, 10)));
        return dir;
    }

    private static string CommitWorld(Repository repo, string worldDir, string msg)
    {
        Manifest m = Snapshotter.Snapshot(repo, worldDir);
        string? head = repo.HeadCommit();
        return repo.CreateCommit(repo.WriteManifest(m), head is null ? [] : [head], msg, "test");
    }
}
