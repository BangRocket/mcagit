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
        if (Repository.IsRepository(dir)) return Err($"already a repository: {dir}");

        Repository repo = Repository.Init(dir);
        if (opts.GetValueOrDefault("--worktree") is { } wt) repo.Worktree = wt;
        Console.Error.WriteLine($"Initialized empty mcadiff repository in {dir}"
            + (repo.Worktree is { } w ? $" (worktree {w})" : ""));
        return 0;
    }

    public static int Commit(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["-m", "--message", "--author"], ["-S"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string? message = opts.GetValueOrDefault("-m") ?? opts.GetValueOrDefault("--message");
        if (message is null) return Err("commit requires -m <message>");

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
        if (head is not null && repo.ReadCommit(head).Tree == tree)
        {
            Console.Error.WriteLine($"nothing to commit — {(fromIndex ? "index" : "world")} matches HEAD");
            if (fromIndex) StagingIndex.Clear(repo);
            return 0;
        }

        bool sign = opts.ContainsKey("-S") || string.Equals(repo.GetConfig("commit.gpgsign"), "true", StringComparison.OrdinalIgnoreCase);
        Func<string, string>? signer;
        try { signer = Signer(repo, sign); }
        catch (Exception ex) { return Err(ex.Message); }

        string hash = repo.CreateCommit(tree, head is null ? [] : [head], message,
            Author(repo, opts.GetValueOrDefault("--author")), sign: signer);
        if (fromIndex) StagingIndex.Clear(repo);
        int chunks = manifest.Regions.Sum(r => r.Value.Count);
        int files = manifest.Regions.Count + manifest.Nbt.Count + manifest.Blobs.Count;
        string where = repo.CurrentBranch() ?? "detached HEAD";
        Console.Error.WriteLine($"[{where} {hash[..10]}] {message}  ({files} files, {chunks} chunks{(sign ? ", signed" : "")}{(fromIndex ? ", from index" : "")})");
        return 0;
    }

    public static int Log(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["-n"], ["--oneline", "-p", "--stat", "--no-color"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string? start = pos.Count > 0 ? TryResolve(repo, pos[0]) : repo.HeadCommit();
        if (start is null) { Console.Error.WriteLine("no commits yet"); return 0; }

        int limit = opts.GetValueOrDefault("-n") is { } ns && int.TryParse(ns, out int lv) ? lv : int.MaxValue;
        bool oneline = opts.ContainsKey("--oneline"), patch = opts.ContainsKey("-p"),
             stat = opts.ContainsKey("--stat"), noColor = opts.ContainsKey("--no-color");

        int count = 0;
        for (string? cur = start; cur is not null && count < limit; count++)
        {
            CommitObject c = repo.ReadCommit(cur);
            if (oneline)
            {
                Console.WriteLine($"{cur[..10]} {c.Message}");
            }
            else
            {
                Console.WriteLine($"commit {cur}{(c.Signature is not null ? " (signed)" : "")}");
                if (c.Parents.Count > 1) Console.WriteLine($"Merge:  {string.Join(" ", c.Parents.Select(p => p[..10]))}");
                Console.WriteLine($"Author: {c.Author}");
                if (!string.IsNullOrEmpty(c.Committer) && c.Committer != c.Author) Console.WriteLine($"Commit: {c.Committer}");
                Console.WriteLine($"Date:   {c.Time}");
                Console.WriteLine();
                Console.WriteLine($"    {c.Message}");
                Console.WriteLine();
                if (stat || patch)
                {
                    WorldDiff d = CommitDiff(repo, cur);
                    if (stat) Console.WriteLine("  " + Counts(d));
                    if (patch) RenderDiff(d, noColor);
                }
            }
            cur = c.Parents.Count > 0 ? c.Parents[0] : null;
        }
        return 0;
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
        var (pos, opts) = Parse(a, [], ["--hard", "--soft"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (pos.Count < 1) return Err("usage: reset <ref> [--hard]");
        if (repo.CurrentBranch() is not { } branch) return Err("reset requires being on a branch");
        string target;
        try { target = repo.ResolveRef(pos[0]); } catch (Exception ex) { return Err(ex.Message); }

        repo.WriteBranch(branch, target);
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
        var (pos, opts) = Parse(a, ["--author"], []);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (pos.Count < 1) return Err("usage: revert <commit>");
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
        if (tree == repo.ReadCommit(head).Tree) { Console.Error.WriteLine("nothing to revert — already absent"); return 0; }

        string commit = repo.CreateCommit(tree, [head], $"Revert \"{tc.Message}\"", Author(repo, opts.GetValueOrDefault("--author")));
        Console.Error.WriteLine($"[{branch} {commit[..10]}] revert {target[..10]} — {conflicts.Count} conflicts");
        foreach (MergeConflict cf in conflicts.Take(20))
            Console.Error.WriteLine($"  conflict: {cf.File}{(cf.Chunk is null ? "" : $" chunk {cf.Chunk}")}{(cf.Path.Length == 0 ? "" : $" {cf.Path}")} — {cf.Reason}");
        return conflicts.Count > 0 ? 1 : 0;
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
        var (pos, opts) = Parse(a, ["-m", "--message"], ["-d", "-a", "-s", "-v", "-n"]);
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
            Object = commit, Type = "commit", Tag = pos[0],
            Tagger = Author(repo, null), Time = DateTimeOffset.Now.ToString("o"), Message = msg,
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

        if (!opts.ContainsKey("--force") && Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            return Err($"output directory is not empty: {outDir} (use --force)");

        Manifest manifest = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        Repo.Checkout.Materialize(repo, manifest, outDir, prune: true);

        bool onBranch = repo.ReadBranch(refName) is not null;
        if (onBranch) repo.SetHeadToBranch(refName); else repo.SetHeadDetached(commit);
        Console.Error.WriteLine($"Checked out {refName} ({commit[..10]}) into {outDir} — "
            + (onBranch ? $"on branch {refName}" : "detached HEAD"));
        return 0;
    }

    public static int Branch(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        if (a.Length == 0)
        {
            string? current = repo.CurrentBranch();
            foreach (string br in repo.Branches())
                Console.WriteLine($"{(br == current ? "* " : "  ")}{br}");
            return 0;
        }
        if (repo.HeadCommit() is not { } head) return Err("cannot create a branch before the first commit");
        repo.WriteBranch(a[0], head);
        Console.Error.WriteLine($"Created branch {a[0]} at {head[..10]}");
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
        if (a[0] == "add")
        {
            if (a.Length < 3) return Err("usage: remote add <name> <path>");
            repo.AddRemote(a[1], a[2]);
            Console.Error.WriteLine($"Added remote {a[1]} -> {repo.GetRemote(a[1])}");
            return 0;
        }
        return Err("usage: remote | remote add <name> <path>");
    }

    public static int Clone(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--token"], []);
        if (pos.Count < 2) return Err("usage: clone <src> <dest> [--token T]  (src: path, http(s)://, or ssh://)");
        if (Repository.IsRepository(pos[1])) return Err($"already a repository: {pos[1]}");
        try { RemoteOps.Clone(pos[0], pos[1], Token(opts)); }
        catch (Exception ex) { return Err(ex.Message); }
        Console.Error.WriteLine($"Cloned {pos[0]} -> {pos[1]}");
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
        var (pos, opts) = Parse(a, ["--token"], ["--force"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        string remote = pos.Count > 0 ? pos[0] : "origin";
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
        foreach (string line in repo.Reflog())
        {
            string[] parts = line.Split(' ', 3);
            string to = parts.Length > 1 ? parts[1] : line;
            string msg = parts.Length > 2 ? parts[2] : "";
            Console.WriteLine($"{(to.Length >= 10 ? to[..10] : to)} {msg}");
        }
        return 0;
    }

    public static int CherryPick(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        if (a.Length < 1) return Err("usage: cherry-pick <commit>");
        if (repo.CurrentBranch() is not { } branch) return Err("cherry-pick requires being on a branch");
        if (repo.HeadCommit() is not { } head) return Err("nothing to cherry-pick onto (no commits)");
        string target;
        try { target = repo.ResolveRef(a[0]); } catch (Exception ex) { return Err(ex.Message); }

        CommitObject tc = repo.ReadCommit(target);
        string? parent = tc.Parents.Count > 0 ? tc.Parents[0] : null;
        Manifest baseM = parent is not null ? repo.ReadManifest(repo.ReadCommit(parent).Tree) : new Manifest();
        Manifest oursM = repo.ReadManifest(repo.ReadCommit(head).Tree);
        Manifest theirsM = repo.ReadManifest(tc.Tree);

        var conflicts = new List<MergeConflict>();
        Manifest merged = Merger.MergeManifests(repo, baseM, oursM, theirsM, false, conflicts);
        string tree = repo.WriteManifest(merged);
        if (tree == repo.ReadCommit(head).Tree) { Console.Error.WriteLine("nothing to cherry-pick — no changes"); return 0; }

        string commit = repo.CreateCommit(tree, [head], tc.Message,
            author: tc.Author, committer: Author(repo, null), authorTime: tc.Time);
        Console.Error.WriteLine($"[{branch} {commit[..10]}] {tc.Message} — {conflicts.Count} conflicts");
        foreach (MergeConflict cf in conflicts.Take(20))
            Console.Error.WriteLine($"  conflict: {cf.File}{(cf.Chunk is null ? "" : $" chunk {cf.Chunk}")}{(cf.Path.Length == 0 ? "" : $" {cf.Path}")} — {cf.Reason}");
        return conflicts.Count > 0 ? 1 : 0;
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
                string? br = spec == "HEAD" ? repo.CurrentBranch() : (repo.ReadBranch(spec) is not null ? spec : null);
                if (br is not null) { Console.WriteLine(br); continue; }
            }
            try { string h = repo.ResolveRef(spec); Console.WriteLine(opts.ContainsKey("--short") ? h[..10] : h); }
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
        if (Open(dashC) is not { } repo) return NoRepo();
        var (pos, opts) = Parse(a, [], ["--global", "--list", "-l", "--unset"]);
        bool global = opts.ContainsKey("--global");

        if (opts.ContainsKey("--list") || opts.ContainsKey("-l"))
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
}
