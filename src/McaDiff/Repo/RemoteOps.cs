using System.Text.RegularExpressions;
using McaDiff.Output;

namespace McaDiff.Repo;

/// <summary>
/// Transport-agnostic clone / fetch / push. Works over any
/// <see cref="IRemoteTransport"/> (local filesystem, HTTP, ssh). Because objects
/// are content-addressed, transfer copies only what the other side lacks.
/// </summary>
public static class RemoteOps
{
    public sealed record PushResult(int ObjectsCopied, bool FastForward);

    public static PushResult Push(Repository repo, string remote, string branch, bool force, string? token, Progress? progress = null)
    {
        using IRemoteTransport t = Transports.Connect(repo.GetRemote(remote) ?? remote, token);
        PushResult r = PushTo(repo, t, branch, force, progress);
        if (IsName(remote)) repo.WriteRemoteTracking(remote, branch, repo.ReadBranch(branch)!);
        return r;
    }

    public static int Fetch(Repository repo, string remote, string? branch, string? token)
    {
        using IRemoteTransport t = Transports.Connect(repo.GetRemote(remote) ?? remote, token);
        return FetchInto(repo, t, branch, IsName(remote) ? remote : "origin");
    }

    public static void Clone(string url, string dstDir, string? token, int depth = 0)
    {
        using IRemoteTransport t = Transports.Connect(url, token);
        CloneFrom(t, dstDir, url, depth);
    }

    // ---- transport-agnostic cores (also used by tests with an injected transport) ----

    public static PushResult PushTo(Repository repo, IRemoteTransport t, string branch, bool force, Progress? progress = null)
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
        progress?.Begin("Counting objects");
        progress?.Done(candidates.Count, candidates.Count);
        IReadOnlyList<string> missing = t.Missing(candidates.ToList());

        // A bucket/HTTP batches the missing objects into one pack (≈1 write); other transports
        // stay one-object-per-call. Either way report per object as we read them out; the phase ends
        // (", done.") once the batch is sent / the last object is written.
        progress?.Begin("Writing objects");
        long sent = 0;
        if (t is IBatchTransport batch)
        {
            var toSend = new List<(string Hash, byte[] Content)>(missing.Count);
            foreach (string h in missing) { toSend.Add((h, repo.Objects.Read(h))); progress?.Update(++sent, missing.Count); }
            batch.PutObjects(toSend);
        }
        else
            foreach (string h in missing) { t.PutObject(h, repo.Objects.ReadRaw(h)); progress?.Update(++sent, missing.Count); }
        progress?.Done(missing.Count, missing.Count);

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

    public static void CloneFrom(IRemoteTransport t, string dstDir, string originUrl, int depth = 0)
    {
        Repository dst = Repository.Init(dstDir);
        dst.AddRemote("origin", originUrl);

        RefAdvertisement refs = t.ListRefs();
        var boundary = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string b, string tip) in refs.Branches)
        {
            FetchTip(dst, t, tip, depth, boundary);
            dst.WriteBranch(b, tip);
            dst.WriteRemoteTracking("origin", b, tip);
        }
        // A shallow clone skips tags: they may point into the pruned history.
        if (depth <= 0)
            foreach ((string tag, string h) in refs.Tags)
            {
                FetchReachable(dst, t, h);
                dst.WriteTag(tag, h);
            }
        dst.SetHeadToBranch(refs.Head ?? Repository.DefaultBranch);
        if (boundary.Count > 0) dst.WriteShallow(boundary);
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

    /// <summary>Fetches a tip with optional depth limit. When <paramref name="depth"/> &gt; 0,
    /// walks at most that many commits deep (BFS, so the first time a commit is seen is at its
    /// minimum depth) and records commits whose parents were pruned into <paramref name="boundary"/>.</summary>
    private static void FetchTip(Repository repo, IRemoteTransport t, string tip, int depth, HashSet<string> boundary)
    {
        if (depth <= 0) { FetchReachable(repo, t, tip); return; }

        var minDepth = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<(string Hash, int Depth)>();
        queue.Enqueue((tip, 1));
        while (queue.Count > 0)
        {
            (string h, int d) = queue.Dequeue();
            if (!minDepth.TryAdd(h, d)) continue; // first dequeue is the minimum depth (FIFO levels)

            if (!repo.Objects.Exists(h)) repo.Objects.ImportRaw(h, t.GetObject(h));
            CommitObject c = repo.ReadCommit(h);
            if (!repo.Objects.Exists(c.Tree)) repo.Objects.ImportRaw(c.Tree, t.GetObject(c.Tree));
            foreach (string obj in ManifestObjects(repo.ReadManifest(c.Tree)))
                if (!repo.Objects.Exists(obj)) repo.Objects.ImportRaw(obj, t.GetObject(obj));

            if (d < depth)
                foreach (string p in c.Parents) queue.Enqueue((p, d + 1));
            else if (c.Parents.Count > 0)
                boundary.Add(h); // pruned this commit's parents → it's a shallow boundary
        }
    }

    private static IEnumerable<string> ManifestObjects(Manifest m)
    {
        foreach (var region in m.Regions.Values)
            foreach (string hash in region.Values) yield return hash;
        foreach (string hash in m.Nbt.Values) yield return hash;
        foreach (string hash in m.Blobs.Values) yield return hash;
    }

    private static bool IsName(string remote) => Regex.IsMatch(remote, "^[A-Za-z0-9._-]+$");

    // ---- remote verify (offsite integrity: walk refs → objects over the transport) ----

    public sealed record VerifyResult(int Branches, int Commits, int Objects, List<string> Missing, List<string> Corrupt)
    {
        public bool Ok => Missing.Count == 0 && Corrupt.Count == 0;
    }

    /// <summary>Walks every branch's history on the remote, confirming each commit/tree
    /// decodes to its hash and every referenced leaf object is present (with <paramref name="deep"/>,
    /// also hash-checks each leaf — downloads everything). Catches partial uploads / bit-rot offsite.</summary>
    public static VerifyResult Verify(IRemoteTransport t, bool deep)
    {
        RefAdvertisement refs = t.ListRefs();
        var seen = new HashSet<string>();
        var leaves = new HashSet<string>();
        var missing = new List<string>();
        var corrupt = new List<string>();
        int commits = 0;

        var stack = new Stack<string>();
        foreach (string tip in refs.Branches.Values) stack.Push(tip);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!seen.Add(h)) continue;
            if (Fetch1(t, h) is not { } content) { missing.Add($"{h} (commit)"); continue; }
            if (Hash(content) != h) { corrupt.Add($"{h} (commit)"); continue; }

            CommitObject c;
            try { c = CommitObject.FromJson(System.Text.Encoding.UTF8.GetString(content)); }
            catch { corrupt.Add($"{h} (commit)"); continue; }
            commits++;

            if (Fetch1(t, c.Tree) is not { } treeBytes) missing.Add($"{c.Tree} (tree of {h[..10]})");
            else if (Hash(treeBytes) != c.Tree) corrupt.Add($"{c.Tree} (tree)");
            else
                try { foreach (string leaf in ManifestObjects(Manifest.FromJson(System.Text.Encoding.UTF8.GetString(treeBytes)))) leaves.Add(leaf); }
                catch { corrupt.Add($"{c.Tree} (tree)"); }

            foreach (string p in c.Parents) stack.Push(p);
        }

        if (deep)
            foreach (string leaf in leaves)
            {
                if (Fetch1(t, leaf) is not { } b) missing.Add(leaf);
                else if (Hash(b) != leaf) corrupt.Add(leaf);
            }
        else
            missing.AddRange(t.Missing(leaves.ToList())); // one batched presence check (not per-leaf — issue #21)

        return new VerifyResult(refs.Branches.Count, commits, seen.Count + leaves.Count, missing, corrupt);
    }

    private static byte[]? Fetch1(IRemoteTransport t, string hash)
    {
        try { return SafeInflate.Zlib(t.GetObject(hash)); } // bounded — a hostile remote can't bomb verify --deep (issue #21)
        catch { return null; }
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
}
