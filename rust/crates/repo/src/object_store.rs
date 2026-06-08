//! Content-addressed object store: blake3 id over uncompressed content, stored
//! zstd-compressed as loose `objects/<aa>/<rest>` files. Hashing the
//! *uncompressed* content means identical content dedups regardless of how it
//! was compressed on disk.

use crate::Result;
use arc_swap::ArcSwap;
use std::path::{Path, PathBuf};
use std::sync::Arc;

pub struct ObjectStore {
    dir: PathBuf,
    // Lock-free reads: checkout/diff read objects hundreds of thousands of times
    // across threads; an RwLock here ping-pongs a cache line and serializes. An
    // atomic Arc load has no such contention.
    packs: ArcSwap<Vec<crate::pack::Packfile>>,
}

impl ObjectStore {
    /// `dir` is the `objects/` directory inside the repo.
    pub fn new(dir: PathBuf) -> Self {
        let packs = ArcSwap::from_pointee(Self::load_packs(&dir));
        Self { dir, packs }
    }

    /// Re-scan `objects/pack/` (e.g. after writing a new pack mid-process).
    pub fn reload_packs(&self) {
        self.packs.store(Arc::new(Self::load_packs(&self.dir)));
    }

    fn load_packs(dir: &Path) -> Vec<crate::pack::Packfile> {
        let mut packs = Vec::new();
        if let Ok(entries) = std::fs::read_dir(dir.join("pack")) {
            for e in entries.flatten() {
                let p = e.path();
                if p.extension().and_then(|s| s.to_str()) == Some("idx") {
                    let pack = p.with_extension("pack");
                    if let Ok(pf) = crate::pack::Packfile::open(&pack, &p) {
                        packs.push(pf);
                    }
                }
            }
        }
        packs
    }

    /// The `objects/pack/` directory.
    pub fn pack_dir(&self) -> PathBuf {
        self.dir.join("pack")
    }

    /// The `objects/` directory.
    pub fn objects_dir(&self) -> &Path {
        &self.dir
    }

    /// Every stored object id (loose + packed), de-duplicated.
    pub fn all_ids(&self) -> Vec<String> {
        let mut ids = std::collections::HashSet::new();
        if let Ok(subs) = std::fs::read_dir(&self.dir) {
            for sub in subs.flatten() {
                let name = sub.file_name().to_string_lossy().to_string();
                if name.len() == 2 && sub.path().is_dir() {
                    if let Ok(files) = std::fs::read_dir(sub.path()) {
                        for f in files.flatten() {
                            let fname = f.file_name().to_string_lossy().to_string();
                            if !fname.ends_with(".tmp") {
                                ids.insert(format!("{name}{fname}"));
                            }
                        }
                    }
                }
            }
        }
        for pack in self.packs.load().iter() {
            for id in pack.ids() {
                ids.insert(id.clone());
            }
        }
        ids.into_iter().collect()
    }

    /// Store `content`, returning its hex blake3 id. Idempotent: storing the
    /// same content twice yields the same id and a single file. Uses an atomic
    /// `create_new` so a single syscall serves as both the dedup check and the
    /// create — committing a world writes hundreds of thousands of tiny objects,
    /// so per-object syscall count dominates.
    pub fn write(&self, content: &[u8]) -> Result<String> {
        use std::io::Write;
        let id = blake3::hash(content).to_hex().to_string();
        let (sub, rest) = id.split_at(2);
        let path = self.dir.join(sub).join(rest);
        let mut file = match Self::create_new(&path) {
            Ok(Some(f)) => f,
            Ok(None) => return Ok(id), // already stored (dedup)
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                // fan-out subdir missing — create it once, then retry.
                std::fs::create_dir_all(self.dir.join(sub))?;
                match Self::create_new(&path)? {
                    Some(f) => f,
                    None => return Ok(id),
                }
            }
            Err(e) => return Err(e.into()),
        };
        file.write_all(&zstd::encode_all(content, 0)?)?;
        Ok(id)
    }

    /// Try to create `path` exclusively. `Ok(Some(file))` if created (caller
    /// fills it), `Ok(None)` if it already existed.
    fn create_new(path: &std::path::Path) -> std::io::Result<Option<std::fs::File>> {
        match std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path)
        {
            Ok(f) => Ok(Some(f)),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => Ok(None),
            Err(e) => Err(e),
        }
    }

    /// Read and decompress the object with id `id`.
    pub fn read(&self, id: &str) -> Result<Vec<u8>> {
        {
            let packs = self.packs.load();
            for pack in packs.iter() {
                if let Some(v) = pack.read(id)? {
                    return Ok(v);
                }
            }
        }
        let (sub, rest) = id.split_at(2);
        let packed = std::fs::read(self.dir.join(sub).join(rest))?;
        Ok(zstd::decode_all(&packed[..])?)
    }

    /// True if an object with id `id` is present (in a pack or loose).
    pub fn exists(&self, id: &str) -> bool {
        if id.len() < 3 {
            return false;
        }
        if self.packs.load().iter().any(|p| p.contains(id)) {
            return true;
        }
        let (sub, rest) = id.split_at(2);
        self.dir.join(sub).join(rest).exists()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn store() -> (tempfile::TempDir, ObjectStore) {
        let dir = tempfile::tempdir().unwrap();
        let os = ObjectStore::new(dir.path().join("objects"));
        (dir, os)
    }

    #[test]
    fn write_read_roundtrip() {
        let (_d, os) = store();
        let id = os.write(b"hello world").unwrap();
        assert_eq!(id.len(), 64);
        assert_eq!(os.read(&id).unwrap(), b"hello world");
        assert!(os.exists(&id));
    }

    #[test]
    fn same_content_dedups() {
        let (_d, os) = store();
        let a = os.write(b"dup payload").unwrap();
        let b = os.write(b"dup payload").unwrap();
        assert_eq!(a, b);
    }

    #[test]
    fn missing_is_not_exists() {
        let (_d, os) = store();
        assert!(!os.exists(&"0".repeat(64)));
    }
}
