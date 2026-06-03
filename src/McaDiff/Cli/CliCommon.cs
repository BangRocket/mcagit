namespace McaDiff.Cli;

/// <summary>Shared command-line parsing helpers.</summary>
public static class CliCommon
{
    private static readonly HashSet<string> ValidCategories =
        new(StringComparer.OrdinalIgnoreCase) { "region", "entities", "poi", "nbt" };

    /// <summary>
    /// Parses a comma-separated category list, merging into <paramref name="set"/>
    /// (created if null). Returns false with <paramref name="error"/> on an unknown category.
    /// </summary>
    public static bool ParseCategories(string value, ref HashSet<string>? set, out string? error)
    {
        foreach (string cat in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ValidCategories.Contains(cat))
            {
                error = $"unknown category '{cat}' (valid: region, entities, poi, nbt)";
                return false;
            }
            (set ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(cat);
        }
        error = null;
        return true;
    }
}
