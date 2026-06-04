namespace McaDiff.Repo;

/// <summary>What a remote advertises: its branches, tags, and current branch.</summary>
public sealed record RefAdvertisement(
    Dictionary<string, string> Branches,
    Dictionary<string, string> Tags,
    string? Head);

/// <summary>
/// Client-side view of a remote repository. Push/fetch/clone are written once
/// against this; concrete transports are local filesystem, HTTP, and ssh.
/// </summary>
public interface IRemoteTransport : IDisposable
{
    RefAdvertisement ListRefs();
    IReadOnlyList<string> Missing(IReadOnlyList<string> hashes);
    byte[] GetObject(string hash);
    void PutObject(string hash, byte[] compressed);
    void UpdateRef(string branch, string? expectedOld, string newHash, bool force);
}

/// <summary>
/// Server-side implementation of the remote operations against a real
/// <see cref="Repository"/>. Shared by the HTTP server and the ssh stdio server.
/// Writes are gated by <paramref name="allowWrite"/> (HTTP read-only mode; ssh
/// relies on the shell account, so it passes true).
/// </summary>
public sealed class RemoteService(Repository repo, bool allowWrite)
{
    public Repository Repo => repo;

    public RefAdvertisement ListRefs()
    {
        var branches = new Dictionary<string, string>();
        foreach (string b in repo.Branches())
            if (repo.ReadBranch(b) is { } h) branches[b] = h;
        var tags = new Dictionary<string, string>();
        foreach (string t in repo.Tags())
            if (repo.ReadTag(t) is { } h) tags[t] = h;
        return new RefAdvertisement(branches, tags, repo.CurrentBranch());
    }

    public IReadOnlyList<string> Missing(IReadOnlyList<string> hashes)
        => hashes.Where(h => !repo.Objects.Exists(h)).ToList();

    public byte[] GetObject(string hash) => repo.Objects.ReadRaw(hash);

    public void PutObject(string hash, byte[] compressed)
    {
        RequireWrite();
        repo.Objects.ImportRaw(hash, compressed); // verifies hash
    }

    /// <summary>Ingests a batched pack (one push of many objects). Each object is hash-verified.</summary>
    public int PutPack(byte[] pack, byte[] idx)
    {
        RequireWrite();
        return PackTransfer.ImportInto(repo, pack, idx);
    }

    public void UpdateRef(string branch, string? expectedOld, string newHash, bool force)
    {
        RequireWrite();
        string? current = repo.ReadBranch(branch);
        if (!force)
        {
            if (current != expectedOld)
                throw new InvalidOperationException($"ref {branch} moved on the remote (stale push) — fetch + retry");
            if (current is not null && !Transfer.IsAncestor(repo, current, newHash))
                throw new InvalidOperationException($"non-fast-forward update to {branch} (use --force)");
        }
        if (!repo.Objects.Exists(newHash))
            throw new InvalidOperationException("ref points to an object that was not uploaded");
        repo.WriteBranch(branch, newHash);
    }

    private void RequireWrite()
    {
        if (!allowWrite) throw new UnauthorizedAccessException("this remote is read-only");
    }
}
