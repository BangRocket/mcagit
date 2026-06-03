using McaDiff.Diff;

namespace McaDiff.Cli;

/// <summary>Parsed command-line options.</summary>
public sealed class DiffOptions
{
    public string? PathA { get; private set; }
    public string? PathB { get; private set; }
    public bool Json { get; private set; }
    public bool Expand { get; private set; }
    public bool NoColor { get; private set; }
    public bool SummaryOnly { get; private set; }
    public bool ShowHelp { get; private set; }
    public HashSet<string>? Only { get; private set; }
    public string? Error { get; private set; }

    private static readonly HashSet<string> ValidCategories =
        new(StringComparer.OrdinalIgnoreCase) { "region", "entities", "poi", "nbt" };

    public DiffRunOptions ToRunOptions() => new(Expand, Only);

    public static DiffOptions Parse(string[] args)
    {
        var o = new DiffOptions();
        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    o.ShowHelp = true;
                    return o;
                case "--json":
                    o.Json = true;
                    break;
                case "--expand":
                    o.Expand = true;
                    break;
                case "--no-color":
                    o.NoColor = true;
                    break;
                case "--summary":
                    o.SummaryOnly = true;
                    break;
                case "--only":
                    if (i + 1 >= args.Length)
                        return o.Fail("--only requires a value (region,entities,poi,nbt)");
                    foreach (string cat in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!ValidCategories.Contains(cat))
                            return o.Fail($"unknown category '{cat}' (valid: region, entities, poi, nbt)");
                        (o.Only ??= new(StringComparer.OrdinalIgnoreCase)).Add(cat);
                    }
                    break;
                default:
                    if (a.StartsWith('-'))
                        return o.Fail($"unknown option '{a}'");
                    positionals.Add(a);
                    break;
            }
        }

        if (positionals.Count != 2)
            return o.Fail($"expected 2 paths, got {positionals.Count}");

        o.PathA = positionals[0];
        o.PathB = positionals[1];
        return o;
    }

    private DiffOptions Fail(string message)
    {
        Error = message;
        return this;
    }

    public const string Usage = """
        mcadiff — semantic diff for Anvil Minecraft worlds

        USAGE:
            mcadiff [options] <A> <B>

            <A> <B>   Two world folders, or two single files (.mca / .dat).

        OPTIONS:
            --json            Emit a structured JSON change list instead of text.
            --expand          Show every changed array index (default: summarize).
            --only <cats>     Limit to categories: region,entities,poi,nbt (comma-separated, repeatable).
            --summary         Per-file status and counts only; omit per-change detail.
            --no-color        Disable ANSI color (also honors NO_COLOR; auto-off when piped).
            -h, --help        Show this help.

        EXIT CODES:
            0  no differences        1  differences found        2  error
        """;
}
