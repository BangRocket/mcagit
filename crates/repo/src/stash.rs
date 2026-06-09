//! Shelve/restore the worktree. A stash is a commit (parent = HEAD) recorded on
//! a simple stack file; push resets the worktree to HEAD, pop re-materializes it.

use crate::checkout::checkout;
use crate::repository::Repository;
use crate::{snapshot, Result};
use std::path::{Path, PathBuf};

fn stack_path(repo: &Repository) -> PathBuf {
    repo.dir().join("stash")
}

fn read_stack(repo: &Repository) -> Vec<String> {
    std::fs::read_to_string(stack_path(repo))
        .unwrap_or_default()
        .lines()
        .filter(|l| !l.trim().is_empty())
        .map(|l| l.trim().to_string())
        .collect()
}

fn write_stack(repo: &Repository, stack: &[String]) -> Result<()> {
    std::fs::write(stack_path(repo), stack.join("\n") + "\n")?;
    Ok(())
}

/// Shelve the worktree (if it differs from HEAD) and reset it to HEAD.
pub fn push(
    repo: &Repository,
    world_dir: &Path,
    author: &str,
    time: &str,
) -> Result<Option<String>> {
    let m = snapshot::snapshot(repo, world_dir)?;
    let tree = repo.write_manifest(&m)?;
    let head = repo.head_commit();
    if let Some(h) = &head {
        if repo.read_commit(h)?.tree == tree {
            return Ok(None); // nothing to stash
        }
    }
    let parents: Vec<String> = head.clone().into_iter().collect();
    let stash = repo.create_commit(&tree, parents, "WIP stash", author, time)?;
    let mut stack = read_stack(repo);
    stack.push(stash.clone());
    write_stack(repo, &stack)?;

    if let Some(h) = &head {
        let hm = repo.read_manifest(&repo.read_commit(h)?.tree)?;
        checkout(repo, &hm, world_dir, true)?;
    }
    Ok(Some(stash))
}

/// Restore the most recent stash into the worktree and drop it from the stack.
pub fn pop(repo: &Repository, world_dir: &Path) -> Result<Option<String>> {
    let mut stack = read_stack(repo);
    let Some(top) = stack.pop() else {
        return Ok(None);
    };
    let m = repo.read_manifest(&repo.read_commit(&top)?.tree)?;
    checkout(repo, &m, world_dir, true)?;
    write_stack(repo, &stack)?;
    Ok(Some(top))
}

pub fn list(repo: &Repository) -> Vec<String> {
    read_stack(repo)
}

/// Discard the most recent stash without touching the worktree.
pub fn drop_top(repo: &Repository) -> Result<Option<String>> {
    let mut stack = read_stack(repo);
    let Some(top) = stack.pop() else {
        return Ok(None);
    };
    write_stack(repo, &stack)?;
    Ok(Some(top))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn push_resets_and_pop_restores() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("icon.png"), b"v1").unwrap();

        // commit v1 as HEAD/main
        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "v1", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        // modify worktree to v2
        std::fs::write(world.join("icon.png"), b"v2").unwrap();

        // stash push -> back to v1
        assert!(push(&repo, &world, "me", "t").unwrap().is_some());
        assert_eq!(std::fs::read(world.join("icon.png")).unwrap(), b"v1");
        assert_eq!(list(&repo).len(), 1);

        // stash pop -> v2 again
        assert!(pop(&repo, &world).unwrap().is_some());
        assert_eq!(std::fs::read(world.join("icon.png")).unwrap(), b"v2");
        assert!(list(&repo).is_empty());
    }

    #[test]
    fn drop_discards_without_restoring() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("icon.png"), b"v1").unwrap();
        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "v1", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        std::fs::write(world.join("icon.png"), b"v2").unwrap();
        push(&repo, &world, "me", "t").unwrap();
        assert_eq!(list(&repo).len(), 1);

        // drop: stack empties, worktree stays at HEAD (v1)
        assert!(drop_top(&repo).unwrap().is_some());
        assert!(list(&repo).is_empty());
        assert_eq!(std::fs::read(world.join("icon.png")).unwrap(), b"v1");
        // dropping an empty stack is a no-op
        assert!(drop_top(&repo).unwrap().is_none());
    }
}
