//! The bare, external repository: object store + refs/HEAD + config, plus
//! commit/manifest storage and revision resolution.

use crate::manifest::{CommitObject, Manifest};
use crate::object_store::ObjectStore;
use crate::{RepoError, Result};
use std::path::{Path, PathBuf};

pub struct Repository {
    dir: PathBuf,
    objects: ObjectStore,
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
            .filter(|l| l.split_once('=').map(|(k, _)| k.trim() != key).unwrap_or(true))
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

    // ---- refs / HEAD ----

    fn head_path(&self) -> PathBuf {
        self.dir.join("HEAD")
    }
    fn branch_path(&self, name: &str) -> PathBuf {
        self.dir.join("refs").join("heads").join(name)
    }

    pub fn read_branch(&self, name: &str) -> Option<String> {
        std::fs::read_to_string(self.branch_path(name))
            .ok()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
    }

    pub fn write_branch(&self, name: &str, hash: &str) -> Result<()> {
        let p = self.branch_path(name);
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(p, format!("{hash}\n"))?;
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
        self.write_commit(&CommitObject {
            tree: tree.to_string(),
            parents,
            message: message.to_string(),
            author: author.to_string(),
            time: time.to_string(),
            committer: None,
            commit_time: None,
        })
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
        if base.is_empty() || base == "HEAD" {
            return self
                .head_commit()
                .ok_or_else(|| RepoError::BadRef("HEAD".into()));
        }
        if let Some(h) = self.read_branch(base) {
            return Ok(h);
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

        let c1 = repo.create_commit(&tree, vec![], "first", "me", "t1").unwrap();
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
    fn discover_walks_up() {
        let d = tempfile::tempdir().unwrap();
        Repository::init(d.path()).unwrap();
        let nested = d.path().join("a").join("b");
        std::fs::create_dir_all(&nested).unwrap();
        let repo = Repository::discover(&nested).unwrap();
        assert!(Repository::is_repository(repo.dir()));
    }
}
