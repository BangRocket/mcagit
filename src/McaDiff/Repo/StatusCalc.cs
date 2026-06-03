namespace McaDiff.Repo;

public sealed record StatusEntry(string Path, string Change, string? Detail = null);

/// <summary>
/// Compares a working world directory against the manifest at HEAD, by content
/// hash — no full NBT diff, so it's fast.
/// </summary>
public static class StatusCalc
{
    public static List<StatusEntry> Compute(Repository repo, string worldDir)
        => Compute(HeadManifest(repo), Snapshotter.HashOnly(repo, worldDir));

    /// <summary>Status of <paramref name="to"/> relative to <paramref name="from"/>
    /// (added = present in <c>to</c> only, removed = in <c>from</c> only).</summary>
    public static List<StatusEntry> Compute(Manifest from, Manifest to)
    {
        var entries = new List<StatusEntry>();

        // Region files: compare per-chunk maps.
        foreach (string rel in Union(to.Regions.Keys, from.Regions.Keys))
        {
            bool inT = to.Regions.TryGetValue(rel, out var t);
            bool inF = from.Regions.TryGetValue(rel, out var f);
            if (inT && !inF) entries.Add(new StatusEntry(rel, "added", $"{t!.Count} chunks"));
            else if (!inT && inF) entries.Add(new StatusEntry(rel, "removed", $"{f!.Count} chunks"));
            else if (!ChunksEqual(t!, f!)) entries.Add(new StatusEntry(rel, "modified", $"{ChangedChunks(t!, f!)} chunks"));
        }

        AddScalar(entries, to.Nbt, from.Nbt);
        AddScalar(entries, to.Blobs, from.Blobs);

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    private static Manifest HeadManifest(Repository repo)
        => repo.HeadCommit() is { } c ? repo.ReadManifest(repo.ReadCommit(c).Tree) : new Manifest();

    private static void AddScalar(List<StatusEntry> entries, IDictionary<string, string> to, IDictionary<string, string> from)
    {
        foreach (string rel in Union(to.Keys, from.Keys))
        {
            bool inT = to.TryGetValue(rel, out string? t);
            bool inF = from.TryGetValue(rel, out string? f);
            if (inT && !inF) entries.Add(new StatusEntry(rel, "added"));
            else if (!inT && inF) entries.Add(new StatusEntry(rel, "removed"));
            else if (t != f) entries.Add(new StatusEntry(rel, "modified"));
        }
    }

    private static IEnumerable<string> Union(IEnumerable<string> a, IEnumerable<string> b)
    {
        var set = new HashSet<string>(a, StringComparer.Ordinal);
        set.UnionWith(b);
        return set;
    }

    private static bool ChunksEqual(IDictionary<string, string> a, IDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out string? v) && v == kv.Value);

    private static int ChangedChunks(IDictionary<string, string> a, IDictionary<string, string> b)
    {
        var keys = new HashSet<string>(a.Keys, StringComparer.Ordinal);
        keys.UnionWith(b.Keys);
        return keys.Count(k => !(a.TryGetValue(k, out string? va) & b.TryGetValue(k, out string? vb)) || va != vb);
    }
}
