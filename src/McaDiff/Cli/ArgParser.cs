namespace McaDiff.Cli;

/// <summary>Minimal flag parser shared by the CLI command groups: value flags consume the next token,
/// bool flags don't, everything else is positional.</summary>
public static class ArgParser
{
    public static (List<string> Positionals, Dictionary<string, string?> Opts) Parse(
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
}
