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

    private sealed class RepoConfig { public string? Worktree { get; set; } }

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

    /// <summary>Resolves <c>HEAD</c>, a branch name, or a commit hash to a commit hash.</summary>
    public string ResolveRef(string refName)
    {
        if (refName == "HEAD")
            return HeadCommit() ?? throw new InvalidOperationException("HEAD has no commits yet");
        if (ReadBranch(refName) is { } byBranch) return byBranch;
        if (Objects.Exists(refName)) return refName;
        throw new InvalidOperationException($"unknown ref: {refName}");
    }

    // ---- typed object IO ----

    public CommitObject ReadCommit(string hash) => CommitObject.FromJson(Objects.ReadText(hash));
    public string WriteCommit(CommitObject c) => Objects.WriteText(c.ToJson());
    public Manifest ReadManifest(string hash) => Manifest.FromJson(Objects.ReadText(hash));
    public string WriteManifest(Manifest m) => Objects.WriteText(m.ToJson());

    /// <summary>
    /// Writes a commit object and advances the current branch (creating it if this
    /// is the first commit). Returns the new commit hash.
    /// </summary>
    public string CreateCommit(string treeHash, IReadOnlyList<string> parents, string message, string author)
    {
        var commit = new CommitObject
        {
            Tree = treeHash,
            Parents = [.. parents],
            Message = message,
            Author = author,
            Time = DateTimeOffset.Now.ToString("o"),
        };
        string hash = WriteCommit(commit);

        // Advance the current branch; if HEAD is detached, move HEAD itself and
        // leave every branch untouched (committing on a detached HEAD must never
        // silently clobber 'main').
        if (CurrentBranch() is { } branch) WriteBranch(branch, hash);
        else SetHeadDetached(hash);
        return hash;
    }

    private string BranchPath(string branch) => Path.Combine(Dir, "refs", "heads", branch);
}
