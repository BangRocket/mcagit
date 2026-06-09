//! Serve the mcagit hub protocol two ways over one set of per-repo operations:
//!
//! - [`serve`] — an HTTP server hosting a directory of bare repos at `/r/<name>/…`
//!   so `clone | fetch | push http://host/r/<name>` works.
//! - [`serve_stdio`] — a single repo over stdin/stdout (length-framed), so
//!   `clone | fetch | push ssh://host/path` works by running `ssh host mcagit
//!   serve-stdio <path>` and piping the protocol.
//!
//! Object bodies carry decompressed content (re-hashed with blake3 on store). A
//! push to a new name/path auto-creates the repo. Both servers route through the
//! same `op_*` handlers so the two transports can't drift.

use crate::repository::Repository;
use crate::{RepoError, Result};
use std::io::{self, BufReader, BufWriter, Read, Write};
use std::path::Path;
use tiny_http::{Method, Request, Response, Server, StatusCode};

// ---- HTTP server ----

/// Serve the repos under `root` on `addr` (e.g. `0.0.0.0:5080`). Blocks.
pub fn serve(root: &Path, addr: &str) -> Result<()> {
    std::fs::create_dir_all(root).ok();
    let token = std::env::var("MCAGIT_TOKEN").ok().filter(|s| !s.is_empty());
    let server = Server::http(addr).map_err(|e| RepoError::Other(format!("serve bind: {e}")))?;
    eprintln!(
        "mcagit serving {} on http://{addr}/  ({} writes)",
        root.display(),
        if token.is_some() {
            "token-gated"
        } else {
            "open"
        }
    );
    for req in server.incoming_requests() {
        if let Err(e) = handle(root, token.as_deref(), req) {
            eprintln!("serve: {e}");
        }
    }
    Ok(())
}

fn valid_name(n: &str) -> bool {
    !n.is_empty()
        && n.len() <= 64
        && n.chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_' || c == '.')
        && n != "."
        && n != ".."
}

fn has_token(req: &Request, token: &str) -> bool {
    let want = format!("Bearer {token}");
    req.headers()
        .iter()
        .any(|h| h.field.equiv("Authorization") && h.value.as_str() == want)
}

fn handle(root: &Path, token: Option<&str>, mut req: Request) -> Result<()> {
    let method = req.method().clone();
    let url = req.url().to_string();
    let path = url.split('?').next().unwrap_or("").trim_matches('/');

    if path == "health" {
        return respond(req, 200, b"ok".to_vec());
    }

    let parts: Vec<&str> = path.split('/').collect();
    // /r/<name>/<action...>
    if parts.len() < 3 || parts[0] != "r" || !valid_name(parts[1]) {
        return respond(req, 404, b"not found".to_vec());
    }
    let dir = root.join(parts[1]);
    let action = parts[2..].join("/");
    if method == Method::Post {
        if let Some(t) = token {
            if !has_token(&req, t) {
                return respond(req, 401, b"authenticate with MCAGIT_TOKEN bearer".to_vec());
            }
        }
    }

    // GET /info/refs
    if method == Method::Get && action == "info/refs" {
        return respond(req, 200, op_info_refs(&dir));
    }
    // POST /have
    if method == Method::Post && action == "have" {
        let body = read_body(&mut req)?;
        return respond(req, 200, op_have(&dir, &body));
    }
    // GET /objects/<hash>
    if method == Method::Get {
        if let Some(hash) = action.strip_prefix("objects/") {
            return match op_get(&dir, hash) {
                Some(bytes) => respond(req, 200, bytes),
                None => respond(req, 404, b"no such object".to_vec()),
            };
        }
    }
    // POST /objects/<hash>
    if method == Method::Post {
        if let Some(hash) = action.strip_prefix("objects/") {
            let body = read_body(&mut req)?;
            return match op_put(&dir, hash, &body) {
                Ok(()) => respond(req, 200, Vec::new()),
                Err(e) => respond(req, 400, e.into_bytes()),
            };
        }
    }
    // POST /pack — a batched object upload (one wire pack)
    if method == Method::Post && action == "pack" {
        let body = read_body(&mut req)?;
        return match op_put_pack(&dir, &body) {
            Ok(n) => respond(req, 200, n.to_string().into_bytes()),
            Err(e) => respond(req, 400, e.into_bytes()),
        };
    }
    // POST /refs/heads/<branch>
    if method == Method::Post {
        if let Some(branch) = action.strip_prefix("refs/heads/") {
            let body = read_body(&mut req)?;
            return match op_set_ref(&dir, branch, &body) {
                Ok(()) => respond(req, 200, Vec::new()),
                Err(e) => respond(req, 409, e.into_bytes()),
            };
        }
    }

    respond(req, 404, b"not found".to_vec())
}

// ---- stdio server (ssh) ----

/// Serve a single repo at `dir` over stdin/stdout (the ssh transport's server
/// side). Reads length-framed requests, dispatches through the same `op_*`
/// handlers as HTTP, and writes length-framed responses until EOF or `quit`.
pub fn serve_stdio(dir: &Path) -> Result<()> {
    let stdin = io::stdin();
    let stdout = io::stdout();
    let mut r = BufReader::new(stdin.lock());
    let mut w = BufWriter::new(stdout.lock());
    while let Some((head, body)) = read_msg(&mut r).map_err(frame_err)? {
        let (rhead, rbody) = dispatch(dir, &head, &body);
        write_msg(&mut w, rhead, &rbody).map_err(frame_err)?;
        if head == "quit" {
            break;
        }
    }
    Ok(())
}

fn dispatch(dir: &Path, head: &str, body: &[u8]) -> (&'static str, Vec<u8>) {
    let (verb, arg) = head.split_once(' ').unwrap_or((head, ""));
    match verb {
        "list-refs" => ("ok", op_info_refs(dir)),
        "missing" => ("ok", op_have(dir, body)),
        "get" => match op_get(dir, arg) {
            Some(b) => ("ok", b),
            None => ("none", Vec::new()),
        },
        "put" => match op_put(dir, arg, body) {
            Ok(()) => ("ok", Vec::new()),
            Err(e) => ("err", e.into_bytes()),
        },
        "put-pack" => match op_put_pack(dir, body) {
            Ok(n) => ("ok", n.to_string().into_bytes()),
            Err(e) => ("err", e.into_bytes()),
        },
        "set-ref" => match op_set_ref(dir, arg, body) {
            Ok(()) => ("ok", Vec::new()),
            Err(e) => ("err", e.into_bytes()),
        },
        "quit" => ("ok", Vec::new()),
        _ => ("err", b"unknown verb".to_vec()),
    }
}

// ---- shared per-repo operations (one source of truth for both transports) ----

/// Advertise refs as JSON `{branches, tags, head}` (empty for a missing repo).
pub(crate) fn op_info_refs(dir: &Path) -> Vec<u8> {
    let (mut branches, mut tags, mut head) = (
        serde_json::Map::new(),
        serde_json::Map::new(),
        serde_json::Value::Null,
    );
    if let Ok(repo) = Repository::open(dir) {
        for b in repo.branches() {
            if let Some(h) = repo.read_branch(&b) {
                branches.insert(b, h.into());
            }
        }
        for t in repo.tags() {
            if let Some(h) = repo.read_tag(&t) {
                tags.insert(t, h.into());
            }
        }
        head = repo
            .current_branch()
            .map(Into::into)
            .unwrap_or(serde_json::Value::Null);
    }
    let adv = serde_json::json!({ "branches": branches, "tags": tags, "head": head });
    serde_json::to_vec(&adv).unwrap_or_default()
}

/// `body` is a JSON `[id,…]`; reply with the subset the repo is missing (JSON).
pub(crate) fn op_have(dir: &Path, body: &[u8]) -> Vec<u8> {
    let ids: Vec<String> = serde_json::from_slice(body).unwrap_or_default();
    let missing: Vec<String> = match Repository::open(dir) {
        Ok(repo) => ids
            .into_iter()
            .filter(|id| !repo.objects().exists(id))
            .collect(),
        Err(_) => ids, // empty remote → all missing
    };
    serde_json::to_vec(&missing).unwrap_or_default()
}

/// An object's decompressed content, or `None` if absent.
pub(crate) fn op_get(dir: &Path, hash: &str) -> Option<Vec<u8>> {
    Repository::open(dir).ok()?.objects().read(hash).ok()
}

/// Store `content` after verifying `blake3(content) == hash`; auto-creates the repo.
pub(crate) fn op_put(dir: &Path, hash: &str, content: &[u8]) -> std::result::Result<(), String> {
    if blake3::hash(content).to_hex().as_str() != hash {
        return Err("object hash mismatch".into());
    }
    let repo = open_or_init(dir).map_err(|e| e.to_string())?;
    repo.objects().write(content).map_err(|e| e.to_string())?;
    Ok(())
}

/// Ingest a batched wire pack. Every object is hash-verified and every inflate
/// size-bounded by `wirepack::parse` before anything is stored; auto-creates
/// the repo (a push to a new name). Returns the number of objects stored.
pub(crate) fn op_put_pack(dir: &Path, body: &[u8]) -> std::result::Result<usize, String> {
    let objects = crate::wirepack::parse(body).map_err(|e| e.to_string())?;
    let repo = open_or_init(dir).map_err(|e| e.to_string())?;
    let n = objects.len();
    for (_, content) in objects {
        repo.objects().write(&content).map_err(|e| e.to_string())?;
    }
    Ok(n)
}

/// Advance a branch from a JSON `{old, new, force}` body (fast-forward guarded).
pub(crate) fn op_set_ref(dir: &Path, branch: &str, body: &[u8]) -> std::result::Result<(), String> {
    let upd: serde_json::Value = serde_json::from_slice(body).map_err(|e| e.to_string())?;
    let new = upd.get("new").and_then(|v| v.as_str()).unwrap_or("");
    let old = upd.get("old").and_then(|v| v.as_str());
    let force = upd.get("force").and_then(|v| v.as_bool()).unwrap_or(false);
    let repo = open_or_init(dir).map_err(|e| e.to_string())?;
    if !repo.objects().exists(new) {
        return Err("ref points at an object that was not uploaded".into());
    }
    let current = repo.read_branch(branch);
    if !force && current.as_deref() != old {
        return Err("ref moved on the remote (stale push) - fetch + retry".into());
    }
    repo.write_branch(branch, new).map_err(|e| e.to_string())?;
    Ok(())
}

fn open_or_init(dir: &Path) -> Result<Repository> {
    match Repository::open(dir) {
        Ok(r) => Ok(r),
        Err(_) => Repository::init(dir),
    }
}

// ---- length framing (shared by serve_stdio + the StdioTransport client) ----

/// Cap on a single framed message body (anti allocation-bomb on untrusted input).
const MAX_MSG_BODY: usize = 512 * 1024 * 1024;

/// Write one message: u32-LE head length, head bytes, u32-LE body length, body.
pub(crate) fn write_msg(w: &mut impl Write, head: &str, body: &[u8]) -> io::Result<()> {
    w.write_all(&(head.len() as u32).to_le_bytes())?;
    w.write_all(head.as_bytes())?;
    w.write_all(&(body.len() as u32).to_le_bytes())?;
    w.write_all(body)?;
    w.flush()
}

/// Read one message, or `None` at clean EOF. Bounds the head/body sizes.
pub(crate) fn read_msg(r: &mut impl Read) -> io::Result<Option<(String, Vec<u8>)>> {
    let mut len = [0u8; 4];
    match r.read_exact(&mut len) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(e),
    }
    let hlen = u32::from_le_bytes(len) as usize;
    if hlen > 64 * 1024 {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "head too large"));
    }
    let mut head = vec![0u8; hlen];
    r.read_exact(&mut head)?;
    r.read_exact(&mut len)?;
    let blen = u32::from_le_bytes(len) as usize;
    if blen > MAX_MSG_BODY {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "body too large"));
    }
    // Grow as we read rather than pre-allocating a (bounded but still large)
    // claimed length, so a lying peer can't force a big up-front allocation.
    let mut body = Vec::new();
    r.take(blen as u64).read_to_end(&mut body)?;
    if body.len() != blen {
        return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "short body"));
    }
    Ok(Some((String::from_utf8_lossy(&head).into_owned(), body)))
}

fn frame_err(e: io::Error) -> RepoError {
    RepoError::Other(format!("stdio framing: {e}"))
}

// ---- HTTP helpers ----

fn read_body(req: &mut Request) -> Result<Vec<u8>> {
    let mut buf = Vec::new();
    req.as_reader()
        .read_to_end(&mut buf)
        .map_err(|e| RepoError::Other(format!("read body: {e}")))?;
    Ok(buf)
}

fn respond(req: Request, code: u16, body: Vec<u8>) -> Result<()> {
    req.respond(Response::from_data(body).with_status_code(StatusCode(code)))
        .map_err(|e| RepoError::Other(format!("respond: {e}")))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn op_put_get_and_refs_round_trip() {
        let d = tempfile::tempdir().unwrap();
        let dir = d.path();
        let content = b"hello object";
        let hash = blake3::hash(content).to_hex().to_string();
        // put with the wrong hash is rejected
        assert!(op_put(dir, "deadbeef", content).is_err());
        // put + get round-trips the content
        op_put(dir, &hash, content).unwrap();
        assert_eq!(op_get(dir, &hash).as_deref(), Some(&content[..]));
        // have: a stored id is not missing; an unknown one is
        let m: Vec<String> = serde_json::from_slice(&op_have(
            dir,
            format!("[\"{hash}\",\"missing1\"]").as_bytes(),
        ))
        .unwrap();
        assert_eq!(m, vec!["missing1".to_string()]);
        // set-ref guards a stale fast-forward but accepts a fresh one
        let body = format!("{{\"old\":null,\"new\":\"{hash}\",\"force\":false}}");
        op_set_ref(dir, "main", body.as_bytes()).unwrap();
        let adv: serde_json::Value = serde_json::from_slice(&op_info_refs(dir)).unwrap();
        assert_eq!(adv["branches"]["main"].as_str(), Some(hash.as_str()));
    }

    #[test]
    fn op_put_pack_ingests_and_rejects_tampering() {
        let d = tempfile::tempdir().unwrap();
        let dir = d.path();
        let a = b"object alpha".to_vec();
        let b = b"object beta".to_vec();
        let objects = vec![
            (blake3::hash(&a).to_hex().to_string(), a.clone()),
            (blake3::hash(&b).to_hex().to_string(), b.clone()),
        ];
        let body = crate::wirepack::build(&objects).unwrap();
        assert_eq!(op_put_pack(dir, &body).unwrap(), 2);
        assert_eq!(op_get(dir, &objects[0].0).as_deref(), Some(&a[..]));
        assert_eq!(op_get(dir, &objects[1].0).as_deref(), Some(&b[..]));

        // a tampered pack stores nothing
        let mut bad = body.clone();
        let n = bad.len();
        bad[n - 1] ^= 0xff;
        assert!(op_put_pack(dir, &bad).is_err());
        // and garbage framing is rejected outright
        assert!(op_put_pack(dir, b"junk").is_err());
    }

    #[test]
    fn rejects_path_traversal_in_ref_and_object_id() {
        let d = tempfile::tempdir().unwrap();
        let dir = d.path();
        let content = b"x";
        let hash = blake3::hash(content).to_hex().to_string();
        op_put(dir, &hash, content).unwrap();
        // a traversal branch name is refused (no arbitrary file write outside refs/heads)
        let body = format!("{{\"old\":null,\"new\":\"{hash}\",\"force\":true}}");
        assert!(op_set_ref(dir, "../../HEAD", body.as_bytes()).is_err());
        assert!(op_set_ref(dir, "../escape", body.as_bytes()).is_err());
        assert!(!dir.parent().unwrap().join("HEAD").exists() || dir.join("HEAD").exists());
        // a traversal / non-hex object id reads nothing (no path escape)
        assert!(op_get(dir, "aa/../../../etc/hosts").is_none());
        assert!(op_get(dir, "not-a-valid-hex-id").is_none());
    }

    #[test]
    fn framing_round_trips() {
        let mut buf = Vec::new();
        write_msg(&mut buf, "put abc", b"body-bytes").unwrap();
        write_msg(&mut buf, "quit", b"").unwrap();
        let mut r = &buf[..];
        let (h1, b1) = read_msg(&mut r).unwrap().unwrap();
        assert_eq!((h1.as_str(), &b1[..]), ("put abc", &b"body-bytes"[..]));
        let (h2, b2) = read_msg(&mut r).unwrap().unwrap();
        assert_eq!((h2.as_str(), b2.len()), ("quit", 0));
        assert!(read_msg(&mut r).unwrap().is_none()); // clean EOF
    }
}
