namespace McaDiff.Repo;

/// <summary>Walks the commit DAG to copy or enumerate the objects a commit reaches.</summary>
public static class Transfer
{
    /// <summary>
    /// Copies every object reachable from <paramref name="commit"/> in
    /// <paramref name="src"/> into <paramref name="dst"/>, pruning history the
    /// destination already has. Returns the number of objects copied.
    /// </summary>
    public static int CopyReachable(Repository src, ObjectStore dst, string commit)
    {
        int copied = 0;
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(commit);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!seen.Add(h)) continue;

            copied += Import(dst, src.Objects, h);                 // commit object
            CommitObject c = src.ReadCommit(h);
            copied += Import(dst, src.Objects, c.Tree);            // manifest
            foreach (string objHash in ManifestObjects(src.ReadManifest(c.Tree)))
                copied += Import(dst, src.Objects, objHash);

            foreach (string p in c.Parents)
                if (!dst.Exists(p)) stack.Push(p);                  // prune shared history
        }
        return copied;
    }

    /// <summary>Adds every object hash reachable from <paramref name="commit"/> to <paramref name="into"/>.</summary>
    public static void CollectReachable(Repository repo, string commit, HashSet<string> into)
    {
        var stack = new Stack<string>();
        stack.Push(commit);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!into.Add(h)) continue;
            CommitObject c = repo.ReadCommit(h);
            into.Add(c.Tree);
            foreach (string objHash in ManifestObjects(repo.ReadManifest(c.Tree))) into.Add(objHash);
            foreach (string p in c.Parents) stack.Push(p);
        }
    }

    /// <summary>True if <paramref name="ancestor"/> is an ancestor of (or equals) <paramref name="descendant"/>.</summary>
    public static bool IsAncestor(Repository repo, string ancestor, string descendant)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(descendant);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (h == ancestor) return true;
            if (!seen.Add(h)) continue;
            foreach (string p in repo.ReadCommit(h).Parents) stack.Push(p);
        }
        return false;
    }

    private static IEnumerable<string> ManifestObjects(Manifest m)
    {
        foreach (var region in m.Regions.Values)
            foreach (string hash in region.Values) yield return hash;
        foreach (string hash in m.Nbt.Values) yield return hash;
        foreach (string hash in m.Blobs.Values) yield return hash;
    }

    private static int Import(ObjectStore dst, ObjectStore src, string hash)
    {
        if (dst.Exists(hash)) return 0;
        dst.Import(src, hash);
        return 1;
    }
}
