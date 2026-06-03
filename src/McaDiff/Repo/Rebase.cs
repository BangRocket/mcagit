namespace McaDiff.Repo;

/// <summary>
/// Non-interactive rebase: replays the current branch's commits (those not already in
/// <c>upstream</c>) onto a new base via the 3-way engine, then moves the branch.
/// Supports <c>--onto</c>. Same-node clashes keep the replayed change and are reported.
/// </summary>
public static class Rebase
{
    public sealed record Result(bool UpToDate, bool FastForward, int Replayed, List<MergeConflict> Conflicts, string? NewTip);

    public static Result Run(Repository repo, string upstreamRef, string? ontoRef, string author)
    {
        string branch = repo.CurrentBranch() ?? throw new InvalidOperationException("rebase requires being on a branch");
        string upstream = repo.ResolveRef(upstreamRef);
        string onto = ontoRef is not null ? repo.ResolveRef(ontoRef) : upstream;
        string head = repo.ReadBranch(branch) ?? throw new InvalidOperationException("branch has no commits");

        // Branch entirely behind upstream → fast-forward.
        if (Transfer.IsAncestor(repo, head, upstream))
        {
            repo.WriteBranch(branch, upstream);
            MoveWorktree(repo, upstream);
            return new Result(false, true, 0, [], upstream);
        }
        // Already on top of upstream and no relocation requested → nothing to do.
        if (ontoRef is null && MergeBase.Find(repo, head, upstream) == upstream)
            return new Result(true, false, 0, [], head);

        // Commits to replay: head's first-parent line down to (but excluding) upstream's ancestry.
        HashSet<string> upAnc = AncestorsIncl(repo, upstream);
        var toReplay = new List<string>();
        for (string? c = head; c is not null && !upAnc.Contains(c);)
        {
            toReplay.Add(c);
            List<string> parents = repo.ReadCommit(c).Parents;
            c = parents.Count > 0 ? parents[0] : null;
        }
        toReplay.Reverse(); // oldest first

        var conflicts = new List<MergeConflict>();
        string newBase = onto;
        foreach (string c in toReplay)
        {
            CommitObject cc = repo.ReadCommit(c);
            string? p = cc.Parents.Count > 0 ? cc.Parents[0] : null;
            Manifest baseM = p is not null ? repo.ReadManifest(repo.ReadCommit(p).Tree) : new Manifest();
            Manifest oursM = repo.ReadManifest(repo.ReadCommit(newBase).Tree);
            Manifest theirsM = repo.ReadManifest(cc.Tree);

            Manifest merged = Merger.MergeManifests(repo, baseM, oursM, theirsM, preferTheirs: true, conflicts);
            string tree = repo.WriteManifest(merged);
            // Replayed commit: preserve original author/date, record the rebaser as committer.
            // WriteCommit (not CreateCommit) so the branch only moves once, at the end.
            newBase = repo.WriteCommit(new CommitObject
            {
                Tree = tree, Parents = [newBase], Message = cc.Message,
                Author = cc.Author, Time = cc.Time,
                Committer = author, CommitTime = DateTimeOffset.Now.ToString("o"),
            });
        }

        repo.WriteBranch(branch, newBase);
        repo.RecordHead(head, newBase, $"rebase onto {onto[..10]}");
        MoveWorktree(repo, newBase);
        return new Result(false, false, toReplay.Count, conflicts, newBase);
    }

    private static void MoveWorktree(Repository repo, string commit)
    {
        if (repo.Worktree is { } w)
            Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(commit).Tree), w, prune: true);
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
