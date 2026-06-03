using System.Text.RegularExpressions;

namespace McaDiff.Repo;

/// <summary>
/// gitignore-lite: patterns from a world's <c>.mcaignore</c> that exclude files
/// from being committed. Supported forms: <c>name</c> (a file or dir of that name
/// anywhere), <c>dir/</c> (any directory of that name), <c>*.ext</c> / globs
/// (matched against the file name), and <c>/anchored/path</c> (relative to the
/// world root). Blank lines and <c>#</c> comments are ignored.
/// </summary>
public sealed class IgnoreRules
{
    private readonly List<Rule> _rules;
    private IgnoreRules(List<Rule> rules) => _rules = rules;

    public static IgnoreRules Empty { get; } = new([]);

    public static IgnoreRules Load(string worldDir)
    {
        string path = Path.Combine(worldDir, ".mcaignore");
        if (!File.Exists(path)) return Empty;
        var rules = new List<Rule>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            rules.Add(Rule.Parse(line));
        }
        return new IgnoreRules(rules);
    }

    /// <summary>True if the forward-slash relative path should be excluded.</summary>
    public bool IsIgnored(string rel)
    {
        foreach (Rule r in _rules)
            if (r.Matches(rel))
                return true;
        return false;
    }

    private sealed class Rule
    {
        private readonly string _pattern;
        private readonly bool _anchored;
        private readonly bool _dir;
        private readonly Regex? _glob;

        private Rule(string pattern, bool anchored, bool dir, Regex? glob)
        {
            _pattern = pattern;
            _anchored = anchored;
            _dir = dir;
            _glob = glob;
        }

        public static Rule Parse(string line)
        {
            bool anchored = line.StartsWith('/');
            if (anchored) line = line[1..];
            bool dir = line.EndsWith('/');
            if (dir) line = line[..^1];
            Regex? glob = line.Contains('*') || line.Contains('?')
                ? new Regex("^" + Regex.Escape(line).Replace("\\*", ".*").Replace("\\?", ".") + "$")
                : null;
            return new Rule(line, anchored, dir, glob);
        }

        public bool Matches(string rel)
        {
            string[] segs = rel.Split('/');
            string name = segs[^1];

            if (_glob is not null) return _glob.IsMatch(name);
            if (_anchored) return rel == _pattern || rel.StartsWith(_pattern + "/", StringComparison.Ordinal);
            if (_dir) return segs[..^1].Contains(_pattern); // a directory of that name on the path
            return rel == _pattern || name == _pattern || segs.Contains(_pattern);
        }
    }
}
