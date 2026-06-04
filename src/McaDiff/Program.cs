using McaDiff.Cli;
using McaDiff.Diff;
using McaDiff.Output;
using McaDiff.Patch;
using McaDiff.Repo;

// Global, git-style: an optional leading `-C <repo>` selects the repository
// (otherwise it's discovered from the current directory). `diff` is the default
// when the first token isn't a known subcommand, so `mcadiff <A> <B>` still works.
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

return cmd switch
{
    "diff" => RunDiff(tail[1..], dashC),
    "extract" => RunExtract(tail[1..]),
    "apply" => RunApply(tail[1..]),
    "init" => RepoCommands.Init(dashC, tail[1..]),
    "add" => RepoCommands.Add(dashC, tail[1..]),
    "commit" => RepoCommands.Commit(dashC, tail[1..]),
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
    "rev-parse" => RepoCommands.RevParse(dashC, tail[1..]),
    "cat-file" => RepoCommands.CatFile(dashC, tail[1..]),
    "hash-object" => RepoCommands.HashObject(dashC, tail[1..]),
    "ls-tree" => RepoCommands.LsTree(dashC, tail[1..]),
    null or "-h" or "--help" => Help(),
    _ => RunDiff(tail, dashC), // shorthand: `mcadiff <A> <B>`
};

int Help() { Console.WriteLine(TopUsage); return 0; }

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
    Console.Error.WriteLine($"mcadiff: {message}");
    if (usage is not null) { Console.Error.WriteLine(); Console.Error.WriteLine(usage); }
    return 2;
}

partial class Program
{
    private const string TopUsage = """
        mcadiff — semantic diff, patch & version control for Anvil Minecraft worlds

        Add `-C <repo>` before any command to select the repository (default: the
        current directory or nearest ancestor).

        DIFF / PATCH
            mcadiff diff <A> <B>                  Git-style diff of two worlds/files
            mcadiff diff [<a> [<b>]]              In a repo: worktree vs HEAD, <a> vs
                                                  worktree, or <a> vs <b> (refs/worlds)
            mcadiff extract <old> <new> -o <p>    Write a portable patch
            mcadiff apply <patch> <target> -o <o> Apply a patch (non-destructive)

        REPOSITORY (content-addressed, deduplicated)
            mcadiff init [<repo>] [--worktree <world>]
            mcadiff add <path>... | add .         Stage paths into the index
            mcadiff commit [-m <msg>] [<world>] [--push <remote>] [--json]
                                                  Commit the index/worktree; optionally
                                                  push, and emit a machine-readable result
            mcadiff restore --staged <path>...    Unstage paths (index → HEAD)
            mcadiff status [<world>]              Staged / unstaged changes
            mcadiff diff --staged                 Staged changes (index vs HEAD)
            mcadiff bisect (start|bad|good|skip|reset|log)   Binary-search for a bad commit
            mcadiff log [--oneline|-p|--stat] [-n N] [<ref>]
            mcadiff show [<ref>]                  A commit's metadata + diff
            mcadiff checkout <ref> [<world-out>]  Materialize a snapshot
            mcadiff reset [<ref>] [--soft|--mixed|--hard]   Move HEAD (default --mixed, ref HEAD)
            mcadiff restore <ref> <path>...       Restore paths from a snapshot
            mcadiff revert <commit> | --continue | --abort   Undo a commit (stops on conflict)
            mcadiff branch [<name> [<start>]] | -d <name> | -m <old> <new>
            mcadiff tag [-a -m <msg> [-s]] [-f] [<name> [<ref>]] | -d <name> | -v <name>
            mcadiff merge <ref> [--theirs|--ours]  3-way merge (stops on conflict)
            mcadiff merge --continue | --abort     Finish / undo a conflicted merge
            mcadiff cherry-pick <commit> | --continue | --abort   (stops on conflict)
            mcadiff rebase [--onto <base>] <up> | --continue | --skip | --abort
            mcadiff stash [push|list|pop|apply|drop|clear]   Shelve / restore the worktree
            mcadiff clean [-n|-f]                 Remove untracked worktree files
            mcadiff commit -S …                   Sign the commit (SSH key)
            mcadiff config [--global] <key> [<v>] | --list | --unset <key>

        REMOTES (path, http://, ssh://) & MAINTENANCE
            mcadiff clone <src> <dest> [--depth N] [--token T]   --depth N: shallow clone (last N commits)
            mcadiff remote [add <name> <url>]     url: path | http(s)://host:port | ssh://host/path
            mcadiff fetch [<remote> [<branch>]] [--token T]
            mcadiff push  [<remote> [<branch>]] [--force|--all] [--token T]
            mcadiff ls-remote [<remote>] [--token T]   List a remote's refs
            mcadiff verify-remote [<remote>] [--deep] [--token T]   Check offsite integrity (--deep: hash every object)
            mcadiff serve [<repo>] [--port N] [--host H] [--allow-push] [--token T]
            mcadiff reflog                        HEAD movement history
            mcadiff gc                            Prune unreachable objects
            mcadiff fsck                          Verify object integrity + reachability

        PLUMBING
            mcadiff rev-parse [--short|--abbrev-ref] <rev>...
            mcadiff cat-file (-t|-s|-p|-e) <object>
            mcadiff hash-object [-w] <file>
            mcadiff ls-tree [-r] [--name-only] <tree-ish>

        Identity comes from user.name / user.email config; signing uses user.signingkey
        (an SSH private key) and gpg.ssh.allowedSignersFile for verification.

        Revisions accept HEAD, branches, tags, short hashes, and ~n / ^n suffixes.

        Run diff/extract/apply with --help for their options.
        """;
}
