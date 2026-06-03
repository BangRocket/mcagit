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
        if (Open(dashC) is not { } repo) return NoRepo();
        string? start = a.Length > 0 ? TryResolve(repo, a[0]) : repo.HeadCommit();
        if (start is null) { Console.Error.WriteLine("no commits yet"); return 0; }

        for (string? cur = start; cur is not null;)
        {
            CommitObject c = repo.ReadCommit(cur);
            Console.WriteLine($"commit {cur}");
            if (c.Parents.Count > 1) Console.WriteLine($"Merge:  {string.Join(" ", c.Parents.Select(p => p[..10]))}");
            Console.WriteLine($"Author: {c.Author}");
            Console.WriteLine($"Date:   {c.Time}");
            Console.WriteLine();
            Console.WriteLine($"    {c.Message}");
            Console.WriteLine();
            cur = c.Parents.Count > 0 ? c.Parents[0] : null;
        }
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
