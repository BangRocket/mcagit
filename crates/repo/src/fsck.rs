//! Integrity + reachability check: every stored object must re-hash to its id,
//! and we report objects unreachable from any branch/HEAD.

use crate::manifest::Manifest;
use crate::repository::Repository;
use crate::Result;
use std::collections::HashSet;

#[derive(Debug, Default)]
pub struct FsckReport {
    pub checked: usize,
    pub corrupt: Vec<String>,
    pub missing: Vec<String>,
    pub unreachable: usize,
    /// Leaf objects absent because this is a partial clone — backfilled on
    /// demand, so not counted as missing.
    pub promised: usize,
}

impl FsckReport {
    pub fn is_clean(&self) -> bool {
        self.corrupt.is_empty() && self.missing.is_empty()
    }
}

/// Verify object integrity and reachability.
pub fn fsck(repo: &Repository) -> Result<FsckReport> {
    let store = repo.objects();
    let all = store.all_ids();
    let mut report = FsckReport::default();

    for id in &all {
        report.checked += 1;
        match store.read(id) {
            Ok(bytes) => {
                if blake3::hash(&bytes).to_hex().to_string() != *id {
                    report.corrupt.push(id.clone());
                }
            }
            Err(_) => report.corrupt.push(id.clone()),
        }
    }

    // In a partial clone, leaf objects are intentionally absent (backfilled on
    // demand), so their absence is "promised", not "missing".
    let partial = repo.is_partial();
    let mut reachable: HashSet<String> = HashSet::new();
    let mut stack = tips(repo);
    while let Some(c) = stack.pop() {
        if !reachable.insert(c.clone()) {
            continue;
        }
        // An annotated tag object: keep it and walk its target commit.
        if let Some(tag) = repo.read_tag_object(&c) {
            stack.push(tag.object);
            continue;
        }
        let commit = match repo.read_commit(&c) {
            Ok(c) => c,
            Err(_) => {
                report.missing.push(c.clone());
                continue;
            }
        };
        reachable.insert(commit.tree.clone());
        match repo.read_manifest(&commit.tree) {
            Ok(m) => {
                for id in manifest_ids(&m) {
                    if !store.exists(&id) {
                        if partial {
                            report.promised += 1;
                        } else {
                            report.missing.push(id.clone());
                        }
                    }
                    reachable.insert(id);
                }
            }
            Err(_) => report.missing.push(commit.tree.clone()),
        }
        for p in repo.parents_of(&c)? {
            stack.push(p);
        }
    }

    report.unreachable = all.iter().filter(|id| !reachable.contains(*id)).count();
    Ok(report)
}

pub(crate) fn tips(repo: &Repository) -> Vec<String> {
    let mut tips = Vec::new();
    if let Some(h) = repo.head_commit() {
        tips.push(h);
    }
    for b in repo.branches() {
        if let Some(h) = repo.read_branch(&b) {
            tips.push(h);
        }
    }
    // Tags are roots too: a commit (or annotated tag object) referenced only by
    // a tag must survive gc and count as reachable.
    for t in repo.tags() {
        if let Some(h) = repo.read_tag(&t) {
            tips.push(h);
        }
    }
    tips
}

/// Every leaf object id a manifest references (chunk objects, loose NBT, blobs)
/// — i.e. all the content a partial clone backfills, distinct from the
/// commit/tree skeleton.
pub fn manifest_ids(m: &Manifest) -> Vec<String> {
    let mut ids = Vec::new();
    for chunks in m.regions.values() {
        for id in chunks.values() {
            ids.push(id.clone());
        }
    }
    ids.extend(m.nbt.values().cloned());
    ids.extend(m.blobs.values().cloned());
    ids
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{snapshot, Manifest};
    use std::path::Path;

    fn tiny_world(world: &Path) {
        std::fs::create_dir_all(world).unwrap();
        std::fs::write(world.join("icon.png"), b"PNGDATA").unwrap();
    }

    #[test]
    fn partial_repo_tolerates_absent_leaves() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("a.bin"), b"alpha").unwrap();
        std::fs::write(world.join("b.bin"), b"beta").unwrap();
        // hash_only computes the manifest WITHOUT storing leaf objects — exactly
        // a partial clone's skeleton: tree + commit present, leaves absent.
        let m: Manifest = snapshot::hash_only(&repo, &world).unwrap();
        assert_eq!(manifest_ids(&m).len(), 2);
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        // without the promisor marker, the absent leaves are reported missing
        let r = fsck(&repo).unwrap();
        assert!(!r.is_clean(), "absent leaves are corruption in a full repo");

        // with the marker, they're expected: clean, counted as promised
        repo.write_promisor("path:///origin").unwrap();
        let r = fsck(&repo).unwrap();
        assert!(r.is_clean(), "missing={:?}", r.missing);
        assert_eq!(r.promised, 2);
        assert!(r.missing.is_empty());
    }

    #[test]
    fn clean_repo_has_no_issues() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        tiny_world(&world);
        let m: Manifest = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        let r = fsck(&repo).unwrap();
        assert!(
            r.is_clean(),
            "corrupt={:?} missing={:?}",
            r.corrupt,
            r.missing
        );
        assert!(r.checked > 0);
        assert_eq!(r.unreachable, 0);
    }
}
