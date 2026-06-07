//! Extract a diff between two worlds into a portable [`WorldPatch`].

use crate::model::{ChunkPatch, EntryKind, PatchFileEntry, PatchOp, Status, WorldPatch};
use crate::op_sink::PatchOpSink;
use crate::Result;
use base64::Engine;
use mca_anvil::{codec, ChunkPos, RegionFile};
use mca_diff::walk;
use mca_nbt::{to_json, NbtValue};
use std::collections::BTreeSet;
use std::path::Path;

const B64: base64::engine::general_purpose::GeneralPurpose = base64::engine::general_purpose::STANDARD;

/// Extract the changes turning `world_a` into `world_b`.
pub fn extract(world_a: &Path, world_b: &Path) -> Result<WorldPatch> {
    let mut patch = WorldPatch::new();
    patch.base = Some(world_a.display().to_string());
    patch.target = Some(world_b.display().to_string());

    let mut rels: BTreeSet<String> = BTreeSet::new();
    rels.extend(list_files(world_a));
    rels.extend(list_files(world_b));
    for rel in rels {
        if let Some(entry) = extract_one(&rel, world_a, world_b)? {
            patch.files.push(entry);
        }
    }
    Ok(patch)
}

fn extract_one(rel: &str, a_root: &Path, b_root: &Path) -> Result<Option<PatchFileEntry>> {
    let pa = a_root.join(rel);
    let pb = b_root.join(rel);
    let kind = classify(rel);
    match (pa.is_file(), pb.is_file()) {
        (true, true) => {
            let ba = std::fs::read(&pa)?;
            let bb = std::fs::read(&pb)?;
            if ba == bb {
                return Ok(None);
            }
            match kind {
                EntryKind::Region => {
                    let chunks = region_patches(&pa, &ba, &pb, &bb)?;
                    if chunks.is_empty() {
                        Ok(None)
                    } else {
                        Ok(Some(region_entry(rel, Status::Modified, chunks)))
                    }
                }
                EntryKind::Nbt => match (codec::load_nbt_file(&pa), codec::load_nbt_file(&pb)) {
                    (Ok(va), Ok(vb)) => {
                        let ops = node_ops(&va, &vb);
                        if ops.is_empty() {
                            Ok(None)
                        } else {
                            Ok(Some(nbt_entry(rel, Status::Modified, ops)))
                        }
                    }
                    _ => Ok(Some(blob_entry(rel, Status::Modified, Some(&ba), Some(&bb)))),
                },
                EntryKind::Blob => Ok(Some(blob_entry(rel, Status::Modified, Some(&ba), Some(&bb)))),
            }
        }
        (true, false) => Ok(Some(removed_entry(rel, kind, &pa)?)),
        (false, true) => Ok(Some(added_entry(rel, kind, &pb)?)),
        (false, false) => Ok(None),
    }
}

fn region_patches(a_path: &Path, ba: &[u8], b_path: &Path, bb: &[u8]) -> Result<Vec<ChunkPatch>> {
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
                    continue;
                }
                let ops = node_ops(&codec::decode(ca)?, &codec::decode(cb)?);
                if !ops.is_empty() {
                    out.push(ChunkPatch {
                        x,
                        z,
                        status: Status::Modified,
                        ops,
                    });
                }
            }
            (Some(ca), None) => out.push(ChunkPatch {
                x,
                z,
                status: Status::Removed,
                ops: vec![whole_remove(&codec::decode(ca)?)],
            }),
            (None, Some(cb)) => out.push(ChunkPatch {
                x,
                z,
                status: Status::Added,
                ops: vec![whole_add(&codec::decode(cb)?)],
            }),
            (None, None) => {}
        }
    }
    Ok(out)
}

fn removed_entry(rel: &str, kind: EntryKind, pa: &Path) -> Result<PatchFileEntry> {
    Ok(match kind {
        EntryKind::Region => {
            let ba = std::fs::read(pa)?;
            let ra = RegionFile::parse(pa, &ba)?;
            let mut chunks = Vec::new();
            for c in ra.chunks() {
                chunks.push(ChunkPatch {
                    x: c.pos.x,
                    z: c.pos.z,
                    status: Status::Removed,
                    ops: vec![whole_remove(&codec::decode(c)?)],
                });
            }
            region_entry(rel, Status::Removed, chunks)
        }
        EntryKind::Nbt => match codec::load_nbt_file(pa) {
            Ok(v) => nbt_entry(rel, Status::Removed, vec![whole_remove(&v)]),
            Err(_) => blob_entry(rel, Status::Removed, Some(&std::fs::read(pa)?), None),
        },
        EntryKind::Blob => blob_entry(rel, Status::Removed, Some(&std::fs::read(pa)?), None),
    })
}

fn added_entry(rel: &str, kind: EntryKind, pb: &Path) -> Result<PatchFileEntry> {
    Ok(match kind {
        EntryKind::Region => {
            let bb = std::fs::read(pb)?;
            let rb = RegionFile::parse(pb, &bb)?;
            let mut chunks = Vec::new();
            for c in rb.chunks() {
                chunks.push(ChunkPatch {
                    x: c.pos.x,
                    z: c.pos.z,
                    status: Status::Added,
                    ops: vec![whole_add(&codec::decode(c)?)],
                });
            }
            region_entry(rel, Status::Added, chunks)
        }
        EntryKind::Nbt => match codec::load_nbt_file(pb) {
            Ok(v) => nbt_entry(rel, Status::Added, vec![whole_add(&v)]),
            Err(_) => blob_entry(rel, Status::Added, None, Some(&std::fs::read(pb)?)),
        },
        EntryKind::Blob => blob_entry(rel, Status::Added, None, Some(&std::fs::read(pb)?)),
    })
}

fn node_ops(a: &NbtValue, b: &NbtValue) -> Vec<PatchOp> {
    let mut sink = PatchOpSink::default();
    walk(a, b, &mut sink);
    sink.ops
}

fn whole_add(v: &NbtValue) -> PatchOp {
    PatchOp {
        path: String::new(),
        base: None,
        value: Some(to_json(v)),
    }
}
fn whole_remove(v: &NbtValue) -> PatchOp {
    PatchOp {
        path: String::new(),
        base: Some(to_json(v)),
        value: None,
    }
}

fn region_entry(rel: &str, status: Status, chunks: Vec<ChunkPatch>) -> PatchFileEntry {
    PatchFileEntry {
        path: rel.to_string(),
        kind: EntryKind::Region,
        status,
        ops: None,
        chunks: Some(chunks),
        base_blob: None,
        value_blob: None,
    }
}
fn nbt_entry(rel: &str, status: Status, ops: Vec<PatchOp>) -> PatchFileEntry {
    PatchFileEntry {
        path: rel.to_string(),
        kind: EntryKind::Nbt,
        status,
        ops: Some(ops),
        chunks: None,
        base_blob: None,
        value_blob: None,
    }
}
fn blob_entry(rel: &str, status: Status, base: Option<&[u8]>, value: Option<&[u8]>) -> PatchFileEntry {
    PatchFileEntry {
        path: rel.to_string(),
        kind: EntryKind::Blob,
        status,
        ops: None,
        chunks: None,
        base_blob: base.map(|b| B64.encode(b)),
        value_blob: value.map(|b| B64.encode(b)),
    }
}

fn classify(rel: &str) -> EntryKind {
    if is_region(rel) {
        EntryKind::Region
    } else if rel.to_ascii_lowercase().ends_with(".dat") {
        EntryKind::Nbt
    } else {
        EntryKind::Blob
    }
}

fn is_region(rel: &str) -> bool {
    if !rel.to_ascii_lowercase().ends_with(".mca") {
        return false;
    }
    let p = format!("/{rel}");
    p.contains("/region/") || p.contains("/entities/") || p.contains("/poi/")
}

fn list_files(root: &Path) -> Vec<String> {
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
                if let Ok(rel) = p.strip_prefix(root) {
                    let s = rel.to_string_lossy().replace('\\', "/");
                    if !s.is_empty() && s != "session.lock" {
                        out.push(s);
                    }
                }
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_anvil::{ChunkCompression, RawChunk, RegionWriter};
    use mca_nbt::{Compound, NbtValue};

    fn chunk(x: i32, z: i32, hp: i32) -> RawChunk {
        let mut c = Compound::new();
        c.insert("hp".into(), NbtValue::Int(hp));
        RawChunk {
            pos: ChunkPos::new(x, z),
            compression: ChunkCompression::ZLib,
            payload: codec::encode(&NbtValue::Compound(c), ChunkCompression::ZLib).unwrap(),
            external: false,
            timestamp: 0,
        }
    }

    fn world(dir: &Path, hp: i32, extra: bool) {
        std::fs::create_dir_all(dir.join("region")).unwrap();
        RegionWriter::write(
            &dir.join("region").join("r.0.0.mca"),
            std::slice::from_ref(&chunk(0, 0, hp)),
        )
        .unwrap();
        std::fs::write(dir.join("icon.png"), b"PNG").unwrap();
        if extra {
            std::fs::write(dir.join("extra.txt"), b"hello").unwrap();
        }
    }

    #[test]
    fn extract_modified_chunk_and_added_file() {
        let d = tempfile::tempdir().unwrap();
        let a = d.path().join("a");
        let b = d.path().join("b");
        world(&a, 20, false);
        world(&b, 18, true);

        let p = extract(&a, &b).unwrap();
        let region = p.files.iter().find(|f| f.path.contains("r.0.0.mca")).unwrap();
        assert_eq!(region.kind, EntryKind::Region);
        assert_eq!(region.status, Status::Modified);
        assert_eq!(region.chunks.as_ref().unwrap()[0].ops[0].path, "hp");
        let extra = p.files.iter().find(|f| f.path == "extra.txt").unwrap();
        assert_eq!(extra.status, Status::Added);
        assert!(extra.value_blob.is_some());
    }
}
