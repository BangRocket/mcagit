//! Worktree status: compare the live world against a commit, by content hash.

use crate::repository::Repository;
use crate::{snapshot, Manifest, Result};
use std::collections::BTreeMap;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChangeKind {
    Added,
    Modified,
    Removed,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Change {
    pub path: String,
    pub kind: ChangeKind,
}

/// A three-way status: HEAD↔index (staged), index↔worktree (unstaged + untracked).
#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub struct StatusReport {
    pub staged: Vec<Change>,
    pub unstaged: Vec<Change>,
    pub untracked: Vec<String>,
}

/// Three-way status. The index falls back to HEAD's tree when no index file
/// exists, so this works on an unborn HEAD too (staged is then empty).
pub fn status_full(repo: &Repository, world_dir: &Path) -> Result<StatusReport> {
    let head = crate::index::head_tree(repo)?;
    let index = match crate::index::read(repo)? {
        Some(m) => m,
        None => head.clone(),
    };
    let cur = snapshot::hash_only(repo, world_dir)?;

    let staged = diff(&head, &index);

    let mut unstaged = Vec::new();
    let mut untracked = Vec::new();
    for ch in diff(&index, &cur) {
        match ch.kind {
            ChangeKind::Added => untracked.push(ch.path),
            _ => unstaged.push(ch),
        }
    }
    untracked.sort();
    Ok(StatusReport {
        staged,
        unstaged,
        untracked,
    })
}

/// Changes in `world_dir` relative to `commit` (by content hash; fast).
pub fn status(repo: &Repository, world_dir: &Path, commit: &str) -> Result<Vec<Change>> {
    let tree = repo.read_commit(commit)?.tree;
    let head = repo.read_manifest(&tree)?;
    let cur = snapshot::hash_only(repo, world_dir)?;
    Ok(diff(&head, &cur))
}

/// Flatten a manifest to `path → signature` (a region's signature folds all its
/// chunk ids, so a changed chunk marks the region modified).
fn flatten(m: &Manifest) -> BTreeMap<String, String> {
    let mut out = BTreeMap::new();
    for (rel, chunks) in &m.regions {
        let mut s = String::new();
        for (k, v) in chunks {
            s.push_str(k);
            s.push('=');
            s.push_str(v);
            s.push(';');
        }
        out.insert(
            rel.clone(),
            format!("r:{}", blake3::hash(s.as_bytes()).to_hex()),
        );
    }
    for (rel, h) in &m.nbt {
        out.insert(rel.clone(), format!("n:{h}"));
    }
    for (rel, h) in &m.blobs {
        out.insert(rel.clone(), format!("b:{h}"));
    }
    for rel in &m.empty_dirs {
        out.insert(rel.clone(), "d:".to_string());
    }
    out
}

fn diff(head: &Manifest, cur: &Manifest) -> Vec<Change> {
    let h = flatten(head);
    let c = flatten(cur);
    let mut out = Vec::new();
    for (path, sig) in &c {
        match h.get(path) {
            None => out.push(Change {
                path: path.clone(),
                kind: ChangeKind::Added,
            }),
            Some(hsig) if hsig != sig => out.push(Change {
                path: path.clone(),
                kind: ChangeKind::Modified,
            }),
            _ => {}
        }
    }
    for path in h.keys() {
        if !c.contains_key(path) {
            out.push(Change {
                path: path.clone(),
                kind: ChangeKind::Removed,
            });
        }
    }
    out.sort_by(|a, b| a.path.cmp(&b.path));
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn three_way_status_classifies_staged_unstaged_untracked() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("tracked.bin"), b"v1").unwrap();

        // commit tracked.bin as HEAD
        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        // stage a modification to tracked.bin
        std::fs::write(world.join("tracked.bin"), b"v2").unwrap();
        crate::index::add_paths(&repo, &world, &["tracked.bin".into()]).unwrap();

        // a second, unstaged modification on top
        std::fs::write(world.join("tracked.bin"), b"v3").unwrap();
        // and a brand-new untracked file
        std::fs::write(world.join("new.bin"), b"new").unwrap();

        let r = status_full(&repo, &world).unwrap();
        assert_eq!(r.staged.len(), 1);
        assert_eq!(r.staged[0].path, "tracked.bin");
        assert_eq!(r.staged[0].kind, ChangeKind::Modified);
        assert_eq!(r.unstaged.len(), 1);
        assert_eq!(r.unstaged[0].path, "tracked.bin");
        assert_eq!(r.unstaged[0].kind, ChangeKind::Modified);
        assert_eq!(r.untracked, vec!["new.bin".to_string()]);
    }

    #[test]
    fn status_full_classifies_worktree_deletion_as_unstaged_removed() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("f.bin"), b"v1").unwrap();
        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        std::fs::remove_file(world.join("f.bin")).unwrap();
        let r = status_full(&repo, &world).unwrap();
        assert!(r.staged.is_empty());
        assert!(r.untracked.is_empty());
        assert_eq!(r.unstaged.len(), 1);
        assert_eq!(r.unstaged[0].path, "f.bin");
        assert_eq!(r.unstaged[0].kind, ChangeKind::Removed);
    }

    #[test]
    fn status_full_unborn_head_lists_everything_untracked() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("a.bin"), b"a").unwrap();
        let r = status_full(&repo, &world).unwrap();
        assert!(r.staged.is_empty());
        assert!(r.unstaged.is_empty());
        assert_eq!(r.untracked, vec!["a.bin".to_string()]);
    }

    #[test]
    fn status_full_shows_staged_empty_dir_change() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(world.join("emptydir")).unwrap();
        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        std::fs::remove_dir(world.join("emptydir")).unwrap();
        crate::index::add_paths(&repo, &world, &["emptydir".to_string()]).unwrap();
        let r = status_full(&repo, &world).unwrap();
        assert_eq!(
            r.staged.len(),
            1,
            "staged empty-dir removal must show: {r:?}"
        );
        assert_eq!(r.staged[0].path, "emptydir");
        assert_eq!(r.staged[0].kind, ChangeKind::Removed);
    }

    #[test]
    fn detects_unchanged_and_modified() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("icon.png"), b"PNGDATA").unwrap();

        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();

        assert!(status(&repo, &world, &c).unwrap().is_empty());

        std::fs::write(world.join("icon.png"), b"DIFFERENT").unwrap();
        let ch = status(&repo, &world, &c).unwrap();
        assert_eq!(ch.len(), 1);
        assert_eq!(ch[0].kind, ChangeKind::Modified);
        assert_eq!(ch[0].path, "icon.png");
    }
}
