namespace McaDiff.Repo;

/// <summary>
/// Binary search over history for the first bad commit. The suspect set is the
/// commits reachable from <c>bad</c> but exonerated by no <c>good</c>; each step
/// checks out the suspect that most evenly halves that set, until one remains.
/// </summary>
public static class Bisect
{
    public sealed record State(bool NeedMarks, bool Done, string? FirstBad, string? Next, int Remaining);

    public static State Compute(Repository repo)
    {
        string? bad = repo.BisectBad();
        List<string> goods = repo.BisectGood();
        if (bad is null || goods.Count == 0) return new State(NeedMarks: true, false, null, null, 0);

        HashSet<string> suspects = AncestorsIncl(repo, bad);
        foreach (string g in goods) suspects.ExceptWith(AncestorsIncl(repo, g));

        var skip = repo.BisectSkip().ToHashSet(StringComparer.Ordinal);
        var candidates = suspects.Where(c => c != bad && !skip.Contains(c)).ToList();
        if (candidates.Count == 0) return new State(false, Done: true, FirstBad: bad, null, 0);

        // Pick the candidate whose suspect-ancestor count is closest to half the set.
        string best = candidates
            .OrderByDescending(c =>
            {
                int n = CountSuspectAncestors(repo, c, suspects);
                return Math.Min(n, suspects.Count - n);
            })
            .ThenBy(c => c, StringComparer.Ordinal)
            .First();
        return new State(false, false, null, best, candidates.Count);
    }

    private static int CountSuspectAncestors(Repository repo, string c, HashSet<string> suspects)
    {
        HashSet<string> anc = AncestorsIncl(repo, c);
        anc.IntersectWith(suspects);
        return anc.Count;
    }

    private static HashSet<string> AncestorsIncl(Repository repo, string commit)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(commit);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!set.Add(h)) continue;
            foreach (string p in repo.ReadCommit(h).Parents) stack.Push(p);
        }
        return set;
    }
}
