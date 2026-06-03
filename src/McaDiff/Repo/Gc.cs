namespace McaDiff.Repo;

/// <summary>Garbage collection: prune objects unreachable from any ref.</summary>
public static class Gc
{
    public sealed record Result(int Pruned, long BytesFreed, int Kept);

    public static Result Prune(Repository repo)
    {
        var reachable = new HashSet<string>();
        foreach (string tip in AllTips(repo))
            Transfer.CollectReachable(repo, tip, reachable);

        int pruned = 0;
        long freed = 0;
        foreach (string hash in repo.Objects.AllHashes().ToList()) // materialize: we delete while iterating
        {
            if (reachable.Contains(hash)) continue;
            freed += repo.Objects.Delete(hash);
            pruned++;
        }
        return new Result(pruned, freed, reachable.Count);
    }

    private static IEnumerable<string> AllTips(Repository repo)
    {
        var tips = new HashSet<string>();
        foreach (string b in repo.Branches()) if (repo.ReadBranch(b) is { } h) tips.Add(h);
        foreach (string t in repo.Tags()) if (repo.ReadTag(t) is { } h) tips.Add(h);
        if (repo.HeadCommit() is { } head) tips.Add(head);
        return tips;
    }
}
