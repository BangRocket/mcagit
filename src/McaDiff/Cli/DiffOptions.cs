using McaDiff.Diff;

namespace McaDiff.Cli;

/// <summary>Parsed options for <c>diff</c> (file mode or, inside a repo, ref mode).</summary>
public sealed class DiffOptions
{
    public List<string> Positionals { get; } = [];
    public bool Json { get; private set; }
    public bool Expand { get; private set; }
    public bool NoColor { get; private set; }
    public bool SummaryOnly { get; private set; }
    public bool ShowHelp { get; private set; }
    public HashSet<string>? Only { get; private set; }
    public string? Error { get; private set; }

    public DiffRunOptions ToRunOptions() => new(Expand, Only);

    public static DiffOptions Parse(string[] args)
    {
        var o = new DiffOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help": o.ShowHelp = true; return o;
                case "--json": o.Json = true; break;
                case "--expand": o.Expand = true; break;
                case "--no-color": o.NoColor = true; break;
                case "--summary": o.SummaryOnly = true; break;
                case "--only":
                    if (i + 1 >= args.Length) return o.Fail("--only requires a value (region,entities,poi,nbt)");
                    HashSet<string>? only = o.Only;
                    if (!CliCommon.ParseCategories(args[++i], ref only, out string? err)) return o.Fail(err!);
                    o.Only = only;
                    break;
                default:
                    if (a.StartsWith('-')) return o.Fail($"unknown option '{a}'");
                    o.Positionals.Add(a);
                    break;
            }
        }
        return o;
    }

    private DiffOptions Fail(string message) { Error = message; return this; }

    public const string Usage = """
        mcadiff diff — show a git-style diff

        USAGE:
            mcadiff diff <A> <B>                 Two world folders or files (.mca/.dat)
            mcadiff [-C <repo>] diff [<a> [<b>]] Inside a repo, compare snapshots:
                                                   (no args)  worktree vs HEAD
                                                   <a>        <a> vs worktree
                                                   <a> <b>    <a> vs <b>
                                                 where each <a>/<b> is a branch, commit,
                                                 HEAD, or a path to a working world.

        OPTIONS:
            --json        Structured JSON instead of text.
            --expand      Show every changed array index (default: summarize).
            --only <cats> Limit to region,entities,poi,nbt (comma-separated).
            --summary     Per-file status and totals only.
            --no-color    Disable ANSI color (also honors NO_COLOR; auto-off when piped).
            -h, --help    Show this help.
        """;
}
