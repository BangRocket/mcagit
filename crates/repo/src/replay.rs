//! Replaying single-commit changes: cherry-pick, revert, and rebase — all built
//! on the shared manifest-level 3-way combine.

use crate::manifest::Manifest;
use crate::merge::{merge_base, three_way_manifest};
use crate::repository::Repository;
use crate::Result;

#[derive(Debug, PartialEq, Eq)]
pub enum ReplayOutcome {
    Done(String),
    Conflicts(Vec<String>),
}

fn manifest_of(repo: &Repository, commit: &str) -> Result<Manifest> {
    repo.read_manifest(&repo.read_commit(commit)?.tree)
}

fn parent_manifest(repo: &Repository, commit: &str) -> Result<Manifest> {
    match repo.parents_of(commit)?.first() {
        Some(p) => manifest_of(repo, p),
        None => Ok(Manifest::default()),
    }
}

/// Apply `pick`'s change (its parent → it) on top of `onto`, preserving its
/// author and message. New commit's single parent is `onto`.
pub fn cherry_pick(repo: &Repository, onto: &str, pick: &str, time: &str) -> Result<ReplayOutcome> {
    let base = parent_manifest(repo, pick)?;
    let ours = manifest_of(repo, onto)?;
    let theirs = manifest_of(repo, pick)?;
    let (merged, conflicts) = three_way_manifest(&base, &ours, &theirs);
    if !conflicts.is_empty() {
        return Ok(ReplayOutcome::Conflicts(conflicts));
    }
    let pc = repo.read_commit(pick)?;
    let tree = repo.write_manifest(&merged)?;
    let commit =
        repo.create_commit(&tree, vec![onto.to_string()], &pc.message, &pc.author, time)?;
    Ok(ReplayOutcome::Done(commit))
}

/// Create a commit on top of `onto` that undoes `target` (its content → its parent's).
pub fn revert(
    repo: &Repository,
    onto: &str,
    target: &str,
    author: &str,
    time: &str,
) -> Result<ReplayOutcome> {
    let base = manifest_of(repo, target)?;
    let ours = manifest_of(repo, onto)?;
    let theirs = parent_manifest(repo, target)?;
    let (merged, conflicts) = three_way_manifest(&base, &ours, &theirs);
    if !conflicts.is_empty() {
        return Ok(ReplayOutcome::Conflicts(conflicts));
    }
    let tree = repo.write_manifest(&merged)?;
    let short = &target[..target.len().min(10)];
    let commit = repo.create_commit(
        &tree,
        vec![onto.to_string()],
        &format!("Revert commit {short}"),
        author,
        time,
    )?;
    Ok(ReplayOutcome::Done(commit))
}

/// Replay the commits in `merge_base(head, upstream)..head` onto `upstream`
/// (oldest first) via cherry-pick. Returns the new head, or the first conflict.
pub fn rebase(repo: &Repository, upstream: &str, head: &str, time: &str) -> Result<ReplayOutcome> {
    let base = merge_base(repo, head, upstream)?;
    let mut commits = Vec::new();
    let mut cur = Some(head.to_string());
    while let Some(c) = cur {
        if Some(&c) == base.as_ref() {
            break;
        }
        commits.push(c.clone());
        cur = repo.parents_of(&c)?.into_iter().next();
    }
    commits.reverse();

    let mut new_head = upstream.to_string();
    for c in commits {
        match cherry_pick(repo, &new_head, &c, time)? {
            ReplayOutcome::Done(h) => new_head = h,
            conflict => return Ok(conflict),
        }
    }
    Ok(ReplayOutcome::Done(new_head))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;

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

    fn chunks_of(repo: &Repository, commit: &str) -> BTreeMap<String, String> {
        manifest_of(repo, commit)
            .unwrap()
            .regions
            .get("region/r.0.0.mca")
            .cloned()
            .unwrap_or_default()
    }

    #[test]
    fn cherry_pick_applies_a_commits_change() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let base = commit_with(&repo, &[("0,0", "h0")], vec![]);
        // a side commit that adds chunk 5,5
        let side = commit_with(&repo, &[("0,0", "h0"), ("5,5", "h5")], vec![base.clone()]);
        // an unrelated commit on the main line
        let main = commit_with(&repo, &[("0,0", "h0"), ("1,1", "h1")], vec![base.clone()]);

        match cherry_pick(&repo, &main, &side, "t").unwrap() {
            ReplayOutcome::Done(c) => {
                let cm = chunks_of(&repo, &c);
                assert!(cm.contains_key("1,1") && cm.contains_key("5,5"));
            }
            x => panic!("{x:?}"),
        }
    }

    #[test]
    fn revert_undoes_a_commit() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let base = commit_with(&repo, &[("0,0", "h0")], vec![]);
        let added = commit_with(&repo, &[("0,0", "h0"), ("9,9", "h9")], vec![base.clone()]);
        match revert(&repo, &added, &added, "me", "t").unwrap() {
            ReplayOutcome::Done(c) => {
                let cm = chunks_of(&repo, &c);
                assert!(!cm.contains_key("9,9"), "revert should drop 9,9");
            }
            x => panic!("{x:?}"),
        }
    }

    #[test]
    fn rebase_replays_onto_upstream() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let base = commit_with(&repo, &[("0,0", "h0")], vec![]);
        // feature branch: base -> f1 -> f2
        let f1 = commit_with(&repo, &[("0,0", "h0"), ("1,1", "h1")], vec![base.clone()]);
        let f2 = commit_with(
            &repo,
            &[("0,0", "h0"), ("1,1", "h1"), ("2,2", "h2")],
            vec![f1],
        );
        // upstream advanced: base -> u1
        let u1 = commit_with(&repo, &[("0,0", "h0"), ("8,8", "h8")], vec![base.clone()]);

        match rebase(&repo, &u1, &f2, "t").unwrap() {
            ReplayOutcome::Done(c) => {
                let cm = chunks_of(&repo, &c);
                assert!(cm.contains_key("1,1") && cm.contains_key("2,2") && cm.contains_key("8,8"));
            }
            x => panic!("{x:?}"),
        }
    }
}
