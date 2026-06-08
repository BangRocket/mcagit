//! Whole-world diff: file union → per region (chunk-level) / loose NBT / blob,
//! parallel over files, with byte-identical fast paths.

use crate::change::{compare as compare_nbt, NbtChange};
use crate::Result;
use mca_anvil::{codec, ChunkPos, RegionFile};
use rayon::prelude::*;
use std::collections::BTreeSet;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FileStatus {
    Added,
    Removed,
    Modified,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChunkStatus {
    Added,
    Removed,
    Modified,
}

#[derive(Debug)]
pub struct ChunkDiff {
    pub x: i32,
    pub z: i32,
    pub status: ChunkStatus,
    pub changes: Vec<NbtChange>,
}

#[derive(Debug)]
pub struct FileDiff {
    pub path: String,
    pub status: FileStatus,
    pub changes: Vec<NbtChange>, // loose NBT node changes
    pub chunks: Vec<ChunkDiff>,  // region chunk changes
}

#[derive(Debug, Default)]
pub struct WorldDiff {
    pub files: Vec<FileDiff>, // only differing files
}

impl WorldDiff {
    pub fn is_empty(&self) -> bool {
        self.files.is_empty()
    }
}

/// Diff two world directories.
pub fn diff(world_a: &Path, world_b: &Path) -> Result<WorldDiff> {
    let mut rels: BTreeSet<String> = BTreeSet::new();
    rels.extend(list_files(world_a));
    rels.extend(list_files(world_b));
    let rels: Vec<String> = rels.into_iter().collect();

    let mut files: Vec<FileDiff> = rels
        .par_iter()
        .map(|rel| diff_one(rel, world_a, world_b))
        .collect::<Result<Vec<_>>>()?
        .into_iter()
        .flatten()
        .collect();
    files.sort_by(|a, b| a.path.cmp(&b.path));
    Ok(WorldDiff { files })
}

fn diff_one(rel: &str, a_root: &Path, b_root: &Path) -> Result<Option<FileDiff>> {
    let pa = a_root.join(rel);
    let pb = b_root.join(rel);
    let (ea, eb) = (pa.is_file(), pb.is_file());
    match (ea, eb) {
        (true, false) => Ok(Some(FileDiff {
            path: rel.into(),
            status: FileStatus::Removed,
            changes: vec![],
            chunks: vec![],
        })),
        (false, true) => Ok(Some(FileDiff {
            path: rel.into(),
            status: FileStatus::Added,
            changes: vec![],
            chunks: vec![],
        })),
        (true, true) => {
            let ba = std::fs::read(&pa)?;
            let bb = std::fs::read(&pb)?;
            if ba == bb {
                return Ok(None); // whole-file fast path
            }
            if is_region(rel) {
                let chunks = diff_region(&pa, &ba, &pb, &bb)?;
                if chunks.is_empty() {
                    return Ok(None); // semantically identical despite byte differences
                }
                Ok(Some(FileDiff {
                    path: rel.into(),
                    status: FileStatus::Modified,
                    changes: vec![],
                    chunks,
                }))
            } else if rel.to_ascii_lowercase().ends_with(".dat") {
                if let (Ok(va), Ok(vb)) = (codec::load_nbt_file(&pa), codec::load_nbt_file(&pb)) {
                    let changes = compare_nbt(&va, &vb);
                    if changes.is_empty() {
                        return Ok(None);
                    }
                    return Ok(Some(FileDiff {
                        path: rel.into(),
                        status: FileStatus::Modified,
                        changes,
                        chunks: vec![],
                    }));
                }
                // unparseable → treat as blob (bytes already differ)
                Ok(Some(FileDiff {
                    path: rel.into(),
                    status: FileStatus::Modified,
                    changes: vec![],
                    chunks: vec![],
                }))
            } else {
                Ok(Some(FileDiff {
                    path: rel.into(),
                    status: FileStatus::Modified,
                    changes: vec![],
                    chunks: vec![],
                }))
            }
        }
        (false, false) => Ok(None),
    }
}

fn diff_region(a_path: &Path, ba: &[u8], b_path: &Path, bb: &[u8]) -> Result<Vec<ChunkDiff>> {
    let ra = RegionFile::parse(a_path, ba)?;
    let rb = RegionFile::parse(b_path, bb)?;
    let mut positions: BTreeSet<(i32, i32)> = BTreeSet::new();
    for c in ra.chunks() {
        positions.insert((c.pos.x, c.pos.z));
    }
    for c in rb.chunks() {
        positions.insert((c.pos.x, c.pos.z));
    }
    let mut out = Vec::new();
    for (x, z) in positions {
        let pos = ChunkPos::new(x, z);
        match (ra.get(pos), rb.get(pos)) {
            (Some(ca), Some(cb)) => {
                if ca.payload_equals(cb) {
                    continue; // chunk fast path
                }
                let va = codec::decode(ca)?;
                let vb = codec::decode(cb)?;
                let changes = compare_nbt(&va, &vb);
                if !changes.is_empty() {
                    out.push(ChunkDiff {
                        x,
                        z,
                        status: ChunkStatus::Modified,
                        changes,
                    });
                }
            }
            (Some(_), None) => out.push(ChunkDiff {
                x,
                z,
                status: ChunkStatus::Removed,
                changes: vec![],
            }),
            (None, Some(_)) => out.push(ChunkDiff {
                x,
                z,
                status: ChunkStatus::Added,
                changes: vec![],
            }),
            (None, None) => {}
        }
    }
    Ok(out)
}

fn list_files(root: &Path) -> Vec<String> {
    let mut out = Vec::new();
    for e in walkdir_min(root) {
        if let Ok(rel) = e.strip_prefix(root) {
            let s = rel.to_string_lossy().replace('\\', "/");
            if !s.is_empty() && s != "session.lock" {
                out.push(s);
            }
        }
    }
    out
}

// Minimal recursive file lister (no external dep in this crate).
fn walkdir_min(root: &Path) -> Vec<std::path::PathBuf> {
    let mut out = Vec::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for e in entries.flatten() {
            let p = e.path();
            if p.is_dir() {
                stack.push(p);
            } else if p.is_file() {
                out.push(p);
            }
        }
    }
    out
}

fn is_region(rel: &str) -> bool {
    if !rel.to_ascii_lowercase().ends_with(".mca") {
        return false;
    }
    let p = format!("/{rel}");
    p.contains("/region/") || p.contains("/entities/") || p.contains("/poi/")
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_anvil::{codec, ChunkCompression, ChunkPos, RawChunk, RegionWriter};
    use mca_nbt::{Compound, NbtValue};

    fn chunk(x: i32, z: i32, hp: i32) -> RawChunk {
        let mut c = Compound::new();
        c.insert("hp".into(), NbtValue::Int(hp));
        let payload = codec::encode(&NbtValue::Compound(c), ChunkCompression::ZLib).unwrap();
        RawChunk {
            pos: ChunkPos::new(x, z),
            compression: ChunkCompression::ZLib,
            payload,
            external: false,
            timestamp: 0,
        }
    }

    fn world(dir: &Path, hp: i32, with_extra: bool) {
        std::fs::create_dir_all(dir.join("region")).unwrap();
        RegionWriter::write(
            &dir.join("region").join("r.0.0.mca"),
            std::slice::from_ref(&chunk(0, 0, hp)),
        )
        .unwrap();
        std::fs::write(dir.join("icon.png"), b"PNG").unwrap();
        if with_extra {
            std::fs::write(dir.join("extra.txt"), b"hello").unwrap();
        }
    }

    #[test]
    fn detects_chunk_change_and_added_file() {
        let d = tempfile::tempdir().unwrap();
        let a = d.path().join("a");
        let b = d.path().join("b");
        world(&a, 20, false);
        world(&b, 18, true); // chunk hp differs + extra.txt added

        let wd = diff(&a, &b).unwrap();
        assert!(!wd.is_empty());
        let region = wd
            .files
            .iter()
            .find(|f| f.path.contains("r.0.0.mca"))
            .unwrap();
        assert_eq!(region.status, FileStatus::Modified);
        assert_eq!(region.chunks.len(), 1);
        assert_eq!(region.chunks[0].status, ChunkStatus::Modified);
        let extra = wd.files.iter().find(|f| f.path == "extra.txt").unwrap();
        assert_eq!(extra.status, FileStatus::Added);
    }

    #[test]
    fn identical_worlds_have_no_diff() {
        let d = tempfile::tempdir().unwrap();
        let a = d.path().join("a");
        let b = d.path().join("b");
        world(&a, 20, false);
        world(&b, 20, false);
        assert!(diff(&a, &b).unwrap().is_empty());
    }
}
