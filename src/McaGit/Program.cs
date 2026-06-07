using McaGit.Cli;
using McaGit.Diff;
using McaGit.Model;
using McaGit.Output;
using McaGit.Patch;
using McaGit.Repo;

// Global, git-style: an optional leading `-C <repo>` selects the repository
// (otherwise it's discovered from the current directory). `diff` is the default
// when the first token isn't a known subcommand, so `mcagit <A> <B>` still works.
string? dashC = null;
int idx = 0;
if (args.Length >= 1 && args[0] == "-C")
{
    if (args.Length < 2) return Fail("-C requires a repository path");
    dashC = args[1];
    idx = 2;
}

string[] tail = args[idx..];
string? cmd = tail.Length > 0 ? tail[0] : null;

try
{
    return Dispatch();
}
catch (Exception ex)
{
    // Never show a raw stack trace — every failure is a clean one-liner + exit 2 (issue #26).
    Console.Error.WriteLine($"mcagit: error: {ex.Message}");
    return 2;
}

int Dispatch() => cmd switch
{
    "diff" => RunDiff(tail[1..], dashC),
    "extract" => RunExtract(tail[1..]),
    "apply" => RunApply(tail[1..]),
    "init" => RepoCommands.Init(dashC, tail[1..]),
    "add" => RepoCommands.Add(dashC, tail[1..]),
    "commit" or "backup" => RepoCommands.Commit(dashC, BackupArgs(cmd, tail[1..])),
    "undo" => RepoCommands.Undo(dashC, tail[1..]),
    "bisect" => RepoCommands.Bisect(dashC, tail[1..]),
    "log" => RepoCommands.Log(dashC, tail[1..]),
    "show" => RepoCommands.Show(dashC, tail[1..]),
    "status" => RepoCommands.Status(dashC, tail[1..]),
    "checkout" => RepoCommands.Checkout(dashC, tail[1..]),
    "reset" => RepoCommands.Reset(dashC, tail[1..]),
    "restore" => RepoCommands.Restore(dashC, tail[1..]),
    "revert" => RepoCommands.Revert(dashC, tail[1..]),
    "branch" => RepoCommands.Branch(dashC, tail[1..]),
    "tag" => RepoCommands.Tag(dashC, tail[1..]),
    "merge" => RepoCommands.Merge(dashC, tail[1..]),
    "cherry-pick" => RepoCommands.CherryPick(dashC, tail[1..]),
    "rebase" => RepoCommands.RebaseCmd(dashC, tail[1..]),
    "stash" => RepoCommands.StashCmd(dashC, tail[1..]),
    "clean" => RepoCommands.Clean(dashC, tail[1..]),
    "config" => RepoCommands.Config(dashC, tail[1..]),
    "remote" => RepoCommands.Remote(dashC, tail[1..]),
    "clone" => RepoCommands.Clone(dashC, tail[1..]),
    "fetch" => RepoCommands.Fetch(dashC, tail[1..]),
    "push" => RepoCommands.Push(dashC, tail[1..]),
    "ls-remote" => RepoCommands.LsRemote(dashC, tail[1..]),
    "verify-remote" => RepoCommands.VerifyRemote(dashC, tail[1..]),
    "serve" => RepoCommands.Serve(dashC, tail[1..]),
    "serve-stdio" => RepoCommands.ServeStdio(dashC, tail[1..]),
    "reflog" => RepoCommands.Reflog(dashC, tail[1..]),
    "gc" => RepoCommands.GcCmd(dashC, tail[1..]),
    "fsck" => RepoCommands.FsckCmd(dashC, tail[1..]),
    "inspect" => QueryCommands.Inspect(dashC, tail[1..]),
    "find" => QueryCommands.Find(dashC, tail[1..]),
    "players" => QueryCommands.Players(dashC, tail[1..]),
    "poi" => QueryCommands.Poi(dashC, tail[1..]),
    "where-changed" => QueryCommands.WhereChanged(dashC, tail[1..]),
    "rev-parse" => RepoCommands.RevParse(dashC, tail[1..]),
    "cat-file" => RepoCommands.CatFile(dashC, tail[1..]),
    "hash-object" => RepoCommands.HashObject(dashC, tail[1..]),
    "ls-tree" => RepoCommands.LsTree(dashC, tail[1..]),
    "-h" or "--help" or "help" or "/?" or "-?" => Help(),
    null => ShortHelp(),
    _ => UnknownOrDiff(tail, dashC),
};

int Help() { Console.WriteLine(TopUsage); return 0; }

// Goal-oriented screen for a bare invocation — the full git-style menu is behind `--help`.
int ShortHelp()
{
    Console.WriteLine("""
        mcagit — back up, compare, and restore Minecraft worlds.

          Set it up:        mcagit init <repo>.mcagit --worktree <world>
          Save a backup:    mcagit -C <repo> backup -m "before the raid"
          See what changed: mcagit -C <repo> diff
          Go back:          mcagit -C <repo> undo            (discard changes since the last backup)
                            mcagit -C <repo> checkout <ref>  (restore a specific backup)
          List backups:     mcagit -C <repo> log --oneline

        Full command list: mcagit --help
        """);
    return 0;
}

// `backup` is `commit` with a friendly default message when none is given.
static string[] BackupArgs(string? verb, string[] a)
{
    if (verb != "backup" || a.Contains("-m") || a.Contains("--message")) return a;
    return [.. a, "-m", $"backup {DateTime.Now:yyyy-MM-dd HH:mm}"];
}

// `mcagit <A> <B>` shorthand only when the first token is an existing path; otherwise it's a
// mistyped/unknown subcommand — reject it (don't silently run a diff) and suggest the closest.
int UnknownOrDiff(string[] t, string? repoDir)
{
    if (t.Length >= 1 && (File.Exists(t[0]) || Directory.Exists(t[0])))
        return RunDiff(t, repoDir);
    string token = t.Length > 0 ? t[0] : "";
    string? guess = NearestCommand(token);
    Console.Error.WriteLine($"mcagit: '{token}' is not a command"
        + (guess is not null ? $" — did you mean '{guess}'?" : "."));
    Console.Error.WriteLine("Run 'mcagit --help' for usage.");
    return 2;
}

static string? NearestCommand(string token)
{
    string[] commands =
    [
        "diff", "extract", "apply", "init", "add", "commit", "bisect", "log", "show", "status",
        "checkout", "reset", "restore", "revert", "branch", "tag", "merge", "cherry-pick", "rebase",
        "stash", "clean", "config", "remote", "clone", "fetch", "push", "ls-remote", "verify-remote",
        "serve", "reflog", "gc", "fsck", "rev-parse", "cat-file", "hash-object", "ls-tree",
        "inspect", "find", "players", "poi", "where-changed", "backup", "undo",
    ];
    string? best = null;
    int bestD = int.MaxValue;
    foreach (string c in commands)
    {
        int d = Levenshtein(token, c);
        if (d < bestD) { bestD = d; best = c; }
    }
    return bestD <= 2 ? best : null; // only suggest a close match
}

static int Levenshtein(string a, string b)
{
    int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
    for (int j = 0; j <= b.Length; j++) prev[j] = j;
    for (int i = 1; i <= a.Length; i++)
    {
        cur[0] = i;
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
        }
        (prev, cur) = (cur, prev);
    }
    return prev[b.Length];
}

int RunDiff(string[] a, string? repoDir)
{
    DiffOptions o = DiffOptions.Parse(a);
    if (o.ShowHelp) { Console.WriteLine(DiffOptions.Usage); return 0; }
    if (o.Error is not null) return Fail(o.Error, DiffOptions.Usage);

    try
    {
        Repository? repo = Repository.Discover(repoDir);
        WorldDiff diff;
        if (repo is not null)
        {
            diff = o.Staged ? StagedDiff(repo, o.ToRunOptions()) : RepoDiffMode(repo, o.Positionals, o.ToRunOptions());
        }
        else
        {
            if (o.Positionals.Count != 2) return Fail("diff needs two paths, or run inside a repository", DiffOptions.Usage);
            diff = RunFileDiff(o.Positionals[0], o.Positionals[1], o.ToRunOptions());
        }

        if (o.Json) JsonDiffFormatter.Write(diff, Console.Out);
        else new TextDiffFormatter(new Ansi(Ansi.ShouldColor(o.NoColor)), o.SummaryOnly).Write(diff, Console.Out);
        return diff.HasDifferences ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static WorldDiff RunFileDiff(string pathA, string pathB, DiffRunOptions opt)
{
    foreach (string p in new[] { pathA, pathB })
        if (!File.Exists(p) && !Directory.Exists(p))
            throw new FileNotFoundException($"path not found: {p}");
    // A folder that's neither a world nor a region/loose file would diff as "No differences" —
    // false reassurance. Warn so the user knows they pointed at the wrong thing (off-by-one folder).
    foreach (string p in new[] { pathA, pathB })
        if (Directory.Exists(p) && !File.Exists(Path.Combine(p, "level.dat")) && !Directory.Exists(Path.Combine(p, "region")))
            Console.Error.WriteLine($"mcagit: warning: '{p}' has no level.dat or region/ — is it a Minecraft world?");
    return WorldDiffer.Diff(pathA, pathB, opt);
}

static WorldDiff RepoDiffMode(Repository repo, List<string> pos, DiffRunOptions opt)
{
    (string a, string b) = pos.Count switch
    {
        0 => ("HEAD", Worktree(repo)),
        1 => (pos[0], Worktree(repo)),
        2 => (pos[0], pos[1]),
        _ => throw new InvalidOperationException("diff takes at most two refs"),
    };
    var (mA, srcA, labelA) = DiffSide(repo, a);
    var (mB, srcB, labelB) = DiffSide(repo, b);
    return RepoDiffer.Diff(labelA, mA, srcA, labelB, mB, srcB, opt);
}

static WorldDiff StagedDiff(Repository repo, DiffRunOptions opt)
{
    Manifest head = repo.HeadCommit() is { } h ? repo.ReadManifest(repo.ReadCommit(h).Tree) : new Manifest();
    Manifest idx = StagingIndex.Exists(repo) ? StagingIndex.Load(repo) : head; // nothing staged → no diff
    return RepoDiffer.Diff(
        "HEAD", head, new RepoDiffer.CommitSource(repo, head),
        "index", idx, new RepoDiffer.CommitSource(repo, idx), opt);
}

static string Worktree(Repository repo) =>
    repo.Worktree ?? throw new InvalidOperationException("no worktree bound; pass paths or set `config worktree`");

static (Manifest, RepoDiffer.IContentSource, string) DiffSide(Repository repo, string spec)
{
    try
    {
        string commit = repo.ResolveRef(spec);
        Manifest m = repo.ReadManifest(repo.ReadCommit(commit).Tree);
        return (m, new RepoDiffer.CommitSource(repo, m), $"{spec} ({commit[..10]})");
    }
    catch (Exception) when (Directory.Exists(spec))
    {
        Manifest m = Snapshotter.HashOnly(repo, spec);
        return (m, new RepoDiffer.WorldContentSource(spec), $"{spec} (working)");
    }
}

int RunExtract(string[] a)
{
    ExtractOptions o = ExtractOptions.Parse(a);
    if (o.ShowHelp) { Console.WriteLine(ExtractOptions.Usage); return 0; }
    if (o.Error is not null) return Fail(o.Error, ExtractOptions.Usage);
    if (MissingPath(o.OldPath!) is { } m1) return Fail(m1);
    if (MissingPath(o.NewPath!) is { } m2) return Fail(m2);

    try
    {
        WorldPatch patch = PatchExtractor.Extract(o.OldPath!, o.NewPath!, o.ToRunOptions(), o.WholeChunk, o.WholeFile);
        if (o.Note is not null) patch.Note = o.Note;
        File.WriteAllText(o.OutputPath!, patch.ToJson());
        int ops = patch.Files.Sum(f => (f.Ops?.Count ?? 0) + (f.Chunks?.Sum(c => c.Ops.Count) ?? 0));
        Console.Error.WriteLine($"Wrote {o.OutputPath} — {patch.Files.Count} files, {ops} ops.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

int RunApply(string[] a)
{
    ApplyOptions o = ApplyOptions.Parse(a);
    if (o.ShowHelp) { Console.WriteLine(ApplyOptions.Usage); return 0; }
    if (o.Error is not null) return Fail(o.Error, ApplyOptions.Usage);
    if (!File.Exists(o.PatchPath!)) return Fail($"patch not found: {o.PatchPath}");
    if (!Directory.Exists(o.TargetPath!)) return Fail($"target world not found: {o.TargetPath}");

    try
    {
        WorldPatch patch = WorldPatch.FromJson(File.ReadAllText(o.PatchPath!));

        // Warn (don't block) on a DataVersion mismatch — paths can silently shift across MC versions,
        // surfacing as guarded conflicts. Forward apply expects the base version; reverse the target's.
        int? expected = o.Reverse ? patch.TargetDataVersion : patch.BaseDataVersion;
        int? actual = WorldSource.DataVersion(o.TargetPath!);
        if (expected is { } e && actual is { } act && e != act)
            Console.Error.WriteLine($"mcagit: warning: patch was made for DataVersion {e} but the target is {act} — "
                + "paths may not match (see README \"Patches are version-specific\").");

        var settings = new ApplySettings(o.Reverse, o.Force, o.DryRun, o.Only);
        ApplyReport report = PatchApplier.Apply(patch, o.TargetPath!, o.OutputPath ?? o.TargetPath!, settings);
        string mode = o.DryRun ? "[dry-run] " : "";
        Console.Error.WriteLine($"{mode}Applied {report.Applied} ops across {report.FilesWritten} files; {report.Conflicts.Count} conflicts.");
        foreach (Conflict c in report.Conflicts.Take(20))
            Console.Error.WriteLine($"  conflict: {c.File}{(c.Chunk is null ? "" : $" chunk {c.Chunk}")} {c.Path} — {c.Reason}");
        if (report.Conflicts.Count > 20) Console.Error.WriteLine($"  … and {report.Conflicts.Count - 20} more");
        return report.HasConflicts ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static string? MissingPath(string p) => File.Exists(p) || Directory.Exists(p) ? null : $"path not found: {p}";

static int Fail(string message, string? usage = null)
{
    Console.Error.WriteLine($"mcagit: {message}");
    if (usage is not null) { Console.Error.WriteLine(); Console.Error.WriteLine(usage); }
    return 2;
}

partial class Program
{
    private const string TopUsage = """
        mcagit — semantic diff, patch & version control for Anvil Minecraft worlds

        Add `-C <repo>` before any command to select the repository (default: the
        current directory or nearest ancestor).

        DIFF / PATCH
            mcagit diff <A> <B>                  Git-style diff of two worlds/files
            mcagit diff [<a> [<b>]]              In a repo: worktree vs HEAD, <a> vs
                                                  worktree, or <a> vs <b> (refs/worlds)
            mcagit extract <old> <new> -o <p>    Write a portable patch
            mcagit apply <patch> <target> -o <o> Apply a patch (non-destructive)

        REPOSITORY (content-addressed, deduplicated)
            mcagit init [<repo>] [--worktree <world>]
            mcagit add <path>... | add .         Stage paths into the index
            mcagit commit [-m <msg>] [<world>] [--push <remote>] [--json]
                                                  Commit the index/worktree; optionally
                                                  push, and emit a machine-readable result
            mcagit restore --staged <path>...    Unstage paths (index → HEAD)
            mcagit status [<world>]              Staged / unstaged changes
            mcagit diff --staged                 Staged changes (index vs HEAD)
            mcagit bisect (start|bad|good|skip|reset|log)   Binary-search for a bad commit
            mcagit log [--oneline|-p|--stat] [-n N] [<ref>]
                        [--author S] [--grep S] [--since D] [--until D] [--merges|--no-merges] [--decorate] [--all]
            mcagit show [<ref>]                  A commit's metadata + diff
            mcagit checkout <ref> [<world-out>] [--force] [-y]   Materialize a snapshot
            mcagit reset [<ref>] [--soft|--mixed|--hard] [-y]   Move HEAD (default --mixed, ref HEAD)
            mcagit restore <ref> <path>...       Restore paths from a snapshot
            mcagit revert <commit> | --continue | --abort   Undo a commit (stops on conflict)
            mcagit branch [<name> [<start>]] | -d <name> | -m <old> <new>
            mcagit tag [-a -m <msg> [-s]] [-f] [<name> [<ref>]] | -d <name> | -v <name>
            mcagit merge <ref> [--theirs|--ours]  3-way merge (stops on conflict)
            mcagit merge --continue | --abort     Finish / undo a conflicted merge
            mcagit cherry-pick <commit> | --continue | --abort   (stops on conflict)
            mcagit rebase [--onto <base>] <up> | --continue | --skip | --abort
            mcagit stash [push|list|pop|apply|drop|clear]   Shelve / restore the worktree
            mcagit clean [-n|-f] [-d] [-y]      Remove untracked worktree files (-d: also dirs; -y: no prompt)
            mcagit commit -S …                   Sign the commit (SSH key)
            mcagit config [--global] <key> [<v>] | --list | --unset <key>

        REMOTES (path, http://, ssh://, azure://, s3://) & MAINTENANCE
            mcagit clone <src> <dest> [--depth N] [--token T]   --depth N: shallow clone (last N commits)
            mcagit remote [add|remove|rename|set-url|get-url ...]   url: path | http(s):// | ssh:// | azure:// | s3://
            mcagit fetch [<remote> [<branch>]] [--token T]
            mcagit push  [<remote> [<branch>]] [--force|--all] [--token T]
            mcagit ls-remote [<remote>] [--token T]   List a remote's refs
            mcagit verify-remote [<remote>] [--deep] [--token T]   Check offsite integrity (--deep: hash every object)
            mcagit serve [<repo>] [--port N] [--host H] [--allow-push] [--token T]
            mcagit reflog                        HEAD movement history
            mcagit gc                            Prune unreachable objects
            mcagit fsck                          Verify object integrity + reachability

        WORLD INSPECTION (read-only; 0 found / 1 none / 2 error)
            mcagit inspect <x> <y> <z> [<world>] [--dim D] [--json]   Block + biome at a coordinate
            mcagit find <entity|block-entity|sign> <id|--text P> [<world>] [--near x,y,z] [--radius N] [--dim D] [--json]
            mcagit players [<world>] [--json]            Last-saved player positions / health
            mcagit poi [<world>] [--type T] [--near x,y,z] [--radius N] [--dim D]   Points of interest
            mcagit where-changed <old-world> <new-world> [--verbose] [--json]   What/where blocks changed (grief detector)

        PLUMBING
            mcagit rev-parse [--short|--abbrev-ref] <rev>...
            mcagit cat-file (-t|-s|-p|-e) <object>
            mcagit hash-object [-w] <file>
            mcagit ls-tree [-r] [--name-only] <tree-ish>

        Identity comes from user.name / user.email config; signing uses user.signingkey
        (an SSH private key) and gpg.ssh.allowedSignersFile for verification.

        Revisions accept HEAD, branches, tags, short hashes, and ~n / ^n suffixes.

        Run diff/extract/apply with --help for their options.
        """;
}
