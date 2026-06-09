//! Persistent map from a chunk's *compressed payload* hash to its stored
//! chunk-object hash. Lets re-commits of a mostly-unchanged world skip
//! decoding + canonicalizing chunks whose raw bytes are unchanged (the common
//! backup case). Keys are content-derived, so entries never go stale; a hit is
//! only trusted after verifying the object actually exists in the store.

use crate::Result;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub struct ChunkCache {
    path: PathBuf,
    map: Mutex<HashMap<String, String>>,
    /// Whether anything changed since load (skip the save when nothing did).
    dirty: std::sync::atomic::AtomicBool,
}

impl ChunkCache {
    /// The cache key for a chunk payload: compression scheme + raw-bytes hash.
    pub fn key(compression_byte: u8, payload: &[u8]) -> String {
        format!("{compression_byte}:{}", blake3::hash(payload).to_hex())
    }

    /// Load `<repo>/chunkcache.json` (a corrupt or missing cache starts empty —
    /// it only ever accelerates).
    pub fn load(repo_dir: &Path) -> Self {
        let path = repo_dir.join("chunkcache.json");
        let map = std::fs::read_to_string(&path)
            .ok()
            .and_then(|text| serde_json::from_str::<HashMap<String, String>>(&text).ok())
            .unwrap_or_default();
        Self {
            path,
            map: Mutex::new(map),
            dirty: std::sync::atomic::AtomicBool::new(false),
        }
    }

    pub fn get(&self, key: &str) -> Option<String> {
        self.map.lock().ok()?.get(key).cloned()
    }

    pub fn set(&self, key: String, object_id: String) {
        if let Ok(mut m) = self.map.lock() {
            m.insert(key, object_id);
            self.dirty.store(true, std::sync::atomic::Ordering::Relaxed);
        }
    }

    /// Persist atomically (tmp + rename): a crash or a concurrent commit
    /// mid-write must not corrupt the cache. No-op when nothing changed.
    pub fn save(&self) -> Result<()> {
        if !self.dirty.load(std::sync::atomic::Ordering::Relaxed) {
            return Ok(());
        }
        let body = {
            let m = self
                .map
                .lock()
                .map_err(|_| crate::RepoError::Other("chunk cache poisoned".into()))?;
            serde_json::to_vec(&*m)?
        };
        let tmp = self
            .path
            .with_extension(format!("json.{}.tmp", std::process::id()));
        std::fs::write(&tmp, body)?;
        std::fs::rename(&tmp, &self.path)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_and_corrupt_tolerance() {
        let d = tempfile::tempdir().unwrap();
        let c = ChunkCache::load(d.path());
        assert!(c.get("2:abc").is_none());
        c.set("2:abc".into(), "objid".into());
        assert_eq!(c.get("2:abc").as_deref(), Some("objid"));
        c.save().unwrap();

        // reload sees the entry
        let c2 = ChunkCache::load(d.path());
        assert_eq!(c2.get("2:abc").as_deref(), Some("objid"));

        // a corrupt cache file starts empty instead of erroring
        std::fs::write(d.path().join("chunkcache.json"), b"{ not json").unwrap();
        let c3 = ChunkCache::load(d.path());
        assert!(c3.get("2:abc").is_none());
    }

    #[test]
    fn key_distinguishes_compression() {
        let a = ChunkCache::key(2, b"payload");
        let b = ChunkCache::key(1, b"payload");
        assert_ne!(a, b);
        assert_eq!(a, ChunkCache::key(2, b"payload"));
    }
}
