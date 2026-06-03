using System.Diagnostics;

namespace McaDiff.Repo;

/// <summary>
/// Transport over ssh: runs <c>mcadiff serve-stdio &lt;path&gt;</c> on the remote host
/// and speaks the framed stdio protocol over the ssh pipe. Authentication and
/// encryption are ssh's job (keys/agent) — exactly like git over ssh. Requires
/// mcadiff installed on the remote. URL: <c>ssh://[user@]host[:port]/path/to/repo</c>.
/// </summary>
public sealed class SshTransport : IRemoteTransport
{
    private readonly Process _proc;
    private readonly StdioTransport _inner;

    public SshTransport(string url)
    {
        var uri = new Uri(url);
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (uri.Port > 0) { psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(uri.Port.ToString()); }
        psi.ArgumentList.Add(string.IsNullOrEmpty(uri.UserInfo) ? uri.Host : $"{uri.UserInfo}@{uri.Host}");
        psi.ArgumentList.Add("mcadiff");
        psi.ArgumentList.Add("serve-stdio");
        psi.ArgumentList.Add(Uri.UnescapeDataString(uri.AbsolutePath));

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ssh");
        _inner = new StdioTransport(_proc.StandardInput.BaseStream, _proc.StandardOutput.BaseStream);
    }

    public RefAdvertisement ListRefs() => _inner.ListRefs();
    public IReadOnlyList<string> Missing(IReadOnlyList<string> hashes) => _inner.Missing(hashes);
    public byte[] GetObject(string hash) => _inner.GetObject(hash);
    public void PutObject(string hash, byte[] compressed) => _inner.PutObject(hash, compressed);
    public void UpdateRef(string branch, string? expectedOld, string newHash, bool force)
        => _inner.UpdateRef(branch, expectedOld, newHash, force);

    public void Dispose()
    {
        try { _proc.StandardInput.Close(); } catch { }
        try { if (!_proc.WaitForExit(2000)) _proc.Kill(); } catch { }
        _proc.Dispose();
    }
}
