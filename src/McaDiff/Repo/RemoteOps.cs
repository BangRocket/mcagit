namespace McaDiff.Repo;

/// <summary>
/// Filesystem-based remote operations — clone / fetch / push between repository
/// directories (no network). Objects are content-addressed, so transfer is just
/// copying the objects a branch reaches that the other side lacks, then moving a
/// ref. The same dedup that makes local history cheap makes pushes cheap.
/// </summary>
public static class RemoteOps
{
    public sealed record PushResult(int ObjectsCopied, bool FastForward);

    public static PushResult Push(Repository repo, string remoteName, string branch, bool force)
    {
        if (repo.GetRemote(remoteName) is not { } remoteDir)
            throw new InvalidOperationException($"no such remote: {remoteName}");
        if (!Repository.IsRepository(remoteDir))
            throw new InvalidOperationException($"remote is not a repository: {remoteDir}");
        if (repo.ReadBranch(branch) is not { } local)
            throw new InvalidOperationException($"no such branch: {branch}");

        Repository remote = Repository.Open(remoteDir);
        string? remoteTip = remote.ReadBranch(branch);
        bool ff = remoteTip is null || Transfer.IsAncestor(repo, remoteTip, local);
        if (!ff && !force)
            throw new InvalidOperationException($"non-fast-forward push to {remoteName}/{branch} (use --force)");

        int copied = Transfer.CopyReachable(repo, remote.Objects, local);
        remote.WriteBranch(branch, local);
        repo.WriteRemoteTracking(remoteName, branch, local);
        return new PushResult(copied, ff);
    }

    /// <summary>Fetches one or all branches from a remote into remote-tracking refs.</summary>
    public static int Fetch(Repository repo, string remoteName, string? branch)
    {
        if (repo.GetRemote(remoteName) is not { } remoteDir)
            throw new InvalidOperationException($"no such remote: {remoteName}");
        Repository remote = Repository.Open(remoteDir);

        int copied = 0;
        IEnumerable<string> branches = branch is not null ? [branch] : remote.Branches();
        foreach (string b in branches)
        {
            if (remote.ReadBranch(b) is not { } tip) continue;
            copied += Transfer.CopyReachable(remote, repo.Objects, tip);
            repo.WriteRemoteTracking(remoteName, b, tip);
        }
        return copied;
    }

    public static void Clone(string srcDir, string dstDir)
    {
        Repository src = Repository.Open(srcDir);
        Repository dst = Repository.Init(dstDir);
        dst.AddRemote("origin", srcDir);

        foreach (string b in src.Branches())
        {
            if (src.ReadBranch(b) is not { } tip) continue;
            Transfer.CopyReachable(src, dst.Objects, tip);
            dst.WriteBranch(b, tip);
            dst.WriteRemoteTracking("origin", b, tip);
        }
        dst.SetHeadToBranch(src.CurrentBranch() ?? Repository.DefaultBranch);
    }
}
