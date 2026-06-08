using System.Text.RegularExpressions;
using McaGit.Diff;

namespace McaGit.Query;

/// <summary>One block that changed between two snapshots, at absolute coords.</summary>
public sealed record BlockChange(int X, int Y, int Z, string Old, string New, string Kind);

/// <summary>An aggregate "what happened here" report over a world diff: destruction / construction /
/// replacement counts, the bounding box + centre of the destruction, and the most-destroyed blocks.</summary>
public sealed record GriefSummary(
    int Destroyed, int Built, int Replaced,
    (int X, int Y, int Z)? Min, (int X, int Y, int Z)? Max, (int X, int Y, int Z)? Center,
    IReadOnlyList<(string Block, int Count)> TopDestroyed,
    IReadOnlyList<BlockChange> Events);

/// <summary>
/// Turns the coordinate-level block diff (<see cref="BlockDiff"/>, surfaced as
/// <c>sections[Y].block_states[@x,y,z]</c> changes) into a spatial answer to "where did the griefing
/// occur?" — classify each changed block, then aggregate. Pure post-processing of a <see cref="WorldDiff"/>.
/// </summary>
public static partial class GriefReport
{
    private static readonly HashSet<string> Airs = new(StringComparer.Ordinal)
    { "minecraft:air", "minecraft:cave_air", "minecraft:void_air" };

    public static GriefSummary Analyze(WorldDiff diff)
    {
        var events = new List<BlockChange>();
        foreach (FileDiff f in diff.Files)
        {
            if (!f.Category.Equals("region", StringComparison.Ordinal)) continue;
            foreach (ChunkDiff c in f.Chunks)
                foreach (NbtChange ch in c.Changes)
                {
                    if (BlockPath().Match(ch.Path) is not { Success: true } m) continue;
                    int secY = int.Parse(m.Groups[1].Value);
                    int lx = int.Parse(m.Groups[2].Value), ly = int.Parse(m.Groups[3].Value), lz = int.Parse(m.Groups[4].Value);
                    string oldB = ch.OldValue ?? "?", newB = ch.NewValue ?? "?";
                    events.Add(new BlockChange(
                        c.Pos.X * 16 + lx, secY * 16 + ly, c.Pos.Z * 16 + lz, oldB, newB, Classify(oldB, newB)));
                }
        }

        var destroyed = events.Where(e => e.Kind == "destroyed").ToList();
        (int, int, int)? min = null, max = null, center = null;
        if (destroyed.Count > 0)
        {
            min = (destroyed.Min(e => e.X), destroyed.Min(e => e.Y), destroyed.Min(e => e.Z));
            max = (destroyed.Max(e => e.X), destroyed.Max(e => e.Y), destroyed.Max(e => e.Z));
            center = ((min.Value.Item1 + max.Value.Item1) / 2, (min.Value.Item2 + max.Value.Item2) / 2, (min.Value.Item3 + max.Value.Item3) / 2);
        }
        var top = destroyed.GroupBy(e => e.Old)
            .Select(g => (Block: g.Key, Count: g.Count())).OrderByDescending(t => t.Count).Take(5).ToList();

        return new GriefSummary(
            destroyed.Count, events.Count(e => e.Kind == "built"), events.Count(e => e.Kind == "replaced"),
            min, max, center, top, events);
    }

    private static string Classify(string oldB, string newB)
    {
        bool oa = Airs.Contains(oldB), na = Airs.Contains(newB);
        return oa && !na ? "built" : !oa && na ? "destroyed" : "replaced";
    }

    [GeneratedRegex(@"^sections\[(-?\d+)\]\.block_states\[@(\d+),(\d+),(\d+)\]$")]
    private static partial Regex BlockPath();
}
