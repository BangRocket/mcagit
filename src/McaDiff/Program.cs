using McaDiff.Cli;
using McaDiff.Diff;
using McaDiff.Output;

DiffOptions options = DiffOptions.Parse(args);

if (options.ShowHelp)
{
    Console.WriteLine(DiffOptions.Usage);
    return 0;
}

if (options.Error is not null)
{
    Console.Error.WriteLine($"mcadiff: {options.Error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(DiffOptions.Usage);
    return 2;
}

string pathA = options.PathA!;
string pathB = options.PathB!;
foreach (string p in new[] { pathA, pathB })
{
    if (!File.Exists(p) && !Directory.Exists(p))
    {
        Console.Error.WriteLine($"mcadiff: path not found: {p}");
        return 2;
    }
}

try
{
    WorldDiff diff = WorldDiffer.Diff(pathA, pathB, options.ToRunOptions());

    if (options.Json)
    {
        JsonDiffFormatter.Write(diff, Console.Out);
    }
    else
    {
        var ansi = new Ansi(Ansi.ShouldColor(options.NoColor));
        new TextDiffFormatter(ansi, options.SummaryOnly).Write(diff, Console.Out);
    }

    return diff.HasDifferences ? 1 : 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"mcadiff: {ex.Message}");
    return 2;
}
