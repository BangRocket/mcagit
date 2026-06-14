//! The staging index: a persistent partial `Manifest` (`<repo>/index`) that
//! `commit` turns into the next tree. A *missing* index file means "index ≡
//! HEAD's tree" — a clean index is the file's absence, never a copy of HEAD.

use crate::manifest::Manifest;
use crate::repository::Repository;
use crate::{pathspec, snapshot, RepoError, Result};
use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};

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

/// Stage the worktree state of every path selected by `specs` (relative to the
/// worktree root) into the index: update/insert entries for present files and
/// remove entries for in-scope paths that no longer exist (staged deletions).
/// Returns the number of index entries that changed vs. before. Errors if the
/// pathspecs match nothing (no worktree file and no in-scope index entry).
pub fn add_paths(repo: &Repository, world_dir: &Path, specs: &[String]) -> Result<usize> {
    let accept = |rel: &str| pathspec::matches_any(specs, rel);
    let partial = snapshot::snapshot_scoped(repo, world_dir, &accept)?;

    // Paths actually present in the worktree within scope.
    let present: HashSet<String> = partial
        .regions
        .keys()
        .chain(partial.nbt.keys())
        .chain(partial.blobs.keys())
        .cloned()
        .collect();

    let before = effective(repo)?;
    let mut idx = before.clone();

    // Overlay freshly-snapshotted in-scope entries.
    for (k, v) in partial.regions {
        idx.regions.insert(k, v);
    }
    for (k, v) in partial.nbt {
        idx.nbt.insert(k, v);
    }
    for (k, v) in partial.blobs {
        idx.blobs.insert(k, v);
    }

    // Staged deletions: in-scope index entries no longer in the worktree.
    idx.regions.retain(|k, _| !accept(k) || present.contains(k));
    idx.nbt.retain(|k, _| !accept(k) || present.contains(k));
    idx.blobs.retain(|k, _| !accept(k) || present.contains(k));

    // Recompute in-scope empty dirs.
    idx.empty_dirs.retain(|dir| !accept(dir));
    idx.empty_dirs.extend(partial.empty_dirs);
    idx.empty_dirs.sort();
    idx.empty_dirs.dedup();

    // Pathspec matched nothing at all → git-style error.
    let in_scope_before = before
        .regions
        .keys()
        .chain(before.nbt.keys())
        .chain(before.blobs.keys())
        .chain(before.empty_dirs.iter())
        .any(|k| accept(k));
    if present.is_empty() && !in_scope_before {
        return Err(RepoError::Other(format!(
            "pathspec '{}' did not match any files",
            specs.join(" ")
        )));
    }

    let changed = changed_entry_count(&before, &idx);
    if changed > 0 {
        write(repo, &idx)?;
    }
    Ok(changed)
}

/// Count paths whose manifest entry differs between two manifests — regions by
/// their full chunk map, nbt/blobs by object id — plus any empty-dir changes.
fn changed_entry_count(a: &Manifest, b: &Manifest) -> usize {
    fn count<V: PartialEq>(ma: &BTreeMap<String, V>, mb: &BTreeMap<String, V>) -> usize {
        let changed = mb.iter().filter(|(k, v)| ma.get(*k) != Some(*v)).count();
        let removed = ma.keys().filter(|k| !mb.contains_key(*k)).count();
        changed + removed
    }
    let dirs: HashSet<&String> = a.empty_dirs.iter().collect();
    let dirs_b: HashSet<&String> = b.empty_dirs.iter().collect();
    count(&a.regions, &b.regions)
        + count(&a.nbt, &b.nbt)
        + count(&a.blobs, &b.blobs)
        + dirs.symmetric_difference(&dirs_b).count()
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

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

    fn world(dir: &TempDir) -> std::path::PathBuf {
        let w = dir.path().join("world");
        std::fs::create_dir_all(w.join("sub")).unwrap();
        std::fs::write(w.join("a.bin"), b"alpha").unwrap();
        std::fs::write(w.join("sub").join("b.bin"), b"beta").unwrap();
        std::fs::write(w.join("c.bin"), b"gamma").unwrap();
        w
    }

    #[test]
    fn add_stages_a_single_file() {
        let (d, repo) = repo();
        let w = world(&d);
        let n = add_paths(&repo, &w, &["a.bin".to_string()]).unwrap();
        assert_eq!(n, 1);
        let idx = read(&repo).unwrap().unwrap();
        assert!(idx.blobs.contains_key("a.bin"));
        assert!(!idx.blobs.contains_key("c.bin"), "c.bin not staged");
        assert!(!idx.blobs.contains_key("sub/b.bin"), "sub/b.bin not staged");
    }

    #[test]
    fn add_directory_is_recursive() {
        let (d, repo) = repo();
        let w = world(&d);
        add_paths(&repo, &w, &["sub".to_string()]).unwrap();
        let idx = read(&repo).unwrap().unwrap();
        assert!(idx.blobs.contains_key("sub/b.bin"));
        assert!(!idx.blobs.contains_key("a.bin"));
    }

    #[test]
    fn add_dot_stages_everything() {
        let (d, repo) = repo();
        let w = world(&d);
        let n = add_paths(&repo, &w, &[".".to_string()]).unwrap();
        assert_eq!(n, 3);
        let idx = read(&repo).unwrap().unwrap();
        assert_eq!(idx.blobs.len(), 3);
    }

    #[test]
    fn add_stages_a_deletion_within_scope() {
        let (d, repo) = repo();
        let w = world(&d);
        // stage everything, then delete a file and re-add its directory scope
        add_paths(&repo, &w, &[".".to_string()]).unwrap();
        std::fs::remove_file(w.join("a.bin")).unwrap();
        add_paths(&repo, &w, &["a.bin".to_string()]).unwrap();
        let idx = read(&repo).unwrap().unwrap();
        assert!(!idx.blobs.contains_key("a.bin"), "deletion staged");
        assert!(idx.blobs.contains_key("c.bin"), "others untouched");
    }

    #[test]
    fn add_nonmatching_pathspec_errors() {
        let (d, repo) = repo();
        let w = world(&d);
        let err = add_paths(&repo, &w, &["nope/x.bin".to_string()]).unwrap_err();
        assert!(err.to_string().contains("did not match"), "got: {err}");
    }

    #[test]
    fn add_unchanged_file_returns_zero_changes() {
        let (d, repo) = repo();
        let w = world(&d);
        assert_eq!(add_paths(&repo, &w, &["a.bin".to_string()]).unwrap(), 1);
        // staging the identical file again changes nothing
        let n = add_paths(&repo, &w, &["a.bin".to_string()]).unwrap();
        assert_eq!(n, 0);
        assert!(read(&repo).unwrap().unwrap().blobs.contains_key("a.bin"));
    }

    #[test]
    fn add_stages_an_empty_dir_deletion() {
        let (d, repo) = repo();
        let w = world(&d);
        std::fs::create_dir(w.join("emptydir")).unwrap();
        add_paths(&repo, &w, &[".".to_string()]).unwrap();
        assert!(read(&repo)
            .unwrap()
            .unwrap()
            .empty_dirs
            .contains(&"emptydir".to_string()));

        // remove the empty dir and re-stage just its scope
        std::fs::remove_dir(w.join("emptydir")).unwrap();
        let n = add_paths(&repo, &w, &["emptydir".to_string()]).unwrap();
        assert!(
            n >= 1,
            "empty-dir removal must be staged (not silently dropped)"
        );
        assert!(!read(&repo)
            .unwrap()
            .unwrap()
            .empty_dirs
            .contains(&"emptydir".to_string()));
    }
}
