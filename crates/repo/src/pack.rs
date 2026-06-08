//! Packfiles: many objects in one file (zstd-per-object) + a JSON index
//! (`id → [offset, len]`). The `.pack` is mmap'd so reads are slice + zstd
//! decode with no per-object file open — the fix for the loose-object commit/
//! checkout syscall wall.

use crate::Result;
use memmap2::Mmap;
use std::collections::BTreeMap;
use std::fs::File;
use std::io::Write;
use std::path::Path;

pub struct Packfile {
    mmap: Mmap,
    index: BTreeMap<String, (u64, u64)>,
}

impl Packfile {
    pub fn open(pack: &Path, idx: &Path) -> Result<Self> {
        let index: BTreeMap<String, (u64, u64)> = serde_json::from_slice(&std::fs::read(idx)?)?;
        let file = File::open(pack)?;
        // Safety: the pack file is content-addressed and treated as read-only.
        let mmap = unsafe { Mmap::map(&file)? };
        Ok(Self { mmap, index })
    }

    pub fn contains(&self, id: &str) -> bool {
        self.index.contains_key(id)
    }

    pub fn read(&self, id: &str) -> Result<Option<Vec<u8>>> {
        let Some(&(off, len)) = self.index.get(id) else {
            return Ok(None);
        };
        let (off, len) = (off as usize, len as usize);
        Ok(Some(zstd::decode_all(&self.mmap[off..off + len])?))
    }

    pub fn ids(&self) -> impl Iterator<Item = &String> {
        self.index.keys()
    }
}

/// Streams objects into a single pack file under construction. Append-only, so
/// it bounds memory to one object at a time (vs. buffering the whole pack).
pub struct PackWriter {
    tmp: std::path::PathBuf,
    file: File,
    index: BTreeMap<String, (u64, u64)>,
    offset: u64,
}

impl PackWriter {
    pub fn new(pack_dir: &Path) -> Result<Self> {
        std::fs::create_dir_all(pack_dir)?;
        let tmp = pack_dir.join("incoming.pack.tmp");
        let file = File::create(&tmp)?;
        Ok(Self {
            tmp,
            file,
            index: BTreeMap::new(),
            offset: 0,
        })
    }

    /// Append `content` (raw) under `id`; no-op if already present in this pack.
    pub fn add(&mut self, id: &str, content: &[u8]) -> Result<()> {
        let packed = zstd::encode_all(content, 0)?;
        self.push_packed(id, &packed)
    }

    /// Append an already-zstd-compressed body under `id`; no-op if already
    /// present. Lets callers compress in parallel and only serialize the write.
    pub fn push_packed(&mut self, id: &str, packed: &[u8]) -> Result<()> {
        if self.index.contains_key(id) {
            return Ok(());
        }
        self.file.write_all(packed)?;
        let len = packed.len() as u64;
        self.index.insert(id.to_string(), (self.offset, len));
        self.offset += len;
        Ok(())
    }

    pub fn is_empty(&self) -> bool {
        self.index.is_empty()
    }

    /// Finalize: name the pack by content hash, write the index, return the id.
    pub fn finish(self, pack_dir: &Path) -> Result<String> {
        self.file.sync_all()?;
        drop(self.file);
        let bytes = std::fs::read(&self.tmp)?;
        let pack_id = blake3::hash(&bytes).to_hex().to_string();
        let pack_path = pack_dir.join(format!("pack-{pack_id}.pack"));
        std::fs::rename(&self.tmp, &pack_path)?;
        std::fs::write(
            pack_dir.join(format!("pack-{pack_id}.idx")),
            serde_json::to_vec(&self.index)?,
        )?;
        Ok(pack_id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn writer_then_read() {
        let d = tempfile::tempdir().unwrap();
        let pack_dir = d.path().join("pack");
        let mut w = PackWriter::new(&pack_dir).unwrap();
        let ida = blake3::hash(b"hello").to_hex().to_string();
        let idb = blake3::hash(b"world").to_hex().to_string();
        w.add(&ida, b"hello").unwrap();
        w.add(&idb, b"world").unwrap();
        let id = w.finish(&pack_dir).unwrap();

        let pf = Packfile::open(
            &pack_dir.join(format!("pack-{id}.pack")),
            &pack_dir.join(format!("pack-{id}.idx")),
        )
        .unwrap();
        assert_eq!(pf.read(&ida).unwrap().unwrap(), b"hello");
        assert_eq!(pf.read(&idb).unwrap().unwrap(), b"world");
        assert!(pf.contains(&ida));
        assert!(pf.read("deadbeef").unwrap().is_none());
    }
}
