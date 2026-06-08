//! Remote transport abstraction: a small object/ref protocol with a scheme
//! dispatch. The local (filesystem path) backend is implemented here; http/ssh
//! and cloud (s3/azure) backends layer onto the same `Transport` trait.
//!
//! Because objects are content-addressed, transfer copies only what the other
//! side lacks: push asks the remote which reachable objects are `missing` and
//! sends those; fetch walks the commit graph from the advertised tip, pulling
//! only objects the local repo doesn't already have.

use crate::repository::Repository;
use crate::{RepoError, Result};
use std::path::Path;

/// The minimal remote protocol.
pub trait Transport {
    /// `(refname, hash)` for every branch and tag, e.g. `("refs/heads/main", h)`.
    fn list_refs(&self) -> Result<Vec<(String, String)>>;
    /// Object content for `id` (decompressed canonical bytes).
    fn get_object(&self, id: &str) -> Result<Vec<u8>>;
    /// Which of `ids` the remote is missing.
    fn missing(&self, ids: &[String]) -> Result<Vec<String>>;
    /// Store object `content` (its id is `content`-addressed).
    fn put_object(&self, content: &[u8]) -> Result<()>;
    /// Point `branch` at `hash` on the remote.
    fn set_branch(&self, branch: &str, hash: &str) -> Result<()>;
    /// Flush buffered state (e.g. reload packs). Default no-op.
    fn finish(&self) -> Result<()> {
        Ok(())
    }
}

/// Filesystem (path) transport: the remote is a local bare repo.
pub struct LocalTransport {
    remote: Repository,
}

impl LocalTransport {
    pub fn open(dir: &Path) -> Result<Self> {
        Ok(Self {
            remote: Repository::open(dir)?,
        })
    }
}

impl Transport for LocalTransport {
    fn list_refs(&self) -> Result<Vec<(String, String)>> {
        let mut out = Vec::new();
        for b in self.remote.branches() {
            if let Some(h) = self.remote.read_branch(&b) {
                out.push((format!("refs/heads/{b}"), h));
            }
        }
        for t in self.remote.tags() {
            if let Some(h) = self.remote.read_tag(&t) {
                out.push((format!("refs/tags/{t}"), h));
            }
        }
        Ok(out)
    }
    fn get_object(&self, id: &str) -> Result<Vec<u8>> {
        self.remote.objects().read(id)
    }
    fn missing(&self, ids: &[String]) -> Result<Vec<String>> {
        Ok(ids
            .iter()
            .filter(|id| !self.remote.objects().exists(id))
            .cloned()
            .collect())
    }
    fn put_object(&self, content: &[u8]) -> Result<()> {
        self.remote.objects().write(content)?;
        Ok(())
    }
    fn set_branch(&self, branch: &str, hash: &str) -> Result<()> {
        self.remote.write_branch(branch, hash)
    }
    fn finish(&self) -> Result<()> {
        self.remote.objects().reload_packs();
        Ok(())
    }
}

const NET_SCHEMES: [&str; 5] = ["http://", "https://", "ssh://", "s3://", "azure://"];

/// Dispatch a remote URL/path to a transport. Local filesystem paths work today;
/// http/ssh and cloud backends are tracked but not yet implemented.
pub fn connect(url_or_path: &str) -> Result<Box<dyn Transport>> {
    let lower = url_or_path.to_ascii_lowercase();
    if let Some(scheme) = NET_SCHEMES.iter().find(|s| lower.starts_with(**s)) {
        return Err(RepoError::Other(format!(
            "remote transport not yet implemented: {scheme} — local-path remotes work today"
        )));
    }
    Ok(Box::new(LocalTransport::open(Path::new(url_or_path))?))
}

/// Resolve a configured remote name to its URL, or pass a literal URL/path through.
pub fn resolve(repo: &Repository, name_or_url: &str) -> String {
    repo.remote_url(name_or_url)
        .unwrap_or_else(|| name_or_url.to_string())
}

/// Push `branch` from `local` over the transport. Returns the number of objects copied.
pub fn push(local: &Repository, t: &dyn Transport, branch: &str) -> Result<usize> {
    let tip = local
        .read_branch(branch)
        .ok_or_else(|| RepoError::BadRef(branch.to_string()))?;
    let ids = crate::transfer::reachable(local, &tip)?;
    let missing = t.missing(&ids)?;
    for id in &missing {
        t.put_object(&local.objects().read(id)?)?;
    }
    t.set_branch(branch, &tip)?;
    t.finish()?;
    Ok(missing.len())
}

/// Fetch `branch` from the transport into `local`, pulling only missing objects.
/// Returns `(tip, objects_copied)`. Does not move any local branch — the caller
/// records the result (e.g. as a remote-tracking ref).
pub fn fetch(local: &Repository, t: &dyn Transport, branch: &str) -> Result<(String, usize)> {
    let want = format!("refs/heads/{branch}");
    let tip = t
        .list_refs()?
        .into_iter()
        .find(|(r, _)| *r == want)
        .map(|(_, h)| h)
        .ok_or_else(|| RepoError::BadRef(branch.to_string()))?;
    let copied = fetch_reachable(local, t, &tip)?;
    Ok((tip, copied))
}

/// Walk from `tip` over the transport, storing every reachable object `local`
/// lacks. Each object is written before being parsed so its children resolve.
fn fetch_reachable(local: &Repository, t: &dyn Transport, tip: &str) -> Result<usize> {
    use std::collections::HashSet;
    let mut seen: HashSet<String> = HashSet::new();
    let mut stack = vec![tip.to_string()];
    let mut copied = 0usize;
    while let Some(c) = stack.pop() {
        if !seen.insert(c.clone()) {
            continue;
        }
        if !local.objects().exists(&c) {
            local.objects().write(&t.get_object(&c)?)?;
            copied += 1;
        }
        if let Ok(commit) = local.read_commit(&c) {
            if !local.objects().exists(&commit.tree) {
                local.objects().write(&t.get_object(&commit.tree)?)?;
                copied += 1;
            }
            if let Ok(m) = local.read_manifest(&commit.tree) {
                for id in crate::fsck::manifest_ids(&m) {
                    if !local.objects().exists(&id) {
                        local.objects().write(&t.get_object(&id)?)?;
                        copied += 1;
                    }
                }
            }
            for p in commit.parents {
                stack.push(p);
            }
        }
    }
    if copied > 0 {
        local.objects().reload_packs();
    }
    Ok(copied)
}

/// Clone the repo at `url_or_path` into a fresh repo at `dst`: fetch every branch
/// and tag, set an `origin` remote, and check out the default branch.
pub fn clone(url_or_path: &str, dst: &Path) -> Result<Repository> {
    let t = connect(url_or_path)?;
    let dest = Repository::init(dst)?;
    let refs = t.list_refs()?;
    let mut branches: Vec<(String, String)> = Vec::new();
    for (refname, hash) in &refs {
        if let Some(b) = refname.strip_prefix("refs/heads/") {
            fetch_reachable(&dest, t.as_ref(), hash)?;
            dest.write_branch(b, hash)?;
            branches.push((b.to_string(), hash.clone()));
        }
    }
    for (refname, hash) in &refs {
        if let Some(tag) = refname.strip_prefix("refs/tags/") {
            fetch_reachable(&dest, t.as_ref(), hash)?;
            dest.write_tag(tag, hash)?;
        }
    }
    dest.set_remote_url("origin", url_or_path)?;
    let default = branches
        .iter()
        .find(|(b, _)| b == "main" || b == "master")
        .or_else(|| branches.first())
        .map(|(b, _)| b.clone());
    if let Some(b) = default {
        dest.set_head_to_branch(&b)?;
    }
    Ok(dest)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{checkout, snapshot};

    fn commit_world(repo: &Repository, contents: &[u8], msg: &str) -> String {
        let d = repo.dir().parent().unwrap().join(format!("w-{msg}"));
        std::fs::create_dir_all(d.join("region")).unwrap();
        std::fs::write(d.join("level.dat_marker"), contents).unwrap();
        let m = snapshot::snapshot(repo, &d).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], msg, "me", "0").unwrap();
        let cur = repo.current_branch().unwrap_or_else(|| "main".into());
        repo.write_branch(&cur, &c).unwrap();
        c
    }

    #[test]
    fn remote_add_lsremote_fetch_push() {
        let tmp = tempfile::tempdir().unwrap();
        let origin = Repository::init(&tmp.path().join("origin")).unwrap();
        origin.set_head_to_branch("main").unwrap();
        let c1 = commit_world(&origin, b"v1", "one");

        // clone via transport
        let clone_dir = tmp.path().join("clone");
        let local = clone(origin.dir().to_str().unwrap(), &clone_dir).unwrap();
        assert_eq!(local.read_branch("main").as_deref(), Some(c1.as_str()));
        assert_eq!(
            local.remote_url("origin").as_deref(),
            Some(origin.dir().to_str().unwrap())
        );

        // ls-remote advertises the origin branch
        let t = connect(origin.dir().to_str().unwrap()).unwrap();
        let refs = t.list_refs().unwrap();
        assert!(refs.iter().any(|(r, h)| r == "refs/heads/main" && h == &c1));

        // new commit on origin, fetch into clone as a tracking ref
        let c2 = commit_world(&origin, b"v2", "two");
        let t = connect(&resolve(&local, "origin")).unwrap();
        let (tip, copied) = fetch(&local, t.as_ref(), "main").unwrap();
        assert_eq!(tip, c2);
        assert!(copied > 0);
        local.write_remote_ref("origin", "main", &tip).unwrap();
        assert_eq!(
            local.read_remote_ref("origin", "main").as_deref(),
            Some(c2.as_str())
        );
        // local main is untouched by fetch
        assert_eq!(local.read_branch("main").as_deref(), Some(c1.as_str()));

        // push a clone-side commit back to origin
        local.set_head_to_branch("main").unwrap();
        local.write_branch("main", &c2).unwrap();
        let c3 = commit_world(&local, b"v3", "three");
        let t = connect(&resolve(&local, "origin")).unwrap();
        let n = push(&local, t.as_ref(), "main").unwrap();
        assert!(n > 0);
        assert_eq!(origin.read_branch("main").as_deref(), Some(c3.as_str()));
    }

    #[test]
    fn network_schemes_are_stubbed() {
        for url in ["http://x/y", "ssh://h/p", "s3://b/k", "azure://a/c"] {
            assert!(connect(url).is_err(), "{url} should be unimplemented");
        }
    }

    #[test]
    fn fetched_world_checks_out() {
        let tmp = tempfile::tempdir().unwrap();
        let origin = Repository::init(&tmp.path().join("origin")).unwrap();
        origin.set_head_to_branch("main").unwrap();
        let c1 = commit_world(&origin, b"hello", "one");
        let local = clone(origin.dir().to_str().unwrap(), &tmp.path().join("clone")).unwrap();
        let m = local
            .read_manifest(&local.read_commit(&c1).unwrap().tree)
            .unwrap();
        let out = tmp.path().join("out");
        checkout(&local, &m, &out, false).unwrap();
        assert!(out.join("level.dat_marker").exists());
    }
}
