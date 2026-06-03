namespace McaDiff.Repo;

public sealed record StatusEntry(string Path, string Change, string? Detail = null);

/// <summary>
/// Compares a working world directory against the manifest at HEAD, by content
/// hash — no full NBT diff, so it's fast.
/// </summary>
public static class StatusCalc
{
    public static List<StatusEntry> Compute(Repository repo, string worldDir)
    {
        Manifest work = Snapshotter.HashOnly(worldDir);
        Manifest head = HeadManifest(repo);

        var entries = new List<StatusEntry>();

        // Region files: compare per-chunk maps.
        foreach (string rel in Union(work.Regions.Keys, head.Regions.Keys))
        {
            bool inW = work.Regions.TryGetValue(rel, out var w);
            bool inH = head.Regions.TryGetValue(rel, out var h);
            if (inW && !inH) entries.Add(new StatusEntry(rel, "added", $"{w!.Count} chunks"));
            else if (!inW && inH) entries.Add(new StatusEntry(rel, "removed", $"{h!.Count} chunks"));
            else if (!ChunksEqual(w!, h!)) entries.Add(new StatusEntry(rel, "modified", $"{ChangedChunks(w!, h!)} chunks"));
        }

        AddScalar(entries, work.Nbt, head.Nbt);
        AddScalar(entries, work.Blobs, head.Blobs);

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    private static Manifest HeadManifest(Repository repo)
        => repo.HeadCommit() is { } c ? repo.ReadManifest(repo.ReadCommit(c).Tree) : new Manifest();

    private static void AddScalar(List<StatusEntry> entries, IDictionary<string, string> work, IDictionary<string, string> head)
    {
        foreach (string rel in Union(work.Keys, head.Keys))
        {
            bool inW = work.TryGetValue(rel, out string? w);
            bool inH = head.TryGetValue(rel, out string? h);
            if (inW && !inH) entries.Add(new StatusEntry(rel, "added"));
            else if (!inW && inH) entries.Add(new StatusEntry(rel, "removed"));
            else if (w != h) entries.Add(new StatusEntry(rel, "modified"));
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
