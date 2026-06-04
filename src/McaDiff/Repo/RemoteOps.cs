using System.Text.RegularExpressions;

namespace McaDiff.Repo;

/// <summary>
/// Transport-agnostic clone / fetch / push. Works over any
/// <see cref="IRemoteTransport"/> (local filesystem, HTTP, ssh). Because objects
/// are content-addressed, transfer copies only what the other side lacks.
/// </summary>
public static class RemoteOps
{
    public sealed record PushResult(int ObjectsCopied, bool FastForward);

    public static PushResult Push(Repository repo, string remote, string branch, bool force, string? token)
    {
        using IRemoteTransport t = Transports.Connect(repo.GetRemote(remote) ?? remote, token);
        PushResult r = PushTo(repo, t, branch, force);
        if (IsName(remote)) repo.WriteRemoteTracking(remote, branch, repo.ReadBranch(branch)!);
        return r;
    }

    public static int Fetch(Repository repo, string remote, string? branch, string? token)
    {
        using IRemoteTransport t = Transports.Connect(repo.GetRemote(remote) ?? remote, token);
        return FetchInto(repo, t, branch, IsName(remote) ? remote : "origin");
    }

    public static void Clone(string url, string dstDir, string? token)
    {
        using IRemoteTransport t = Transports.Connect(url, token);
        CloneFrom(t, dstDir, url);
    }

    // ---- transport-agnostic cores (also used by tests with an injected transport) ----

    public static PushResult PushTo(Repository repo, IRemoteTransport t, string branch, bool force)
    {
        if (repo.ReadBranch(branch) is not { } local)
            throw new InvalidOperationException($"no such branch: {branch}");
        RefAdvertisement refs = t.ListRefs();
        string? remoteTip = refs.Branches.GetValueOrDefault(branch);
        bool ff = remoteTip is null || Transfer.IsAncestor(repo, remoteTip, local);
        if (!ff && !force)
            throw new InvalidOperationException($"non-fast-forward push to {branch} (use --force)");

        var candidates = new HashSet<string>();
        Transfer.CollectReachable(repo, local, candidates);
        IReadOnlyList<string> missing = t.Missing(candidates.ToList());

        // A bucket batches the missing objects into one pack (≈1 write); other transports
        // stay one-object-per-call.
        if (t is IBatchTransport batch)
            batch.PutObjects(missing.Select(h => (h, repo.Objects.Read(h))).ToList());
        else
            foreach (string h in missing) t.PutObject(h, repo.Objects.ReadRaw(h));

        t.UpdateRef(branch, remoteTip, local, force);
        return new PushResult(missing.Count, ff);
    }

    public static int FetchInto(Repository repo, IRemoteTransport t, string? branch, string trackName)
    {
        RefAdvertisement refs = t.ListRefs();
        IEnumerable<string> branches = branch is not null ? [branch] : refs.Branches.Keys;
        int copied = 0;
        foreach (string b in branches)
        {
            if (!refs.Branches.TryGetValue(b, out string? tip)) continue;
            copied += FetchReachable(repo, t, tip);
            repo.WriteRemoteTracking(trackName, b, tip);
        }
        return copied;
    }

    public static void CloneFrom(IRemoteTransport t, string dstDir, string originUrl)
    {
        Repository dst = Repository.Init(dstDir);
        dst.AddRemote("origin", originUrl);

        RefAdvertisement refs = t.ListRefs();
        foreach ((string b, string tip) in refs.Branches)
        {
            FetchReachable(dst, t, tip);
            dst.WriteBranch(b, tip);
            dst.WriteRemoteTracking("origin", b, tip);
        }
        foreach ((string tag, string h) in refs.Tags)
        {
            FetchReachable(dst, t, h);
            dst.WriteTag(tag, h);
        }
        dst.SetHeadToBranch(refs.Head ?? Repository.DefaultBranch);
    }

    /// <summary>Pulls every object reachable from <paramref name="tip"/> that the local repo lacks.</summary>
    private static int FetchReachable(Repository repo, IRemoteTransport t, string tip)
    {
        int copied = 0;
        var stack = new Stack<string>();
        stack.Push(tip);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (repo.Objects.Exists(h)) continue; // have this commit (+ its subtree) → prune

            repo.Objects.ImportRaw(h, t.GetObject(h)); copied++;
            CommitObject c = repo.ReadCommit(h);
            if (!repo.Objects.Exists(c.Tree)) { repo.Objects.ImportRaw(c.Tree, t.GetObject(c.Tree)); copied++; }
            foreach (string obj in ManifestObjects(repo.ReadManifest(c.Tree)))
                if (!repo.Objects.Exists(obj)) { repo.Objects.ImportRaw(obj, t.GetObject(obj)); copied++; }
            foreach (string p in c.Parents) stack.Push(p);
        }
        return copied;
    }

    private static IEnumerable<string> ManifestObjects(Manifest m)
    {
        foreach (var region in m.Regions.Values)
            foreach (string hash in region.Values) yield return hash;
        foreach (string hash in m.Nbt.Values) yield return hash;
        foreach (string hash in m.Blobs.Values) yield return hash;
    }

    private static bool IsName(string remote) => Regex.IsMatch(remote, "^[A-Za-z0-9._-]+$");
}
