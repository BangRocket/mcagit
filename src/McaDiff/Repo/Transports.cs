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
