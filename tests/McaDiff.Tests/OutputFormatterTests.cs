using System.Text.Json;
using McaDiff.Anvil;
using McaDiff.Diff;
using McaDiff.Output;
using Xunit;

namespace McaDiff.Tests;

/// <summary>The diff renderers (#19). The JSON formatter is a scripting contract — a renamed
/// property or enum case would silently break callers — so these pin its shape directly from a
/// synthetic WorldDiff (no TestAnvil needed).</summary>
public class OutputFormatterTests
{
    private static WorldDiff Sample() => new("worldA", "worldB",
    [
        new FileDiff("region/r.0.0.mca", "region", UnitKind.Region, DiffStatus.Modified,
        [
            new ChunkDiff(new ChunkPos(0, 0), DiffStatus.Modified,
            [
                new NbtChange("Level.xPos", ChangeKind.Modified, "0", "1"),
            ]),
        ], []),
        new FileDiff("level.dat", "loose", UnitKind.Loose, DiffStatus.Modified, [],
        [
            new NbtChange("Data.Time", ChangeKind.Modified, "5", "9"),
        ]),
    ]);

    [Fact]
    public void Json_HasStableContract_AndCorrectCounts()
    {
        var sw = new StringWriter();
        JsonDiffFormatter.Write(Sample(), sw);
        using JsonDocument doc = JsonDocument.Parse(sw.ToString());
        JsonElement root = doc.RootElement;

        Assert.Equal("worldA", root.GetProperty("worldA").GetString());
        Assert.Equal("worldB", root.GetProperty("worldB").GetString());

        JsonElement summary = root.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("filesChanged").GetInt32());
        Assert.Equal(1, summary.GetProperty("chunksChanged").GetInt32());
        Assert.Equal(2, summary.GetProperty("nbtChanges").GetInt32()); // 1 chunk change + 1 loose change

        JsonElement files = root.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        JsonElement region = files[0];
        Assert.Equal("region/r.0.0.mca", region.GetProperty("path").GetString());
        Assert.Equal("modified", region.GetProperty("status").GetString());   // enum → camelCase string
        Assert.Equal("region", region.GetProperty("kind").GetString());
        JsonElement chunk = region.GetProperty("chunks")[0];
        Assert.Equal(0, chunk.GetProperty("x").GetInt32());
        Assert.Equal("Level.xPos", chunk.GetProperty("changes")[0].GetProperty("path").GetString());
        Assert.Equal("modified", chunk.GetProperty("changes")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Json_NoDifferences_IsValidWithZeroCounts()
    {
        var sw = new StringWriter();
        JsonDiffFormatter.Write(new WorldDiff("a", "b", []), sw);
        using JsonDocument doc = JsonDocument.Parse(sw.ToString());
        Assert.Equal(0, doc.RootElement.GetProperty("summary").GetProperty("filesChanged").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void Text_RendersChangeLines_AndSummarySuppressesThem()
    {
        var full = new StringWriter();
        new TextDiffFormatter(new Ansi(false), summaryOnly: false).Write(Sample(), full);
        string text = full.ToString();
        Assert.Contains("region/r.0.0.mca", text);
        Assert.Contains("Level.xPos", text); // per-change detail present in full mode

        var summary = new StringWriter();
        new TextDiffFormatter(new Ansi(false), summaryOnly: true).Write(Sample(), summary);
        Assert.DoesNotContain("Level.xPos", summary.ToString()); // suppressed in summary mode
    }

    [Fact]
    public void Text_NoDifferences_SaysSo()
    {
        var sw = new StringWriter();
        new TextDiffFormatter(new Ansi(false), summaryOnly: false).Write(new WorldDiff("a", "b", []), sw);
        Assert.Contains("No differences", sw.ToString());
    }
}
