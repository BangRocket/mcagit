namespace McaGit.Output;

/// <summary>Minimal ANSI colorizer that no-ops when color is disabled.</summary>
public sealed class Ansi
{
    private const string Reset = "[0m";
    private readonly bool _on;

    public Ansi(bool on) => _on = on;

    public bool Enabled => _on;

    public string Green(string s) => Wrap(s, "32");
    public string Red(string s) => Wrap(s, "31");
    public string Yellow(string s) => Wrap(s, "33");
    public string Magenta(string s) => Wrap(s, "35");
    public string Cyan(string s) => Wrap(s, "36");
    public string Bold(string s) => Wrap(s, "1");
    public string Dim(string s) => Wrap(s, "90");

    private string Wrap(string s, string code) => _on ? $"[{code}m{s}{Reset}" : s;

    /// <summary>Color on iff not suppressed and stdout is an interactive terminal.</summary>
    public static bool ShouldColor(bool noColorFlag)
    {
        if (noColorFlag) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
        return !Console.IsOutputRedirected;
    }
}
