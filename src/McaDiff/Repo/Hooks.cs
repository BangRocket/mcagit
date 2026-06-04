using System.Diagnostics;

namespace McaDiff.Repo;

/// <summary>
/// Runs repository hooks from <c>&lt;repo&gt;/hooks/&lt;name&gt;</c> (e.g. pre-commit,
/// post-commit). A non-zero exit from a blocking hook (pre-commit) aborts the action.
/// Missing hooks are a no-op (exit 0).
/// </summary>
public static class Hooks
{
    public static int Run(Repository repo, string name)
    {
        string path = Path.Combine(repo.Dir, "hooks", name);
        if (!File.Exists(path)) return 0;
        if (Exec(path, repo) is { } direct) return direct; // directly executable
        foreach (string sh in ShCandidates())              // else via a POSIX sh
            if (Exec(sh, repo, path) is { } rc) return rc;
        return 0;
    }

    /// <summary>Places a POSIX <c>sh</c> may live, in order. On Windows there is no
    /// <c>/bin/sh</c>; Git for Windows ships one (on PATH on CI runners, at a known
    /// install path otherwise). <c>bash</c> is deliberately not a candidate — on a
    /// stock Windows box it resolves to WSL, which can't take a Windows script path.</summary>
    private static IEnumerable<string> ShCandidates()
    {
        yield return "/bin/sh";
        if (!OperatingSystem.IsWindows()) yield break;
        yield return "sh";
        foreach (string? pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            yield return Path.Combine(pf, "Git", "bin", "sh.exe");
            yield return Path.Combine(pf, "Git", "usr", "bin", "sh.exe");
        }
    }

    private static int? Exec(string exe, Repository repo, string? scriptArg = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = repo.Worktree ?? repo.Dir,
            };
            if (scriptArg is not null) psi.ArgumentList.Add(scriptArg);
            psi.Environment["MCADIFF_DIR"] = repo.Dir;
            if (repo.Worktree is { } w) psi.Environment["MCADIFF_WORKTREE"] = w;

            using Process? p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception) { return null; } // not executable / not found → let caller fall back
    }
}
