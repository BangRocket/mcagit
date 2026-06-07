using fNbt;
using McaGit.Diff;
using Xunit;

namespace McaGit.Tests;

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
    public void JsonAndMcc_SingleFileDiff_AreBlobs_NotNbtParsed()
    {
        // World-level enumeration skips non-NBT files (the v1 patch can't carry a blob), but a
        // single-file diff of JSON / .mcc still works as a byte compare — and must not NBT-parse them.
        WorldDiff json = WorldDiffer.Diff(File4("p.json", [123, 125]), File4("p.json", [123, 49, 125]), Opt());
        Assert.Equal("blob", Assert.Single(json.Files).Category);

        WorldDiff mcc = WorldDiffer.Diff(File4("c.0.0.mcc", [1, 2, 3, 4]), File4("c.0.0.mcc", [1, 2, 3, 9]), Opt());
        Assert.Equal("blob", Assert.Single(mcc.Files).Category);
    }

    [Fact]
    public void AdvancementsJson_NotEnumeratedAtWorldLevel()
    {
        // The whole-world diff doesn't surface JSON (it would break the patch round-trip).
        string a = World(w => WriteText(Path.Combine(w, "advancements", "p.json"), "{}"));
        string b = World(w => WriteText(Path.Combine(w, "advancements", "p.json"), "{\"done\":true}"));
        Assert.False(WorldDiffer.Diff(a, b, new DiffRunOptions()).HasDifferences);
    }

    private static DiffRunOptions Opt() => new();

    private static string File4(string name, byte[] bytes)
    {
        string dir = TestAnvil.TempDir("wsf");
        string p = Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
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
