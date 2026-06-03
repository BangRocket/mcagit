using McaDiff.Repo;

namespace McaDiff.Cli;

/// <summary>Implements the repository subcommands (init/commit/log/status/checkout/branch/merge).</summary>
public static class RepoCommands
{
    public static int Init(string[] a)
    {
        if (a.Length < 1) return Err("init requires <repo>");
        if (Repository.IsRepository(a[0])) return Err($"already a repository: {a[0]}");
        Repository.Init(a[0]);
        Console.Error.WriteLine($"Initialized empty mcadiff repository in {a[0]}");
        return 0;
    }

    public static int Commit(string[] a)
    {
        var (pos, opts) = Parse(a, valueFlags: ["-m", "--message", "--author"], boolFlags: []);
        if (pos.Count < 2) return Err("usage: commit <repo> <world> -m <message> [--author X]");
        string? message = opts.GetValueOrDefault("-m") ?? opts.GetValueOrDefault("--message");
        if (message is null) return Err("commit requires -m <message>");
        if (!Repository.IsRepository(pos[0])) return Err($"not a repository: {pos[0]}");

        Repository repo = Repository.Open(pos[0]);
        Manifest manifest = Snapshotter.Snapshot(repo, pos[1]);
        string tree = repo.WriteManifest(manifest);

        string? head = repo.HeadCommit();
        if (head is not null && repo.ReadCommit(head).Tree == tree)
        {
            Console.Error.WriteLine("nothing to commit — world matches HEAD");
            return 0;
        }

        string author = Author(opts.GetValueOrDefault("--author"));
        string hash = repo.CreateCommit(tree, head is null ? [] : [head], message, author);
        int chunks = manifest.Regions.Sum(r => r.Value.Count);
        int files = manifest.Regions.Count + manifest.Nbt.Count + manifest.Blobs.Count;
        string where = repo.CurrentBranch() ?? "detached HEAD";
        Console.Error.WriteLine($"[{where} {hash[..10]}] {message}  ({files} files, {chunks} chunks, {repo.Objects.Count()} objects in store)");
        return 0;
    }

    public static int Log(string[] a)
    {
        var (pos, opts) = Parse(a, valueFlags: ["--branch"], boolFlags: []);
        if (pos.Count < 1) return Err("usage: log <repo> [--branch b]");
        if (!Repository.IsRepository(pos[0])) return Err($"not a repository: {pos[0]}");
        Repository repo = Repository.Open(pos[0]);

        string? start = opts.TryGetValue("--branch", out string? b) ? repo.ReadBranch(b!) : repo.HeadCommit();
        if (start is null) { Console.Error.WriteLine("no commits yet"); return 0; }

        string? cur = start;
        while (cur is not null)
        {
            CommitObject c = repo.ReadCommit(cur);
            Console.WriteLine($"commit {cur}");
            if (c.Parents.Count > 1) Console.WriteLine($"Merge:  {string.Join(" ", c.Parents.Select(p => p[..10]))}");
            Console.WriteLine($"Author: {c.Author}");
            Console.WriteLine($"Date:   {c.Time}");
            Console.WriteLine();
            Console.WriteLine($"    {c.Message}");
            Console.WriteLine();
            cur = c.Parents.Count > 0 ? c.Parents[0] : null; // first-parent walk
        }
        return 0;
    }

    public static int Status(string[] a)
    {
        if (a.Length < 2) return Err("usage: status <repo> <world>");
        if (!Repository.IsRepository(a[0])) return Err($"not a repository: {a[0]}");
        Repository repo = Repository.Open(a[0]);

        List<StatusEntry> entries = StatusCalc.Compute(repo, a[1]);
        if (entries.Count == 0) { Console.WriteLine("clean — world matches HEAD"); return 0; }
        Console.WriteLine($"changes vs HEAD ({repo.CurrentBranch()}):");
        foreach (StatusEntry e in entries)
            Console.WriteLine($"  {e.Change,-9} {e.Path}{(e.Detail is null ? "" : $"  ({e.Detail})")}");
        return 0;
    }

    public static int Checkout(string[] a)
    {
        var (pos, opts) = Parse(a, valueFlags: [], boolFlags: ["--force"]);
        if (pos.Count < 3) return Err("usage: checkout <repo> <ref> <world-out> [--force]");
        if (!Repository.IsRepository(pos[0])) return Err($"not a repository: {pos[0]}");
        Repository repo = Repository.Open(pos[0]);
        string refName = pos[1], outDir = pos[2];

        string commit;
        try { commit = repo.ResolveRef(refName); }
        catch (Exception ex) { return Err(ex.Message); }

        if (!opts.ContainsKey("--force") && Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            return Err($"output directory is not empty: {outDir} (use --force)");

        Manifest manifest = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        Repo.Checkout.Materialize(repo, manifest, outDir);

        bool onBranch = repo.ReadBranch(refName) is not null;
        if (onBranch) repo.SetHeadToBranch(refName);
        else repo.SetHeadDetached(commit);
        string note = onBranch ? $"on branch {refName}" : "detached HEAD (commits won't move a branch)";
        Console.Error.WriteLine($"Checked out {refName} ({commit[..10]}) into {outDir} — {note}");
        return 0;
    }

    public static int Branch(string[] a)
    {
        if (a.Length < 1) return Err("usage: branch <repo> [name]");
        if (!Repository.IsRepository(a[0])) return Err($"not a repository: {a[0]}");
        Repository repo = Repository.Open(a[0]);

        if (a.Length < 2)
        {
            string? current = repo.CurrentBranch();
            foreach (string br in repo.Branches())
                Console.WriteLine($"{(br == current ? "* " : "  ")}{br}");
            return 0;
        }

        string? head = repo.HeadCommit();
        if (head is null) return Err("cannot create a branch before the first commit");
        repo.WriteBranch(a[1], head);
        Console.Error.WriteLine($"Created branch {a[1]} at {head[..10]}");
        return 0;
    }

    public static int Merge(string[] a)
    {
        var (pos, opts) = Parse(a, valueFlags: ["--author"], boolFlags: ["--theirs"]);
        if (pos.Count < 2) return Err("usage: merge <repo> <ref> [--theirs] [--author X]");
        if (!Repository.IsRepository(pos[0])) return Err($"not a repository: {pos[0]}");
        Repository repo = Repository.Open(pos[0]);

        MergeResult result;
        try { result = Merger.Merge(repo, pos[1], opts.ContainsKey("--theirs"), Author(opts.GetValueOrDefault("--author"))); }
        catch (Exception ex) { return Err(ex.Message); }

        if (result.AlreadyUpToDate) { Console.Error.WriteLine("Already up to date."); return 0; }
        if (result.FastForward) { Console.Error.WriteLine($"Fast-forward to {result.CommitHash![..10]}"); return 0; }

        Console.Error.WriteLine($"Merge commit {result.CommitHash![..10]} — {result.Conflicts.Count} conflicts.");
        foreach (MergeConflict c in result.Conflicts.Take(30))
            Console.Error.WriteLine($"  conflict: {c.File}{(c.Chunk is null ? "" : $" chunk {c.Chunk}")}{(c.Path.Length == 0 ? "" : $" {c.Path}")} — {c.Reason}");
        if (result.Conflicts.Count > 30) Console.Error.WriteLine($"  … and {result.Conflicts.Count - 30} more");
        return result.HasConflicts ? 1 : 0;
    }

    // ---- helpers ----

    private static string Author(string? flag) =>
        flag ?? Environment.UserName ?? "unknown";

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
