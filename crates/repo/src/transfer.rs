//! Object transfer between repositories over the filesystem (path transport):
//! clone, push, and fetch copy only the objects the destination lacks. Network
//! transports (http/ssh) and cloud remotes layer on top of this same copy core.

use crate::fsck::manifest_ids;
use crate::repository::Repository;
use crate::Result;
use std::collections::HashSet;
use std::path::Path;

/// All objects reachable from `tip` (commit + tree + manifest objects, recursively).
pub(crate) fn reachable(repo: &Repository, tip: &str) -> Result<Vec<String>> {
    let mut out = Vec::new();
    let mut seen = HashSet::new();
    let mut stack = vec![tip.to_string()];
    while let Some(c) = stack.pop() {
        if !seen.insert(c.clone()) {
            continue;
        }
        if !repo.objects().exists(&c) {
            continue;
        }
        out.push(c.clone());
        // An annotated tag object: include it and walk its target commit.
        if let Some(tag) = repo.read_tag_object(&c) {
            stack.push(tag.object);
            continue;
        }
        if let Ok(commit) = repo.read_commit(&c) {
            if seen.insert(commit.tree.clone()) {
                out.push(commit.tree.clone());
            }
            if let Ok(m) = repo.read_manifest(&commit.tree) {
                for id in manifest_ids(&m) {
                    if seen.insert(id.clone()) {
                        out.push(id);
                    }
                }
            }
            for p in repo.parents_of(&c)? {
                stack.push(p);
            }
        }
    }
    Ok(out)
}

/// Copy objects in `ids` from `src` to `dst` that `dst` lacks. Returns the count copied.
fn copy_objects(src: &Repository, dst: &Repository, ids: &[String]) -> Result<usize> {
    let mut copied = 0;
    for id in ids {
        if !dst.objects().exists(id) {
            let bytes = src.objects().read(id)?;
            dst.objects().write(&bytes)?;
            copied += 1;
        }
    }
    if copied > 0 {
        dst.objects().reload_packs();
    }
    Ok(copied)
}

/// Clone `src` into a fresh repo at `dst`: copy all branch/tag objects + refs.
pub fn clone_local(src: &Path, dst: &Path) -> Result<Repository> {
    let source = Repository::open(src)?;
    let dest = Repository::init(dst)?;
    for b in source.branches() {
        if let Some(tip) = source.read_branch(&b) {
            let ids = reachable(&source, &tip)?;
            copy_objects(&source, &dest, &ids)?;
            dest.write_branch(&b, &tip)?;
        }
    }
    for t in source.tags() {
        if let Some(tip) = source.read_tag(&t) {
            let ids = reachable(&source, &tip)?;
            copy_objects(&source, &dest, &ids)?;
            dest.write_tag(&t, &tip)?;
        }
    }
    if let Some(cur) = source.current_branch() {
        dest.set_head_to_branch(&cur)?;
    }
    Ok(dest)
}

/// Push `branch` from `local` to the repo at `remote_dir`. Returns objects copied.
pub fn push_local(local: &Repository, remote_dir: &Path, branch: &str) -> Result<usize> {
    let remote = Repository::open(remote_dir)?;
    let tip = local
        .read_branch(branch)
        .ok_or_else(|| crate::RepoError::BadRef(branch.to_string()))?;
    let ids = reachable(local, &tip)?;
    let copied = copy_objects(local, &remote, &ids)?;
    remote.write_branch(branch, &tip)?;
    Ok(copied)
}

/// Fetch `branch` from the repo at `remote_dir` into `local`. Returns objects copied.
pub fn fetch_local(local: &Repository, remote_dir: &Path, branch: &str) -> Result<usize> {
    let remote = Repository::open(remote_dir)?;
    let tip = remote
        .read_branch(branch)
        .ok_or_else(|| crate::RepoError::BadRef(branch.to_string()))?;
    let ids = reachable(&remote, &tip)?;
    let copied = copy_objects(&remote, local, &ids)?;
    local.write_branch(branch, &tip)?;
    Ok(copied)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{checkout, snapshot};

    fn make_world(dir: &Path, contents: &[u8]) {
        std::fs::create_dir_all(dir).unwrap();
        std::fs::write(dir.join("icon.png"), contents).unwrap();
    }

    fn commit_world(repo: &Repository, world: &Path, msg: &str) -> String {
        let m = snapshot::snapshot(repo, world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let parents = repo.head_commit().into_iter().collect();
        let c = repo.create_commit(&tree, parents, msg, "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();
        c
    }

    #[test]
    fn clone_then_push_roundtrip() {
        let d = tempfile::tempdir().unwrap();
        let srcdir = d.path().join("src");
        let src = Repository::init(&srcdir).unwrap();
        let world = d.path().join("world");
        make_world(&world, b"v1");
        let c1 = commit_world(&src, &world, "v1");

        // clone src -> dst; dst reproduces the world
        let dstdir = d.path().join("dst");
        let dst = clone_local(&srcdir, &dstdir).unwrap();
        assert_eq!(dst.read_branch("main").as_deref(), Some(c1.as_str()));
        let out = d.path().join("checkout");
        let m = dst
            .read_manifest(&dst.read_commit(&c1).unwrap().tree)
            .unwrap();
        checkout(&dst, &m, &out, false).unwrap();
        assert_eq!(std::fs::read(out.join("icon.png")).unwrap(), b"v1");

        // new commit in src, push to dst
        std::fs::write(world.join("icon.png"), b"v2").unwrap();
        let c2 = commit_world(&src, &world, "v2");
        let copied = push_local(&src, &dstdir, "main").unwrap();
        assert!(copied > 0);
        let dst2 = Repository::open(&dstdir).unwrap();
        assert_eq!(dst2.read_branch("main").as_deref(), Some(c2.as_str()));
        assert!(dst2.objects().exists(&c2));
    }

    #[test]
    fn fetch_pulls_new_objects() {
        let d = tempfile::tempdir().unwrap();
        let remotedir = d.path().join("remote");
        let remote = Repository::init(&remotedir).unwrap();
        let world = d.path().join("world");
        make_world(&world, b"r1");
        let c1 = commit_world(&remote, &world, "r1");

        let localdir = d.path().join("local");
        let local = Repository::init(&localdir).unwrap();
        let copied = fetch_local(&local, &remotedir, "main").unwrap();
        assert!(copied > 0);
        assert_eq!(local.read_branch("main").as_deref(), Some(c1.as_str()));
        assert!(local.objects().exists(&c1));
    }
}
