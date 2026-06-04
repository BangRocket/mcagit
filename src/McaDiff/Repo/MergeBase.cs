namespace McaDiff.Repo;

/// <summary>
/// Finds a merge base (common ancestor) of two commits by walking the parent DAG.
/// BFS from <c>b</c> returns the nearest ancestor that is also an ancestor of
/// <c>a</c> — correct for linear and single-merge histories (no criss-cross LCA).
/// </summary>
public static class MergeBase
{
    public static string? Find(Repository repo, string a, string b)
    {
        var ancestorsOfA = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(a);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!ancestorsOfA.Add(h)) continue;
            foreach (string p in repo.ParentsOf(h)) stack.Push(p);
        }

        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(b);
        while (queue.Count > 0)
        {
            string h = queue.Dequeue();
            if (!seen.Add(h)) continue;
            if (ancestorsOfA.Contains(h)) return h;
            foreach (string p in repo.ParentsOf(h)) queue.Enqueue(p);
        }
        return null;
    }

    /// <summary>
    /// All merge bases — the maximal common ancestors (a single commit for linear or
    /// single-merge histories; two or more for a criss-cross). A common ancestor is a
    /// base unless it is itself an ancestor of another common ancestor.
    /// </summary>
    public static List<string> FindAll(Repository repo, string a, string b)
    {
        HashSet<string> ancA = Ancestors(repo, a);
        HashSet<string> ancB = Ancestors(repo, b);
        var common = ancA.Where(ancB.Contains).ToHashSet(StringComparer.Ordinal);
        if (common.Count == 0) return [];

        var bases = new List<string>();
        foreach (string c in common)
            if (!common.Any(d => d != c && IsAncestor(repo, c, d, common)))
                bases.Add(c);
        bases.Sort(StringComparer.Ordinal); // deterministic order
        return bases;
    }

    private static HashSet<string> Ancestors(Repository repo, string commit)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(commit);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!set.Add(h)) continue;
            foreach (string p in repo.ParentsOf(h)) stack.Push(p);
        }
        return set;
    }

    /// <summary>True if <paramref name="anc"/> is an ancestor of <paramref name="desc"/>,
    /// searching only within <paramref name="within"/> (the common-ancestor set).</summary>
    private static bool IsAncestor(Repository repo, string anc, string desc, HashSet<string> within)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(desc);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (h == anc) return true;
            if (!seen.Add(h)) continue;
            foreach (string p in repo.ParentsOf(h)) stack.Push(p);
        }
        return false;
    }
}
