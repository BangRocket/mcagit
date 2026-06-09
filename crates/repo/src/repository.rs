//! The bare, external repository: object store + refs/HEAD + config, plus
//! commit/manifest storage and revision resolution.

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

    /// A directory is a repo if it has both `HEAD` and `objects/`.
    pub fn is_repository(dir: &Path) -> bool {
        dir.join("HEAD").is_file() && dir.join("objects").is_dir()
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

    pub fn open(dir: &Path) -> Result<Self> {
        if !Self::is_repository(dir) {
            return Err(RepoError::NotARepository(dir.display().to_string()));
        }
        Ok(Self {
            dir: dir.to_path_buf(),
            objects: ObjectStore::new(dir.join("objects")),
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
        let p = self.branch_path(name);
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(p, format!("{hash}\n"))?;
        Ok(())
    }

    pub fn delete_branch(&self, name: &str) -> Result<()> {
        if !safe_ref_name(name) {
            return Err(RepoError::BadRef(name.to_string()));
        }
        let _ = std::fs::remove_file(self.branch_path(name));
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

    // ---- reflog (logs/HEAD) ----

    /// Append a HEAD movement to the reflog: `<from|zeros> <to> <message>`.
    pub fn record_head(&self, from: Option<&str>, to: &str, message: &str) -> Result<()> {
        let p = self.dir.join("logs").join("HEAD");
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

    /// Reflog entries, most recent first (`<from> <to> <message>` lines).
    pub fn reflog(&self) -> Vec<String> {
        std::fs::read_to_string(self.dir.join("logs").join("HEAD"))
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

    /// The commit HEAD pointed at `n` reflog entries ago (`HEAD@{n}`).
    /// `HEAD@{0}` is the most recent recorded position.
    pub fn reflog_commit_at(&self, n: usize) -> Result<String> {
        let entries = self.reflog();
        let line = entries
            .get(n)
            .ok_or_else(|| RepoError::BadRef(format!("reflog for HEAD has no entry @{{{n}}}")))?;
        line.split(' ')
            .nth(1)
            .filter(|h| !h.is_empty())
            .map(str::to_string)
            .ok_or_else(|| RepoError::BadRef(format!("malformed reflog entry @{{{n}}}")))
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

    /// Parents of `commit` (the single graph-walk entry point — later grafted
    /// to empty at a shallow boundary).
    pub fn parents_of(&self, commit: &str) -> Result<Vec<String>> {
        Ok(self.read_commit(commit)?.parents)
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
}
