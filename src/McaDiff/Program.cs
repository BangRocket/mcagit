using McaDiff.Cli;
using McaDiff.Diff;
using McaDiff.Output;
using McaDiff.Patch;

// Subcommand dispatch. `diff` is the default so `mcadiff <A> <B>` still works.
if (args.Length > 0)
{
    switch (args[0])
    {
        case "diff": return RunDiff(args[1..]);
        case "extract": return RunExtract(args[1..]);
        case "apply": return RunApply(args[1..]);
        case "-h" or "--help": Console.WriteLine(TopUsage); return 0;
    }
}
return RunDiff(args);

int RunDiff(string[] a)
{
    DiffOptions options = DiffOptions.Parse(a);
    if (options.ShowHelp) { Console.WriteLine(DiffOptions.Usage); return 0; }
    if (options.Error is not null) return Fail(options.Error, DiffOptions.Usage);

    string pathA = options.PathA!, pathB = options.PathB!;
    if (MissingPath(pathA) is { } m1) return Fail(m1);
    if (MissingPath(pathB) is { } m2) return Fail(m2);

    try
    {
        WorldDiff diff = WorldDiffer.Diff(pathA, pathB, options.ToRunOptions());
        if (options.Json)
            JsonDiffFormatter.Write(diff, Console.Out);
        else
            new TextDiffFormatter(new Ansi(Ansi.ShouldColor(options.NoColor)), options.SummaryOnly).Write(diff, Console.Out);
        return diff.HasDifferences ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
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
        if (report.Conflicts.Count > 20)
            Console.Error.WriteLine($"  … and {report.Conflicts.Count - 20} more");
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
        mcadiff — semantic diff & patch for Anvil Minecraft worlds

        USAGE:
            mcadiff diff    <A> <B> [options]                 Show a git-style diff
            mcadiff extract <old> <new> -o <patch> [options]  Write a portable patch
            mcadiff apply   <patch> <target> -o <out> [opts]  Apply a patch (non-destructive)

            mcadiff <A> <B>                                   Shorthand for `diff`

        Run any subcommand with --help for its options.
        """;
}
