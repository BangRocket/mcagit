//! The repository: object store + refs/HEAD + config, plus commit/manifest
//! storage and revision resolution. Supports an embedded `.mcagit/` layout
//! (worktree = the containing folder) and a bare layout (metadata in the repo
//! dir, worktree external via config or none).

use crate::manifest::{CommitObject, Manifest, TagObject};
use crate::object_store::ObjectStore;
use crate::{RepoError, Result};
use std::path::{Path, PathBuf};

pub struct Repository {
    dir: PathBuf,
    objects: ObjectStore,
}

/// A signing callback: signable payload → armored signature.
pub type SignFn = dyn Fn(&str) -> Result<String>;

/// A ref name safe to use as a path component under `refs/`: rejects traversal
/// (`..`, a leading `/` or `.`, a trailing `/`, backslashes, NUL) while allowing
/// namespaced names like `feature/foo`. Untrusted ref names (from the transport)
/// are validated here before any `refs/…` path is built.
pub(crate) fn safe_ref_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 255
        && !name.starts_with('/')
        && !name.starts_with('.')
        && !name.ends_with('/')
        && !name.contains("..")
        && !name.contains('\\')
        && !name.contains('\0')
}

impl Repository {
    pub fn dir(&self) -> &Path {
        &self.dir
    }
    pub fn objects(&self) -> &ObjectStore {
        &self.objects
    }

    /// True if `dir` *directly* holds the repo metadata (flat / bare layout).
    fn is_flat_repo(dir: &Path) -> bool {
        dir.join("HEAD").is_file() && dir.join("objects").is_dir()
    }

    /// A path is (or contains) a repo if it is a flat repo or has an embedded
    /// `.mcagit/` flat repo inside it.
    pub fn is_repository(dir: &Path) -> bool {
        Self::is_flat_repo(dir) || Self::is_flat_repo(&dir.join(".mcagit"))
    }

    /// Create a fresh repo (idempotent — re-init keeps existing HEAD).
    pub fn init(dir: &Path) -> Result<Self> {
        std::fs::create_dir_all(dir.join("objects"))?;
        std::fs::create_dir_all(dir.join("refs").join("heads"))?;
        let head = dir.join("HEAD");
        if !head.exists() {
            std::fs::write(&head, "ref: refs/heads/main\n")?;
        }
        Self::open(dir)
    }

    /// Create an embedded repo: metadata under `<folder>/.mcagit`, with `folder`
    /// bound as the worktree (git-style). Idempotent: re-opens an existing
    /// embedded repo, and keeps an existing *bare* repo bare (no nested `.mcagit/`).
    pub fn init_embedded(folder: &Path) -> Result<Self> {
        std::fs::create_dir_all(folder)?;
        // Already embedded → re-open (no redundant config write).
        if Self::is_flat_repo(&folder.join(".mcagit")) {
            return Self::open(folder);
        }
        // Already a bare repo → keep it bare (don't nest a `.mcagit/`).
        if Self::is_flat_repo(folder) {
            return Self::open(folder);
        }
        let repo = Self::init(&folder.join(".mcagit"))?;
        // Bind the worktree to an absolute path (canonical if possible, else an
        // absolute fallback — never a relative path in config).
        let abs = std::fs::canonicalize(folder).unwrap_or_else(|_| {
            std::env::current_dir()
                .map(|cwd| cwd.join(folder))
                .unwrap_or_else(|_| folder.to_path_buf())
        });
        repo.set_worktree(&abs.to_string_lossy())?;
        Ok(repo)
    }

    /// Open an existing repo at `path`. Prefers an embedded `path/.mcagit/` over
    /// a flat/bare repo directly at `path`.
    pub fn open(path: &Path) -> Result<Self> {
        // Embedded `<path>/.mcagit` wins; otherwise a flat repo at `path`.
        let dir = if Self::is_flat_repo(&path.join(".mcagit")) {
            path.join(".mcagit")
        } else if Self::is_flat_repo(path) {
            path.to_path_buf()
        } else {
            return Err(RepoError::NotARepository(path.display().to_string()));
        };
        Ok(Self {
            objects: ObjectStore::new(dir.join("objects")),
            dir,
        })
    }

    /// Walk up from `start` looking for a repo directory (git-style).
    pub fn discover(start: &Path) -> Result<Self> {
        let mut cur = std::fs::canonicalize(start).unwrap_or_else(|_| start.to_path_buf());
        loop {
            if Self::is_repository(&cur) {
                return Self::open(&cur);
            }
            match cur.parent() {
                Some(p) => cur = p.to_path_buf(),
                None => return Err(RepoError::NotARepository(start.display().to_string())),
            }
        }
    }

    // ---- config (simple `key = value` lines) ----

    fn config_path(&self) -> PathBuf {
        self.dir.join("config")
    }

    pub fn config_get(&self, key: &str) -> Option<String> {
        let text = std::fs::read_to_string(self.config_path()).ok()?;
        for line in text.lines() {
            if let Some((k, v)) = line.split_once('=') {
                if k.trim() == key {
                    return Some(v.trim().to_string());
                }
            }
        }
        None
    }

    pub fn config_set(&self, key: &str, value: &str) -> Result<()> {
        let mut lines: Vec<String> = std::fs::read_to_string(self.config_path())
            .unwrap_or_default()
            .lines()
            .filter(|l| {
                l.split_once('=')
                    .map(|(k, _)| k.trim() != key)
                    .unwrap_or(true)
            })
            .map(|l| l.to_string())
            .collect();
        lines.push(format!("{key} = {value}"));
        std::fs::write(self.config_path(), lines.join("\n") + "\n")?;
        Ok(())
    }

    pub fn worktree(&self) -> Option<String> {
        self.config_get("worktree")
    }
    pub fn set_worktree(&self, w: &str) -> Result<()> {
        self.config_set("worktree", w)
    }

    /// Remove a config key (if present).
    pub fn config_unset(&self, key: &str) -> Result<()> {
        let lines: Vec<String> = std::fs::read_to_string(self.config_path())
            .unwrap_or_default()
            .lines()
            .filter(|l| {
                l.split_once('=')
                    .map(|(k, _)| k.trim() != key)
                    .unwrap_or(true)
            })
            .map(|l| l.to_string())
            .collect();
        std::fs::write(self.config_path(), lines.join("\n") + "\n")?;
        Ok(())
    }

    // ---- remotes (stored as `remote.<name>.url` config; tracking refs under
    // refs/remotes/<name>/<branch>) ----

    pub fn remote_url(&self, name: &str) -> Option<String> {
        self.config_get(&format!("remote.{name}.url"))
    }
    pub fn set_remote_url(&self, name: &str, url: &str) -> Result<()> {
        self.config_set(&format!("remote.{name}.url"), url)
    }
    pub fn remove_remote(&self, name: &str) -> Result<()> {
        self.config_unset(&format!("remote.{name}.url"))?;
        let _ = std::fs::remove_dir_all(self.dir.join("refs").join("remotes").join(name));
        Ok(())
    }
    pub fn rename_remote(&self, old: &str, new: &str) -> Result<()> {
        let url = self
            .remote_url(old)
            .ok_or_else(|| crate::RepoError::Other(format!("no such remote: {old}")))?;
        self.set_remote_url(new, &url)?;
        self.config_unset(&format!("remote.{old}.url"))?;
        let from = self.dir.join("refs").join("remotes").join(old);
        let to = self.dir.join("refs").join("remotes").join(new);
        if from.exists() {
            if let Some(p) = to.parent() {
                std::fs::create_dir_all(p)?;
            }
            let _ = std::fs::rename(&from, &to);
        }
        Ok(())
    }
    /// Configured remote names, sorted.
    pub fn remotes(&self) -> Vec<String> {
        let mut out = Vec::new();
        if let Ok(text) = std::fs::read_to_string(self.config_path()) {
            for line in text.lines() {
                if let Some((k, _)) = line.split_once('=') {
                    if let Some(name) = k
                        .trim()
                        .strip_prefix("remote.")
                        .and_then(|r| r.strip_suffix(".url"))
                    {
                        out.push(name.to_string());
                    }
                }
            }
        }
        out.sort();
        out.dedup();
        out
    }

    fn remote_ref_path(&self, name: &str, branch: &str) -> PathBuf {
        self.dir
            .join("refs")
            .join("remotes")
            .join(name)
            .join(branch)
    }
    pub fn read_remote_ref(&self, name: &str, branch: &str) -> Option<String> {
        std::fs::read_to_string(self.remote_ref_path(name, branch))
            .ok()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
    }
    pub fn write_remote_ref(&self, name: &str, branch: &str, hash: &str) -> Result<()> {
        if !safe_ref_name(name) || !safe_ref_name(branch) {
            return Err(RepoError::BadRef(format!("{name}/{branch}")));
        }
        let p = self.remote_ref_path(name, branch);
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(p, format!("{hash}\n"))?;
        Ok(())
    }

    // ---- refs / HEAD ----

    fn head_path(&self) -> PathBuf {
        self.dir.join("HEAD")
    }
    fn branch_path(&self, name: &str) -> PathBuf {
        self.dir.join("refs").join("heads").join(name)
    }

    pub fn read_branch(&self, name: &str) -> Option<String> {
        if !safe_ref_name(name) {
            return None;
        }
        std::fs::read_to_string(self.branch_path(name))
            .ok()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
    }

    pub fn write_branch(&self, name: &str, hash: &str) -> Result<()> {
        if !safe_ref_name(name) {
            return Err(RepoError::BadRef(name.to_string()));
        }
        let old = self.read_branch(name);
        let p = self.branch_path(name);
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(p, format!("{hash}\n"))?;
        // Every branch move is logged so a force-moved tip stays recoverable.
        self.append_reflog(&self.branch_log_path(name), old.as_deref(), hash, "update")?;
        Ok(())
    }

    pub fn delete_branch(&self, name: &str) -> Result<()> {
        if !safe_ref_name(name) {
            return Err(RepoError::BadRef(name.to_string()));
        }
        let _ = std::fs::remove_file(self.branch_path(name));
        let _ = std::fs::remove_file(self.branch_log_path(name));
        Ok(())
    }

    pub fn branches(&self) -> Vec<String> {
        let mut out = Vec::new();
        if let Ok(entries) = std::fs::read_dir(self.dir.join("refs").join("heads")) {
            for e in entries.flatten() {
                if let Ok(name) = e.file_name().into_string() {
                    out.push(name);
                }
            }
        }
        out.sort();
        out
    }

    fn tag_path(&self, name: &str) -> PathBuf {
        self.dir.join("refs").join("tags").join(name)
    }

    pub fn write_tag(&self, name: &str, commit: &str) -> Result<()> {
        if !safe_ref_name(name) {
            return Err(RepoError::BadRef(name.to_string()));
        }
        let p = self.tag_path(name);
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(p, format!("{commit}\n"))?;
        Ok(())
    }

    pub fn read_tag(&self, name: &str) -> Option<String> {
        if !safe_ref_name(name) {
            return None;
        }
        std::fs::read_to_string(self.tag_path(name))
            .ok()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
    }

    pub fn delete_tag(&self, name: &str) -> Result<()> {
        if !safe_ref_name(name) {
            return Err(RepoError::BadRef(name.to_string()));
        }
        let _ = std::fs::remove_file(self.tag_path(name));
        Ok(())
    }

    /// Stores an annotated tag object and points `refs/tags/<name>` at it.
    /// Returns the tag object's hash.
    pub fn write_annotated_tag(&self, tag: &TagObject) -> Result<String> {
        let hash = self.objects.write(tag.to_json()?.as_bytes())?;
        self.write_tag(&tag.tag, &hash)?;
        Ok(hash)
    }

    /// The tag object a tag ref points at, or `None` for a lightweight tag (or
    /// an absent one).
    pub fn read_annotated_tag(&self, name: &str) -> Option<TagObject> {
        let target = self.read_tag(name)?;
        let bytes = self.objects.read(&target).ok()?;
        TagObject::try_from_json(&String::from_utf8_lossy(&bytes))
    }

    /// The stored object at `hash` parsed as a tag object, if it is one.
    pub fn read_tag_object(&self, hash: &str) -> Option<TagObject> {
        let bytes = self.objects.read(hash).ok()?;
        TagObject::try_from_json(&String::from_utf8_lossy(&bytes))
    }

    /// Follows a chain of annotated tag objects down to the commit it names.
    /// A hash that isn't stored (or isn't a tag) is returned as-is.
    pub fn peel_to_commit(&self, hash: &str) -> Result<String> {
        let mut h = hash.to_string();
        for _ in 0..100 {
            let Ok(bytes) = self.objects.read(&h) else {
                return Ok(h); // not present — let the caller surface the error
            };
            match TagObject::try_from_json(&String::from_utf8_lossy(&bytes)) {
                Some(tag) => h = tag.object,
                None => return Ok(h),
            }
        }
        Err(RepoError::Other("tag chain too deep (cycle?)".into()))
    }

    pub fn tags(&self) -> Vec<String> {
        let mut out = Vec::new();
        if let Ok(entries) = std::fs::read_dir(self.dir.join("refs").join("tags")) {
            for e in entries.flatten() {
                if let Ok(name) = e.file_name().into_string() {
                    out.push(name);
                }
            }
        }
        out.sort();
        out
    }

    pub fn current_branch(&self) -> Option<String> {
        let head = std::fs::read_to_string(self.head_path()).ok()?;
        head.trim()
            .strip_prefix("ref: refs/heads/")
            .map(|s| s.to_string())
    }

    pub fn set_head_to_branch(&self, name: &str) -> Result<()> {
        std::fs::write(self.head_path(), format!("ref: refs/heads/{name}\n"))?;
        Ok(())
    }

    pub fn set_head_detached(&self, hash: &str) -> Result<()> {
        std::fs::write(self.head_path(), format!("{hash}\n"))?;
        Ok(())
    }

    pub fn head_commit(&self) -> Option<String> {
        let head = std::fs::read_to_string(self.head_path()).ok()?;
        let head = head.trim();
        if let Some(branch) = head.strip_prefix("ref: refs/heads/") {
            self.read_branch(branch)
        } else if head.is_empty() {
            None
        } else {
            Some(head.to_string())
        }
    }

    // ---- reflogs (logs/HEAD + logs/refs/heads/<branch>) ----

    fn head_log_path(&self) -> PathBuf {
        self.dir.join("logs").join("HEAD")
    }
    fn branch_log_path(&self, name: &str) -> PathBuf {
        self.dir.join("logs").join("refs").join("heads").join(name)
    }

    /// Append a ref movement to a reflog: `<from|zeros> <to> <message>`.
    fn append_reflog(&self, p: &Path, from: Option<&str>, to: &str, message: &str) -> Result<()> {
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent)?;
        }
        use std::io::Write;
        let mut f = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(p)?;
        let from = from.unwrap_or(&"0".repeat(64)).to_string();
        let message = message.replace('\n', " ");
        writeln!(f, "{from} {to} {message}")?;
        Ok(())
    }

    fn read_reflog(p: &Path) -> Vec<String> {
        std::fs::read_to_string(p)
            .map(|text| {
                let mut lines: Vec<String> = text
                    .lines()
                    .filter(|l| !l.trim().is_empty())
                    .map(str::to_string)
                    .collect();
                lines.reverse();
                lines
            })
            .unwrap_or_default()
    }

    fn reflog_at(entries: &[String], what: &str, n: usize) -> Result<String> {
        let line = entries
            .get(n)
            .ok_or_else(|| RepoError::BadRef(format!("reflog for {what} has no entry @{{{n}}}")))?;
        line.split(' ')
            .nth(1)
            .filter(|h| !h.is_empty())
            .map(str::to_string)
            .ok_or_else(|| RepoError::BadRef(format!("malformed reflog entry @{{{n}}}")))
    }

    /// Append a HEAD movement to the reflog: `<from|zeros> <to> <message>`.
    pub fn record_head(&self, from: Option<&str>, to: &str, message: &str) -> Result<()> {
        self.append_reflog(&self.head_log_path(), from, to, message)
    }

    /// HEAD reflog entries, most recent first (`<from> <to> <message>` lines).
    pub fn reflog(&self) -> Vec<String> {
        Self::read_reflog(&self.head_log_path())
    }

    /// Branch reflog entries, most recent first (empty if never moved).
    pub fn branch_reflog(&self, name: &str) -> Vec<String> {
        if !safe_ref_name(name) {
            return Vec::new();
        }
        Self::read_reflog(&self.branch_log_path(name))
    }

    /// The commit HEAD pointed at `n` reflog entries ago (`HEAD@{n}`).
    /// `HEAD@{0}` is the most recent recorded position.
    pub fn reflog_commit_at(&self, n: usize) -> Result<String> {
        Self::reflog_at(&self.reflog(), "HEAD", n)
    }

    /// The commit `name` pointed at `n` branch-reflog entries ago (`name@{n}`).
    pub fn branch_reflog_commit_at(&self, name: &str, n: usize) -> Result<String> {
        Self::reflog_at(&self.branch_reflog(name), name, n)
    }

    // ---- objects: commits + manifests ----

    pub fn write_manifest(&self, m: &Manifest) -> Result<String> {
        self.objects.write(m.to_json()?.as_bytes())
    }
    pub fn read_manifest(&self, tree: &str) -> Result<Manifest> {
        Manifest::from_json(&String::from_utf8_lossy(&self.objects.read(tree)?))
    }
    pub fn write_commit(&self, c: &CommitObject) -> Result<String> {
        self.objects.write(c.to_json()?.as_bytes())
    }
    pub fn read_commit(&self, hash: &str) -> Result<CommitObject> {
        CommitObject::from_json(&String::from_utf8_lossy(&self.objects.read(hash)?))
    }

    pub fn create_commit(
        &self,
        tree: &str,
        parents: Vec<String>,
        message: &str,
        author: &str,
        time: &str,
    ) -> Result<String> {
        self.create_commit_signed(tree, parents, message, author, time, None)
    }

    /// Like [`Repository::create_commit`], optionally signing the commit. The
    /// signature is computed over the payload with the signature field cleared,
    /// then stored in the object — so the object hash covers the signature
    /// (git's model: a signed commit is its own object).
    pub fn create_commit_signed(
        &self,
        tree: &str,
        parents: Vec<String>,
        message: &str,
        author: &str,
        time: &str,
        sign: Option<&SignFn>,
    ) -> Result<String> {
        let mut commit = CommitObject {
            tree: tree.to_string(),
            parents,
            message: message.to_string(),
            author: author.to_string(),
            time: time.to_string(),
            committer: None,
            commit_time: None,
            signature: None,
        };
        if let Some(signer) = sign {
            commit.signature = Some(signer(&commit.signable_payload()?)?);
        }
        self.write_commit(&commit)
    }

    // ---- revision resolution ----

    /// Resolve a revision spec: `HEAD`, a branch name, a (possibly abbreviated)
    /// hex id, with optional trailing `~n` (n-th first-parent ancestor) and
    /// `^`/`^n` (n-th parent).
    pub fn resolve_ref(&self, spec: &str) -> Result<String> {
        let split = spec.find(['~', '^']).unwrap_or(spec.len());
        let base = &spec[..split];
        let mut hash = self.resolve_base(base)?;
        let mut rest = &spec[split..];
        while let Some(op) = rest.chars().next() {
            rest = &rest[op.len_utf8()..];
            let digits = rest.bytes().take_while(u8::is_ascii_digit).count();
            let n: usize = if digits > 0 {
                rest[..digits].parse().unwrap_or(1)
            } else {
                1
            };
            rest = &rest[digits..];
            hash = match op {
                '~' => {
                    let mut h = hash;
                    for _ in 0..n {
                        h = self.first_parent(&h)?;
                    }
                    h
                }
                '^' => self.nth_parent(&hash, n)?,
                _ => return Err(RepoError::BadRef(spec.to_string())),
            };
        }
        Ok(hash)
    }

    fn resolve_base(&self, base: &str) -> Result<String> {
        // HEAD@{n} / @{n}: the nth-previous HEAD value from the reflog.
        if let Some(rest) = base
            .strip_prefix("HEAD@{")
            .or_else(|| base.strip_prefix("@{"))
        {
            if let Some(n) = rest.strip_suffix('}') {
                let n: usize = n.parse().map_err(|_| RepoError::BadRef(base.to_string()))?;
                return self.reflog_commit_at(n);
            }
        }
        // <branch>@{n}: the nth-previous tip from that branch's reflog.
        if let Some((name, rest)) = base.split_once("@{") {
            if let Some(n) = rest.strip_suffix('}') {
                if self.read_branch(name).is_some() || !self.branch_reflog(name).is_empty() {
                    let n: usize = n.parse().map_err(|_| RepoError::BadRef(base.to_string()))?;
                    return self.branch_reflog_commit_at(name, n);
                }
            }
        }
        if base.is_empty() || base == "HEAD" {
            return self
                .head_commit()
                .ok_or_else(|| RepoError::BadRef("HEAD".into()));
        }
        if let Some(h) = self.read_branch(base) {
            return Ok(h);
        }
        if let Some(h) = self.read_tag(base) {
            // An annotated tag ref points at a tag object — peel to its commit.
            return self.peel_to_commit(&h);
        }
        if base.len() >= 4 && base.chars().all(|c| c.is_ascii_hexdigit()) {
            if self.objects.exists(base) {
                return Ok(base.to_string());
            }
            if let Some(full) = self.find_object_by_prefix(base) {
                return Ok(full);
            }
        }
        Err(RepoError::BadRef(base.to_string()))
    }

    // ---- shallow history graft ----
    // A depth-limited clone records the commits whose parents were
    // intentionally not fetched in `<repo>/shallow`; every graph walk treats
    // those commits as parentless so history cleanly ends at the boundary.

    fn shallow_path(&self) -> PathBuf {
        self.dir.join("shallow")
    }

    /// Commits whose parents were intentionally not fetched (the shallow
    /// boundary). Empty for a full clone.
    pub fn shallow_boundary(&self) -> std::collections::HashSet<String> {
        std::fs::read_to_string(self.shallow_path())
            .map(|text| {
                text.lines()
                    .map(str::trim)
                    .filter(|l| !l.is_empty())
                    .map(str::to_string)
                    .collect()
            })
            .unwrap_or_default()
    }

    pub fn is_shallow(&self) -> bool {
        self.shallow_path().is_file()
    }

    /// Records the shallow boundary (replacing any prior one); an empty set
    /// clears it.
    pub fn write_shallow<I: IntoIterator<Item = String>>(&self, boundary: I) -> Result<()> {
        let mut set: Vec<String> = boundary.into_iter().collect();
        set.sort();
        set.dedup();
        if set.is_empty() {
            let _ = std::fs::remove_file(self.shallow_path());
        } else {
            std::fs::write(self.shallow_path(), set.join("\n") + "\n")?;
        }
        Ok(())
    }

    /// Parents of `commit` — the single graph-walk entry point, grafted to
    /// empty at a shallow boundary.
    pub fn parents_of(&self, commit: &str) -> Result<Vec<String>> {
        if self.is_shallow() && self.shallow_boundary().contains(commit) {
            return Ok(Vec::new());
        }
        Ok(self.read_commit(commit)?.parents)
    }

    // ---- promisor (partial clone) ----
    // A `--filter=blob:none` clone fetches the commit/tree skeleton but not the
    // leaf chunk/nbt/blob objects; `<repo>/promisor` records the remote URL that
    // backfills them lazily. With the marker present, intentionally-absent leaf
    // objects are expected, not corruption.

    fn promisor_path(&self) -> PathBuf {
        self.dir.join("promisor")
    }

    /// True if this is a partial clone (leaf objects fetched on demand).
    pub fn is_partial(&self) -> bool {
        self.promisor_path().is_file()
    }

    /// The remote URL that backfills missing leaf objects, if partial.
    pub fn promisor_remote(&self) -> Option<String> {
        std::fs::read_to_string(self.promisor_path())
            .ok()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
    }

    /// Mark this repo partial, backfilled from `remote_url`.
    pub fn write_promisor(&self, remote_url: &str) -> Result<()> {
        std::fs::write(self.promisor_path(), format!("{remote_url}\n"))?;
        Ok(())
    }

    fn first_parent(&self, h: &str) -> Result<String> {
        self.read_commit(h)?
            .parents
            .into_iter()
            .next()
            .ok_or_else(|| RepoError::BadRef(format!("{h}: no parent")))
    }

    fn nth_parent(&self, h: &str, n: usize) -> Result<String> {
        self.read_commit(h)?
            .parents
            .get(n.saturating_sub(1))
            .cloned()
            .ok_or_else(|| RepoError::BadRef(format!("{h}^{n}: no such parent")))
    }

    fn find_object_by_prefix(&self, prefix: &str) -> Option<String> {
        let (sub, rest) = prefix.split_at(2);
        let entries = std::fs::read_dir(self.dir.join("objects").join(sub)).ok()?;
        let mut found = None;
        for e in entries.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if name.ends_with(".tmp") || !name.starts_with(rest) {
                continue;
            }
            if found.is_some() {
                return None; // ambiguous
            }
            found = Some(format!("{sub}{name}"));
        }
        found
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn init_open_and_config() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        assert!(Repository::is_repository(d.path()));
        repo.set_worktree("/some/world").unwrap();
        assert_eq!(repo.worktree().as_deref(), Some("/some/world"));
        // re-open sees the config
        let repo2 = Repository::open(d.path()).unwrap();
        assert_eq!(repo2.worktree().as_deref(), Some("/some/world"));
    }

    #[test]
    fn commit_flow_and_resolution() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let tree = repo.write_manifest(&Manifest::default()).unwrap();

        let c1 = repo
            .create_commit(&tree, vec![], "first", "me", "t1")
            .unwrap();
        repo.write_branch("main", &c1).unwrap();
        assert_eq!(repo.head_commit().as_deref(), Some(c1.as_str()));
        assert_eq!(repo.current_branch().as_deref(), Some("main"));

        let c2 = repo
            .create_commit(&tree, vec![c1.clone()], "second", "me", "t2")
            .unwrap();
        repo.write_branch("main", &c2).unwrap();

        assert_eq!(repo.resolve_ref("HEAD").unwrap(), c2);
        assert_eq!(repo.resolve_ref("main").unwrap(), c2);
        assert_eq!(repo.resolve_ref("HEAD~1").unwrap(), c1);
        assert_eq!(repo.resolve_ref("HEAD^").unwrap(), c1);
        assert_eq!(repo.resolve_ref(&c1).unwrap(), c1);
        assert_eq!(repo.resolve_ref(&c1[..8]).unwrap(), c1); // abbreviated
        assert!(repo.resolve_ref("nope").is_err());

        let commit = repo.read_commit(&c2).unwrap();
        assert_eq!(commit.message, "second");
        assert_eq!(commit.parents, vec![c1]);
    }

    #[test]
    fn reflog_records_and_resolves_head_at_n() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let tree = repo.write_manifest(&Manifest::default()).unwrap();
        let c1 = repo.create_commit(&tree, vec![], "one", "me", "t").unwrap();
        repo.write_branch("main", &c1).unwrap();
        repo.record_head(None, &c1, "commit: one").unwrap();
        let c2 = repo
            .create_commit(&tree, vec![c1.clone()], "two", "me", "t")
            .unwrap();
        repo.write_branch("main", &c2).unwrap();
        repo.record_head(Some(&c1), &c2, "commit: two").unwrap();

        let log = repo.reflog();
        assert_eq!(log.len(), 2);
        assert!(log[0].contains("commit: two"), "most recent first");

        assert_eq!(repo.reflog_commit_at(0).unwrap(), c2);
        assert_eq!(repo.reflog_commit_at(1).unwrap(), c1);
        assert_eq!(repo.resolve_ref("HEAD@{1}").unwrap(), c1);
        assert_eq!(repo.resolve_ref("@{0}").unwrap(), c2);
        assert!(repo.resolve_ref("HEAD@{9}").is_err());
        // combines with ancestry operators
        assert_eq!(repo.resolve_ref("HEAD@{0}~1").unwrap(), c1);
    }

    #[test]
    fn branch_reflog_records_moves_and_resolves_at_n() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let tree = repo.write_manifest(&Manifest::default()).unwrap();
        let c1 = repo.create_commit(&tree, vec![], "one", "me", "t").unwrap();
        repo.write_branch("main", &c1).unwrap();
        let c2 = repo
            .create_commit(&tree, vec![c1.clone()], "two", "me", "t")
            .unwrap();
        repo.write_branch("main", &c2).unwrap();

        let log = repo.branch_reflog("main");
        assert_eq!(log.len(), 2, "every branch move is recorded");
        // most recent first: c1 → c2, then zeros → c1
        assert!(log[0].starts_with(&format!("{c1} {c2}")));
        assert!(log[1].starts_with(&format!("{} {c1}", "0".repeat(64))));

        // a force-moved tip is recoverable via <branch>@{n}
        assert_eq!(repo.resolve_ref("main@{0}").unwrap(), c2);
        assert_eq!(repo.resolve_ref("main@{1}").unwrap(), c1);
        assert!(repo.resolve_ref("main@{9}").is_err());
        assert_eq!(repo.resolve_ref("main@{0}~1").unwrap(), c1);

        // deleting the branch removes its log (git behavior)
        repo.delete_branch("main").unwrap();
        assert!(repo.branch_reflog("main").is_empty());
    }

    #[test]
    fn annotated_tags_store_peel_and_resolve() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let tree = repo.write_manifest(&Manifest::default()).unwrap();
        let c = repo.create_commit(&tree, vec![], "rel", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        let tag = TagObject {
            object: c.clone(),
            kind: "commit".into(),
            tag: "v1".into(),
            tagger: "me".into(),
            time: "t".into(),
            message: "first release".into(),
            signature: None,
        };
        let th = repo.write_annotated_tag(&tag).unwrap();
        assert_ne!(th, c); // the ref holds the tag object, not the commit
        assert_eq!(repo.read_tag("v1").as_deref(), Some(th.as_str()));
        assert_eq!(
            repo.read_annotated_tag("v1").unwrap().message,
            "first release"
        );

        // resolution peels to the commit (so checkout/log/`v1~0` work)
        assert_eq!(repo.resolve_ref("v1").unwrap(), c);
        assert_eq!(repo.peel_to_commit(&th).unwrap(), c);

        // a lightweight tag is not an annotated tag
        repo.write_tag("lw", &c).unwrap();
        assert!(repo.read_annotated_tag("lw").is_none());
        assert_eq!(repo.resolve_ref("lw").unwrap(), c);
    }

    #[test]
    fn signed_commit_payload_roundtrip() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(d.path()).unwrap();
        let tree = repo.write_manifest(&Manifest::default()).unwrap();
        let sign = |payload: &str| Ok(format!("SIG({})", payload.len()));
        let c = repo
            .create_commit_signed(&tree, vec![], "m", "me", "t", Some(&sign))
            .unwrap();
        let commit = repo.read_commit(&c).unwrap();
        let sig = commit.signature.clone().unwrap();
        assert!(sig.starts_with("SIG("));
        // the signature covers the payload-without-signature
        assert_eq!(
            sig,
            format!("SIG({})", commit.signable_payload().unwrap().len())
        );
    }

    #[test]
    fn discover_walks_up() {
        let d = tempfile::tempdir().unwrap();
        Repository::init(d.path()).unwrap();
        let nested = d.path().join("a").join("b");
        std::fs::create_dir_all(&nested).unwrap();
        let repo = Repository::discover(&nested).unwrap();
        assert!(Repository::is_repository(repo.dir()));
    }

    #[test]
    fn init_embedded_creates_dotmcagit_and_binds_worktree() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        let repo = Repository::init_embedded(&world).unwrap();
        assert!(world.join(".mcagit").join("HEAD").is_file());
        assert!(world.join(".mcagit").join("objects").is_dir());
        assert!(repo.dir().ends_with(".mcagit"), "dir() points at .mcagit");
        // worktree is bound to the folder (canonicalized)
        let wt = repo.worktree().unwrap();
        assert_eq!(
            std::fs::canonicalize(&wt).unwrap(),
            std::fs::canonicalize(&world).unwrap()
        );
    }

    #[test]
    fn open_detects_embedded_and_bare() {
        let d = tempfile::tempdir().unwrap();
        // embedded
        let world = d.path().join("world");
        Repository::init_embedded(&world).unwrap();
        let e = Repository::open(&world).unwrap();
        assert!(e.dir().ends_with(".mcagit"));
        // bare
        let bare = d.path().join("bare");
        Repository::init(&bare).unwrap();
        let b = Repository::open(&bare).unwrap();
        assert_eq!(b.dir(), bare.as_path());
        // neither
        assert!(Repository::open(&d.path().join("nope")).is_err());
    }

    #[test]
    fn discover_finds_embedded_from_nested_subdir() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        Repository::init_embedded(&world).unwrap();
        let nested = world.join("region").join("sub");
        std::fs::create_dir_all(&nested).unwrap();
        let r = Repository::discover(&nested).unwrap();
        assert!(r.dir().ends_with(".mcagit"));
        assert_eq!(
            std::fs::canonicalize(r.worktree().unwrap()).unwrap(),
            std::fs::canonicalize(&world).unwrap()
        );
    }

    #[test]
    fn init_embedded_on_existing_bare_stays_bare() {
        let d = tempfile::tempdir().unwrap();
        let dir = d.path().join("repo");
        Repository::init(&dir).unwrap(); // bare: HEAD/objects at top
        let r = Repository::init_embedded(&dir).unwrap();
        assert_eq!(
            r.dir(),
            dir.as_path(),
            "re-init keeps the existing bare layout"
        );
        assert!(!dir.join(".mcagit").exists(), "must not nest a .mcagit");
    }

    #[test]
    fn is_repository_recognizes_both_layouts() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        Repository::init_embedded(&world).unwrap();
        let bare = d.path().join("bare");
        Repository::init(&bare).unwrap();
        assert!(Repository::is_repository(&world));
        assert!(Repository::is_repository(&bare));
        assert!(!Repository::is_repository(&d.path().join("nope")));
    }

    #[test]
    fn init_embedded_twice_is_idempotent() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        let r1 = Repository::init_embedded(&world).unwrap();
        let wt1 = r1.worktree().unwrap();
        let r2 = Repository::init_embedded(&world).unwrap();
        assert!(r2.dir().ends_with(".mcagit"));
        assert_eq!(r2.worktree().unwrap(), wt1, "worktree unchanged on re-init");
        assert!(Repository::open(&world).is_ok());
    }
}
