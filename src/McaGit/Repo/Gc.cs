namespace McaGit.Repo;

/// <summary>
/// Garbage collection: prune objects unreachable from any ref, and repack the
/// reachable ones into a single delta-compressed packfile.
/// </summary>
public static class Gc
{
    public sealed record Result(int Pruned, long BytesFreed, int Kept);

    public sealed record RepackResult(int Packed, int Pruned, long BytesFreed, string? PackId);

    /// <summary>Deletes loose objects unreachable from any ref (no repacking).</summary>
    public static Result Prune(Repository repo)
    {
        HashSet<string> reachable = ReachableSet(repo);

        int pruned = 0;
        long freed = 0;
        foreach (string hash in repo.Objects.LooseHashes().ToList()) // materialize: we delete while iterating
        {
            if (reachable.Contains(hash)) continue;
            freed += repo.Objects.Delete(hash);
            pruned++;
        }
        return new Result(pruned, freed, reachable.Count);
    }

    /// <summary>
    /// Packs every reachable object into one new pack, then removes the old packs and all
    /// loose objects (reachable ones now live in the pack; unreachable ones are dropped).
    /// </summary>
    public static RepackResult Repack(Repository repo)
    {
        ObjectStore store = repo.Objects;

        // Sweep stale temp files left by crashed writers (invisible to fsck; they only waste space).
        foreach (string tmp in Directory.EnumerateFiles(store.ObjectsDir, "*.tmp", SearchOption.AllDirectories))
            try { File.Delete(tmp); } catch { /* in use / already gone */ }

        HashSet<string> reachable = ReachableSet(repo);
        if (reachable.Count == 0) return new RepackResult(0, 0, 0, null);

        // Order by a cheap size proxy so similar (often identical-length) objects sit
        // adjacent and delta well; content is loaded on demand inside the writer.
        List<string> ordered = reachable
            .OrderByDescending(store.StoredSize)
            .ThenBy(h => h, StringComparer.Ordinal)
            .ToList();

        List<string> oldPacks = store.PackFilePaths().ToList();
        string? packId = Packfile.Write(store.ObjectsDir, ordered, store.Read);
        store.ReloadPacks(); // the new pack is now visible

        long freed = 0;
        string? newPackPath = packId is null ? null
            : Path.Combine(store.ObjectsDir, "pack", $"pack-{packId}.pack");
        foreach (string old in oldPacks)
            if (old != newPackPath) freed += store.DeletePack(old);
        store.ReloadPacks();

        // Every reachable object is now in the pack; drop all loose objects (reachable or not).
        int packed = 0, pruned = 0;
        foreach (string hash in store.LooseHashes().ToList())
        {
            freed += store.Delete(hash);
            if (reachable.Contains(hash)) packed++; else pruned++;
        }
        return new RepackResult(packed, pruned, freed, packId);
    }

    private static HashSet<string> ReachableSet(Repository repo)
    {
        var reachable = new HashSet<string>();
        // Annotated tag objects are reachable in their own right (their ref points at them,
        // not at the commit) — keep them so refs/tags/* don't dangle after a repack.
        foreach (string t in repo.Tags())
            if (repo.ReadTag(t) is { } h && repo.Objects.Exists(h)) reachable.Add(h);
        foreach (string tip in AllTips(repo))
            Transfer.CollectReachable(repo, tip, reachable);
        return reachable;
    }

    private static IEnumerable<string> AllTips(Repository repo)
    {
        var tips = new HashSet<string>();
        foreach (string b in repo.Branches()) if (repo.ReadBranch(b) is { } h) tips.Add(h);
        foreach (string t in repo.Tags()) if (repo.ReadTag(t) is { } h) tips.Add(repo.PeelToCommit(h));
        foreach (string line in repo.Reflog())                     // keep reflog-reachable history
        {
            string[] parts = line.Split(' ', 3);
            if (parts.Length > 1 && parts[1].Length == 64 && repo.Objects.Exists(parts[1])) tips.Add(parts[1]);
        }
        foreach (string s in Stash.Stack(repo)) tips.Add(s);       // keep stashed snapshots
        if (repo.HeadCommit() is { } head) tips.Add(head);
        return tips;
    }
}
