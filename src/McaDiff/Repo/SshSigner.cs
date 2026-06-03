using System.Diagnostics;

namespace McaDiff.Repo;

/// <summary>
/// Signs and verifies commit/tag payloads with an SSH key, the way git does with
/// <c>gpg.format=ssh</c> — by shelling to <c>ssh-keygen -Y sign|verify</c>. No GPG
/// dependency; auth and key handling are ssh's. Signing is always optional: if
/// <c>ssh-keygen</c> or the key is missing, callers commit/tag unsigned.
/// </summary>
public static class SshSigner
{
    public const string Namespace = "mcadiff";

    /// <summary>Whether <c>ssh-keygen</c> is on PATH (signing/verifying needs it).</summary>
    public static bool Available => Which("ssh-keygen") is not null;

    /// <summary>Signs <paramref name="payload"/> with an SSH private key, returning the
    /// armored <c>-----BEGIN SSH SIGNATURE-----</c> blob.</summary>
    public static string Sign(string payload, string keyFile)
    {
        string keyPath = ExpandHome(keyFile);
        if (!File.Exists(keyPath))
            throw new FileNotFoundException($"signing key not found: {keyFile} (set user.signingkey)");
        if (!Available)
            throw new InvalidOperationException("ssh-keygen not found on PATH — cannot sign");

        string dir = Directory.CreateTempSubdirectory("mcadiff-sign").FullName;
        try
        {
            string dataFile = Path.Combine(dir, "payload");
            File.WriteAllText(dataFile, payload);
            int code = Run("ssh-keygen", ["-Y", "sign", "-f", keyPath, "-n", Namespace, dataFile], stdin: null, out _, out string err);
            string sigFile = dataFile + ".sig";
            if (code != 0 || !File.Exists(sigFile))
                throw new InvalidOperationException($"ssh-keygen sign failed: {err.Trim()}");
            return File.ReadAllText(sigFile);
        }
        finally { TryDelete(dir); }
    }

    public readonly record struct VerifyResult(bool Valid, bool SignerVerified, string? Identity, string Detail);

    /// <summary>Verifies a signature over <paramref name="payload"/>. With an
    /// allowed-signers file (and a matching principal) it confirms the signer; without
    /// one it falls back to <c>check-novalidate</c> (signature is cryptographically
    /// valid for its embedded key, but the signer isn't established as trusted).</summary>
    public static VerifyResult Verify(string payload, string signature, string? allowedSignersFile)
    {
        if (string.IsNullOrEmpty(signature)) return new(false, false, null, "object is not signed");
        if (!Available) return new(false, false, null, "ssh-keygen not found on PATH — cannot verify");

        string dir = Directory.CreateTempSubdirectory("mcadiff-verify").FullName;
        try
        {
            string sigFile = Path.Combine(dir, "payload.sig");
            File.WriteAllText(sigFile, signature);

            string? signers = allowedSignersFile is null ? null : ExpandHome(allowedSignersFile);
            if (signers is not null && File.Exists(signers) && FindPrincipal(sigFile, signers) is { } principal)
            {
                int code = Run("ssh-keygen",
                    ["-Y", "verify", "-f", signers, "-I", principal, "-n", Namespace, "-s", sigFile],
                    stdin: payload, out _, out string err);
                return code == 0
                    ? new(true, true, principal, $"Good signature from {principal}")
                    : new(false, false, principal, err.Trim());
            }

            int c2 = Run("ssh-keygen", ["-Y", "check-novalidate", "-n", Namespace, "-s", sigFile],
                stdin: payload, out _, out string e2);
            return c2 == 0
                ? new(true, false, null, "Valid signature (signer not verified — set gpg.ssh.allowedSignersFile)")
                : new(false, false, null, e2.Trim());
        }
        finally { TryDelete(dir); }
    }

    private static string? FindPrincipal(string sigFile, string allowedSigners)
    {
        Run("ssh-keygen", ["-Y", "find-principals", "-s", sigFile, "-f", allowedSigners], stdin: null, out string outp, out _);
        return outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    private static int Run(string exe, string[] args, string? stdin, out string stdout, out string stderr)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        stdout = p.StandardOutput.ReadToEnd();
        stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string? Which(string exe)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            string cand = Path.Combine(dir, exe);
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    private static string ExpandHome(string path) => path.StartsWith('~')
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/', '\\'))
        : path;

    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
}
