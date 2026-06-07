namespace McaDiff.Cli;

/// <summary>Parsed options for <c>mcagit apply</c>.</summary>
public sealed class ApplyOptions
{
    public string? PatchPath { get; private set; }
    public string? TargetPath { get; private set; }
    public string? OutputPath { get; private set; }
    public bool Reverse { get; private set; }
    public bool Force { get; private set; }
    public bool DryRun { get; private set; }
    public bool ShowHelp { get; private set; }
    public HashSet<string>? Only { get; private set; }
    public string? Error { get; private set; }

    public static ApplyOptions Parse(string[] args)
    {
        var o = new ApplyOptions();
        var positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help": o.ShowHelp = true; return o;
                case "-o" or "--output":
                    if (i + 1 >= args.Length) return o.Fail("-o/--output requires a directory path");
                    o.OutputPath = args[++i];
                    break;
                case "--reverse": o.Reverse = true; break;
                case "--force": o.Force = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--only":
                    if (i + 1 >= args.Length) return o.Fail("--only requires a value (region,entities,poi,nbt)");
                    HashSet<string>? only = o.Only;
                    if (!CliCommon.ParseCategories(args[++i], ref only, out string? err)) return o.Fail(err!);
                    o.Only = only;
                    break;
                default:
                    if (a.StartsWith('-')) return o.Fail($"unknown option '{a}'");
                    positionals.Add(a);
                    break;
            }
        }
        if (positionals.Count != 2) return o.Fail($"expected <patch> <target>, got {positionals.Count} paths");
        if (o.OutputPath is null && !o.DryRun) return o.Fail("-o/--output <dir> is required (unless --dry-run)");
        o.PatchPath = positionals[0];
        o.TargetPath = positionals[1];
        return o;
    }

    private ApplyOptions Fail(string message) { Error = message; return this; }

    public const string Usage = """
        mcagit apply — apply a patch to a target world, non-destructively

        USAGE:
            mcagit apply [options] <patch.mcapatch> <target> -o <output-dir>

        The target world is copied to <output-dir>; only patched nodes are rewritten,
        each guarded so it never clobbers unexpected data (a mismatch is reported as a
        conflict and skipped).

        OPTIONS:
            -o, --output <dir>    Output world directory to create (required unless --dry-run).
            --reverse             Apply in reverse (restore the OLD state onto a newer world).
            --force               Skip the base-value guard (apply unconditionally).
            --dry-run             Report what would change / conflict; write nothing.
            --only <cats>         Limit to region,entities,poi,nbt (comma-separated).
            -h, --help            Show this help.

        EXIT CODES:
            0  applied cleanly        1  conflicts were skipped        2  error
        """;
}
