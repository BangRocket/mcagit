namespace McaDiff.Repo;

/// <summary>
/// Verifies repository integrity: every stored object decompresses and re-hashes to
/// its name, every object referenced from a ref/commit/tree is present, and objects
/// reachable from no ref are reported as dangling (git's <c>fsck</c>).
/// </summary>
public static class Fsck
{
    public sealed record Report(int Checked, List<string> Corrupt, List<string> Missing,
                                List<string> DanglingCommits, int Unreachable)
    {
        public bool Ok => Corrupt.Count == 0 && Missing.Count == 0;
    }

    public static Report Check(Repository repo)
    {
        var all = repo.Objects.AllHashes().ToHashSet(StringComparer.Ordinal);

        // 1. Integrity — does each object still hash to its name?
        var corrupt = new List<string>();
        foreach (string h in all)
            if (!repo.Objects.VerifyIntegrity(h)) corrupt.Add(h);

        // 2. Reachability — walk every ref down through commits and trees.
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var queue = new Queue<(string Hash, string Referrer)>();

        void Enqueue(string commit, string referrer)
        {
            if (!repo.Objects.Exists(commit)) { missing.Add($"{commit} (from {referrer})"); return; }
            if (reachable.Add(commit)) queue.Enqueue((commit, referrer));
        }

        foreach (string br in repo.Branches())
            if (repo.ReadBranch(br) is { } h) Enqueue(h, $"branch {br}");
        foreach (string tg in repo.Tags())
            if (repo.ReadTag(tg) is { } h) { reachable.Add(h); Enqueue(repo.PeelToCommit(h), $"tag {tg}"); }
        foreach ((string name, string h) in RemoteRefs(repo)) Enqueue(h, $"remote {name}");
        if (repo.HeadCommit() is { } head) Enqueue(head, "HEAD");
        foreach (string line in repo.Reflog())
        {
            string[] parts = line.Split(' ', 3);
            if (parts.Length > 1 && parts[1].Length == 64) Enqueue(parts[1], "reflog");
        }

        while (queue.Count > 0)
        {
            (string c, string referrer) = queue.Dequeue();
            CommitObject commit;
            try { commit = repo.ReadCommit(c); }
            catch { continue; } // reachable but not a commit (e.g. a peeled blob) — nothing to walk

            if (!repo.Objects.Exists(commit.Tree)) missing.Add($"{commit.Tree} (tree of {c[..10]})");
            else if (reachable.Add(commit.Tree))
                foreach (string h in ManifestObjects(repo.ReadManifest(commit.Tree)))
                {
                    if (!repo.Objects.Exists(h)) missing.Add($"{h} (in tree {commit.Tree[..10]})");
                    else reachable.Add(h);
                }

            foreach (string p in commit.Parents) Enqueue(p, $"commit {c[..10]}");
        }

        // 3. Dangling — present but reachable from nothing.
        var danglingCommits = new List<string>();
        int unreachable = 0;
        foreach (string h in all)
            if (!reachable.Contains(h))
            {
                unreachable++;
                if (repo.Classify(h) == Repository.ObjectKind.Commit) danglingCommits.Add(h);
            }

        return new Report(all.Count, corrupt, missing, danglingCommits, unreachable);
    }

    private static IEnumerable<string> ManifestObjects(Manifest m)
    {
        foreach (var r in m.Regions) foreach (var c in r.Value) yield return c.Value;
        foreach (var n in m.Nbt) yield return n.Value;
        foreach (var b in m.Blobs) yield return b.Value;
    }

    private static IEnumerable<(string Name, string Hash)> RemoteRefs(Repository repo)
    {
        string dir = Path.Combine(repo.Dir, "refs", "remotes");
        if (!Directory.Exists(dir)) yield break;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            yield return (Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f).Trim());
    }
}
