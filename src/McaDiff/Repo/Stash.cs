namespace McaDiff.Repo;

/// <summary>
/// A stack of shelved worktrees. <see cref="Push"/> snapshots the worktree as an
/// (off-ref) commit, stacks it, and resets the worktree to HEAD; <see cref="Apply"/>
/// 3-way-merges a stashed snapshot back onto the current worktree.
/// </summary>
public static class Stash
{
    private static string StackPath(Repository repo) => Path.Combine(repo.Dir, "stash");

    /// <summary>Stash commit hashes, most recent first (index 0 == stash@{0}).</summary>
    public static List<string> Stack(Repository repo) => File.Exists(StackPath(repo))
        ? File.ReadLines(StackPath(repo)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
        : [];

    private static void SaveStack(Repository repo, List<string> stack)
    {
        if (stack.Count == 0) { if (File.Exists(StackPath(repo))) File.Delete(StackPath(repo)); }
        else File.WriteAllLines(StackPath(repo), stack);
    }

    public sealed record PushResult(bool Created, string? Commit);

    public static PushResult Push(Repository repo, string message, string author)
    {
        string world = repo.Worktree ?? throw new InvalidOperationException("stash needs a bound worktree");
        string head = repo.HeadCommit() ?? throw new InvalidOperationException("cannot stash before the first commit");

        Manifest work = Snapshotter.Snapshot(repo, world);
        string tree = repo.WriteManifest(work);
        if (repo.ReadCommit(head).Tree == tree) return new PushResult(false, null); // nothing to stash

        string branch = repo.CurrentBranch() ?? "detached HEAD";
        string stash = repo.WriteCommit(new CommitObject
        {
            Tree = tree,
            Parents = [head],
            Message = $"stash: {message} (on {branch})",
            Author = author,
            Time = DateTimeOffset.Now.ToString("o"),
        });
        List<string> stack = Stack(repo);
        stack.Insert(0, stash);
        SaveStack(repo, stack);

        Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(head).Tree), world, prune: true);
        return new PushResult(true, stash);
    }

    public static List<MergeConflict> Apply(Repository repo, int n, bool pop)
    {
        List<string> stack = Stack(repo);
        if (n < 0 || n >= stack.Count) throw new InvalidOperationException($"no stash@{{{n}}}");
        string world = repo.Worktree ?? throw new InvalidOperationException("stash apply needs a bound worktree");

        CommitObject sc = repo.ReadCommit(stack[n]);
        string? parent = sc.Parents.Count > 0 ? sc.Parents[0] : null;
        Manifest baseM = parent is not null ? repo.ReadManifest(repo.ReadCommit(parent).Tree) : new Manifest();
        Manifest theirsM = repo.ReadManifest(sc.Tree);
        Manifest oursM = Snapshotter.Snapshot(repo, world); // current worktree

        var conflicts = new List<MergeConflict>();
        Manifest merged = Merger.MergeManifests(repo, baseM, oursM, theirsM, preferTheirs: false, conflicts);
        Checkout.Materialize(repo, merged, world, prune: true);

        if (pop && conflicts.Count == 0) { stack.RemoveAt(n); SaveStack(repo, stack); }
        return conflicts;
    }

    public static bool Drop(Repository repo, int n)
    {
        List<string> stack = Stack(repo);
        if (n < 0 || n >= stack.Count) return false;
        stack.RemoveAt(n);
        SaveStack(repo, stack);
        return true;
    }

    public static void Clear(Repository repo) => SaveStack(repo, []);
}
