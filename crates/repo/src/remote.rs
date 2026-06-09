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
    /// Fetch many objects in one shot, returned as a wire-pack body (the
    /// symmetric counterpart to [`Transport::put_objects`]). Network transports
    /// override this with a single request; the default loops `get_object` and
    /// packs locally — correct, but with no round-trip savings. Ids the remote
    /// lacks are simply omitted from the returned pack.
    fn get_pack(&self, ids: &[String]) -> Result<Vec<u8>> {
        let mut objects = Vec::with_capacity(ids.len());
        for id in ids {
            if let Ok(content) = self.get_object(id) {
                objects.push((id.clone(), content));
            }
        }
        crate::wirepack::build(&objects)
    }
    /// Which of `ids` the remote is missing.
    fn missing(&self, ids: &[String]) -> Result<Vec<String>>;
    /// Store object `content` (its id is `content`-addressed).
    fn put_object(&self, content: &[u8]) -> Result<()>;
    /// Store many objects in one shot. Network transports override this with
    /// a single wire-pack request; the default is one round-trip per object.
    fn put_objects(&self, objects: &[(String, Vec<u8>)]) -> Result<()> {
        for (_, content) in objects {
            self.put_object(content)?;
        }
        Ok(())
    }
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

    fn get_pack(&self, ids: &[String]) -> Result<Vec<u8>> {
        if ids.is_empty() {
            return crate::wirepack::build(&[]);
        }
        let url = format!("{}/getpack", self.base);
        // A pack response is many objects in one body, well past ureq's 10 MiB
        // default. Raise the read cap to the wire-pack security bound; the real
        // decompression-bomb guard is `wirepack::for_each` on ingest.
        self.bearer(ureq::post(&url))
            .send_json(ids)
            .map_err(http_err)?
            .body_mut()
            .with_config()
            .limit(crate::wirepack::MAX_PACK_TOTAL)
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

    fn put_objects(&self, objects: &[(String, Vec<u8>)]) -> Result<()> {
        if objects.is_empty() {
            return Ok(());
        }
        let body = crate::wirepack::build(objects)?;
        let url = format!("{}/pack", self.base);
        self.bearer(ureq::post(&url))
            .send(&body[..])
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
    fn get_pack(&self, ids: &[String]) -> Result<Vec<u8>> {
        if ids.is_empty() {
            return crate::wirepack::build(&[]);
        }
        let body = serde_json::to_vec(ids).map_err(stdio_err)?;
        let (st, b) = self.call("get-pack", &body)?;
        if st != "ok" {
            return Err(stdio_status(&st, &b));
        }
        Ok(b)
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
    fn put_objects(&self, objects: &[(String, Vec<u8>)]) -> Result<()> {
        if objects.is_empty() {
            return Ok(());
        }
        let body = crate::wirepack::build(objects)?;
        let (st, b) = self.call("put-pack", &body)?;
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

/// Dispatch a remote URL/path to a transport: `http(s)://` → [`HttpTransport`],
/// `ssh://` → [`StdioTransport`] over ssh, `s3://bucket[/prefix]` and
/// `azure://account/container[/prefix]` → [`crate::bucket::BucketTransport`]
/// over a cloud object store, a local path → [`LocalTransport`].
pub fn connect(url_or_path: &str) -> Result<Box<dyn Transport>> {
    let lower = url_or_path.to_ascii_lowercase();
    if lower.starts_with("http://") || lower.starts_with("https://") {
        return Ok(Box::new(HttpTransport::new(url_or_path)));
    }
    if lower.starts_with("ssh://") {
        return Ok(Box::new(connect_ssh(url_or_path)?));
    }
    if let Some(rest) = url_or_path.strip_prefix("s3://") {
        // s3://bucket[/prefix]
        let (bucket, prefix) = rest.split_once('/').unwrap_or((rest, ""));
        if bucket.is_empty() {
            return Err(RepoError::Other(
                "s3 url needs a bucket: s3://bucket/prefix".into(),
            ));
        }
        let b = crate::cloud::S3Bucket::connect(bucket)?;
        return Ok(Box::new(crate::bucket::BucketTransport::new(
            Box::new(b),
            prefix,
        )));
    }
    if let Some(rest) = url_or_path.strip_prefix("azure://") {
        // azure://account/container[/prefix]
        let mut parts = rest.splitn(3, '/');
        let account = parts.next().unwrap_or("");
        let container = parts.next().unwrap_or("");
        let prefix = parts.next().unwrap_or("");
        if account.is_empty() || container.is_empty() {
            return Err(RepoError::Other(
                "azure url needs account + container: azure://account/container/prefix".into(),
            ));
        }
        let b = crate::cloud::AzureBucket::connect(account, container)?;
        return Ok(Box::new(crate::bucket::BucketTransport::new(
            Box::new(b),
            prefix,
        )));
    }
    Ok(Box::new(LocalTransport::open(Path::new(url_or_path))?))
}

/// Resolve a configured remote name to its URL, or pass a literal URL/path through.
pub fn resolve(repo: &Repository, name_or_url: &str) -> String {
    repo.remote_url(name_or_url)
        .unwrap_or_else(|| name_or_url.to_string())
}

/// Per-batch cap on raw (uncompressed) bytes in one wire pack, so pushing a
/// huge world streams several bounded requests instead of one giant body
/// (zstd shrinks each well under the transports' framed-body cap).
const PUSH_BATCH_RAW_BYTES: usize = 128 * 1024 * 1024;

/// Push `branch` from `local` over the transport. Missing objects travel in
/// batched wire packs (one request per ~128 MiB raw) on transports that
/// support it. Returns the number of objects copied.
pub fn push(local: &Repository, t: &dyn Transport, branch: &str) -> Result<usize> {
    let tip = local
        .read_branch(branch)
        .ok_or_else(|| RepoError::BadRef(branch.to_string()))?;
    let ids = crate::transfer::reachable(local, &tip)?;
    let missing = t.missing(&ids)?;
    let mut batch: Vec<(String, Vec<u8>)> = Vec::new();
    let mut batch_bytes = 0usize;
    for id in &missing {
        let content = local.objects().read(id)?;
        batch_bytes += content.len();
        batch.push((id.clone(), content));
        if batch_bytes >= PUSH_BATCH_RAW_BYTES {
            t.put_objects(&batch)?;
            batch.clear();
            batch_bytes = 0;
        }
    }
    if !batch.is_empty() {
        t.put_objects(&batch)?;
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

/// Leaf ids fetched per pack request. Each pack is built (and held) whole on
/// the remote, so keep batches modest: 256 chunk objects is a few MB to tens of
/// MB, comfortably under the remote's per-request size cap even for dense
/// worlds, while still cutting round-trips by ~256× versus one-per-object.
const FETCH_BATCH: usize = 256;

/// Walk from `tip` over the transport, storing every reachable object `local`
/// lacks. The commit/tree skeleton is fetched per-object (few of them, and each
/// must be parsed to discover its children); the bulk — the leaf chunk/blob
/// objects — is collected across the whole walk and pulled in batched packs, so
/// an active world's fetch is a handful of requests instead of one per chunk.
fn fetch_reachable(local: &Repository, t: &dyn Transport, tip: &str) -> Result<usize> {
    use std::collections::HashSet;
    let mut seen: HashSet<String> = HashSet::new();
    let mut stack = vec![tip.to_string()];
    let mut copied = 0usize;
    let mut want_leaves: Vec<String> = Vec::new();
    let mut queued: HashSet<String> = HashSet::new();
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
                    if !local.objects().exists(&id) && queued.insert(id.clone()) {
                        want_leaves.push(id);
                    }
                }
            }
            for p in local.parents_of(&c)? {
                stack.push(p);
            }
        }
    }
    copied += fetch_leaves(local, t, &want_leaves)?;
    if copied > 0 {
        local.objects().reload_packs();
    }
    Ok(copied)
}

/// Fetch `ids` (objects `local` is known to lack) in batched packs, ingesting
/// each pack streamed and hash-verified. Errors loudly if the remote omits a
/// requested object — a broken remote must not yield a silently-incomplete tree.
fn fetch_leaves(local: &Repository, t: &dyn Transport, ids: &[String]) -> Result<usize> {
    let mut copied = 0usize;
    for batch in ids.chunks(FETCH_BATCH) {
        let body = t.get_pack(batch)?;
        crate::wirepack::for_each(&body, |_id, content| {
            local.objects().write(&content)?;
            copied += 1;
            Ok(())
        })?;
        for id in batch {
            if !local.objects().exists(id) {
                return Err(RepoError::Other(format!(
                    "remote did not return requested object {id}"
                )));
            }
        }
    }
    Ok(copied)
}

/// What `verify_remote` found while walking the remote's history.
#[derive(Debug, Default)]
pub struct VerifyReport {
    pub branches: usize,
    pub commits: usize,
    /// Total objects considered (commits + trees + leaves).
    pub objects: usize,
    pub missing: Vec<String>,
    pub corrupt: Vec<String>,
}

impl VerifyReport {
    pub fn is_ok(&self) -> bool {
        self.missing.is_empty() && self.corrupt.is_empty()
    }
}

/// Walk every branch's history on the remote, confirming each commit/tree
/// decodes to its hash and every referenced leaf object is present. With
/// `deep`, also fetch + hash-check each leaf (downloads everything). Catches
/// partial uploads / bit-rot offsite.
pub fn verify_remote(t: &dyn Transport, deep: bool) -> Result<VerifyReport> {
    let refs = t.list_refs()?;
    let mut report = VerifyReport::default();
    let mut seen: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut leaves: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut stack: Vec<String> = Vec::new();
    for (refname, hash) in &refs {
        if refname.starts_with("refs/heads/") {
            report.branches += 1;
            stack.push(hash.clone());
        }
    }

    while let Some(h) = stack.pop() {
        if !seen.insert(h.clone()) {
            continue;
        }
        let Some(content) = fetch_checked(t, &h) else {
            report.missing.push(format!("{h} (commit)"));
            continue;
        };
        let Some(content) = content else {
            report.corrupt.push(format!("{h} (commit)"));
            continue;
        };
        let text = String::from_utf8_lossy(&content);
        // An annotated tag in branch history can't occur, but a tag object at
        // a tip is possible if a branch was pointed at one; peel it.
        if let Some(tag) = crate::manifest::TagObject::try_from_json(&text) {
            stack.push(tag.object);
            continue;
        }
        let Ok(commit) = crate::manifest::CommitObject::from_json(&text) else {
            report.corrupt.push(format!("{h} (commit)"));
            continue;
        };
        report.commits += 1;

        match fetch_checked(t, &commit.tree) {
            None => report.missing.push(format!(
                "{} (tree of {})",
                commit.tree,
                &h[..10.min(h.len())]
            )),
            Some(None) => report.corrupt.push(format!("{} (tree)", commit.tree)),
            Some(Some(tree_bytes)) => {
                match crate::Manifest::from_json(&String::from_utf8_lossy(&tree_bytes)) {
                    Ok(m) => leaves.extend(crate::fsck::manifest_ids(&m)),
                    Err(_) => report.corrupt.push(format!("{} (tree)", commit.tree)),
                }
            }
        }
        for p in commit.parents {
            stack.push(p);
        }
    }

    if deep {
        for leaf in &leaves {
            match fetch_checked(t, leaf) {
                None => report.missing.push(leaf.clone()),
                Some(None) => report.corrupt.push(leaf.clone()),
                Some(Some(_)) => {}
            }
        }
    } else {
        // one batched presence check instead of a fetch per leaf
        let ids: Vec<String> = leaves.iter().cloned().collect();
        report.missing.extend(t.missing(&ids)?);
    }

    report.objects = seen.len() + leaves.len();
    report.missing.sort();
    report.corrupt.sort();
    Ok(report)
}

/// Fetch one object: `None` = missing, `Some(None)` = present but corrupt
/// (hash mismatch), `Some(Some(bytes))` = good.
#[allow(clippy::option_option)]
fn fetch_checked(t: &dyn Transport, id: &str) -> Option<Option<Vec<u8>>> {
    let bytes = t.get_object(id).ok()?;
    if blake3::hash(&bytes).to_hex().as_str() != id {
        return Some(None);
    }
    Some(Some(bytes))
}

/// Clone the repo at `url_or_path` into a fresh repo at `dst`: fetch every branch
/// and tag, set an `origin` remote, and check out the default branch.
pub fn clone(url_or_path: &str, dst: &Path) -> Result<Repository> {
    clone_depth(url_or_path, dst, 0)
}

/// [`clone`] with an optional history depth. `depth > 0` fetches at most that
/// many commits per branch (BFS, so a commit's first visit is at its minimum
/// depth), records the pruned commits as the shallow boundary, and skips tags
/// (they may point into the pruned history).
pub fn clone_depth(url_or_path: &str, dst: &Path, depth: usize) -> Result<Repository> {
    let t = connect(url_or_path)?;
    let dest = Repository::init(dst)?;
    let refs = t.list_refs()?;
    let mut boundary: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut branches: Vec<(String, String)> = Vec::new();
    for (refname, hash) in &refs {
        if let Some(b) = refname.strip_prefix("refs/heads/") {
            if depth == 0 {
                fetch_reachable(&dest, t.as_ref(), hash)?;
            } else {
                fetch_tip(&dest, t.as_ref(), hash, depth, &mut boundary)?;
            }
            dest.write_branch(b, hash)?;
            branches.push((b.to_string(), hash.clone()));
        }
    }
    if depth == 0 {
        for (refname, hash) in &refs {
            if let Some(tag) = refname.strip_prefix("refs/tags/") {
                fetch_reachable(&dest, t.as_ref(), hash)?;
                dest.write_tag(tag, hash)?;
            }
        }
    }
    dest.set_remote_url("origin", url_or_path)?;
    if !boundary.is_empty() {
        dest.write_shallow(boundary)?;
    }
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

/// Fetch a tip to a depth limit (BFS over parents). Commits whose parents are
/// pruned by the limit go into `boundary`.
fn fetch_tip(
    local: &Repository,
    t: &dyn Transport,
    tip: &str,
    depth: usize,
    boundary: &mut std::collections::HashSet<String>,
) -> Result<usize> {
    use std::collections::{HashSet, VecDeque};
    let mut seen: HashSet<String> = HashSet::new();
    let mut queue: VecDeque<(String, usize)> = VecDeque::new();
    queue.push_back((tip.to_string(), 1));
    let mut copied = 0usize;
    while let Some((h, d)) = queue.pop_front() {
        if !seen.insert(h.clone()) {
            continue; // FIFO levels: the first visit is the minimum depth
        }
        if !local.objects().exists(&h) {
            local.objects().write(&t.get_object(&h)?)?;
            copied += 1;
        }
        let Ok(commit) = local.read_commit(&h) else {
            continue;
        };
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
        if d < depth {
            for p in commit.parents {
                queue.push_back((p, d + 1));
            }
        } else if !commit.parents.is_empty() {
            boundary.insert(h); // parents pruned → shallow boundary
        }
    }
    if copied > 0 {
        local.objects().reload_packs();
    }
    Ok(copied)
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
        let parents: Vec<String> = repo.head_commit().into_iter().collect();
        let c = repo.create_commit(&tree, parents, msg, "me", "0").unwrap();
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
    fn shallow_clone_limits_history_and_grafts_parents() {
        let tmp = tempfile::tempdir().unwrap();
        let origin = Repository::init(&tmp.path().join("origin")).unwrap();
        origin.set_head_to_branch("main").unwrap();
        let mut tips = Vec::new();
        for v in 1..=5 {
            tips.push(commit_world(
                &origin,
                format!("v{v}").as_bytes(),
                &format!("c{v}"),
            ));
        }
        // a tag deep in history (must be skipped by a shallow clone)
        origin.write_tag("old", &tips[0]).unwrap();

        let dst = tmp.path().join("shallow");
        let local = clone_depth(origin.dir().to_str().unwrap(), &dst, 2).unwrap();
        assert!(local.is_shallow());
        assert_eq!(local.read_branch("main").as_deref(), Some(tips[4].as_str()));
        // depth 2: tip + its parent present, grandparent absent
        assert!(local.objects().exists(&tips[4]));
        assert!(local.objects().exists(&tips[3]));
        assert!(
            !local.objects().exists(&tips[2]),
            "history past depth pruned"
        );
        assert!(local.read_tag("old").is_none(), "tags skipped when shallow");

        // the boundary commit is grafted parentless: walks terminate cleanly
        assert_eq!(local.parents_of(&tips[3]).unwrap(), Vec::<String>::new());
        assert_eq!(local.parents_of(&tips[4]).unwrap(), vec![tips[3].clone()]);
        // fsck sees no missing objects in a shallow clone
        let r = crate::fsck::fsck(&local).unwrap();
        assert!(r.is_clean(), "missing={:?}", r.missing);
        // and the world checks out
        let m = local
            .read_manifest(&local.read_commit(&tips[4]).unwrap().tree)
            .unwrap();
        let out = tmp.path().join("out");
        checkout(&local, &m, &out, false).unwrap();
        assert!(out.join("level.dat_marker").exists());
    }

    /// A transport wrapper that forbids per-object puts, proving push uses
    /// the batched wire-pack path.
    struct BatchOnly<T: Transport>(T);
    impl<T: Transport> Transport for BatchOnly<T> {
        fn list_refs(&self) -> Result<Vec<(String, String)>> {
            self.0.list_refs()
        }
        fn get_object(&self, id: &str) -> Result<Vec<u8>> {
            self.0.get_object(id)
        }
        fn missing(&self, ids: &[String]) -> Result<Vec<String>> {
            self.0.missing(ids)
        }
        fn put_object(&self, _content: &[u8]) -> Result<()> {
            panic!("push must batch via put_objects");
        }
        fn put_objects(&self, objects: &[(String, Vec<u8>)]) -> Result<()> {
            // exercise the real wire encoding both ways
            let body = crate::wirepack::build(objects).unwrap();
            for (_, content) in crate::wirepack::parse(&body).unwrap() {
                self.0.put_object(&content)?;
            }
            Ok(())
        }
        fn set_branch(&self, branch: &str, hash: &str) -> Result<()> {
            self.0.set_branch(branch, hash)
        }
        fn finish(&self) -> Result<()> {
            self.0.finish()
        }
    }

    #[test]
    fn push_travels_as_wire_packs() {
        let tmp = tempfile::tempdir().unwrap();
        let local = Repository::init(&tmp.path().join("local")).unwrap();
        local.set_head_to_branch("main").unwrap();
        let c1 = commit_world(&local, b"v1", "one");

        let remote_dir = tmp.path().join("remote");
        Repository::init(&remote_dir).unwrap();
        let t = BatchOnly(LocalTransport::open(&remote_dir).unwrap());
        let n = push(&local, &t, "main").unwrap();
        assert!(n > 0);
        let remote = Repository::open(&remote_dir).unwrap();
        assert_eq!(remote.read_branch("main").as_deref(), Some(c1.as_str()));
        assert!(remote.objects().exists(&c1));
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
        // http(s) construct a transport (no request made yet).
        // (ssh:// spawns the ssh client — exercised by the e2e, not here.)
        assert!(connect("http://x/y").is_ok());
        assert!(connect("https://x/y").is_ok());
        // s3/azure construct iff their credentials are present in the env; the
        // url-shape errors (missing bucket/container) are deterministic.
        assert!(connect("s3://").is_err());
        assert!(connect("azure://account").is_err());
    }

    /// Counts per-object gets vs batched pack gets, to prove fetch pulls the
    /// leaf set as packs rather than one round-trip per object.
    struct Counting<T: Transport> {
        inner: T,
        objects: std::sync::atomic::AtomicUsize,
        packs: std::sync::atomic::AtomicUsize,
    }
    impl<T: Transport> Counting<T> {
        fn new(inner: T) -> Self {
            Self {
                inner,
                objects: std::sync::atomic::AtomicUsize::new(0),
                packs: std::sync::atomic::AtomicUsize::new(0),
            }
        }
    }
    impl<T: Transport> Transport for Counting<T> {
        fn list_refs(&self) -> Result<Vec<(String, String)>> {
            self.inner.list_refs()
        }
        fn get_object(&self, id: &str) -> Result<Vec<u8>> {
            self.objects
                .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            self.inner.get_object(id)
        }
        fn get_pack(&self, ids: &[String]) -> Result<Vec<u8>> {
            self.packs
                .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            self.inner.get_pack(ids)
        }
        fn missing(&self, ids: &[String]) -> Result<Vec<String>> {
            self.inner.missing(ids)
        }
        fn put_object(&self, content: &[u8]) -> Result<()> {
            self.inner.put_object(content)
        }
        fn set_branch(&self, branch: &str, hash: &str) -> Result<()> {
            self.inner.set_branch(branch, hash)
        }
    }

    #[test]
    fn fetch_pulls_leaves_as_one_pack_not_per_object() {
        let tmp = tempfile::tempdir().unwrap();
        let origin = Repository::init(&tmp.path().join("origin")).unwrap();
        origin.set_head_to_branch("main").unwrap();

        // A world with many distinct leaf objects (one blob each).
        let w = tmp.path().join("world");
        std::fs::create_dir_all(w.join("data")).unwrap();
        for i in 0..12 {
            std::fs::write(w.join("data").join(format!("f{i}")), format!("leaf-{i}")).unwrap();
        }
        let m = snapshot::snapshot(&origin, &w).unwrap();
        let leaf_count = crate::fsck::manifest_ids(&m).len();
        assert!(leaf_count >= 12, "world has many leaves: {leaf_count}");
        let tree = origin.write_manifest(&m).unwrap();
        let c = origin.create_commit(&tree, vec![], "w", "me", "0").unwrap();
        origin.write_branch("main", &c).unwrap();

        let local = Repository::init(&tmp.path().join("local")).unwrap();
        let t = Counting::new(LocalTransport::open(origin.dir()).unwrap());
        let (tip, copied) = fetch(&local, &t, "main").unwrap();
        assert_eq!(tip, c);
        assert_eq!(copied, 2 + leaf_count, "commit + tree + every leaf copied");

        // Graph skeleton (commit + tree) came per-object; leaves came as packs.
        let per_object = t.objects.load(std::sync::atomic::Ordering::Relaxed);
        let pack_reqs = t.packs.load(std::sync::atomic::Ordering::Relaxed);
        assert_eq!(per_object, 2, "only the commit and tree fetched per-object");
        assert_eq!(pack_reqs, 1, "all leaves arrived in a single pack request");

        // and every object actually landed
        for id in crate::fsck::manifest_ids(&m) {
            assert!(local.objects().exists(&id), "leaf {id} fetched");
        }
        assert!(local.objects().exists(&c) && local.objects().exists(&tree));
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
