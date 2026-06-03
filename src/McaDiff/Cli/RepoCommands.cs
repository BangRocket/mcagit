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
        var (pos, opts) = Parse(a, ["-m", "--message", "--author"], []);
        if (Open(dashC) is not { } repo) return NoRepo();
        string? message = opts.GetValueOrDefault("-m") ?? opts.GetValueOrDefault("--message");
        if (message is null) return Err("commit requires -m <message>");
        if (World(repo, pos, 0) is not { } world) return NoWorld();

        Manifest manifest = Snapshotter.Snapshot(repo, world);
        string tree = repo.WriteManifest(manifest);
        string? head = repo.HeadCommit();
        if (head is not null && repo.ReadCommit(head).Tree == tree)
        {
            Console.Error.WriteLine("nothing to commit — world matches HEAD");
            return 0;
        }

        string hash = repo.CreateCommit(tree, head is null ? [] : [head], message, Author(opts.GetValueOrDefault("--author")));
        int chunks = manifest.Regions.Sum(r => r.Value.Count);
        int files = manifest.Regions.Count + manifest.Nbt.Count + manifest.Blobs.Count;
        string where = repo.CurrentBranch() ?? "detached HEAD";
        Console.Error.WriteLine($"[{where} {hash[..10]}] {message}  ({files} files, {chunks} chunks)");
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
                Console.WriteLine($"commit {cur}");
                if (c.Parents.Count > 1) Console.WriteLine($"Merge:  {string.Join(" ", c.Parents.Select(p => p[..10]))}");
                Console.WriteLine($"Author: {c.Author}");
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
        Console.WriteLine($"commit {commit}");
        if (c.Parents.Count > 1) Console.WriteLine($"Merge:  {string.Join(" ", c.Parents.Select(p => p[..10]))}");
        Console.WriteLine($"Author: {c.Author}");
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
            Repo.Checkout.Materialize(repo, repo.ReadManifest(repo.ReadCommit(target).Tree), world);
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

        string commit = repo.CreateCommit(tree, [head], $"Revert \"{tc.Message}\"", Author(opts.GetValueOrDefault("--author")));
        Console.Error.WriteLine($"[{branch} {commit[..10]}] revert {target[..10]} — {conflicts.Count} conflicts");
        foreach (MergeConflict cf in conflicts.Take(20))
            Console.Error.WriteLine($"  conflict: {cf.File}{(cf.Chunk is null ? "" : $" chunk {cf.Chunk}")}{(cf.Path.Length == 0 ? "" : $" {cf.Path}")} — {cf.Reason}");
        return conflicts.Count > 0 ? 1 : 0;
    }

    public static int Restore(string? dashC, string[] a)
    {
        var (pos, opts) = Parse(a, ["--world", "--source"], []);
        if (Open(dashC) is not { } repo) return NoRepo();
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
        var (pos, opts) = Parse(a, [], ["-d"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (opts.ContainsKey("-d"))
        {
            if (pos.Count < 1) return Err("usage: tag -d <name>");
            return repo.DeleteTag(pos[0]) ? 0 : Err($"tag not found: {pos[0]}");
        }
        if (pos.Count == 0)
        {
            foreach (string t in repo.Tags()) Console.WriteLine(t);
            return 0;
        }
        string at = pos.Count > 1 ? pos[1] : "HEAD";
        string commit;
        try { commit = repo.ResolveRef(at); } catch (Exception ex) { return Err(ex.Message); }
        repo.WriteTag(pos[0], commit);
        Console.Error.WriteLine($"Created tag {pos[0]} at {commit[..10]}");
        return 0;
    }

    public static int Status(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        if (World(repo, a.ToList(), 0) is not { } world) return NoWorld();

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
        Repo.Checkout.Materialize(repo, manifest, outDir);

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
        var (pos, opts) = Parse(a, ["--author"], ["--theirs"]);
        if (Open(dashC) is not { } repo) return NoRepo();
        if (pos.Count < 1) return Err("usage: merge <ref> [--theirs] [--author X]");

        MergeResult result;
        try { result = Merger.Merge(repo, pos[0], opts.ContainsKey("--theirs"), Author(opts.GetValueOrDefault("--author"))); }
        catch (Exception ex) { return Err(ex.Message); }

        if (result.AlreadyUpToDate) { Console.Error.WriteLine("Already up to date."); return 0; }
        if (result.FastForward) { Console.Error.WriteLine($"Fast-forward to {result.CommitHash![..10]}"); return 0; }

        Console.Error.WriteLine($"Merge commit {result.CommitHash![..10]} — {result.Conflicts.Count} conflicts.");
        foreach (MergeConflict c in result.Conflicts.Take(30))
            Console.Error.WriteLine($"  conflict: {c.File}{(c.Chunk is null ? "" : $" chunk {c.Chunk}")}{(c.Path.Length == 0 ? "" : $" {c.Path}")} — {c.Reason}");
        if (result.Conflicts.Count > 30) Console.Error.WriteLine($"  … and {result.Conflicts.Count - 30} more");
        return result.HasConflicts ? 1 : 0;
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

        string commit = repo.CreateCommit(tree, [head], tc.Message, Author(null));
        Console.Error.WriteLine($"[{branch} {commit[..10]}] {tc.Message} — {conflicts.Count} conflicts");
        foreach (MergeConflict cf in conflicts.Take(20))
            Console.Error.WriteLine($"  conflict: {cf.File}{(cf.Chunk is null ? "" : $" chunk {cf.Chunk}")}{(cf.Path.Length == 0 ? "" : $" {cf.Path}")} — {cf.Reason}");
        return conflicts.Count > 0 ? 1 : 0;
    }

    public static int GcCmd(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        Gc.Result r = Gc.Prune(repo);
        Console.Error.WriteLine($"Pruned {r.Pruned} objects ({r.BytesFreed / 1024} KiB freed), {r.Kept} reachable.");
        return 0;
    }

    public static int Config(string? dashC, string[] a)
    {
        if (Open(dashC) is not { } repo) return NoRepo();
        if (a.Length == 0 || a[0] != "worktree") return Err("usage: config worktree [<path>]");
        if (a.Length == 1) { Console.WriteLine(repo.Worktree ?? "(unset)"); return 0; }
        repo.Worktree = a[1];
        Console.Error.WriteLine($"worktree = {repo.Worktree}");
        return 0;
    }

    // ---- helpers ----

    private static Repository? Open(string? dashC) => Repository.Discover(dashC);

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

    private static string Author(string? flag) => flag ?? Environment.UserName ?? "unknown";

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
