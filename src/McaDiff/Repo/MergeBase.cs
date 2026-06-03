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
            foreach (string p in repo.ReadCommit(h).Parents) stack.Push(p);
        }

        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(b);
        while (queue.Count > 0)
        {
            string h = queue.Dequeue();
            if (!seen.Add(h)) continue;
            if (ancestorsOfA.Contains(h)) return h;
            foreach (string p in repo.ReadCommit(h).Parents) queue.Enqueue(p);
        }
        return null;
    }
}
