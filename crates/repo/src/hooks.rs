//! Repository hooks: runs `<repo>/hooks/<name>` (e.g. `pre-commit`,
//! `post-commit`). A non-zero exit from a blocking hook (pre-commit) aborts the
//! action; missing hooks are a no-op (exit 0). The hook runs with
//! `MCAGIT_DIR`/`MCAGIT_WORKTREE` set and the worktree (else the repo dir) as
//! its working directory.

use crate::repository::Repository;
use std::path::Path;
use std::process::Command;

/// Run the named hook, returning its exit code (0 if the hook doesn't exist).
pub fn run(repo: &Repository, name: &str) -> i32 {
    let path = repo.dir().join("hooks").join(name);
    if !path.is_file() {
        return 0;
    }
    // Directly executable (has +x and a usable interpreter)?
    if let Some(code) = exec(&path, &[], repo) {
        return code;
    }
    // Else via a POSIX sh. On Windows there is no /bin/sh; Git for Windows
    // ships one (on PATH on CI runners, at a known install path otherwise).
    for sh in sh_candidates() {
        if let Some(code) = exec(Path::new(&sh), &[path.as_path()], repo) {
            return code;
        }
    }
    0
}

fn sh_candidates() -> Vec<String> {
    let mut out = vec!["/bin/sh".to_string()];
    if cfg!(windows) {
        out.push("sh".to_string());
        for pf in ["ProgramFiles", "ProgramFiles(x86)"] {
            if let Ok(base) = std::env::var(pf) {
                out.push(format!("{base}\\Git\\bin\\sh.exe"));
                out.push(format!("{base}\\Git\\usr\\bin\\sh.exe"));
            }
        }
    }
    out
}

/// Spawn `exe args…` with the hook environment. `None` means "could not start"
/// (not executable / not found) so the caller can fall back to a shell.
fn exec(exe: &Path, args: &[&Path], repo: &Repository) -> Option<i32> {
    let mut cmd = Command::new(exe);
    cmd.args(args);
    let cwd = repo
        .worktree()
        .map(std::path::PathBuf::from)
        .filter(|w| w.is_dir())
        .unwrap_or_else(|| repo.dir().to_path_buf());
    cmd.current_dir(cwd);
    cmd.env("MCAGIT_DIR", repo.dir());
    if let Some(w) = repo.worktree() {
        cmd.env("MCAGIT_WORKTREE", w);
    }
    let status = cmd.status().ok()?;
    Some(status.code().unwrap_or(1))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn repo() -> (tempfile::TempDir, Repository) {
        let d = tempfile::tempdir().unwrap();
        let r = Repository::init(&d.path().join("repo")).unwrap();
        (d, r)
    }

    #[test]
    fn missing_hook_is_ok() {
        let (_d, r) = repo();
        assert_eq!(run(&r, "pre-commit"), 0);
    }

    #[cfg(unix)]
    #[test]
    fn hook_exit_codes_and_env_propagate() {
        use std::os::unix::fs::PermissionsExt;
        let (_d, r) = repo();
        let hooks = r.dir().join("hooks");
        std::fs::create_dir_all(&hooks).unwrap();

        // failing hook (executable)
        let pre = hooks.join("pre-commit");
        std::fs::write(&pre, "#!/bin/sh\nexit 3\n").unwrap();
        std::fs::set_permissions(&pre, std::fs::Permissions::from_mode(0o755)).unwrap();
        assert_eq!(run(&r, "pre-commit"), 3);

        // succeeding hook without +x runs via the sh fallback, sees MCAGIT_DIR
        let post = hooks.join("post-commit");
        std::fs::write(
            &post,
            "#!/bin/sh\ntest -n \"$MCAGIT_DIR\" || exit 9\nexit 0\n",
        )
        .unwrap();
        assert_eq!(run(&r, "post-commit"), 0);
    }
}
