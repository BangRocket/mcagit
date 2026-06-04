using System.Text.Json;

namespace McaDiff.Repo;

/// <summary>
/// Non-interactive rebase that replays the current branch's commits (those not in
/// <c>upstream</c>) onto a new base via the 3-way engine. Like git, it <b>stops at the
/// first conflicting commit</b>, persisting REBASE_STATE so the user can resolve and
/// <c>--continue</c> (or <c>--skip</c> / <c>--abort</c>) — never silently baking
/// unreviewed auto-resolutions. Original author/date are preserved per commit.
/// </summary>
public static class Rebase
{
    public sealed record Result(bool UpToDate, bool FastForward, bool Stopped, int Replayed,
        string? StoppedAt, List<MergeConflict> Conflicts, string? NewTip);

    public sealed class State
    {
        public string Branch { get; set; } = "";
        public string OrigHead { get; set; } = "";
        public string Onto { get; set; } = "";
        public string Current { get; set; } = "";          // tip of commits replayed so far
        public List<string> Remaining { get; set; } = [];  // commits still to replay (oldest first; [0] is in conflict)
        public int Done { get; set; }

        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public string ToJson() => JsonSerializer.Serialize(this, Json);
        public static State FromJson(string s) => JsonSerializer.Deserialize<State>(s, Json)!;
    }

    public static Result Start(Repository repo, string upstreamRef, string? ontoRef, string author)
    {
        if (repo.InRebase) throw new InvalidOperationException("a rebase is already in progress (use --continue/--abort/--skip)");
        string branch = repo.CurrentBranch() ?? throw new InvalidOperationException("rebase requires being on a branch");
        string upstream = repo.ResolveRef(upstreamRef);
        string onto = ontoRef is not null ? repo.ResolveRef(ontoRef) : upstream;
        string head = repo.ReadBranch(branch) ?? throw new InvalidOperationException("branch has no commits");

        if (Transfer.IsAncestor(repo, head, upstream)) { FastForward(repo, branch, head, upstream); return new Result(false, true, false, 0, null, [], upstream); }
        if (ontoRef is null && MergeBase.Find(repo, head, upstream) == upstream) return new Result(true, false, false, 0, null, [], head);

        HashSet<string> upAnc = AncestorsIncl(repo, upstream);
        var toReplay = new List<string>();
        for (string? c = head; c is not null && !upAnc.Contains(c);)
        {
            toReplay.Add(c);
            List<string> parents = repo.ReadCommit(c).Parents;
            c = parents.Count > 0 ? parents[0] : null;
        }
        toReplay.Reverse(); // oldest first

        var state = new State { Branch = branch, OrigHead = head, Onto = onto, Current = onto, Remaining = toReplay };
        return Resume(repo, state, author, commitResolution: false);
    }

    /// <summary>Snapshots the resolved worktree as the conflicted commit, then replays the rest.</summary>
    public static Result Continue(Repository repo, string author) => Resume(repo, LoadState(repo), author, commitResolution: true);

    /// <summary>Drops the conflicted commit and replays the rest.</summary>
    public static Result Skip(Repository repo, string author)
    {
        State st = LoadState(repo);
        if (st.Remaining.Count > 0) { st.Remaining.RemoveAt(0); st.Done++; }
        return Resume(repo, st, author, commitResolution: false);
    }

    public static void Abort(Repository repo)
    {
        State st = LoadState(repo);
        repo.WriteBranch(st.Branch, st.OrigHead);
        if (repo.Worktree is { } w) Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(st.OrigHead).Tree), w, prune: true);
        repo.RecordHead(st.Current, st.OrigHead, "rebase --abort");
        repo.ClearRebaseState();
    }

    private static Result Resume(Repository repo, State st, string author, bool commitResolution)
    {
        // --continue: the worktree holds the user's resolution of Remaining[0] — commit it as that commit.
        if (commitResolution && st.Remaining.Count > 0)
        {
            string world = repo.Worktree ?? throw new InvalidOperationException("rebase --continue needs a bound worktree");
            CommitObject cc = repo.ReadCommit(st.Remaining[0]);
            string tree = repo.WriteManifest(Snapshotter.Snapshot(repo, world));
            st.Current = ReplayCommit(repo, st.Current, tree, cc, author);
            st.Remaining.RemoveAt(0);
            st.Done++;
        }

        while (st.Remaining.Count > 0)
        {
            string c = st.Remaining[0];
            CommitObject cc = repo.ReadCommit(c);
            string? p = cc.Parents.Count > 0 ? cc.Parents[0] : null;
            Manifest baseM = p is not null ? repo.ReadManifest(repo.ReadCommit(p).Tree) : new Manifest();
            Manifest oursM = repo.ReadManifest(repo.ReadCommit(st.Current).Tree);
            Manifest theirsM = repo.ReadManifest(cc.Tree);
            var conflicts = new List<MergeConflict>();
            Manifest merged = Merger.MergeManifests(repo, baseM, oursM, theirsM, preferTheirs: true, conflicts);

            if (conflicts.Count > 0) // STOP here: persist + lay partial result into the worktree
            {
                repo.WriteRebaseState(st.ToJson());
                repo.WriteOrigHead(st.OrigHead);
                if (repo.Worktree is { } w) Checkout.Materialize(repo, merged, w, prune: true);
                return new Result(false, false, true, st.Done, c, conflicts, null);
            }

            st.Current = ReplayCommit(repo, st.Current, repo.WriteManifest(merged), cc, author);
            st.Remaining.RemoveAt(0);
            st.Done++;
        }

        // Finished: advance the branch (which only moves on success) and clean up.
        repo.WriteBranch(st.Branch, st.Current);
        repo.RecordHead(st.OrigHead, st.Current, $"rebase finished onto {st.Onto[..10]}");
        if (repo.Worktree is { } w2) Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(st.Current).Tree), w2, prune: true);
        repo.ClearRebaseState();
        return new Result(false, false, false, st.Done, null, [], st.Current);
    }

    /// <summary>Writes a replayed commit (preserving original author/date) and a per-commit reflog entry.</summary>
    private static string ReplayCommit(Repository repo, string parent, string tree, CommitObject original, string author)
    {
        string commit = repo.WriteCommit(new CommitObject
        {
            Tree = tree, Parents = [parent], Message = original.Message,
            Author = original.Author, Time = original.Time,
            Committer = author, CommitTime = DateTimeOffset.Now.ToString("o"),
        });
        repo.RecordHead(parent, commit, $"rebase: {Oneline(original.Message)}");
        return commit;
    }

    private static State LoadState(Repository repo)
        => repo.ReadRebaseState() is { } json ? State.FromJson(json) : throw new InvalidOperationException("no rebase in progress");

    private static void FastForward(Repository repo, string branch, string from, string to)
    {
        repo.WriteBranch(branch, to);
        repo.RecordHead(from, to, "rebase: fast-forward");
        if (repo.Worktree is { } w) Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(to).Tree), w, prune: true);
    }

    private static string Oneline(string msg) { int nl = msg.IndexOf('\n'); return nl < 0 ? msg : msg[..nl]; }

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
