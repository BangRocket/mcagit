using McaDiff.Diff;
using McaDiff.Output;
using McaDiff.Repo;

namespace McaDiff.Cli;

/// <summary>
/// Repository subcommands, git-style: the repo is the current directory (or the
/// nearest ancestor), overridable with <c>-C &lt;repo&gt;</c>; commit/status/
/// checkout default to the repo's bound worktree. Each method takes the parsed
/// <c>-C</c> value (or null) and the command's remaining args.
/// </summary>
public static class RepoCommands
{
    public static int Init(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--worktree"], []);
        string dir = dashC ?? (pos.Count > 0 ? pos[0] : Directory.GetCurrentDirectory());

        // git init is idempotent — re-running it on an existing repo reinitializes (exit 0).
        if (Repository.IsRepository(dir))
        {
            Repository existing = Repository.Open(dir);
            if (opts.GetValueOrDefault("--worktree") is { } wt0) existing.Worktree = wt0;
            Console.Error.WriteLine($"Reinitialized existing mcadiff repository in {dir}"
                + (existing.Worktree is { } w0 ? $" (worktree {w0})" : ""));
            return 0;
        }

        Repository repo = Repository.Init(dir);
        if (opts.GetValueOrDefault("--worktree") is { } wt) repo.Worktree = wt;
        Console.Error.WriteLine($"Initialized empty mcadiff repository in {dir}"
            + (repo.Worktree is { } w ? $" (worktree {w})" : ""));
        return 0;
    }

    public static int Commit(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["-m", "--message", "--author", "--push", "--token"], ["-S", "--json"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        bool json = opts.ContainsKey("--json");
        string? message = opts.GetValueOrDefault("-m") ?? opts.GetValueOrDefault("--message");
        if (message is null) return Err("commit requires -m <message>");
        // Serialize against a concurrent commit/push (e.g. a backup driver whose run overran its
        // interval) — branch advancement is last-writer-wins, so overlapping runs could drop a commit.
        if (TryLock(repo, "commit") is not { } commitLock) return Err(LockedMsg);
        using var _commitLock = commitLock;
        if (Hooks.Run(repo, "pre-commit") != 0) return Err("pre-commit hook failed; commit aborted");

        // With a staging index, commit the staged tree; otherwise snapshot the whole worktree.
        bool fromIndex = StagingIndex.Exists(repo);
        Manifest manifest;
        if (fromIndex) manifest = StagingIndex.Load(repo);
        else
        {
            if (World(repo, pos, 0) is not { } world) return NoWorld();
            manifest = Snapshotter.Snapshot(repo, world);
        }
        string tree = repo.WriteManifest(manifest);
        string? head = repo.HeadCommit();
        bool sign = opts.ContainsKey("-S") || string.Equals(repo.GetConfig("commit.gpgsign"), "true", StringComparison.OrdinalIgnoreCase);

        bool committed;
        string commitHash;
        if (head is not null && repo.ReadCommit(head).Tree == tree)
        {
            committed = false;
            commitHash = head; // nothing changed — HEAD stays put (a backup driver can detect this via --json)
            if (fromIndex) StagingIndex.Clear(repo);
        }
        else
        {
            Func<string, string>? signer;
            try { signer = Signer(repo, sign); }
            catch (Exception ex) { return Err(ex.Message); }
            commitHash = repo.CreateCommit(tree, head is null ? [] : [head], message,
                Author(repo, opts.GetValueOrDefault("--author")), sign: signer);
            committed = true;
            if (fromIndex) StagingIndex.Clear(repo);
            Hooks.Run(repo, "post-commit");
        }

        int chunks = manifest.Regions.Sum(r => r.Value.Count);
        int files = manifest.Regions.Count + manifest.Nbt.Count + manifest.Blobs.Count;
        string? branch = repo.CurrentBranch();

        // Optional one-shot push (runs even if nothing was committed, to keep the offsite in sync).
        int rc = 0;
        System.Text.Json.Nodes.JsonObject? pushJson = null;
        string? pushHuman = null;
        if (opts.TryGetValue("--push", out string? pushRaw))
        {
            string remote = string.IsNullOrEmpty(pushRaw) ? "origin" : pushRaw;
            if (branch is null) { pushHuman = "skipped push (detached HEAD)"; pushJson = new() { ["error"] = "detached HEAD" }; rc = 1; }
            else
                try
                {
                    RemoteOps.PushResult pr = RemoteOps.Push(repo, remote, branch, force: false, Token(opts));
                    pushHuman = $"pushed {branch} -> {remote} ({pr.ObjectsCopied} objects{(pr.FastForward ? "" : ", forced")})";
                    pushJson = new() { ["remote"] = remote, ["branch"] = branch, ["objects"] = pr.ObjectsCopied, ["fastForward"] = pr.FastForward };
                }
                catch (Exception ex) { pushHuman = $"push failed: {ex.Message}"; pushJson = new() { ["remote"] = remote, ["error"] = ex.Message }; rc = 1; }
        }

        if (json)
        {
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["committed"] = committed,
                ["commit"] = commitHash,
                ["branch"] = branch,
                ["message"] = message,
                ["files"] = files,
                ["chunks"] = chunks,
                ["signed"] = committed && sign,
                ["fromIndex"] = fromIndex,
            };
            if (!committed) obj["reason"] = $"nothing to commit — {(fromIndex ? "index" : "world")} matches HEAD";
            if (pushJson is not null) obj["push"] = pushJson;
            Console.WriteLine(obj.ToJsonString());
        }
        else
        {
            Console.Error.WriteLine(committed
                ? $"[{branch ?? "detached HEAD"} {commitHash[..10]}] {message}  ({files} files, {chunks} chunks{(sign ? ", signed" : "")}{(fromIndex ? ", from index" : "")})"
                : $"nothing to commit — {(fromIndex ? "index" : "world")} matches HEAD");
            if (pushHuman is not null) Console.Error.WriteLine($"  {pushHuman}");
        }
        return rc;
    }

    public static int Log(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["-n"], ["--oneline", "-p", "--stat", "--no-color"]);
        if (Open(dashC) is not { } repo) return NoRepo();

        int limit = opts.GetValueOrDefault("-n") is { } ns && int.TryParse(ns, out int lv) ? lv : int.MaxValue;
        bool oneline = opts.ContainsKey("--oneline"), patch = opts.ContainsKey("-p"),
             stat = opts.ContainsKey("--stat"), noColor = opts.ContainsKey("--no-color");

        List<string> commits;
        if (pos.Count > 0 && (pos[0].Contains("...") || pos[0].Contains("..")))
        {
            try { commits = RangeCommits(repo, pos[0]); } catch (Exception ex) { return Err(ex.Message); }
        }
        else
        {
            string? start = pos.Count > 0 ? TryResolve(repo, pos[0]) : repo.HeadCommit();
            if (start is null) { Console.Error.WriteLine("no commits yet"); return 0; }
            commits = LinearHistory(repo, start);
        }

        foreach (string h in commits.Take(limit)) PrintLogEntry(repo, h, oneline, stat, patch, noColor);
        return 0;
    }

    private static List<string> LinearHistory(Repository repo, string start)
    {
        var list = new List<string>();
        for (string? cur = start; cur is not null;)
        {
            list.Add(cur);
            List<string> parents = repo.ParentsOf(cur);
            cur = parents.Count > 0 ? parents[0] : null;
        }
        return list;
    }

    /// <summary>Commits in <c>A..B</c> (reachable from B, not A) or <c>A...B</c> (symmetric
    /// difference), newest first. An empty side defaults to HEAD.</summary>
    private static List<string> RangeCommits(Repository repo, string spec)
    {
        bool sym = spec.Contains("...");
        string sep = sym ? "..." : "..";
        int i = spec.IndexOf(sep, StringComparison.Ordinal);
        string aSpec = spec[..i] is { Length: > 0 } sa ? sa : "HEAD";
        string bSpec = spec[(i + sep.Length)..] is { Length: > 0 } sb ? sb : "HEAD";

        HashSet<string> ancA = Ancestors(repo, repo.ResolveRef(aSpec));
        HashSet<string> ancB = Ancestors(repo, repo.ResolveRef(bSpec));
        IEnumerable<string> set = sym
            ? ancA.Except(ancB).Concat(ancB.Except(ancA))
            : ancB.Except(ancA);
        // Sort by parsed timestamp, not the raw ISO string (which mis-orders across timezone offsets).
        return set.OrderByDescending(h => DateTimeOffset.TryParse(repo.ReadCommit(h).Time, out DateTimeOffset dt) ? dt : DateTimeOffset.MinValue)
                  .ThenBy(h => h, StringComparer.Ordinal).ToList();
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

    private static void PrintLogEntry(Repository repo, string hash, bool oneline, bool stat, bool patch, bool noColor)
    {
        CommitObject c = repo.ReadCommit(hash);
        if (oneline) { Console.WriteLine($"{repo.Objects.Abbreviate(hash)} {c.Message}"); return; }

        Console.WriteLine($"commit {hash}{(c.Signature is not null ? " (signed)" : "")}");
        if (c.Parents.Count > 1) Console.WriteLine($"Merge:  {string.Join(" ", c.Parents.Select(p => p[..10]))}");
        Console.WriteLine($"Author: {c.Author}");
        if (!string.IsNullOrEmpty(c.Committer) && c.Committer != c.Author) Console.WriteLine($"Commit: {c.Committer}");
        Console.WriteLine($"Date:   {c.Time}");
        Console.WriteLine();
        Console.WriteLine($"    {c.Message}");
        Console.WriteLine();
        if (stat || patch)
        {
            WorldDiff d = CommitDiff(repo, hash);
            if (stat) Console.WriteLine("  " + Counts(d));
            if (patch) RenderDiff(d, noColor);
        }
    }

    public static int Show(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["--no-color"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string spec = pos.Count > 0 ? pos[0] : "HEAD";
        string commit;
        try { commit = repo.ResolveRef(spec); } catch (Exception ex) { return Err(ex.Message); }

        CommitObject c = repo.ReadCommit(commit);
        Console.WriteLine($"commit {commit}{(c.Signature is not null ? " (signed)" : "")}");
        if (c.Parents.Count > 1) Console.WriteLine($"Merge:  {string.Join(" ", c.Parents.Select(p => p[..10]))}");
        Console.WriteLine($"Author: {c.Author}");
        if (!string.IsNullOrEmpty(c.Committer) && c.Committer != c.Author) Console.WriteLine($"Commit: {c.Committer}");
        Console.WriteLine($"Date:   {c.Time}");
        Console.WriteLine();
        Console.WriteLine($"    {c.Message}");
        Console.WriteLine();
        RenderDiff(CommitDiff(repo, commit), opts.ContainsKey("--no-color"));
        return 0;
    }

    public static int Reset(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["--hard", "--soft", "--mixed"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string spec = pos.Count > 0 ? pos[0] : "HEAD"; // git defaults to HEAD
        string target;
        try { target = repo.ResolveRef(spec); } catch (Exception ex) { return Err(ex.Message); }
        string? from = repo.HeadCommit();

        // Move HEAD via the branch, or HEAD itself when detached (git moves HEAD either way).
        if (repo.CurrentBranch() is { } branch) repo.WriteBranch(branch, target);
        else repo.SetHeadDetached(target);
        repo.RecordHead(from, target, "reset");

        // --soft moves HEAD only; --mixed (default) also resets the staging index; --hard also the worktree.
        if (!opts.ContainsKey("--soft") && StagingIndex.Exists(repo)) StagingIndex.Clear(repo);
        if (opts.ContainsKey("--hard"))
        {
            if (repo.Worktree is not { } world) return Err("--hard requires a bound worktree");
            Repo.Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(target).Tree), world, prune: true);
            Console.Error.WriteLine($"HEAD is now at {target[..10]} (worktree updated)");
        }
        else Console.Error.WriteLine($"HEAD is now at {target[..10]}");
        return 0;
    }

    public static int Revert(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--author"], ["--continue", "--abort"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (opts.ContainsKey("--abort")) return SequencerAbort(repo, "revert", repo.InRevert);
        if (opts.ContainsKey("--continue")) return SequencerContinue(repo, "revert", repo.InRevert, Author(repo, opts.GetValueOrDefault("--author")));

        if (repo.InSequencer) return Err("a cherry-pick/revert is already in progress (use --continue or --abort)");
        if (pos.Count < 1) return Err("usage: revert <commit> | --continue | --abort");
        if (repo.CurrentBranch() is not { } branch) return Err("revert requires being on a branch");
        if (repo.HeadCommit() is not { } head) return Err("nothing to revert (no commits)");
        string target;
        try { target = repo.ResolveRef(pos[0]); } catch (Exception ex) { return Err(ex.Message); }

        CommitObject tc = repo.ReadCommit(target);
        Manifest baseM = repo.ReadManifest(tc.Tree);
        Manifest oursM = repo.ReadManifest(repo.ReadCommit(head).Tree);
        string? parent = tc.Parents.Count > 0 ? tc.Parents[0] : null;
        Manifest theirsM = parent is not null ? repo.ReadManifest(repo.ReadCommit(parent).Tree) : new Manifest();

        var conflicts = new List<MergeConflict>();
        Manifest merged = Merger.MergeManifests(repo, baseM, oursM, theirsM, false, conflicts);
        string tree = repo.WriteManifest(merged);
        if (tree == repo.ReadCommit(head).Tree && conflicts.Count == 0) { Console.Error.WriteLine("nothing to revert — already absent"); return 0; }
        string message = $"Revert \"{tc.Message}\"";

        if (conflicts.Count > 0) // STOP — don't commit a half-resolved revert
        {
            repo.BeginRevert(target, message, head);
            if (repo.Worktree is { } w) Repo.Checkout.Materialize(repo, merged, w, prune: true);
            Console.Error.WriteLine($"Revert of {target[..10]} stopped — {conflicts.Count} conflict(s) need resolution.");
            PrintConflicts(conflicts);
            Console.Error.WriteLine("Resolve in the worktree, then `revert --continue` (or `--abort`).");
            return 1;
        }

        string commit = repo.CreateCommit(tree, [head], message, Author(repo, opts.GetValueOrDefault("--author")));
        Console.Error.WriteLine($"[{branch} {commit[..10]}] {message}");
        return 0;
    }

    public static int Restore(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--world", "--source"], ["--staged", "--cached"]);
        if (Open(dashC) is not { } repo) return NoRepo();

        // `restore --staged <path>...` unstages (index → HEAD), like git.
        if (opts.ContainsKey("--staged") || opts.ContainsKey("--cached"))
        {
            if (pos.Count == 0) return Err("usage: restore --staged <path>...");
            Staging.Unstage(repo, pos);
            Console.Error.WriteLine($"Unstaged {pos.Count} path(s).");
            return 0;
        }

        string? refName = opts.GetValueOrDefault("--source") ?? (pos.Count > 0 ? pos[0] : null);
        var specs = (opts.ContainsKey("--source") ? pos : pos.Skip(1)).ToList();
        if (refName is null || specs.Count == 0) return Err("usage: restore [--source <ref>] <ref> <path>...");

        string? world = opts.GetValueOrDefault("--world") ?? repo.Worktree;
        if (world is null) return Err("no worktree bound; use --world <dir>");
        string commit;
        try { commit = repo.ResolveRef(refName); } catch (Exception ex) { return Err(ex.Message); }

        Manifest filtered = FilterManifest(repo.ReadManifest(repo.ReadCommit(commit).Tree), specs);
        int n = filtered.Regions.Count + filtered.Nbt.Count + filtered.Blobs.Count;
        if (n == 0) return Err($"no files in {refName} match the given paths");
        Repo.Checkout.Materialize(repo, filtered, world);
        Console.Error.WriteLine($"Restored {n} file(s) from {refName} into {world}");
        return 0;
    }

    public static int Tag(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["-m", "--message"], ["-d", "-a", "-s", "-v", "-n", "-f"]);
        if (Open(dashC) is not { } repo) return NoRepo();

        if (opts.ContainsKey("-d"))
        {
            if (pos.Count < 1) return Err("usage: tag -d <name>");
            return repo.DeleteTag(pos[0]) ? 0 : Err($"tag not found: {pos[0]}");
        }
        if (opts.ContainsKey("-v"))
        {
            if (pos.Count < 1) return Err("usage: tag -v <name>");
            return VerifyTag(repo, pos[0]);
        }
        if (pos.Count == 0)
        {
            foreach (string t in repo.Tags())
                Console.WriteLine(opts.ContainsKey("-n") && repo.ReadAnnotatedTag(t) is { } at
                    ? $"{t,-15} {at.Message.Split('\n')[0]}"
                    : t);
            return 0;
        }

        string atRef = pos.Count > 1 ? pos[1] : "HEAD";
        string commit;
        try { commit = repo.ResolveRef(atRef); } catch (Exception ex) { return Err(ex.Message); }
        if (repo.ReadTag(pos[0]) is not null && !opts.ContainsKey("-f"))
            return Err($"tag already exists: {pos[0]} (use -f to overwrite)");

        bool sign = opts.ContainsKey("-s");
        bool annotated = sign || opts.ContainsKey("-a") || opts.ContainsKey("-m") || opts.ContainsKey("--message");
        if (!annotated)
        {
            repo.WriteTag(pos[0], commit);
            Console.Error.WriteLine($"Created tag {pos[0]} at {commit[..10]}");
            return 0;
        }

        string? msg = opts.GetValueOrDefault("-m") ?? opts.GetValueOrDefault("--message");
        if (msg is null) return Err("annotated tag requires -m <message>");
        var tag = new TagObject
        {
            Object = commit,
            Type = "commit",
            Tag = pos[0],
            Tagger = Author(repo, null),
            Time = DateTimeOffset.Now.ToString("o"),
            Message = msg,
        };
        if (sign)
        {
            try { tag.Signature = Signer(repo, true)!(tag.SignablePayload()); }
            catch (Exception ex) { return Err($"signing failed: {ex.Message}"); }
        }
        string h = repo.WriteAnnotatedTag(tag);
        Console.Error.WriteLine($"Created {(sign ? "signed " : "")}annotated tag {pos[0]} at {commit[..10]} (tag {h[..10]})");
        return 0;
    }

    private static int VerifyTag(Repository repo, string name)
    {
        if (repo.ReadAnnotatedTag(name) is not { } tag) return Err($"{name} is not an annotated tag");
        if (tag.Signature is null) { Console.Error.WriteLine($"{name}: tag is not signed"); return 1; }
        SshSigner.VerifyResult r = SshSigner.Verify(tag.SignablePayload(), tag.Signature, repo.GetConfig("gpg.ssh.allowedSignersFile"));
        Console.Error.WriteLine($"{name}: {r.Detail}");
        return r.Valid ? 0 : 1;
    }

    public static int Status(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        if (World(repo, a.ToList(), 0) is not { } world) return NoWorld();

        if (repo.InMerge)
        {
            Console.WriteLine($"merging {repo.ReadMergeHead()?[..10]} — fix conflicts then `merge --continue` (or `merge --abort`):");
            PrintConflicts(repo.ReadMergeConflicts());
            Console.WriteLine();
        }
        if (repo.InCherryPick)
            Console.WriteLine($"cherry-pick of {repo.ReadCherryPickHead()?[..10]} in progress — resolve, then `cherry-pick --continue` (or `--abort`).\n");
        if (repo.InRevert)
            Console.WriteLine($"revert of {repo.ReadRevertHead()?[..10]} in progress — resolve, then `revert --continue` (or `--abort`).\n");
        if (repo.InRebase)
            Console.WriteLine("rebase in progress — resolve, then `rebase --continue` (or `--skip` / `--abort`).\n");

        if (StagingIndex.Exists(repo))
        {
            Manifest head = repo.HeadCommit() is { } h ? repo.ReadManifest(repo.ReadCommit(h).Tree) : new Manifest();
            Manifest idx = StagingIndex.Load(repo);
            Manifest work = Snapshotter.HashOnly(repo, world);
            List<StatusEntry> staged = StatusCalc.Compute(head, idx);
            List<StatusEntry> unstaged = StatusCalc.Compute(idx, work);
            if (staged.Count == 0 && unstaged.Count == 0) { Console.WriteLine("clean — index matches HEAD and worktree"); return 0; }
            PrintStatusSection("Staged for commit", staged);
            PrintStatusSection("Not staged", unstaged);
            return 0;
        }

        List<StatusEntry> entries = StatusCalc.Compute(repo, world);
        if (entries.Count == 0) { Console.WriteLine("clean — world matches HEAD"); return 0; }
        Console.WriteLine($"changes vs HEAD ({repo.CurrentBranch() ?? "detached"}):");
        foreach (StatusEntry e in entries)
            Console.WriteLine($"  {e.Change,-9} {e.Path}{(e.Detail is null ? "" : $"  ({e.Detail})")}");
        return 0;
    }

    public static int Checkout(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["--force"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (pos.Count < 1) return Err("usage: checkout <ref> [<world-out>] [--force]");
        string refName = pos[0];
        if (World(repo, pos, 1) is not { } outDir) return Err("no <world-out> given and no worktree bound");

        string commit;
        try { commit = repo.ResolveRef(refName); }
        catch (Exception ex) { return Err(ex.Message); }

        bool isWorktree = repo.Worktree is { } wt && Path.GetFullPath(wt) == Path.GetFullPath(outDir);
        if (!opts.ContainsKey("--force"))
        {
            if (isWorktree)
            {
                // Don't silently clobber uncommitted changes in the bound worktree.
                if (StatusCalc.Compute(repo, outDir).Count > 0)
                    return Err("worktree has uncommitted changes — commit/stash them, or use --force");
            }
            else if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
                return Err($"output directory is not empty: {outDir} (use --force)");
        }

        string? from = repo.HeadCommit();
        Manifest manifest = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        Repo.Checkout.Materialize(repo, manifest, outDir, prune: true);

        bool onBranch = repo.ReadBranch(refName) is not null;
        if (onBranch) repo.SetHeadToBranch(refName); else repo.SetHeadDetached(commit);
        repo.RecordHead(from, commit, $"checkout: moving to {refName}"); // so HEAD@{1} returns the prior position
        Console.Error.WriteLine($"Checked out {refName} ({commit[..10]}) into {outDir} — "
            + (onBranch ? $"on branch {refName}" : "detached HEAD"));
        return 0;
    }

    public static int Branch(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["-d", "-D", "-f", "-m"]);
        if (Open(dashC) is not { } repo) return NoRepo();

        if (opts.ContainsKey("-d") || opts.ContainsKey("-D"))
        {
            if (pos.Count < 1) return Err("usage: branch -d <name>");
            if (repo.ReadBranch(pos[0]) is null) return Err($"branch not found: {pos[0]}");
            if (repo.CurrentBranch() == pos[0]) return Err($"cannot delete the current branch: {pos[0]}");
            repo.DeleteBranch(pos[0]);
            Console.Error.WriteLine($"Deleted branch {pos[0]}.");
            return 0;
        }
        if (opts.ContainsKey("-m")) // rename
        {
            if (pos.Count < 2) return Err("usage: branch -m <old> <new>");
            if (repo.ReadBranch(pos[0]) is not { } tip) return Err($"branch not found: {pos[0]}");
            if (repo.ReadBranch(pos[1]) is not null && !opts.ContainsKey("-f")) return Err($"branch already exists: {pos[1]}");
            repo.WriteBranch(pos[1], tip);
            repo.DeleteBranch(pos[0]);
            if (repo.CurrentBranch() == pos[0]) repo.SetHeadToBranch(pos[1]);
            Console.Error.WriteLine($"Renamed branch {pos[0]} -> {pos[1]}.");
            return 0;
        }
        if (pos.Count == 0)
        {
            string? current = repo.CurrentBranch();
            foreach (string br in repo.Branches())
                Console.WriteLine($"{(br == current ? "* " : "  ")}{br}");
            return 0;
        }

        // create: branch <name> [<start-point>]
        if (repo.ReadBranch(pos[0]) is not null && !opts.ContainsKey("-f"))
            return Err($"branch already exists: {pos[0]} (use -f to move it)");
        string at;
        if (pos.Count > 1)
        {
            try { at = repo.ResolveRef(pos[1]); } catch (Exception ex) { return Err(ex.Message); }
        }
        else if (repo.HeadCommit() is { } head) at = head;
        else return Err("cannot create a branch before the first commit");
        repo.WriteBranch(pos[0], at);
        Console.Error.WriteLine($"Created branch {pos[0]} at {at[..10]}");
        return 0;
    }

    public static int Merge(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--author"], ["--theirs", "--ours", "--continue", "--abort"]);
        if (Open(dashC) is not { } repo) return NoRepo();

        if (opts.ContainsKey("--abort"))
        {
            try { Merger.Abort(repo); } catch (Exception ex) { return Err(ex.Message); }
            Console.Error.WriteLine("Merge aborted; HEAD and worktree restored.");
            return 0;
        }
        if (opts.ContainsKey("--continue"))
        {
            MergeResult cr;
            try { cr = Merger.Continue(repo, Author(repo, opts.GetValueOrDefault("--author"))); }
            catch (Exception ex) { return Err(ex.Message); }
            Console.Error.WriteLine($"Merge complete: {cr.CommitHash![..10]}");
            return 0;
        }
        if (pos.Count < 1) return Err("usage: merge <ref> [--theirs|--ours] | merge --continue | merge --abort");

        bool preferTheirs = opts.ContainsKey("--theirs");
        bool autoResolve = preferTheirs || opts.ContainsKey("--ours");
        MergeResult result;
        try { result = Merger.Merge(repo, pos[0], preferTheirs, autoResolve, Author(repo, opts.GetValueOrDefault("--author"))); }
        catch (Exception ex) { return Err(ex.Message); }

        if (result.AlreadyUpToDate) { Console.Error.WriteLine("Already up to date."); return 0; }
        if (result.FastForward) { Console.Error.WriteLine($"Fast-forward to {result.CommitHash![..10]}"); return 0; }

        if (result.Stopped)
        {
            Console.Error.WriteLine($"Automatic merge stopped — {result.Conflicts.Count} conflict(s) need resolution.");
            PrintConflicts(result.Conflicts);
            Console.Error.WriteLine("Resolve in the worktree, then `mcadiff merge --continue` (or `mcadiff merge --abort`).");
            return 1;
        }
        if (result.HasConflicts)
        {
            Console.Error.WriteLine($"Merge commit {result.CommitHash![..10]} — {result.Conflicts.Count} conflicts auto-resolved (kept {(preferTheirs ? "theirs" : "ours")}).");
            PrintConflicts(result.Conflicts);
            return 0;
        }
        Console.Error.WriteLine($"Merge commit {result.CommitHash![..10]}.");
        return 0;
    }

    private static void PrintConflicts(IReadOnlyList<MergeConflict> conflicts)
    {
        foreach (MergeConflict c in conflicts.Take(30))
            Console.Error.WriteLine($"  conflict: {c.File}{(c.Chunk is null ? "" : $" chunk {c.Chunk}")}{(c.Path.Length == 0 ? "" : $" {c.Path}")} — {c.Reason}");
        if (conflicts.Count > 30) Console.Error.WriteLine($"  … and {conflicts.Count - 30} more");
    }

    public static int Remote(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        if (a.Length == 0)
        {
            foreach (var kv in repo.Remotes) Console.WriteLine($"{kv.Key}\t{kv.Value}");
            return 0;
        }
        switch (a[0])
        {
            case "add":
                if (a.Length < 3) return Err("usage: remote add <name> <url>");
                if (repo.GetRemote(a[1]) is not null) return Err($"remote {a[1]} already exists");
                repo.AddRemote(a[1], a[2]);
                Console.Error.WriteLine($"Added remote {a[1]} -> {repo.GetRemote(a[1])}");
                return 0;
            case "remove" or "rm":
                if (a.Length < 2) return Err("usage: remote remove <name>");
                return repo.RemoveRemote(a[1]) ? 0 : Err($"no such remote: {a[1]}");
            case "rename":
                if (a.Length < 3) return Err("usage: remote rename <old> <new>");
                if (repo.GetRemote(a[1]) is null) return Err($"no such remote: {a[1]}");
                if (repo.GetRemote(a[2]) is not null) return Err($"remote {a[2]} already exists");
                return repo.RenameRemote(a[1], a[2]) ? 0 : Err($"could not rename {a[1]}");
            case "set-url":
                if (a.Length < 3) return Err("usage: remote set-url <name> <url>");
                if (repo.GetRemote(a[1]) is null) return Err($"no such remote: {a[1]}");
                repo.AddRemote(a[1], a[2]); // upsert
                Console.Error.WriteLine($"{a[1]} -> {repo.GetRemote(a[1])}");
                return 0;
            case "get-url":
                if (a.Length < 2) return Err("usage: remote get-url <name>");
                if (repo.GetRemote(a[1]) is not { } url) return Err($"no such remote: {a[1]}");
                Console.WriteLine(url);
                return 0;
            default:
                return Err("usage: remote | remote (add|remove|rename|set-url|get-url) ...");
        }
    }

    public static int Clone(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--token", "--depth"], []);
        if (pos.Count < 2) return Err("usage: clone <src> <dest> [--depth N] [--token T]  (src: path, http(s)://, ssh://, azure://, or s3://)");
        if (Repository.IsRepository(pos[1])) return Err($"already a repository: {pos[1]}");
        int depth = 0;
        if (opts.GetValueOrDefault("--depth") is { } ds && (!int.TryParse(ds, out depth) || depth < 1))
            return Err("--depth must be a positive integer");
        try { RemoteOps.Clone(pos[0], pos[1], Token(opts), depth); }
        catch (Exception ex) { return Err(ex.Message); }
        Console.Error.WriteLine($"Cloned {pos[0]} -> {pos[1]}" + (depth > 0 ? $" (shallow, depth {depth})" : ""));
        return 0;
    }

    public static int Fetch(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--token"], []);
        if (Open(dashC) is not { } repo) return NoRepo();
        string remote = pos.Count > 0 ? pos[0] : "origin";
        string? branch = pos.Count > 1 ? pos[1] : null;
        try
        {
            int n = RemoteOps.Fetch(repo, remote, branch, Token(opts));
            Console.Error.WriteLine($"Fetched {remote} ({n} objects copied)");
            return 0;
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public static int Push(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--token"], ["--force", "--all"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (TryLock(repo, "push") is not { } pushLock) return Err(LockedMsg);
        using var _pushLock = pushLock;
        string remote = pos.Count > 0 ? pos[0] : "origin";
        bool force = opts.ContainsKey("--force");
        string? token = Token(opts);

        if (opts.ContainsKey("--all"))
        {
            int total = 0, failed = 0;
            foreach (string b in repo.Branches())
            {
                try
                {
                    RemoteOps.PushResult pr = RemoteOps.Push(repo, remote, b, force, token);
                    total += pr.ObjectsCopied;
                    Console.Error.WriteLine($"  {b} -> {remote} ({pr.ObjectsCopied} objects{(pr.FastForward ? "" : ", forced")})");
                }
                catch (Exception ex) { failed++; Console.Error.WriteLine($"  {b}: {ex.Message}"); }
            }
            Console.Error.WriteLine($"Pushed {repo.Branches().Count() - failed} branch(es) to {remote} ({total} objects)"
                + (failed > 0 ? $"; {failed} failed" : "") + ".");
            return failed > 0 ? 1 : 0; // git: non-zero if any branch was rejected
        }

        string? branch = pos.Count > 1 ? pos[1] : repo.CurrentBranch();
        if (branch is null) return Err("detached HEAD — specify a branch to push");
        try
        {
            RemoteOps.PushResult r = RemoteOps.Push(repo, remote, branch, opts.ContainsKey("--force"), Token(opts));
            Console.Error.WriteLine($"Pushed {branch} -> {remote} ({r.ObjectsCopied} objects{(r.FastForward ? "" : ", forced")})");
            return 0;
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static string? Token(Dictionary<string, string?> opts)
        => opts.GetValueOrDefault("--token") ?? Environment.GetEnvironmentVariable("MCADIFF_TOKEN");

    public static int Serve(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--port", "--host", "--token"], ["--allow-push"]);
        string? dir = dashC ?? (pos.Count > 0 ? pos[0] : Directory.GetCurrentDirectory());
        if (!Repository.IsRepository(dir)) return Err($"not a repository: {dir}");

        Repository repo = Repository.Open(dir);
        int port = opts.GetValueOrDefault("--port") is { } ps && int.TryParse(ps, out int p) ? p : 8421;
        string host = opts.GetValueOrDefault("--host") ?? "localhost";
        bool allowPush = opts.ContainsKey("--allow-push");
        string? token = Token(opts);

        var server = new RepoServer(repo, allowPush, token);
        try { server.Start(host, port); }
        catch (Exception ex) { return Err($"could not bind http://{host}:{port}/ — {ex.Message}"); }

        string mode = allowPush ? (token is not null ? "push: token required" : "push: OPEN (no token)") : "read-only";
        Console.Error.WriteLine($"Serving {dir} at http://{host}:{port}/  ({mode}). Ctrl-C to stop.");
        Console.Error.WriteLine("  Note: reads are unauthenticated — any peer can list refs and read every object (full history).");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
        server.Run();
        return 0;
    }

    public static int ServeStdio(string? dashC, string[] a)
    {
        string? dir = dashC ?? (a.Length > 0 ? a[0] : null);
        if (dir is null || !Repository.IsRepository(dir)) { Console.Error.WriteLine("serve-stdio: not a repository"); return 2; }
        var svc = new RemoteService(Repository.Open(dir), allowWrite: true); // ssh already authenticated the user
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        StdioServer.Serve(svc, input, output);
        return 0;
    }

    public static int Reflog(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        List<string> lines = repo.Reflog().ToList(); // most recent first
        for (int i = 0; i < lines.Count; i++)
        {
            string[] parts = lines[i].Split(' ', 3);
            string to = parts.Length > 1 ? parts[1] : lines[i];
            string msg = parts.Length > 2 ? parts[2] : "";
            Console.WriteLine($"{(to.Length >= 10 ? to[..10] : to)} HEAD@{{{i}}}: {msg}"); // git's index column
        }
        return 0;
    }

    public static int CherryPick(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--author"], ["--continue", "--abort"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (opts.ContainsKey("--abort")) return SequencerAbort(repo, "cherry-pick", repo.InCherryPick);
        if (opts.ContainsKey("--continue")) return SequencerContinue(repo, "cherry-pick", repo.InCherryPick, Author(repo, opts.GetValueOrDefault("--author")));

        if (repo.InSequencer) return Err("a cherry-pick/revert is already in progress (use --continue or --abort)");
        if (pos.Count < 1) return Err("usage: cherry-pick <commit> | --continue | --abort");
        if (repo.CurrentBranch() is not { } branch) return Err("cherry-pick requires being on a branch");
        if (repo.HeadCommit() is not { } head) return Err("nothing to cherry-pick onto (no commits)");
        string target;
        try { target = repo.ResolveRef(pos[0]); } catch (Exception ex) { return Err(ex.Message); }

        CommitObject tc = repo.ReadCommit(target);
        string? parent = tc.Parents.Count > 0 ? tc.Parents[0] : null;
        Manifest baseM = parent is not null ? repo.ReadManifest(repo.ReadCommit(parent).Tree) : new Manifest();
        Manifest oursM = repo.ReadManifest(repo.ReadCommit(head).Tree);
        var conflicts = new List<MergeConflict>();
        Manifest merged = Merger.MergeManifests(repo, baseM, oursM, repo.ReadManifest(tc.Tree), false, conflicts);
        string tree = repo.WriteManifest(merged);
        if (tree == repo.ReadCommit(head).Tree && conflicts.Count == 0) { Console.Error.WriteLine("nothing to cherry-pick — no changes"); return 0; }

        if (conflicts.Count > 0) // STOP — don't bake a half-resolved commit; record state for --continue/--abort
        {
            repo.BeginCherryPick(target, tc.Message, head);
            if (repo.Worktree is { } w) Repo.Checkout.Materialize(repo, merged, w, prune: true);
            Console.Error.WriteLine($"Cherry-pick of {target[..10]} stopped — {conflicts.Count} conflict(s) need resolution.");
            PrintConflicts(conflicts);
            Console.Error.WriteLine("Resolve in the worktree, then `cherry-pick --continue` (or `--abort`).");
            return 1;
        }

        string commit = repo.CreateCommit(tree, [head], tc.Message, author: tc.Author, committer: Author(repo, null), authorTime: tc.Time);
        Console.Error.WriteLine($"[{branch} {commit[..10]}] {tc.Message}");
        return 0;
    }

    private static int SequencerContinue(Repository repo, string kind, bool active, string author)
    {
        if (!active) return Err($"no {kind} in progress");
        if (repo.Worktree is not { } world) return Err($"{kind} --continue needs a bound worktree to snapshot the resolution");
        string head = repo.HeadCommit() ?? throw new InvalidOperationException("no HEAD");
        Manifest m = Snapshotter.Snapshot(repo, world);
        string tree = repo.WriteManifest(m);
        if (tree == repo.ReadCommit(head).Tree) { repo.ClearSequencer(); Console.Error.WriteLine($"nothing to commit — {kind} made no change."); return 0; }

        string msg = repo.SeqMessage() ?? kind;
        string commit;
        if (kind == "cherry-pick" && repo.ReadCherryPickHead() is { } src) // preserve original author
        {
            CommitObject sc = repo.ReadCommit(src);
            commit = repo.CreateCommit(tree, [head], msg, author: sc.Author, committer: author, authorTime: sc.Time);
        }
        else commit = repo.CreateCommit(tree, [head], msg, author);
        repo.ClearSequencer();
        Console.Error.WriteLine($"{kind} complete: {commit[..10]}");
        return 0;
    }

    private static int SequencerAbort(Repository repo, string kind, bool active)
    {
        if (!active) return Err($"no {kind} in progress");
        string orig = repo.ReadOrigHead() ?? throw new InvalidOperationException("ORIG_HEAD missing — cannot abort");
        if (repo.CurrentBranch() is { } branch) repo.WriteBranch(branch, orig);
        if (repo.Worktree is { } w) Repo.Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(orig).Tree), w, prune: true);
        repo.ClearSequencer();
        Console.Error.WriteLine($"{kind} aborted; restored to {orig[..10]}.");
        return 0;
    }

    public static int GcCmd(string? dashC, string[] a)
    {
        var (_, opts) = Parse(a, [], ["--prune-only"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (opts.ContainsKey("--prune-only"))
        {
            Gc.Result p = Gc.Prune(repo);
            Console.Error.WriteLine($"Pruned {p.Pruned} objects ({p.BytesFreed / 1024} KiB freed), {p.Kept} reachable.");
            return 0;
        }
        Gc.RepackResult r = Gc.Repack(repo);
        Console.Error.WriteLine($"Packed {r.Packed} objects, pruned {r.Pruned} unreachable "
            + $"({r.BytesFreed / 1024} KiB freed){(r.PackId is null ? "" : $", pack {r.PackId[..10]}")}.");
        return 0;
    }

    public static int FsckCmd(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        Fsck.Report r = Fsck.Check(repo);
        foreach (string c in r.Corrupt) Console.Error.WriteLine($"error: corrupt object {c}");
        foreach (string m in r.Missing) Console.Error.WriteLine($"error: missing object {m}");
        foreach (string d in r.DanglingCommits) Console.WriteLine($"dangling commit {d}");
        Console.Error.WriteLine($"checked {r.Checked} objects — {r.Corrupt.Count} corrupt, "
            + $"{r.Missing.Count} missing, {r.Unreachable} unreachable ({r.DanglingCommits.Count} dangling commits)");
        return r.Ok ? 0 : 1;
    }

    // ---- plumbing ----

    public static int RevParse(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["--short", "--abbrev-ref", "--verify"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (pos.Count == 0) return Err("usage: rev-parse [--short|--abbrev-ref] <rev>...");
        int rc = 0;
        foreach (string spec in pos)
        {
            if (opts.ContainsKey("--abbrev-ref"))
            {
                if (spec == "HEAD") { Console.WriteLine(repo.CurrentBranch() ?? "HEAD"); continue; } // detached → "HEAD", not a hash
                if (repo.ReadBranch(spec) is not null) { Console.WriteLine(spec); continue; }
            }
            try { string h = repo.ResolveRef(spec); Console.WriteLine(opts.ContainsKey("--short") ? repo.Objects.Abbreviate(h) : h); }
            catch (Exception ex) { Console.Error.WriteLine($"mcadiff: {ex.Message}"); rc = 1; }
        }
        return rc;
    }

    public static int CatFile(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["-t", "-s", "-p", "-e"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (pos.Count < 1) return Err("usage: cat-file (-t|-s|-p|-e) <object>");
        if (ResolveObject(repo, pos[0]) is not { } obj || !repo.Objects.Exists(obj))
            return opts.ContainsKey("-e") ? 1 : Err($"not a valid object name: {pos[0]}");

        if (opts.ContainsKey("-e")) return 0;
        Repository.ObjectKind kind = repo.Classify(obj);
        if (opts.ContainsKey("-t")) { Console.WriteLine(kind.ToString().ToLowerInvariant()); return 0; }

        byte[] content = repo.Objects.Read(obj);
        if (opts.ContainsKey("-s")) { Console.WriteLine(content.Length); return 0; }
        if (opts.ContainsKey("-p"))
        {
            if (kind == Repository.ObjectKind.Blob) { using Stream o = Console.OpenStandardOutput(); o.Write(content); }
            else Console.Write(System.Text.Encoding.UTF8.GetString(content));
            return 0;
        }
        return Err("cat-file needs one of -t, -s, -p, -e");
    }

    public static int HashObject(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["-w"]);
        if (pos.Count < 1) return Err("usage: hash-object [-w] <file>");
        if (!File.Exists(pos[0])) return Err($"file not found: {pos[0]}");
        byte[] bytes = File.ReadAllBytes(pos[0]);
        if (opts.ContainsKey("-w"))
        {
            if (Open(dashC) is not { } repo) return NoRepo();
            Console.WriteLine(repo.Objects.Write(bytes));
        }
        else Console.WriteLine(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        return 0;
    }

    public static int LsTree(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["-r", "--name-only"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (pos.Count < 1) return Err("usage: ls-tree [-r] [--name-only] <tree-ish>");
        string treeHash;
        try
        {
            string resolved = repo.ResolveRef(pos[0]);
            treeHash = repo.Classify(resolved) == Repository.ObjectKind.Tree ? resolved : repo.ReadCommit(resolved).Tree;
        }
        catch (Exception ex) { return Err(ex.Message); }

        Manifest m = repo.ReadManifest(treeHash);
        bool nameOnly = opts.ContainsKey("--name-only"), recurse = opts.ContainsKey("-r");
        foreach (var r in m.Regions)
        {
            if (recurse)
                foreach (var ch in r.Value)
                    Console.WriteLine(nameOnly ? $"{r.Key}#{ch.Key}" : $"chunk {ch.Value[..10]}\t{r.Key}#{ch.Key}");
            else
                Console.WriteLine(nameOnly ? r.Key : $"region -          \t{r.Key} ({r.Value.Count} chunks)");
        }
        foreach (var n in m.Nbt) Console.WriteLine(nameOnly ? n.Key : $"nbt    {n.Value[..10]}\t{n.Key}");
        foreach (var b in m.Blobs) Console.WriteLine(nameOnly ? b.Key : $"blob   {b.Value[..10]}\t{b.Key}");
        return 0;
    }

    public static int Config(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, [], ["--global", "--list", "-l", "--unset"]);
        bool global = opts.ContainsKey("--global");
        bool list = opts.ContainsKey("--list") || opts.ContainsKey("-l");

        // --global operates on ~/.mcaconfig and needs no repository (git-like: usable before `init`).
        if (global)
        {
            if (list) { foreach (var (k, v) in Repository.ListGlobalConfig()) Console.WriteLine($"{k}={v}"); return 0; }
            if (opts.ContainsKey("--unset"))
                return pos.Count >= 1
                    ? (Repository.UnsetGlobalConfig(pos[0]) ? 0 : Err($"key not set: {pos[0]}"))
                    : Err("usage: config --unset --global <key>");
            if (pos.Count == 0) return Err("usage: config --global <key> [<value>] | --list | --unset <key>");
            if (pos.Count == 1)
            {
                string? gv = Repository.GetGlobalConfig(pos[0]);
                if (gv is null) return 1; // git: reading an unset key exits 1
                Console.WriteLine(gv);
                return 0;
            }
            Repository.SetGlobalConfig(pos[0], pos[1]);
            return 0;
        }

        if (Open(dashC) is not { } repo) return NoRepo();

        if (list)
        {
            foreach (var (k, v, _) in repo.ListConfig()) Console.WriteLine($"{k}={v}");
            return 0;
        }
        if (opts.ContainsKey("--unset"))
        {
            if (pos.Count < 1) return Err("usage: config --unset [--global] <key>");
            return repo.UnsetConfig(pos[0], global) ? 0 : Err($"key not set: {pos[0]}");
        }
        if (pos.Count == 0)
            return Err("usage: config [--global] <key> [<value>] | config --list | config --unset <key>");

        string key = pos[0];
        if (key == "worktree") // first-class repo setting (binds the world)
        {
            if (pos.Count == 1) { Console.WriteLine(repo.Worktree ?? "(unset)"); return 0; }
            repo.Worktree = pos[1];
            Console.Error.WriteLine($"worktree = {repo.Worktree}");
            return 0;
        }
        if (pos.Count == 1)
        {
            string? v = repo.GetConfig(key);
            if (v is null) return 1; // git: reading an unset key exits 1
            Console.WriteLine(v);
            return 0;
        }
        repo.SetConfig(key, pos[1], global);
        return 0;
    }

    public static int Add(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--world"], []);
        if (Open(dashC) is not { } repo) return NoRepo();
        string? world = opts.GetValueOrDefault("--world") ?? repo.Worktree;
        if (world is null) return Err("no worktree bound; use --world <dir>");
        if (pos.Count == 0) return Err("usage: add <path>... | add .");

        int staged = Staging.Add(repo, world, pos);
        Console.Error.WriteLine($"Staged {staged} change(s).");
        return 0;
    }

    public static int Bisect(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        if (a.Length == 0) return Err("usage: bisect (start|bad|good|skip|reset|log) ...");
        string sub = a[0];
        string[] rest = a[1..];

        string Resolve(string s) => repo.ResolveRef(s);
        try
        {
            switch (sub)
            {
                case "start":
                    string original = repo.CurrentBranch() ?? repo.HeadCommit()
                        ?? throw new InvalidOperationException("no commits to bisect");
                    repo.BisectStart(original);
                    repo.BisectAppendLog("# bisect start");
                    if (rest.Length >= 1) { string bad = Resolve(rest[0]); repo.BisectSetBad(bad); repo.BisectAppendLog($"bad {bad}"); }
                    foreach (string g in rest.Skip(1)) { string gg = Resolve(g); repo.BisectAddGood(gg); repo.BisectAppendLog($"good {gg}"); }
                    return BisectAdvance(repo);

                case "bad":
                    if (!repo.InBisect) return Err("not bisecting (run `bisect start`)");
                    string b = rest.Length > 0 ? Resolve(rest[0]) : repo.HeadCommit()!;
                    repo.BisectSetBad(b); repo.BisectAppendLog($"bad {b}");
                    return BisectAdvance(repo);

                case "good":
                    if (!repo.InBisect) return Err("not bisecting (run `bisect start`)");
                    IEnumerable<string> goods = rest.Length > 0 ? rest.Select(Resolve) : [repo.HeadCommit()!];
                    foreach (string g in goods) { repo.BisectAddGood(g); repo.BisectAppendLog($"good {g}"); }
                    return BisectAdvance(repo);

                case "skip":
                    if (!repo.InBisect) return Err("not bisecting (run `bisect start`)");
                    string sk = rest.Length > 0 ? Resolve(rest[0]) : repo.HeadCommit()!;
                    repo.BisectAddSkip(sk); repo.BisectAppendLog($"skip {sk}");
                    return BisectAdvance(repo);

                case "reset":
                    if (!repo.InBisect) return Err("not bisecting");
                    string orig = repo.BisectOriginal()!;
                    BisectRestore(repo, orig);
                    repo.BisectClear();
                    Console.Error.WriteLine($"Bisect reset; back at {orig}.");
                    return 0;

                case "log":
                    foreach (string line in repo.BisectLogLines()) Console.WriteLine(line);
                    return 0;

                default:
                    return Err($"unknown bisect subcommand: {sub}");
            }
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static int BisectAdvance(Repository repo)
    {
        Repo.Bisect.State s = Repo.Bisect.Compute(repo);
        if (s.NeedMarks)
        {
            Console.Error.WriteLine("bisect: mark at least one bad and one good commit (`bisect bad` / `bisect good`).");
            return 0;
        }
        if (s.Done)
        {
            CommitObject c = repo.ReadCommit(s.FirstBad!);
            Console.WriteLine($"{s.FirstBad} is the first bad commit");
            Console.WriteLine($"    {c.Message}");
            return 0;
        }
        if (repo.Worktree is { } w)
        {
            Repo.Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(s.Next!).Tree), w, prune: true);
            repo.SetHeadDetached(s.Next!);
        }
        int steps = (int)Math.Floor(Math.Log2(Math.Max(1, s.Remaining)));
        Console.Error.WriteLine($"Bisecting: {s.Remaining} revisions left to test after this (roughly {steps} steps); testing {s.Next![..10]}");
        return 0;
    }

    private static void BisectRestore(Repository repo, string orig)
    {
        string commit;
        if (repo.ReadBranch(orig) is { } tip) { repo.SetHeadToBranch(orig); commit = tip; }
        else { repo.SetHeadDetached(orig); commit = orig; }
        if (repo.Worktree is { } w) Repo.Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(commit).Tree), w, prune: true);
    }

    public static int StashCmd(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["-m", "--message", "--author"], []);
        if (Open(dashC) is not { } repo) return NoRepo();
        string sub = pos.Count > 0 ? pos[0] : "push";
        try
        {
            switch (sub)
            {
                case "push" or "save":
                    {
                        string msg = opts.GetValueOrDefault("-m") ?? opts.GetValueOrDefault("--message") ?? "WIP";
                        Stash.PushResult r = Stash.Push(repo, msg, Author(repo, opts.GetValueOrDefault("--author")));
                        if (!r.Created) { Console.Error.WriteLine("No local changes to stash."); return 0; }
                        Console.Error.WriteLine($"Saved working state in stash@{{0}} ({r.Commit![..10]}); worktree reset to HEAD.");
                        return 0;
                    }
                case "list":
                    {
                        List<string> stack = Stash.Stack(repo);
                        for (int i = 0; i < stack.Count; i++)
                            Console.WriteLine($"stash@{{{i}}}: {stack[i][..10]} {repo.ReadCommit(stack[i]).Message}");
                        return 0;
                    }
                case "pop" or "apply":
                    {
                        int n = ParseStashIndex(pos);
                        List<MergeConflict> conflicts = Stash.Apply(repo, n, pop: sub == "pop");
                        if (conflicts.Count > 0)
                        {
                            Console.Error.WriteLine($"Applied stash@{{{n}}} with {conflicts.Count} conflict(s) (kept ours; stash retained):");
                            PrintConflicts(conflicts);
                            return 1;
                        }
                        Console.Error.WriteLine($"{(sub == "pop" ? "Popped" : "Applied")} stash@{{{n}}}.");
                        return 0;
                    }
                case "drop":
                    {
                        int n = ParseStashIndex(pos);
                        if (!Stash.Drop(repo, n)) return Err($"no stash@{{{n}}}");
                        Console.Error.WriteLine($"Dropped stash@{{{n}}}.");
                        return 0;
                    }
                case "clear":
                    Stash.Clear(repo);
                    Console.Error.WriteLine("Cleared the stash stack.");
                    return 0;
                default:
                    return Err($"unknown stash subcommand: {sub} (push|list|pop|apply|drop|clear)");
            }
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private static int ParseStashIndex(List<string> pos)
    {
        foreach (string t in pos.Skip(1))
        {
            string s = t.StartsWith("stash@{") && t.EndsWith('}') ? t[7..^1] : t;
            if (int.TryParse(s, out int n)) return n;
        }
        return 0;
    }

    public static int RebaseCmd(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--onto", "--author"], ["--continue", "--abort", "--skip"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string author = Author(repo, opts.GetValueOrDefault("--author"));
        try
        {
            if (opts.ContainsKey("--abort")) { Rebase.Abort(repo); Console.Error.WriteLine("Rebase aborted; branch restored."); return 0; }

            Rebase.Result r;
            if (opts.ContainsKey("--continue")) r = Rebase.Continue(repo, author);
            else if (opts.ContainsKey("--skip")) r = Rebase.Skip(repo, author);
            else
            {
                if (repo.InRebase) return Err("a rebase is in progress (use --continue / --skip / --abort)");
                if (pos.Count < 1) return Err("usage: rebase [--onto <newbase>] <upstream> | --continue | --skip | --abort");
                r = Rebase.Start(repo, pos[0], opts.GetValueOrDefault("--onto"), author);
            }

            if (r.UpToDate) { Console.Error.WriteLine("Already up to date."); return 0; }
            if (r.FastForward) { Console.Error.WriteLine($"Fast-forwarded to {r.NewTip![..10]}."); return 0; }
            if (r.Stopped)
            {
                Console.Error.WriteLine($"Rebase stopped at {r.StoppedAt![..10]} — {r.Conflicts.Count} conflict(s) need resolution.");
                PrintConflicts(r.Conflicts);
                Console.Error.WriteLine("Resolve in the worktree, then `rebase --continue` (or `--skip` / `--abort`).");
                return 1;
            }
            Console.Error.WriteLine($"Rebased {r.Replayed} commit(s) onto {r.NewTip![..10]}.");
            return 0;
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public static int Clean(string? dashC, string[] a)
    {
        var (_, opts) = Parse(a, ["--world"], ["-n", "--dry-run", "-f", "--force", "-d"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string? world = opts.GetValueOrDefault("--world") ?? repo.Worktree;
        if (world is null) return Err("no worktree bound; use --world <dir>");
        bool dry = opts.ContainsKey("-n") || opts.ContainsKey("--dry-run");
        bool force = opts.ContainsKey("-f") || opts.ContainsKey("--force");
        bool dirs = opts.ContainsKey("-d");
        if (!dry && !force) return Err("clean would remove untracked files; pass -f to remove or -n to preview");

        Manifest head = repo.HeadCommit() is { } h ? repo.ReadManifest(repo.ReadCommit(h).Tree) : new Manifest();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in head.Regions.Keys) keep.Add(k);
        foreach (string k in head.Nbt.Keys) keep.Add(k);
        foreach (string k in head.Blobs.Keys) keep.Add(k);
        IgnoreRules ignore = IgnoreRules.Load(world);
        string Rel(string p) => Path.GetRelativePath(world, p).Replace('\\', '/');
        bool Protected(string rel) => rel == "session.lock" || keep.Contains(rel) || ignore.IsIgnored(rel);

        int n = 0;
        foreach (string file in Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories))
        {
            string rel = Rel(file);
            if (Protected(rel)) continue;
            if (dry) Console.WriteLine($"Would remove {rel}");
            else { File.Delete(file); Console.WriteLine($"Removing {rel}"); }
            n++;
        }

        if (dirs)
        {
            // A directory is removable only if its whole subtree is untracked (no kept/ignored file
            // beneath it). Remove just the top-most such dirs so each subtree is reported once.
            var removable = new List<string>();
            foreach (string dir in Directory.EnumerateDirectories(world, "*", SearchOption.AllDirectories))
            {
                string relDir = Rel(dir);
                if (ignore.IsIgnored(relDir + "/")) continue;
                if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => Protected(Rel(f)))) continue;
                removable.Add(relDir);
            }
            var removableSet = removable.ToHashSet(StringComparer.Ordinal);
            bool HasRemovableAncestor(string rel)
            {
                for (int i = rel.LastIndexOf('/'); i > 0; i = rel.LastIndexOf('/', i - 1))
                    if (removableSet.Contains(rel[..i])) return true;
                return false;
            }
            foreach (string relDir in removable.OrderBy(d => d, StringComparer.Ordinal))
            {
                if (HasRemovableAncestor(relDir)) continue; // a parent will take this subtree with it
                if (dry) Console.WriteLine($"Would remove {relDir}/");
                else if (Directory.Exists(Path.Combine(world, relDir)))
                { Directory.Delete(Path.Combine(world, relDir), recursive: true); Console.WriteLine($"Removing {relDir}/"); }
                n++;
            }
        }

        if (n == 0) Console.Error.WriteLine("Nothing to clean.");
        return 0;
    }

    public static int LsRemote(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--token"], []);
        if (Open(dashC) is not { } repo) return NoRepo();
        string remote = pos.Count > 0 ? pos[0] : "origin";
        string url = repo.GetRemote(remote) ?? remote;
        try
        {
            using IRemoteTransport t = Transports.Connect(url, Token(opts));
            RefAdvertisement refs = t.ListRefs();
            foreach (var kv in refs.Branches) Console.WriteLine($"{kv.Value}\trefs/heads/{kv.Key}");
            foreach (var kv in refs.Tags) Console.WriteLine($"{kv.Value}\trefs/tags/{kv.Key}");
            if (refs.Head is { } hd) Console.WriteLine($"{refs.Branches.GetValueOrDefault(hd) ?? ""}\tHEAD"); // git format: <hash>\tHEAD
            return 0;
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public static int VerifyRemote(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--token"], ["--deep"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string remote = pos.Count > 0 ? pos[0] : "origin";
        string url = repo.GetRemote(remote) ?? remote;
        try
        {
            using IRemoteTransport t = Transports.Connect(url, Token(opts));
            RemoteOps.VerifyResult r = RemoteOps.Verify(t, opts.ContainsKey("--deep"));
            Console.Error.WriteLine($"{remote}: {r.Branches} branch(es), {r.Commits} commit(s), {r.Objects} object(s) checked"
                + (opts.ContainsKey("--deep") ? " (deep)" : ""));
            foreach (string m in r.Missing) Console.Error.WriteLine($"  missing: {m}");
            foreach (string c in r.Corrupt) Console.Error.WriteLine($"  corrupt: {c}");
            if (r.Ok) { Console.Error.WriteLine($"{remote}: ok"); return 0; }
            Console.Error.WriteLine($"{remote}: {r.Missing.Count} missing, {r.Corrupt.Count} corrupt");
            return 1;
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ---- helpers ----

    private static Repository? Open(string? dashC) => Repository.Discover(dashC);

    private static void PrintStatusSection(string title, IReadOnlyList<StatusEntry> entries)
    {
        if (entries.Count == 0) return;
        Console.WriteLine($"{title}:");
        foreach (StatusEntry e in entries)
            Console.WriteLine($"  {e.Change,-9} {e.Path}{(e.Detail is null ? "" : $"  ({e.Detail})")}");
    }

    /// <summary>Diff a commit against its first parent (root commit → empty base).</summary>
    private static WorldDiff CommitDiff(Repository repo, string commit)
    {
        CommitObject c = repo.ReadCommit(commit);
        Manifest mNew = repo.ReadManifest(c.Tree);
        string? parent = c.Parents.Count > 0 ? c.Parents[0] : null;
        Manifest mOld = parent is not null ? repo.ReadManifest(repo.ReadCommit(parent).Tree) : new Manifest();
        return RepoDiffer.Diff(
            parent is null ? "(root)" : parent[..10], mOld, new RepoDiffer.CommitSource(repo, mOld),
            commit[..10], mNew, new RepoDiffer.CommitSource(repo, mNew), new DiffRunOptions());
    }

    private static void RenderDiff(WorldDiff diff, bool noColor)
        => new TextDiffFormatter(new Ansi(Ansi.ShouldColor(noColor)), summaryOnly: false).Write(diff, Console.Out);

    private static string Counts(WorldDiff diff)
    {
        int chunks = diff.Files.Sum(f => f.Chunks.Count);
        int nbt = diff.Files.Sum(f => f.Changes.Count + f.Chunks.Sum(c => c.Changes.Count));
        return $"{diff.Files.Count} files, {chunks} chunks, {nbt} nbt changes";
    }

    private static Manifest FilterManifest(Manifest m, List<string> specs)
    {
        bool Match(string rel) => specs.Any(s => rel == s || rel.StartsWith(s.TrimEnd('/') + "/", StringComparison.Ordinal));
        var f = new Manifest();
        foreach (var kv in m.Regions) if (Match(kv.Key)) f.Regions[kv.Key] = kv.Value;
        foreach (var kv in m.Nbt) if (Match(kv.Key)) f.Nbt[kv.Key] = kv.Value;
        foreach (var kv in m.Blobs) if (Match(kv.Key)) f.Blobs[kv.Key] = kv.Value;
        return f;
    }

    private static int NoRepo() => Err("not a repository — run inside a repo directory or use -C <repo>");
    private static int NoWorld() => Err("no world given and no worktree bound (pass <world> or set `config worktree`)");

    private static string? World(Repository repo, List<string> pos, int index)
        => pos.Count > index ? pos[index] : repo.Worktree;

    private static string? TryResolve(Repository repo, string refName)
    {
        try { return repo.ResolveRef(refName); } catch { return null; }
    }

    private static string Author(Repository repo, string? flag)
        => flag ?? repo.ConfiguredIdentity() ?? Environment.UserName ?? "unknown";

    /// <summary>A signing delegate when signing is requested (throws if no key is configured), else null.</summary>
    private static Func<string, string>? Signer(Repository repo, bool wantSign)
    {
        if (!wantSign) return null;
        string key = repo.GetConfig("user.signingkey")
            ?? throw new InvalidOperationException("signing requested but user.signingkey is not set");
        return payload => SshSigner.Sign(payload, key);
    }

    /// <summary>Resolves a name to a stored object hash <em>without</em> peeling annotated
    /// tags (so cat-file/ls-tree can inspect the tag object itself).</summary>
    private static string? ResolveObject(Repository repo, string spec)
    {
        if (repo.Objects.Exists(spec)) return spec;
        if (repo.Objects.ResolvePrefix(spec) is { } full) return full;
        if (repo.ReadTag(spec) is { } t) return t;
        if (repo.ReadBranch(spec) is { } b) return b;
        if (spec == "HEAD") return repo.HeadCommit();
        try { return repo.ResolveRef(spec); } catch { return null; }
    }

    private static (List<string> Positionals, Dictionary<string, string?> Opts) Parse(
        string[] args, string[] valueFlags, string[] boolFlags)
    {
        var pos = new List<string>();
        var opts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var valueSet = new HashSet<string>(valueFlags, StringComparer.Ordinal);
        var boolSet = new HashSet<string>(boolFlags, StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string x = args[i];
            if (valueSet.Contains(x)) opts[x] = i + 1 < args.Length ? args[++i] : null;
            else if (boolSet.Contains(x)) opts[x] = null;
            else pos.Add(x);
        }
        return (pos, opts);
    }

    private static int Err(string message)
    {
        Console.Error.WriteLine($"mcadiff: {message}");
        return 2;
    }

    private const string LockedMsg =
        "repository is locked by another mcadiff process (concurrent commit/push) — retry once it finishes";

    /// <summary>Takes the repo lock, or returns null if another process holds it (caller exits 2).</summary>
    private static RepoLock? TryLock(Repository repo, string operation)
    {
        try { return RepoLock.Acquire(repo.Dir, operation); }
        catch (RepoLockedException) { return null; }
    }
}
