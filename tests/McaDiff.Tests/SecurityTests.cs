using fNbt;
using McaDiff.Diff;
using McaDiff.Nbt;
using McaDiff.Patch;
using McaDiff.Repo;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Regression tests for the issue #3 audit: path traversal (manifest/patch/refs),
/// object-id validation, and the NBT recursion-depth guard.</summary>
public class SecurityTests
{
    // ---- path confinement (Critical 1) ----

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("a/../../escape.txt")]
    public void PathGuard_RejectsEscapes(string rel)
        => Assert.Throws<InvalidDataException>(() => PathGuard.Confine(TestAnvil.TempDir("pg"), rel));

    [Fact]
    public void PathGuard_AllowsNestedPaths()
    {
        string root = TestAnvil.TempDir("pg2");
        Assert.StartsWith(Path.GetFullPath(root), PathGuard.Confine(root, "a/b/c.dat"));
    }

    [Fact]
    public void PathGuard_RejectsNtfsAlternateDataStream_OnWindows()
    {
        string root = TestAnvil.TempDir("pg3");
        if (OperatingSystem.IsWindows())
            Assert.Throws<InvalidDataException>(() => PathGuard.Confine(root, "session.json:hidden")); // ADS (issue #25)
        else
            Assert.StartsWith(Path.GetFullPath(root), PathGuard.Confine(root, "a:b.dat")); // ':' is a valid char on Linux
    }

    [Fact]
    public void Checkout_RejectsTraversalManifest()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("co"));
        string world = TestAnvil.TempDir("cow");
        var m = new Manifest();
        m.Blobs["../../ESCAPED.txt"] = new string('a', 64); // confinement trips before any object read

        Assert.Throws<InvalidDataException>(() => Checkout.Materialize(repo, m, world));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(world)!.FullName, "ESCAPED.txt")));
    }

    [Fact]
    public void PatchApply_RejectsTraversalPath()
    {
        string target = TestAnvil.TempDir("pt");
        File.WriteAllText(Path.Combine(target, "level.dat"), "x"); // make it look like a world dir
        var patch = new WorldPatch();
        patch.Files.Add(new PatchFileEntry { Path = "../../ESCAPED.dat", Kind = UnitKind.Loose, Ops = [] });

        Assert.Throws<InvalidDataException>(() =>
            PatchApplier.Apply(patch, target, target, new ApplySettings(DryRun: true)));
    }

    // ---- object-id validation (Critical 2, read primitive) ----

    [Theory]
    [InlineData("..config")]        // no slash → bypasses URL normalization, hits PathFor
    [InlineData("../../config")]
    [InlineData("not-a-hash")]
    [InlineData("")]
    public void ObjectStore_RejectsNonHashIds(string bad)
    {
        var store = new ObjectStore(TestAnvil.TempDir("os"));
        Assert.False(ObjectStore.IsValidHash(bad));
        Assert.False(store.Exists(bad));                            // no file-existence oracle
        Assert.Equal(0, store.Delete(bad));
        Assert.Throws<InvalidDataException>(() => store.Read(bad)); // no traversal read
    }

    [Fact]
    public void ObjectStore_AcceptsRealHash()
    {
        var store = new ObjectStore(TestAnvil.TempDir("os2"));
        string h = store.Write([1, 2, 3]);
        Assert.True(ObjectStore.IsValidHash(h));
        Assert.True(store.Exists(h));
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Read(h));
    }

    // ---- ref-name confinement (Critical 2, write primitive) ----

    [Theory]
    [InlineData("../../MERGE_HEAD")]
    [InlineData("../config")]
    public void Repository_RejectsTraversalRefNames(string name)
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rf"));
        Assert.Throws<InvalidDataException>(() => repo.WriteBranch(name, new string('a', 64)));
        Assert.Throws<InvalidDataException>(() => repo.WriteTag(name, new string('a', 64)));
    }

    [Fact]
    public void Repository_AllowsSlashedBranchNames()
    {
        Repository repo = Repository.Init(TestAnvil.TempDir("rf2"));
        repo.WriteBranch("feature/foo", new string('a', 64)); // legit nested ref name stays in refs/heads
        Assert.Equal(new string('a', 64), repo.ReadBranch("feature/foo"));
    }

    // ---- NBT recursion-depth guard (High 3) ----

    [Fact]
    public void NbtCanonical_RejectsExcessiveNesting()
        => Assert.Throws<InvalidDataException>(() => NbtCanonical.Serialize(Nest(700))); // catchable, not a StackOverflow

    [Fact]
    public void NbtCanonical_AllowsRealisticNesting()
        => Assert.True(NbtCanonical.Serialize(Nest(20)).Length > 0);

    private static NbtCompound Nest(int depth)
    {
        var root = new NbtCompound("");
        NbtCompound cur = root;
        for (int i = 0; i < depth; i++) { var next = new NbtCompound("c"); cur.Add(next); cur = next; }
        return root;
    }
}
