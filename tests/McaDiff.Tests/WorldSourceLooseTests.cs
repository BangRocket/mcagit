using fNbt;
using McaDiff.Diff;
using McaDiff.Patch;
using Xunit;

namespace McaDiff.Tests;

/// <summary>Loose-file coverage gaps from #17: custom structures (data/*.nbt) diff as NBT;
/// advancements/stats JSON and .mcc are byte-compared as blobs (not NBT-parsed → no crash) and
/// are skipped by patch extraction.</summary>
public class WorldSourceLooseTests
{
    [Fact]
    public void DataNbt_DiffedAsNbt()
    {
        string a = World(w => TestAnvil.WriteLoose(Path.Combine(w, "data", "s.nbt"),
            TestAnvil.Root(new NbtCompound("") { new NbtInt("Size", 5) })));
        string b = World(w => TestAnvil.WriteLoose(Path.Combine(w, "data", "s.nbt"),
            TestAnvil.Root(new NbtCompound("") { new NbtInt("Size", 9) })));

        WorldDiff diff = WorldDiffer.Diff(a, b, new DiffRunOptions());
        FileDiff f = Assert.Single(diff.Files);
        Assert.Equal("data/s.nbt", f.RelativePath);
        Assert.Equal("nbt", f.Category);
        Assert.Contains(f.Changes, c => c.Path.Contains("Size")); // real NBT-level change
    }

    [Fact]
    public void AdvancementsJson_DiffedAsBlob_NoCrash()
    {
        string a = World(w => WriteText(Path.Combine(w, "advancements", "p.json"), "{}"));
        string b = World(w => WriteText(Path.Combine(w, "advancements", "p.json"), "{\"done\":true}"));

        WorldDiff diff = WorldDiffer.Diff(a, b, new DiffRunOptions()); // must not throw NBT-parsing JSON
        FileDiff f = Assert.Single(diff.Files);
        Assert.Equal("advancements/p.json", f.RelativePath);
        Assert.Equal("blob", f.Category);
        Assert.Equal(DiffStatus.Modified, f.Status);
    }

    [Fact]
    public void MccFile_SingleFileDiff_IsBlobNotNbt()
    {
        string dir = TestAnvil.TempDir("mcc");
        string a = Path.Combine(dir, "c.0.0.mcc"), b = Path.Combine(dir, "c.0.0.b.mcc");
        File.WriteAllBytes(a, [1, 2, 3, 4]);
        File.WriteAllBytes(b, [1, 2, 3, 9]);

        WorldDiff diff = WorldDiffer.Diff(a, b, new DiffRunOptions()); // single-file path, must not NBT-parse
        Assert.Equal("blob", Assert.Single(diff.Files).Category);
    }

    [Fact]
    public void Extract_SkipsBlobs()
    {
        string a = World(w => WriteText(Path.Combine(w, "advancements", "p.json"), "{}"));
        string b = World(w => WriteText(Path.Combine(w, "advancements", "p.json"), "{\"done\":true}"));

        WorldPatch patch = PatchExtractor.Extract(a, b, new DiffRunOptions()); // must not throw
        Assert.Empty(patch.Files); // a JSON blob isn't representable as node ops
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string World(Action<string> populate)
    {
        string dir = TestAnvil.TempDir("wsl");
        Directory.CreateDirectory(dir);
        populate(dir);
        return dir;
    }
}
