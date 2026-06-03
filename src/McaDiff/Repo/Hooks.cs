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
        return Exec(path, repo) ?? Exec("/bin/sh", repo, path) ?? 0; // direct, else via sh
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
