namespace McaDiff.Repo;

/// <summary>
/// An external, bare repository: the object store plus refs and HEAD. Worlds are
/// committed into it and checked out from it. HEAD names the current branch, which
/// commit/merge advance.
/// </summary>
public sealed class Repository
{
    public const string DefaultBranch = "main";

    public string Dir { get; }
    public ObjectStore Objects { get; }

    private ChunkCache? _cache;
    /// <summary>Lazily-loaded decode cache (see <see cref="ChunkCache"/>).</summary>
    public ChunkCache Cache => _cache ??= ChunkCache.Load(Dir);

    private Repository(string dir)
    {
        Dir = dir;
        Objects = new ObjectStore(dir);
    }

    public static bool IsRepository(string dir) => File.Exists(Path.Combine(dir, "HEAD"));

    public static Repository Init(string dir)
    {
        if (IsRepository(dir))
            throw new InvalidOperationException($"already a repository: {dir}");
        Directory.CreateDirectory(Path.Combine(dir, "objects"));
        Directory.CreateDirectory(Path.Combine(dir, "refs", "heads"));
        File.WriteAllText(Path.Combine(dir, "HEAD"), $"ref: refs/heads/{DefaultBranch}\n");
        return new Repository(dir);
    }

    public static Repository Open(string dir)
    {
        if (!IsRepository(dir))
            throw new InvalidOperationException($"not a repository: {dir}");
        return new Repository(dir);
    }

    /// <summary>
    /// Locates the repository git-style: an explicit <paramref name="dashC"/> if
    /// given, otherwise the current directory or the nearest ancestor that is a
    /// repository. Returns null if none is found.
    /// </summary>
    public static Repository? Discover(string? dashC)
    {
        if (dashC is not null)
            return IsRepository(dashC) ? Open(dashC) : null;
        for (string? dir = Directory.GetCurrentDirectory(); dir is not null; dir = Directory.GetParent(dir)?.FullName)
            if (IsRepository(dir))
                return Open(dir);
        return null;
    }

    // ---- config (bound worktree) ----

    /// <summary>The world directory bound to this repo (git's "working tree"), or null.</summary>
    public string? Worktree
    {
        get => ReadConfig().Worktree;
        set { RepoConfig c = ReadConfig(); c.Worktree = value is null ? null : Path.GetFullPath(value); WriteConfig(c); }
    }

    private string ConfigPath => Path.Combine(Dir, "config");
    private RepoConfig ReadConfig() => File.Exists(ConfigPath)
        ? System.Text.Json.JsonSerializer.Deserialize<RepoConfig>(File.ReadAllText(ConfigPath)) ?? new RepoConfig()
        : new RepoConfig();
    private void WriteConfig(RepoConfig c) => File.WriteAllText(ConfigPath,
        System.Text.Json.JsonSerializer.Serialize(c, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    private sealed class RepoConfig
    {
        public string? Worktree { get; set; }
        public Dictionary<string, string> Remotes { get; set; } = new();
        /// <summary>Arbitrary dotted config keys (user.name, user.email, user.signingkey, …).</summary>
        public Dictionary<string, string> Settings { get; set; } = new();
    }

    // ---- generic config (dotted keys; repo overrides ~/.mcaconfig) ----

    private static string GlobalConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcaconfig");

    /// <summary>Reads a config value, repo-level first then global, or null.</summary>
    public string? GetConfig(string key)
    {
        if (ReadConfig().Settings.TryGetValue(key, out string? v)) return v;
        return ReadGlobalConfig().GetValueOrDefault(key);
    }

    public void SetConfig(string key, string value, bool global)
    {
        if (global)
        {
            Dictionary<string, string> g = ReadGlobalConfig();
            g[key] = value;
            WriteGlobalConfig(g);
        }
        else
        {
            RepoConfig c = ReadConfig();
            c.Settings[key] = value;
            WriteConfig(c);
        }
    }

    public bool UnsetConfig(string key, bool global)
    {
        if (global)
        {
            Dictionary<string, string> g = ReadGlobalConfig();
            if (!g.Remove(key)) return false;
            WriteGlobalConfig(g);
            return true;
        }
        RepoConfig c = ReadConfig();
        if (!c.Settings.Remove(key)) return false;
        WriteConfig(c);
        return true;
    }

    /// <summary>All effective config entries (global first, then repo, which wins on display).</summary>
    public IEnumerable<(string Key, string Value, bool Global)> ListConfig()
    {
        foreach (var kv in ReadGlobalConfig()) yield return (kv.Key, kv.Value, true);
        RepoConfig c = ReadConfig();
        if (c.Worktree is not null) yield return ("worktree", c.Worktree, false);
        foreach (var kv in c.Settings) yield return (kv.Key, kv.Value, false);
    }

    /// <summary>"<c>Name &lt;email&gt;</c>" from config, or just one part, or null if neither is set.</summary>
    public string? ConfiguredIdentity()
    {
        string? name = GetConfig("user.name");
        string? email = GetConfig("user.email");
        if (name is not null && email is not null) return $"{name} <{email}>";
        return name ?? (email is not null ? $"<{email}>" : null);
    }

    private static Dictionary<string, string> ReadGlobalConfig() => File.Exists(GlobalConfigPath)
        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(GlobalConfigPath)) ?? new()
        : new();
    private static void WriteGlobalConfig(Dictionary<string, string> g) => File.WriteAllText(GlobalConfigPath,
        System.Text.Json.JsonSerializer.Serialize(g, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    // ---- remotes ----

    public IReadOnlyDictionary<string, string> Remotes => ReadConfig().Remotes;
    public string? GetRemote(string name) => ReadConfig().Remotes.GetValueOrDefault(name);
    public void AddRemote(string name, string urlOrPath)
    {
        RepoConfig c = ReadConfig();
        // Leave URLs (http://, https://, ssh://) intact; only resolve filesystem paths.
        c.Remotes[name] = urlOrPath.Contains("://") ? urlOrPath : Path.GetFullPath(urlOrPath);
        WriteConfig(c);
    }

    public string? ReadRemoteRef(string remoteSlashBranch)
    {
        string p = Path.Combine(Dir, "refs", "remotes", remoteSlashBranch.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
    }

    public void WriteRemoteTracking(string remote, string branch, string commitHash)
    {
        string p = Path.Combine(Dir, "refs", "remotes", remote, branch);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, commitHash + "\n");
    }

    // ---- reflog (logs/HEAD) ----

    public void RecordHead(string? from, string to, string message)
    {
        string p = Path.Combine(Dir, "logs", "HEAD");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.AppendAllText(p, $"{from ?? new string('0', 40)} {to} {message}\n");
    }

    /// <summary>Reflog entries, most recent first.</summary>
    public IEnumerable<string> Reflog()
    {
        string p = Path.Combine(Dir, "logs", "HEAD");
        return File.Exists(p) ? File.ReadLines(p).Reverse() : [];
    }

    // ---- in-progress merge state (MERGE_HEAD / MERGE_MSG / ORIG_HEAD / MERGE_CONFLICTS) ----

    private string MergeHeadPath => Path.Combine(Dir, "MERGE_HEAD");
    private string MergeMsgPath => Path.Combine(Dir, "MERGE_MSG");
    private string OrigHeadPath => Path.Combine(Dir, "ORIG_HEAD");
    private string MergeConflictsPath => Path.Combine(Dir, "MERGE_CONFLICTS");

    /// <summary>True while a conflicted merge awaits resolution.</summary>
    public bool InMerge => File.Exists(MergeHeadPath);

    public void BeginMergeState(string ours, string theirs, string? baseCommit, string message, IReadOnlyList<MergeConflict> conflicts)
    {
        File.WriteAllText(OrigHeadPath, ours + "\n");
        File.WriteAllText(MergeHeadPath, theirs + "\n");
        File.WriteAllText(MergeMsgPath, message + "\n");
        File.WriteAllText(MergeConflictsPath, System.Text.Json.JsonSerializer.Serialize(conflicts, RepoJson.Options));
    }

    public string? ReadMergeHead() => File.Exists(MergeHeadPath) ? File.ReadAllText(MergeHeadPath).Trim() : null;
    public string? ReadOrigHead() => File.Exists(OrigHeadPath) ? File.ReadAllText(OrigHeadPath).Trim() : null;
    public string? ReadMergeMsg() => File.Exists(MergeMsgPath) ? File.ReadAllText(MergeMsgPath).Trim() : null;

    public List<MergeConflict> ReadMergeConflicts() => File.Exists(MergeConflictsPath)
        ? System.Text.Json.JsonSerializer.Deserialize<List<MergeConflict>>(File.ReadAllText(MergeConflictsPath), RepoJson.Options) ?? []
        : [];

    /// <summary>Clears the in-progress merge markers (leaves ORIG_HEAD, like git).</summary>
    public void ClearMergeState()
    {
        foreach (string p in new[] { MergeHeadPath, MergeMsgPath, MergeConflictsPath })
            if (File.Exists(p)) File.Delete(p);
    }

    // ---- HEAD & branches ----

    public string ReadHeadRaw() => File.ReadAllText(Path.Combine(Dir, "HEAD")).Trim();

    /// <summary>Current branch name, or null if HEAD is detached at a commit.</summary>
    public string? CurrentBranch()
    {
        string head = ReadHeadRaw();
        return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)
            ? head["ref: refs/heads/".Length..]
            : null;
    }

    public void SetHeadToBranch(string branch) =>
        File.WriteAllText(Path.Combine(Dir, "HEAD"), $"ref: refs/heads/{branch}\n");

    public void SetHeadDetached(string commitHash) =>
        File.WriteAllText(Path.Combine(Dir, "HEAD"), commitHash + "\n");

    public string? ReadBranch(string branch)
    {
        string p = BranchPath(branch);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
    }

    public void WriteBranch(string branch, string commitHash)
    {
        string p = BranchPath(branch);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, commitHash + "\n");
    }

    public IEnumerable<string> Branches()
    {
        string dir = Path.Combine(Dir, "refs", "heads");
        if (!Directory.Exists(dir)) yield break;
        foreach (string f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            yield return Path.GetFileName(f);
    }

    /// <summary>The commit HEAD currently points at (via its branch), or null if unborn.</summary>
    public string? HeadCommit()
    {
        string? branch = CurrentBranch();
        return branch is not null ? ReadBranch(branch) : ReadHeadRaw();
    }

    // ---- tags (refs/tags/*) ----

    public string? ReadTag(string name)
    {
        string p = TagPath(name);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
    }

    public void WriteTag(string name, string commitHash)
    {
        string p = TagPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, commitHash + "\n");
    }

    /// <summary>Stores an annotated tag object and points <c>refs/tags/&lt;name&gt;</c> at it.</summary>
    public string WriteAnnotatedTag(TagObject tag)
    {
        string hash = Objects.WriteText(tag.ToJson());
        WriteTag(tag.Tag, hash);
        return hash;
    }

    /// <summary>The tag object a tag ref points at, or null for a lightweight tag (or absent).</summary>
    public TagObject? ReadAnnotatedTag(string name)
    {
        string? target = ReadTag(name);
        return target is not null && Objects.Exists(target) ? TagObject.TryFromJson(Objects.ReadText(target)) : null;
    }

    /// <summary>Follows a chain of annotated tag objects down to the commit it names.</summary>
    public string PeelToCommit(string hash)
    {
        for (int i = 0; i < 100; i++)
        {
            if (!Objects.Exists(hash)) return hash; // not present — let the caller surface the error
            if (TagObject.TryFromJson(Objects.ReadText(hash)) is { } tag) hash = tag.Object;
            else return hash;
        }
        throw new InvalidOperationException("tag chain too deep (cycle?)");
    }

    public bool DeleteTag(string name)
    {
        string p = TagPath(name);
        if (!File.Exists(p)) return false;
        File.Delete(p);
        return true;
    }

    public IEnumerable<string> Tags()
    {
        string dir = Path.Combine(Dir, "refs", "tags");
        if (!Directory.Exists(dir)) yield break;
        foreach (string f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            yield return Path.GetFileName(f);
    }

    /// <summary>
    /// Resolves a revision to a commit hash, git-style: a base (<c>HEAD</c>, branch,
    /// tag, full or abbreviated hash) optionally followed by <c>~n</c> / <c>^n</c>
    /// ancestor operators (e.g. <c>main~2</c>, <c>HEAD^</c>, <c>a1b2c3d~1</c>).
    /// </summary>
    public string ResolveRef(string refSpec)
    {
        int op = refSpec.IndexOfAny(['~', '^']);
        string baseName = op < 0 ? refSpec : refSpec[..op];
        string ops = op < 0 ? "" : refSpec[op..];

        string commit = ResolveBase(baseName.Length == 0 ? "HEAD" : baseName);
        return ApplyRevOps(commit, ops, refSpec);
    }

    private string ResolveBase(string name)
    {
        if (name == "HEAD")
            return HeadCommit() ?? throw new InvalidOperationException("HEAD has no commits yet");
        if (ReadBranch(name) is { } b) return b;
        if (ReadTag(name) is { } t) return PeelToCommit(t);          // annotated tags peel to their commit
        if (name.Contains('/') && ReadRemoteRef(name) is { } rr) return rr; // e.g. origin/main
        if (Objects.Exists(name)) return PeelToCommit(name);
        if (Objects.ResolvePrefix(name) is { } full) return PeelToCommit(full);
        throw new InvalidOperationException($"unknown revision: {name}");
    }

    private string ApplyRevOps(string commit, string ops, string full)
    {
        for (int i = 0; i < ops.Length;)
        {
            char c = ops[i++];
            int start = i;
            while (i < ops.Length && char.IsDigit(ops[i])) i++;
            int n = start < i ? int.Parse(ops[start..i]) : 1;

            if (c == '^')
            {
                List<string> parents = ReadCommit(commit).Parents;
                if (n < 1 || n > parents.Count) throw new InvalidOperationException($"{full}: no parent {n}");
                commit = parents[n - 1];
            }
            else // '~' : walk n first-parents
            {
                for (int k = 0; k < n; k++)
                {
                    List<string> parents = ReadCommit(commit).Parents;
                    if (parents.Count == 0) throw new InvalidOperationException($"{full}: no ancestor");
                    commit = parents[0];
                }
            }
        }
        return commit;
    }

    private string TagPath(string name) => Path.Combine(Dir, "refs", "tags", name);

    // ---- typed object IO ----

    public CommitObject ReadCommit(string hash) => CommitObject.FromJson(Objects.ReadText(hash));
    public string WriteCommit(CommitObject c) => Objects.WriteText(c.ToJson());
    public Manifest ReadManifest(string hash) => Manifest.FromJson(Objects.ReadText(hash));
    public string WriteManifest(Manifest m) => Objects.WriteText(m.ToJson());

    /// <summary>The four object kinds, git-style (a manifest is our "tree").</summary>
    public enum ObjectKind { Commit, Tree, Tag, Blob }

    /// <summary>
    /// Classifies a stored object by its content shape: commit/tree/tag are camelCase
    /// JSON with disjoint key sets; everything else (canonical NBT, raw files) is a blob.
    /// </summary>
    public ObjectKind Classify(string hash)
    {
        byte[] raw = Objects.Read(hash);
        if (raw.Length == 0 || raw[0] != (byte)'{') return ObjectKind.Blob;
        string text = System.Text.Encoding.UTF8.GetString(raw);
        if (TagObject.TryFromJson(text) is not null) return ObjectKind.Tag;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            System.Text.Json.JsonElement r = doc.RootElement;
            if (r.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (r.TryGetProperty("tree", out _) && r.TryGetProperty("parents", out _)) return ObjectKind.Commit;
                if (r.TryGetProperty("regions", out _) && r.TryGetProperty("nbt", out _) && r.TryGetProperty("blobs", out _))
                    return ObjectKind.Tree;
            }
        }
        catch (System.Text.Json.JsonException) { /* not our JSON → blob */ }
        return ObjectKind.Blob;
    }

    /// <summary>
    /// Writes a commit object and advances the current branch (creating it if this
    /// is the first commit). Returns the new commit hash.
    /// </summary>
    public string CreateCommit(string treeHash, IReadOnlyList<string> parents, string message, string author,
        string? committer = null, string? authorTime = null, Func<string, string>? sign = null)
    {
        string now = DateTimeOffset.Now.ToString("o");
        var commit = new CommitObject
        {
            Tree = treeHash,
            Parents = [.. parents],
            Message = message,
            Author = author,
            Time = authorTime ?? now,
            Committer = committer ?? author,
            CommitTime = now,
        };
        // Sign over the payload with the signature field cleared; the object hash then
        // covers the signature (git's model), so a signed commit is its own object.
        if (sign is not null) commit.Signature = sign(commit.SignablePayload());
        string? oldTip = HeadCommit();
        string hash = WriteCommit(commit);

        // Advance the current branch; if HEAD is detached, move HEAD itself and
        // leave every branch untouched (committing on a detached HEAD must never
        // silently clobber 'main').
        if (CurrentBranch() is { } branch) WriteBranch(branch, hash);
        else SetHeadDetached(hash);
        RecordHead(oldTip, hash, $"commit: {message}");
        return hash;
    }

    private string BranchPath(string branch) => Path.Combine(Dir, "refs", "heads", branch);
}
