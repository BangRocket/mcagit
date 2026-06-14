//! The staging index: a persistent partial `Manifest` (`<repo>/index`) that
//! `commit` turns into the next tree. A *missing* index file means "index ≡
//! HEAD's tree" — a clean index is the file's absence, never a copy of HEAD.

use crate::manifest::Manifest;
use crate::repository::Repository;
use crate::Result;
use std::path::PathBuf;

fn index_path(repo: &Repository) -> PathBuf {
    repo.dir().join("index")
}

/// The staged tree, or `None` when there is no index file (clean).
pub fn read(repo: &Repository) -> Result<Option<Manifest>> {
    let p = index_path(repo);
    if !p.is_file() {
        return Ok(None);
    }
    Ok(Some(Manifest::from_json(&std::fs::read_to_string(p)?)?))
}

/// Write the staged tree atomically (temp + rename).
pub fn write(repo: &Repository, m: &Manifest) -> Result<()> {
    let p = index_path(repo);
    let tmp = p.with_extension(format!("tmp.{}", std::process::id()));
    std::fs::write(&tmp, m.to_json()?.as_bytes())?;
    std::fs::rename(&tmp, &p)?;
    Ok(())
}

/// Remove the index file (→ clean: index ≡ HEAD).
pub fn clear(repo: &Repository) -> Result<()> {
    let _ = std::fs::remove_file(index_path(repo));
    Ok(())
}

/// HEAD's tree as a manifest, or an empty manifest when HEAD is unborn.
pub fn head_tree(repo: &Repository) -> Result<Manifest> {
    match repo.head_commit() {
        Some(h) => repo.read_manifest(&repo.read_commit(&h)?.tree),
        None => Ok(Manifest::default()),
    }
}

/// The effective staged tree: the index if present, else HEAD's tree, else an
/// empty manifest.
pub fn effective(repo: &Repository) -> Result<Manifest> {
    match read(repo)? {
        Some(m) => Ok(m),
        None => head_tree(repo),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn repo() -> (tempfile::TempDir, Repository) {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        (d, repo)
    }

    #[test]
    fn absent_index_reads_none_and_effective_falls_back_to_head() {
        let (_d, repo) = repo();
        assert!(read(&repo).unwrap().is_none());
        // unborn HEAD → effective is the empty manifest
        assert_eq!(effective(&repo).unwrap(), Manifest::default());

        // commit an empty tree so HEAD exists
        let tree = repo.write_manifest(&Manifest::default()).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();
        // still no index file → effective == HEAD's tree
        assert!(read(&repo).unwrap().is_none());
        assert_eq!(effective(&repo).unwrap(), head_tree(&repo).unwrap());
    }

    #[test]
    fn write_read_clear_roundtrip() {
        let (_d, repo) = repo();
        let mut m = Manifest::default();
        m.blobs.insert("a.bin".into(), "deadbeef".into());

        write(&repo, &m).unwrap();
        assert_eq!(read(&repo).unwrap(), Some(m.clone()));
        assert_eq!(effective(&repo).unwrap(), m);

        clear(&repo).unwrap();
        assert!(read(&repo).unwrap().is_none());
        // clearing an already-absent index is a no-op (no error)
        clear(&repo).unwrap();
    }
}
