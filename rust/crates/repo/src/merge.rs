//! Recursive merge-base and a manifest-level 3-way merge. Because both commits'
//! chunk/nbt/blob objects already live in the shared store, merging is a 3-way
//! comparison of object ids per chunk/file — no checkout needed.

use crate::manifest::Manifest;
use crate::repository::Repository;
use crate::Result;
use std::collections::{BTreeMap, BTreeSet, HashSet};

#[derive(Debug, PartialEq, Eq)]
pub enum MergeOutcome {
    /// `theirs` is already reachable from `ours`.
    UpToDate,
    /// `ours` is an ancestor of `theirs`; move to this commit.
    FastForward(String),
    /// A new merge commit was created.
    Merged(String),
    /// Conflicting paths; nothing was committed.
    Conflicts(Vec<String>),
}

fn ancestors(repo: &Repository, start: &str) -> Result<HashSet<String>> {
    let mut set = HashSet::new();
    let mut stack = vec![start.to_string()];
    while let Some(c) = stack.pop() {
        if !set.insert(c.clone()) {
            continue;
        }
        if let Ok(commit) = repo.read_commit(&c) {
            for p in commit.parents {
                stack.push(p);
            }
        }
    }
    Ok(set)
}

/// The best common ancestor of `a` and `b` (criss-cross: returns one base).
pub fn merge_base(repo: &Repository, a: &str, b: &str) -> Result<Option<String>> {
    let aa = ancestors(repo, a)?;
    let ab = ancestors(repo, b)?;
    let common: Vec<String> = aa.intersection(&ab).cloned().collect();
    if common.is_empty() {
        return Ok(None);
    }
    // A merge base is a common ancestor that is not itself an ancestor of
    // another common ancestor.
    let mut bases = Vec::new();
    for c in &common {
        let mut dominated = false;
        for o in &common {
            if o != c && ancestors(repo, o)?.contains(c) {
                dominated = true;
                break;
            }
        }
        if !dominated {
            bases.push(c.clone());
        }
    }
    bases.sort();
    Ok(bases.into_iter().next())
}

/// 3-way merge `theirs` into `ours`. On a clean merge creates a merge commit
/// (parents `[ours, theirs]`); on overlap returns the conflicting paths.
pub fn merge(
    repo: &Repository,
    ours: &str,
    theirs: &str,
    message: &str,
    author: &str,
    time: &str,
) -> Result<MergeOutcome> {
    if ours == theirs {
        return Ok(MergeOutcome::UpToDate);
    }
    let base = merge_base(repo, ours, theirs)?;
    if base.as_deref() == Some(theirs) {
        return Ok(MergeOutcome::UpToDate);
    }
    if base.as_deref() == Some(ours) {
        return Ok(MergeOutcome::FastForward(theirs.to_string()));
    }

    let mb = match &base {
        Some(b) => repo.read_manifest(&repo.read_commit(b)?.tree)?,
        None => Manifest::default(),
    };
    let mo = repo.read_manifest(&repo.read_commit(ours)?.tree)?;
    let mt = repo.read_manifest(&repo.read_commit(theirs)?.tree)?;

    let mut merged = Manifest::default();
    let mut conflicts = Vec::new();

    // Regions: 3-way per chunk position.
    let region_rels: BTreeSet<&String> = mb
        .regions
        .keys()
        .chain(mo.regions.keys())
        .chain(mt.regions.keys())
        .collect();
    let empty = BTreeMap::new();
    for rel in region_rels {
        let bcm = mb.regions.get(rel).unwrap_or(&empty);
        let ocm = mo.regions.get(rel).unwrap_or(&empty);
        let tcm = mt.regions.get(rel).unwrap_or(&empty);
        let positions: BTreeSet<&String> = bcm.keys().chain(ocm.keys()).chain(tcm.keys()).collect();
        let mut merged_chunks = BTreeMap::new();
        for pos in positions {
            match three_way(bcm.get(pos), ocm.get(pos), tcm.get(pos)) {
                Ok(Some(h)) => {
                    merged_chunks.insert(pos.clone(), h);
                }
                Ok(None) => {}
                Err(()) => conflicts.push(format!("{rel} chunk {pos}")),
            }
        }
        if !merged_chunks.is_empty() {
            merged.regions.insert(rel.clone(), merged_chunks);
        }
    }

    merge_map(&mb.nbt, &mo.nbt, &mt.nbt, &mut merged.nbt, &mut conflicts);
    merge_map(
        &mb.blobs,
        &mo.blobs,
        &mt.blobs,
        &mut merged.blobs,
        &mut conflicts,
    );

    let dirs: BTreeSet<String> = mo
        .empty_dirs
        .iter()
        .chain(mt.empty_dirs.iter())
        .cloned()
        .collect();
    merged.empty_dirs = dirs.into_iter().collect();

    if !conflicts.is_empty() {
        return Ok(MergeOutcome::Conflicts(conflicts));
    }
    let tree = repo.write_manifest(&merged)?;
    let commit = repo.create_commit(
        &tree,
        vec![ours.to_string(), theirs.to_string()],
        message,
        author,
        time,
    )?;
    Ok(MergeOutcome::Merged(commit))
}

/// 3-way of one entry: `Ok(Some)` = keep id, `Ok(None)` = deleted, `Err` = conflict.
fn three_way(
    base: Option<&String>,
    ours: Option<&String>,
    theirs: Option<&String>,
) -> std::result::Result<Option<String>, ()> {
    if ours == theirs {
        Ok(ours.cloned())
    } else if ours == base {
        Ok(theirs.cloned())
    } else if theirs == base {
        Ok(ours.cloned())
    } else {
        Err(())
    }
}

fn merge_map(
    base: &BTreeMap<String, String>,
    ours: &BTreeMap<String, String>,
    theirs: &BTreeMap<String, String>,
    out: &mut BTreeMap<String, String>,
    conflicts: &mut Vec<String>,
) {
    let keys: BTreeSet<&String> = base
        .keys()
        .chain(ours.keys())
        .chain(theirs.keys())
        .collect();
    for k in keys {
        match three_way(base.get(k), ours.get(k), theirs.get(k)) {
            Ok(Some(h)) => {
                out.insert(k.clone(), h);
            }
            Ok(None) => {}
            Err(()) => conflicts.push(k.clone()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn commit_with(repo: &Repository, chunks: &[(&str, &str)], parents: Vec<String>) -> String {
        let mut m = Manifest::default();
        let mut cm = BTreeMap::new();
        for (pos, h) in chunks {
            cm.insert((*pos).to_string(), (*h).to_string());
        }
        m.regions.insert("region/r.0.0.mca".to_string(), cm);
        let tree = repo.write_manifest(&m).unwrap();
        repo.create_commit(&tree, parents, "m", "me", "t").unwrap()
    }

    fn merged_chunks(repo: &Repository, commit: &str) -> BTreeMap<String, String> {
        let m = repo
            .read_manifest(&repo.read_commit(commit).unwrap().tree)
            .unwrap();
        m.regions
            .get("region/r.0.0.mca")
            .cloned()
            .unwrap_or_default()
    }

    #[test]
    fn merge_base_linear_and_branch() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let a = commit_with(&repo, &[("0,0", "h0")], vec![]);
        let b = commit_with(&repo, &[("0,0", "h0"), ("1,0", "h1")], vec![a.clone()]);
        assert_eq!(
            merge_base(&repo, &b, &a).unwrap().as_deref(),
            Some(a.as_str())
        );

        let o = commit_with(&repo, &[("0,0", "h0"), ("2,0", "ho")], vec![a.clone()]);
        let t = commit_with(&repo, &[("0,0", "h0"), ("3,0", "ht")], vec![a.clone()]);
        assert_eq!(
            merge_base(&repo, &o, &t).unwrap().as_deref(),
            Some(a.as_str())
        );
    }

    #[test]
    fn fast_forward_and_up_to_date() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let a = commit_with(&repo, &[("0,0", "h0")], vec![]);
        let b = commit_with(&repo, &[("0,0", "h0"), ("1,0", "h1")], vec![a.clone()]);
        assert_eq!(
            merge(&repo, &a, &b, "m", "me", "t").unwrap(),
            MergeOutcome::FastForward(b.clone())
        );
        assert_eq!(
            merge(&repo, &b, &a, "m", "me", "t").unwrap(),
            MergeOutcome::UpToDate
        );
    }

    #[test]
    fn clean_three_way() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let base = commit_with(&repo, &[("0,0", "h0")], vec![]);
        let ours = commit_with(&repo, &[("0,0", "h0"), ("1,0", "ho")], vec![base.clone()]);
        let theirs = commit_with(&repo, &[("0,0", "h0"), ("2,0", "ht")], vec![base.clone()]);
        match merge(&repo, &ours, &theirs, "m", "me", "t").unwrap() {
            MergeOutcome::Merged(c) => {
                let cm = merged_chunks(&repo, &c);
                assert!(cm.contains_key("1,0") && cm.contains_key("2,0"));
            }
            x => panic!("expected merge, got {x:?}"),
        }
    }

    #[test]
    fn conflict_on_same_chunk() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let base = commit_with(&repo, &[("0,0", "h0")], vec![]);
        let ours = commit_with(&repo, &[("0,0", "ho")], vec![base.clone()]);
        let theirs = commit_with(&repo, &[("0,0", "ht")], vec![base.clone()]);
        assert!(matches!(
            merge(&repo, &ours, &theirs, "m", "me", "t").unwrap(),
            MergeOutcome::Conflicts(_)
        ));
    }
}
