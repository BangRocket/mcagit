//! HTTP server for the mcagit hub protocol: hosts a directory of bare repos at
//! `/r/<name>/…` so `clone | fetch | push http://host/r/<name>` works. Object
//! bodies carry decompressed content (re-hashed with blake3 on store). A push to
//! a new name auto-creates the repo. Optional bearer-token auth on writes via
//! the `MCAGIT_TOKEN` env var. This is the Rust-native equivalent of the .NET
//! mcahub transport, using the Rust (blake3/zstd) object format.

use crate::repository::Repository;
use crate::{RepoError, Result};
use std::path::Path;
use tiny_http::{Method, Request, Response, Server, StatusCode};

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
    let name = parts[1];
    let action = parts[2..].join("/");
    let dir = root.join(name);
    let is_write = method == Method::Post;
    if is_write {
        if let Some(t) = token {
            if !has_token(&req, t) {
                return respond(req, 401, b"authenticate with MCAGIT_TOKEN bearer".to_vec());
            }
        }
    }

    let (code, body) = route(&dir, &method, name, &action, &mut req)?;
    respond(req, code, body)
}

fn route(
    dir: &Path,
    method: &Method,
    _name: &str,
    action: &str,
    req: &mut Request,
) -> Result<(u16, Vec<u8>)> {
    let open = || Repository::open(dir).ok();

    // GET /info/refs — advertise refs (empty for a not-yet-created repo).
    if method == &Method::Get && action == "info/refs" {
        let (mut branches, mut tags, mut head) = (
            serde_json::Map::new(),
            serde_json::Map::new(),
            serde_json::Value::Null,
        );
        if let Some(repo) = open() {
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
        return Ok((200, serde_json::to_vec(&adv).unwrap_or_default()));
    }

    // POST /have — which of these ids is the repo missing?
    if method == &Method::Post && action == "have" {
        let ids: Vec<String> = read_json(req)?;
        let missing: Vec<String> = match open() {
            Some(repo) => ids
                .into_iter()
                .filter(|id| !repo.objects().exists(id))
                .collect(),
            None => ids, // empty remote → all missing
        };
        return Ok((200, serde_json::to_vec(&missing).unwrap_or_default()));
    }

    // GET /objects/<hash> — download an object's (decompressed) content.
    if method == &Method::Get {
        if let Some(hash) = action.strip_prefix("objects/") {
            return match open().and_then(|r| r.objects().read(hash).ok()) {
                Some(bytes) => Ok((200, bytes)),
                None => Ok((404, b"no such object".to_vec())),
            };
        }
    }

    // POST /objects/<hash> — upload content; verify blake3 == hash; auto-create repo.
    if method == &Method::Post {
        if let Some(hash) = action.strip_prefix("objects/") {
            let content = read_body(req)?;
            let got = blake3::hash(&content).to_hex().to_string();
            if got != hash {
                return Ok((400, b"object hash mismatch".to_vec()));
            }
            let repo = open_or_init(dir)?;
            repo.objects().write(&content)?;
            return Ok((200, Vec::new()));
        }
    }

    // POST /refs/heads/<branch> — advance a branch (fast-forward guarded; force ok).
    if method == &Method::Post {
        if let Some(branch) = action.strip_prefix("refs/heads/") {
            let upd: serde_json::Value = read_json(req)?;
            let new = upd.get("new").and_then(|v| v.as_str()).unwrap_or("");
            let old = upd.get("old").and_then(|v| v.as_str());
            let force = upd.get("force").and_then(|v| v.as_bool()).unwrap_or(false);
            let repo = open_or_init(dir)?;
            if !repo.objects().exists(new) {
                return Ok((
                    400,
                    b"ref points at an object that was not uploaded".to_vec(),
                ));
            }
            let current = repo.read_branch(branch);
            if !force && current.as_deref() != old {
                return Ok((
                    409,
                    b"ref moved on the remote (stale push) - fetch + retry".to_vec(),
                ));
            }
            repo.write_branch(branch, new)?;
            return Ok((200, Vec::new()));
        }
    }

    Ok((404, b"not found".to_vec()))
}

fn open_or_init(dir: &Path) -> Result<Repository> {
    match Repository::open(dir) {
        Ok(r) => Ok(r),
        Err(_) => Repository::init(dir),
    }
}

fn read_body(req: &mut Request) -> Result<Vec<u8>> {
    let mut buf = Vec::new();
    req.as_reader()
        .read_to_end(&mut buf)
        .map_err(|e| RepoError::Other(format!("read body: {e}")))?;
    Ok(buf)
}

fn read_json<T: serde::de::DeserializeOwned>(req: &mut Request) -> Result<T> {
    let body = read_body(req)?;
    serde_json::from_slice(&body).map_err(|e| RepoError::Other(format!("bad json: {e}")))
}

fn respond(req: Request, code: u16, body: Vec<u8>) -> Result<()> {
    req.respond(Response::from_data(body).with_status_code(StatusCode(code)))
        .map_err(|e| RepoError::Other(format!("respond: {e}")))
}
