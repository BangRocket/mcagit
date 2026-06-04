namespace McaDiff.Repo;

/// <summary>Picks a transport for a remote URL/path by scheme.</summary>
public static class Transports
{
    public static IRemoteTransport Connect(string urlOrPath, string? token)
    {
        if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new HttpTransport(urlOrPath, token);
        if (urlOrPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return new SshTransport(urlOrPath);
        if (urlOrPath.StartsWith("azure://", StringComparison.OrdinalIgnoreCase))
        {
            // azure://<account>/<container>/<path...>
            string[] p = urlOrPath["azure://".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) throw new InvalidOperationException("azure URL must be azure://<account>/<container>/<path>");
            return new BucketTransport(AzureBucket.Connect(p[0], p[1]), string.Join('/', p.Skip(2)));
        }
        if (urlOrPath.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            // s3://<bucket>/<path...>
            string[] p = urlOrPath["s3://".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 1) throw new InvalidOperationException("s3 URL must be s3://<bucket>/<path>");
            return new BucketTransport(S3Bucket.Connect(p[0]), string.Join('/', p.Skip(1)));
        }
        return new LocalTransport(urlOrPath);
    }
}

/// <summary>Transport to a repository on the local filesystem.</summary>
public sealed class LocalTransport : IRemoteTransport
{
    private readonly RemoteService _svc;

    public LocalTransport(string dir)
    {
        if (!Repository.IsRepository(dir))
            throw new InvalidOperationException($"remote is not a repository: {dir}");
        _svc = new RemoteService(Repository.Open(dir), allowWrite: true);
    }

    public RefAdvertisement ListRefs() => _svc.ListRefs();
    public IReadOnlyList<string> Missing(IReadOnlyList<string> hashes) => _svc.Missing(hashes);
    public byte[] GetObject(string hash) => _svc.GetObject(hash);
    public void PutObject(string hash, byte[] compressed) => _svc.PutObject(hash, compressed);
    public void UpdateRef(string branch, string? expectedOld, string newHash, bool force)
        => _svc.UpdateRef(branch, expectedOld, newHash, force);
    public void Dispose() { }
}
