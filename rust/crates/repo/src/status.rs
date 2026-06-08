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
