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
use std::io::BufReader;
use std::path::Path;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::Mutex;

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

/// HTTP(S) transport speaking the mcagit hub protocol against a repo URL such as
/// `http://host:5080/r/<name>`. Object bodies carry decompressed content (the
/// server re-hashes with blake3 on store); a bearer PAT comes from `MCAGIT_TOKEN`.
pub struct HttpTransport {
    base: String,
    token: Option<String>,
}

impl HttpTransport {
    pub fn new(url: &str) -> Self {
        Self {
            base: url.trim_end_matches('/').to_string(),
            token: std::env::var("MCAGIT_TOKEN").ok().filter(|s| !s.is_empty()),
        }
    }

    fn bearer<B>(&self, b: ureq::RequestBuilder<B>) -> ureq::RequestBuilder<B> {
        match &self.token {
            Some(t) => b.header("Authorization", format!("Bearer {t}")),
            None => b,
        }
    }
}

fn http_err<E: std::fmt::Display>(e: E) -> RepoError {
    RepoError::Other(format!("http transport: {e}"))
}

impl Transport for HttpTransport {
    fn list_refs(&self) -> Result<Vec<(String, String)>> {
        let url = format!("{}/info/refs", self.base);
        let adv: serde_json::Value = self
            .bearer(ureq::get(&url))
            .call()
            .map_err(http_err)?
            .body_mut()
            .read_json()
            .map_err(http_err)?;
        Ok(parse_ref_adv(&adv))
    }

    fn get_object(&self, id: &str) -> Result<Vec<u8>> {
        let url = format!("{}/objects/{id}", self.base);
        self.bearer(ureq::get(&url))
            .call()
            .map_err(http_err)?
            .body_mut()
            .read_to_vec()
            .map_err(http_err)
    }

    fn missing(&self, ids: &[String]) -> Result<Vec<String>> {
        if ids.is_empty() {
            return Ok(Vec::new());
        }
        let url = format!("{}/have", self.base);
        let missing: Vec<String> = self
            .bearer(ureq::post(&url))
            .send_json(ids)
            .map_err(http_err)?
            .body_mut()
            .read_json()
            .map_err(http_err)?;
        Ok(missing)
    }

    fn put_object(&self, content: &[u8]) -> Result<()> {
        let id = blake3::hash(content).to_hex().to_string();
        let url = format!("{}/objects/{id}", self.base);
        self.bearer(ureq::post(&url))
            .send(content)
            .map_err(http_err)?;
        Ok(())
    }

    fn set_branch(&self, branch: &str, hash: &str) -> Result<()> {
        let url = format!("{}/refs/heads/{branch}", self.base);
        let body =
            serde_json::json!({ "old": serde_json::Value::Null, "new": hash, "force": true });
        self.bearer(ureq::post(&url))
            .send_json(&body)
            .map_err(http_err)?;
        Ok(())
    }
}

/// Parse a `{branches, tags, head}` advertisement into `(refname, hash)` pairs.
fn parse_ref_adv(adv: &serde_json::Value) -> Vec<(String, String)> {
    let mut out = Vec::new();
    for (kind, prefix) in [("branches", "refs/heads/"), ("tags", "refs/tags/")] {
        if let Some(map) = adv.get(kind).and_then(|v| v.as_object()) {
            for (name, hash) in map {
                if let Some(h) = hash.as_str() {
                    out.push((format!("{prefix}{name}"), h.to_string()));
                }
            }
        }
    }
    out
}

/// Transport over a child process speaking the length-framed stdio protocol
/// (server side: `mcagit serve-stdio`). The ssh transport is this over `ssh`.
pub struct StdioTransport {
    pipe: Mutex<Pipe>,
}

struct Pipe {
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    child: Child,
}

impl StdioTransport {
    /// Spawn `program args…` (stdin/stdout piped) and speak the framed protocol.
    pub fn spawn(program: &str, args: &[String]) -> Result<Self> {
        let mut child = Command::new(program)
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()
            .map_err(|e| RepoError::Other(format!("spawn {program}: {e}")))?;
        let stdin = child.stdin.take().expect("piped stdin");
        let stdout = BufReader::new(child.stdout.take().expect("piped stdout"));
        Ok(Self {
            pipe: Mutex::new(Pipe {
                stdin,
                stdout,
                child,
            }),
        })
    }

    fn call(&self, head: &str, body: &[u8]) -> Result<(String, Vec<u8>)> {
        let mut guard = self.pipe.lock().unwrap();
        let p = &mut *guard;
        crate::serve::write_msg(&mut p.stdin, head, body).map_err(stdio_err)?;
        crate::serve::read_msg(&mut p.stdout)
            .map_err(stdio_err)?
            .ok_or_else(|| RepoError::Other("remote closed the connection".into()))
    }
}

impl Drop for StdioTransport {
    fn drop(&mut self) {
        if let Ok(mut guard) = self.pipe.lock() {
            let p = &mut *guard;
            let _ = crate::serve::write_msg(&mut p.stdin, "quit", b"");
            let _ = p.child.kill();
            let _ = p.child.wait();
        }
    }
}

fn stdio_err<E: std::fmt::Display>(e: E) -> RepoError {
    RepoError::Other(format!("ssh transport: {e}"))
}
fn stdio_status(st: &str, body: &[u8]) -> RepoError {
    RepoError::Other(format!(
        "remote error ({st}): {}",
        String::from_utf8_lossy(body)
    ))
}

impl Transport for StdioTransport {
    fn list_refs(&self) -> Result<Vec<(String, String)>> {
        let (st, body) = self.call("list-refs", &[])?;
        if st != "ok" {
            return Err(stdio_status(&st, &body));
        }
        let adv: serde_json::Value = serde_json::from_slice(&body).map_err(stdio_err)?;
        Ok(parse_ref_adv(&adv))
    }
    fn get_object(&self, id: &str) -> Result<Vec<u8>> {
        let (st, body) = self.call(&format!("get {id}"), &[])?;
        match st.as_str() {
            "ok" => Ok(body),
            "none" => Err(RepoError::Other(format!("no such object {id}"))),
            _ => Err(stdio_status(&st, &body)),
        }
    }
    fn missing(&self, ids: &[String]) -> Result<Vec<String>> {
        if ids.is_empty() {
            return Ok(Vec::new());
        }
        let body = serde_json::to_vec(ids).map_err(stdio_err)?;
        let (st, b) = self.call("missing", &body)?;
        if st != "ok" {
            return Err(stdio_status(&st, &b));
        }
        serde_json::from_slice(&b).map_err(stdio_err)
    }
    fn put_object(&self, content: &[u8]) -> Result<()> {
        let id = blake3::hash(content).to_hex().to_string();
        let (st, b) = self.call(&format!("put {id}"), content)?;
        if st != "ok" {
            return Err(stdio_status(&st, &b));
        }
        Ok(())
    }
    fn set_branch(&self, branch: &str, hash: &str) -> Result<()> {
        let body =
            serde_json::json!({ "old": serde_json::Value::Null, "new": hash, "force": true });
        let payload = serde_json::to_vec(&body).map_err(stdio_err)?;
        let (st, b) = self.call(&format!("set-ref {branch}"), &payload)?;
        if st != "ok" {
            return Err(stdio_status(&st, &b));
        }
        Ok(())
    }
}

/// Build an ssh stdio transport for `ssh://[user@]host[:port]/path`: runs
/// `ssh [-p port] [user@]host mcagit serve-stdio /path` and speaks the protocol
/// over the pipe. Override the ssh client with `MCAGIT_SSH` and the remote
/// binary with `MCAGIT_REMOTE_BIN`.
fn connect_ssh(url: &str) -> Result<StdioTransport> {
    let rest = url
        .strip_prefix("ssh://")
        .ok_or_else(|| RepoError::Other("not an ssh url".into()))?;
    let (authority, path) = rest
        .split_once('/')
        .ok_or_else(|| RepoError::Other("ssh url needs a /path: ssh://host/path".into()))?;
    let (host, port) = match authority.rsplit_once(':') {
        Some((h, p)) if !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit()) => (h, Some(p)),
        _ => (authority, None),
    };
    if host.is_empty() {
        return Err(RepoError::Other("ssh url has no host".into()));
    }
    // A host beginning with '-' would be parsed by ssh as an option (e.g.
    // `-oProxyCommand=…` → arbitrary command execution). Reject it.
    if host.starts_with('-') {
        return Err(RepoError::Other("ssh host may not start with '-'".into()));
    }
    let ssh = std::env::var("MCAGIT_SSH")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "ssh".into());
    let remote_bin = std::env::var("MCAGIT_REMOTE_BIN")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "mcagit".into());
    let mut args: Vec<String> = Vec::new();
    if let Some(p) = port {
        args.push("-p".into());
        args.push(p.to_string());
    }
    args.push(host.to_string());
    args.push(remote_bin);
    args.push("serve-stdio".into());
    args.push(format!("/{path}"));
    StdioTransport::spawn(&ssh, &args)
}

const STUB_SCHEMES: [&str; 2] = ["s3://", "azure://"];

/// Dispatch a remote URL/path to a transport: `http(s)://` → [`HttpTransport`],
/// `ssh://` → [`StdioTransport`] over ssh, a local path → [`LocalTransport`].
/// Cloud (s3/azure) is not yet built.
pub fn connect(url_or_path: &str) -> Result<Box<dyn Transport>> {
    let lower = url_or_path.to_ascii_lowercase();
    if lower.starts_with("http://") || lower.starts_with("https://") {
        return Ok(Box::new(HttpTransport::new(url_or_path)));
    }
    if lower.starts_with("ssh://") {
        return Ok(Box::new(connect_ssh(url_or_path)?));
    }
    if let Some(scheme) = STUB_SCHEMES.iter().find(|s| lower.starts_with(**s)) {
        return Err(RepoError::Other(format!(
            "remote transport not yet implemented: {scheme} — use http(s)://, ssh://, or a local path"
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
        // An annotated tag object: fetch through it to its target commit.
        if let Some(tag) = local.read_tag_object(&c) {
            stack.push(tag.object);
            continue;
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
    fn clone_carries_annotated_tags() {
        let tmp = tempfile::tempdir().unwrap();
        let origin = Repository::init(&tmp.path().join("origin")).unwrap();
        origin.set_head_to_branch("main").unwrap();
        let c1 = commit_world(&origin, b"v1", "one");
        origin
            .write_annotated_tag(&crate::TagObject {
                object: c1.clone(),
                kind: "commit".into(),
                tag: "v1".into(),
                tagger: "me".into(),
                time: "t".into(),
                message: "release".into(),
                signature: None,
            })
            .unwrap();

        let local = clone(origin.dir().to_str().unwrap(), &tmp.path().join("clone")).unwrap();
        // the tag ref, the tag object, and the commit behind it all arrived
        let tag = local.read_annotated_tag("v1").expect("annotated tag");
        assert_eq!(tag.object, c1);
        assert_eq!(local.resolve_ref("v1").unwrap(), c1);
        assert!(local.objects().exists(&c1));
    }

    #[test]
    fn scheme_dispatch() {
        // http(s) construct a transport (no request made yet); cloud stubbed.
        // (ssh:// spawns the ssh client — exercised by the e2e, not here.)
        assert!(connect("http://x/y").is_ok());
        assert!(connect("https://x/y").is_ok());
        for url in ["s3://b/k", "azure://a/c"] {
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
