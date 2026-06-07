namespace McaGit.Repo;

/// <summary>
/// The staging index (git's index): an optional staged tree at <c>&lt;repo&gt;/index</c>.
/// When present, <c>commit</c> snapshots it instead of the whole worktree, so changes
/// can be staged path-by-path. Initialized from HEAD on first <c>add</c> so unstaged
/// paths still carry through to the commit.
/// </summary>
public static class StagingIndex
{
    private static string Path(Repository repo) => System.IO.Path.Combine(repo.Dir, "index");
    public static bool Exists(Repository repo) => File.Exists(Path(repo));
    public static Manifest Load(Repository repo) => Manifest.FromJson(File.ReadAllText(Path(repo)));
    public static void Save(Repository repo, Manifest m) => File.WriteAllText(Path(repo), m.ToJson());
    public static void Clear(Repository repo) { string p = Path(repo); if (File.Exists(p)) File.Delete(p); }
}

public static class Staging
{
    /// <summary>Stages the given worktree paths (or everything for "." / the world root).</summary>
    public static int Add(Repository repo, string world, IReadOnlyList<string> paths)
    {
        Manifest work = Snapshotter.Snapshot(repo, world);
        string worldFull = System.IO.Path.GetFullPath(world);
        bool stageAll = paths.Count == 0 || paths.Any(p =>
            p is "." or "" || System.IO.Path.GetFullPath(p) == worldFull);
        if (stageAll) { StagingIndex.Save(repo, work); return Count(work); }

        Manifest idx = BaseIndex(repo);
        int staged = 0;
        foreach (string raw in paths) staged += Overlay(idx, work, Normalize(raw));
        StagingIndex.Save(repo, idx);
        return staged;
    }

    /// <summary>Unstages paths: resets their index entries back to HEAD (git restore --staged).</summary>
    public static void Unstage(Repository repo, IReadOnlyList<string> paths)
    {
        Manifest head = HeadManifest(repo);
        Manifest idx = StagingIndex.Exists(repo) ? StagingIndex.Load(repo) : head;
        foreach (string raw in paths) Overlay(idx, head, Normalize(raw));
        if (ManifestsEqual(idx, head)) StagingIndex.Clear(repo); else StagingIndex.Save(repo, idx);
    }

    private static Manifest BaseIndex(Repository repo) =>
        StagingIndex.Exists(repo) ? StagingIndex.Load(repo) : HeadManifest(repo);

    private static Manifest HeadManifest(Repository repo) =>
        repo.HeadCommit() is { } h ? repo.ReadManifest(repo.ReadCommit(h).Tree) : new Manifest();

    /// <summary>Overlays <paramref name="src"/>'s entries under <paramref name="path"/> onto
    /// <paramref name="idx"/> (upserting matches, deleting matched entries absent from src).</summary>
    private static int Overlay(Manifest idx, Manifest src, string path)
    {
        bool Match(string rel) => path.Length == 0 || rel == path || rel.StartsWith(path + "/", StringComparison.Ordinal);
        int n = 0;
        foreach (var kv in src.Regions) if (Match(kv.Key)) { idx.Regions[kv.Key] = kv.Value; n++; }
        foreach (var kv in src.Nbt) if (Match(kv.Key)) { idx.Nbt[kv.Key] = kv.Value; n++; }
        foreach (var kv in src.Blobs) if (Match(kv.Key)) { idx.Blobs[kv.Key] = kv.Value; n++; }
        foreach (string k in idx.Regions.Keys.Where(Match).ToList()) if (!src.Regions.ContainsKey(k)) { idx.Regions.Remove(k); n++; }
        foreach (string k in idx.Nbt.Keys.Where(Match).ToList()) if (!src.Nbt.ContainsKey(k)) { idx.Nbt.Remove(k); n++; }
        foreach (string k in idx.Blobs.Keys.Where(Match).ToList()) if (!src.Blobs.ContainsKey(k)) { idx.Blobs.Remove(k); n++; }
        return n;
    }

    private static string Normalize(string raw) => raw.Replace('\\', '/').TrimEnd('/');

    private static int Count(Manifest m) => m.Regions.Count + m.Nbt.Count + m.Blobs.Count;

    private static bool ManifestsEqual(Manifest a, Manifest b) =>
        DictEqual(a.Nbt, b.Nbt) && DictEqual(a.Blobs, b.Blobs)
        && a.Regions.Count == b.Regions.Count
        && a.Regions.All(kv => b.Regions.TryGetValue(kv.Key, out var v) && DictEqual(kv.Value, v));

    private static bool DictEqual(IDictionary<string, string> a, IDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out string? v) && v == kv.Value);
}
