//! Content-addressed object store: blake3 id over uncompressed content, stored
//! zstd-compressed as loose `objects/<aa>/<rest>` files. Hashing the
//! *uncompressed* content means identical content dedups regardless of how it
//! was compressed on disk.

use crate::Result;
use std::path::PathBuf;

pub struct ObjectStore {
    dir: PathBuf,
}

impl ObjectStore {
    /// `dir` is the `objects/` directory inside the repo.
    pub fn new(dir: PathBuf) -> Self {
        Self { dir }
    }

    /// Store `content`, returning its hex blake3 id. Idempotent: storing the
    /// same content twice yields the same id and a single file.
    pub fn write(&self, content: &[u8]) -> Result<String> {
        let id = blake3::hash(content).to_hex().to_string();
        let (sub, rest) = id.split_at(2);
        let dir = self.dir.join(sub);
        let path = dir.join(rest);
        if !path.exists() {
            std::fs::create_dir_all(&dir)?;
            let packed = zstd::encode_all(content, 0)?; // 0 = zstd default level
            let tmp = dir.join(format!("{rest}.tmp"));
            std::fs::write(&tmp, &packed)?;
            std::fs::rename(&tmp, &path)?;
        }
        Ok(id)
    }

    /// Read and decompress the object with id `id`.
    pub fn read(&self, id: &str) -> Result<Vec<u8>> {
        let (sub, rest) = id.split_at(2);
        let packed = std::fs::read(self.dir.join(sub).join(rest))?;
        Ok(zstd::decode_all(&packed[..])?)
    }

    /// True if an object with id `id` is present.
    pub fn exists(&self, id: &str) -> bool {
        if id.len() < 3 {
            return false;
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
