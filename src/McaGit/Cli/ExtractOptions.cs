using McaGit.Diff;

namespace McaGit.Cli;

/// <summary>Parsed options for <c>mcagit extract</c>.</summary>
public sealed class ExtractOptions
{
    public string? OldPath { get; private set; }
    public string? NewPath { get; private set; }
    public string? OutputPath { get; private set; }
    public string? Note { get; private set; }
    public bool WholeChunk { get; private set; }
    public bool WholeFile { get; private set; }
    public bool ShowHelp { get; private set; }
    public HashSet<string>? Only { get; private set; }
    public string? Error { get; private set; }

    public DiffRunOptions ToRunOptions() => new(ExpandArrays: false, OnlyCategories: Only);

    public static ExtractOptions Parse(string[] args)
    {
        var o = new ExtractOptions();
        var positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help": o.ShowHelp = true; return o;
                case "-o" or "--output":
                    if (i + 1 >= args.Length) return o.Fail("-o/--output requires a file path");
                    o.OutputPath = args[++i];
                    break;
                case "--note":
                    if (i + 1 >= args.Length) return o.Fail("--note requires a value");
                    o.Note = args[++i];
                    break;
                case "--whole-chunk": o.WholeChunk = true; break;
                case "--whole-file": o.WholeFile = true; break;
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
        if (positionals.Count != 2) return o.Fail($"expected <old> <new>, got {positionals.Count} paths");
        if (o.OutputPath is null) return o.Fail("-o/--output <patch file> is required");
        o.OldPath = positionals[0];
        o.NewPath = positionals[1];
        return o;
    }

    private ExtractOptions Fail(string message) { Error = message; return this; }

    public const string Usage = """
        mcagit extract — write a portable patch of the changes from <old> to <new>

        USAGE:
            mcagit extract [options] <old> <new> -o <patch.mcapatch>

        OPTIONS:
            -o, --output <file>   Patch file to write (required).
            --only <cats>         Limit to region,entities,poi,nbt (comma-separated).
            --whole-chunk         Store whole chunk roots instead of node-level ops.
            --whole-file          Store whole loose-file roots instead of node-level ops.
            --note <text>         Free-text note embedded in the patch.
            -h, --help            Show this help.

        The patch stores both old and new values, so it can later be applied forward
        or in reverse (see `mcagit apply`).
        """;
}
