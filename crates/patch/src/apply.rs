//! Apply a [`WorldPatch`] to a base world, producing a fresh output world.
//! Never mutates the input: it copies the base to `out_dir` then edits the copy.
//! 3-way guarded — a node is changed only if it matches the op's `from` value,
//! else it is reported as a conflict and left unchanged (unless `force`).

use crate::model::{PatchFileEntry, PatchOp, Status, WorldPatch};
use crate::{PatchError, Result};
use base64::Engine;
use mca_anvil::{codec, ChunkCompression, ChunkPos, RawChunk, RegionFile, RegionWriter};
use mca_nbt::{from_json, NbtPath, NbtValue};
use std::collections::BTreeMap;
use std::path::Path;

const B64: base64::engine::general_purpose::GeneralPurpose =
    base64::engine::general_purpose::STANDARD;

#[derive(Debug, Default)]
pub struct ApplyReport {
    pub applied: usize,
    pub conflicts: Vec<String>,
}

/// Apply `patch` to `base_world`, writing the result to `out_dir`. `reverse`
/// applies the inverse (target→base). Returns conflicts (none = clean apply).
pub fn apply(
    patch: &WorldPatch,
    base_world: &Path,
    out_dir: &Path,
    reverse: bool,
    force: bool,
) -> Result<ApplyReport> {
    if patch.version != 1 {
        return Err(PatchError::Version(patch.version));
    }
    copy_world(base_world, out_dir)?;
    let mut report = ApplyReport::default();
    for entry in &patch.files {
        apply_entry(entry, out_dir, reverse, force, &mut report)?;
    }
    Ok(report)
}

fn flip(s: Status) -> Status {
    match s {
        Status::Added => Status::Removed,
        Status::Removed => Status::Added,
        Status::Modified => Status::Modified,
    }
}

fn apply_entry(
    entry: &PatchFileEntry,
    out_dir: &Path,
    reverse: bool,
    force: bool,
    report: &mut ApplyReport,
) -> Result<()> {
    let eff = if reverse {
        flip(entry.status)
    } else {
        entry.status
    };
    let path = out_dir.join(&entry.path);
    match entry.kind {
        crate::model::EntryKind::Region => {
            if eff == Status::Removed {
                let _ = std::fs::remove_file(&path);
                return Ok(());
            }
            let mut map: BTreeMap<(i32, i32), NbtValue> = BTreeMap::new();
            if path.is_file() {
                let bytes = std::fs::read(&path)?;
                let rf = RegionFile::parse(&path, &bytes)?;
                for c in rf.chunks() {
                    map.insert((c.pos.x, c.pos.z), codec::decode(c)?);
                }
            }
            for cp in entry.chunks.as_deref().unwrap_or(&[]) {
                let mut slot = map.remove(&(cp.x, cp.z));
                let ctx = format!("{} chunk {},{}", entry.path, cp.x, cp.z);
                for op in &cp.ops {
                    apply_op(&mut slot, op, reverse, force, report, &ctx)?;
                }
                if let Some(v) = slot {
                    map.insert((cp.x, cp.z), v);
                }
            }
            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            let raws: Vec<RawChunk> = map
                .iter()
                .map(|((x, z), v)| {
                    Ok(RawChunk {
                        pos: ChunkPos::new(*x, *z),
                        compression: ChunkCompression::ZLib,
                        payload: codec::encode(v, ChunkCompression::ZLib)?,
                        external: false,
                        timestamp: 0,
                    })
                })
                .collect::<Result<Vec<_>>>()?;
            RegionWriter::write(&path, &raws)?;
        }
        crate::model::EntryKind::Nbt => {
            if eff == Status::Removed {
                let _ = std::fs::remove_file(&path);
                return Ok(());
            }
            let mut slot = if path.is_file() {
                codec::load_nbt_file(&path).ok()
            } else {
                None
            };
            for op in entry.ops.as_deref().unwrap_or(&[]) {
                apply_op(&mut slot, op, reverse, force, report, &entry.path)?;
            }
            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            match slot {
                Some(v) => codec::save_nbt_file(&path, &v, ChunkCompression::GZip)?,
                None => {
                    let _ = std::fs::remove_file(&path);
                }
            }
        }
        crate::model::EntryKind::Blob => {
            let (from_b, to_b) = if reverse {
                (entry.value_blob.as_ref(), entry.base_blob.as_ref())
            } else {
                (entry.base_blob.as_ref(), entry.value_blob.as_ref())
            };
            let from_bytes = from_b.map(|s| B64.decode(s)).transpose()?;
            let to_bytes = to_b.map(|s| B64.decode(s)).transpose()?;
            let current = if path.is_file() {
                Some(std::fs::read(&path)?)
            } else {
                None
            };
            if current.as_deref() != from_bytes.as_deref() && !force {
                report.conflicts.push(format!("{} (blob)", entry.path));
                return Ok(());
            }
            match to_bytes {
                Some(b) => {
                    if let Some(parent) = path.parent() {
                        std::fs::create_dir_all(parent)?;
                    }
                    std::fs::write(&path, b)?;
                }
                None => {
                    let _ = std::fs::remove_file(&path);
                }
            }
            report.applied += 1;
        }
    }
    Ok(())
}

/// Apply one op to an optional NBT slot (the chunk/loose-file root). A `path` of
/// "" addresses the whole slot.
fn apply_op(
    slot: &mut Option<NbtValue>,
    op: &PatchOp,
    reverse: bool,
    force: bool,
    report: &mut ApplyReport,
    ctx: &str,
) -> Result<()> {
    let (from, to) = if reverse {
        (&op.value, &op.base)
    } else {
        (&op.base, &op.value)
    };
    let from_v = from.as_ref().map(from_json).transpose()?;
    let to_v = to.as_ref().map(from_json).transpose()?;

    if op.path.is_empty() {
        if slot.as_ref() != from_v.as_ref() && !force {
            report.conflicts.push(format!("{ctx}: root"));
            return Ok(());
        }
        *slot = to_v;
        report.applied += 1;
        return Ok(());
    }

    let Some(root) = slot.as_mut() else {
        if !force {
            report
                .conflicts
                .push(format!("{ctx}: {} (no root)", op.path));
        }
        return Ok(());
    };
    let path = NbtPath::parse(&op.path)?;
    let current = path.get(root).cloned();
    if current.as_ref() != from_v.as_ref() && !force {
        report.conflicts.push(format!("{ctx}: {}", op.path));
        return Ok(());
    }
    match to_v {
        Some(v) => {
            path.set(root, v);
        }
        None => {
            path.remove(root);
        }
    }
    report.applied += 1;
    Ok(())
}

fn copy_world(src: &Path, dst: &Path) -> Result<()> {
    let mut stack = vec![src.to_path_buf()];
    while let Some(dir) = stack.pop() {
        for e in std::fs::read_dir(&dir)?.flatten() {
            let p = e.path();
            if p.is_dir() {
                stack.push(p);
            } else if p.is_file() {
                if let Ok(rel) = p.strip_prefix(src) {
                    let to = dst.join(rel);
                    if let Some(parent) = to.parent() {
                        std::fs::create_dir_all(parent)?;
                    }
                    // Reflink (copy-on-write clone) when the filesystem supports
                    // it — near-instant and zero extra space for the unchanged
                    // bulk of a world; patched files are rewritten wholesale,
                    // which breaks the share only for them. Falls back to a
                    // byte copy on filesystems/targets without reflink.
                    // reflink_or_copy uses create-new semantics, so clear any
                    // existing target first to preserve fs::copy's overwrite
                    // (apply into a pre-existing output dir must keep working).
                    let _ = std::fs::remove_file(&to);
                    reflink_copy::reflink_or_copy(&p, &to)?;
                }
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::extract::extract;
    use mca_anvil::{ChunkCompression, RawChunk, RegionWriter};
    use mca_nbt::{Compound, NbtValue};

    fn world(dir: &Path, hp: i32, extra: bool, old: bool) {
        std::fs::create_dir_all(dir.join("region")).unwrap();
        let mut c = Compound::new();
        c.insert("hp".into(), NbtValue::Int(hp));
        let chunk = RawChunk {
            pos: ChunkPos::new(0, 0),
            compression: ChunkCompression::ZLib,
            payload: codec::encode(&NbtValue::Compound(c), ChunkCompression::ZLib).unwrap(),
            external: false,
            timestamp: 0,
        };
        RegionWriter::write(
            &dir.join("region").join("r.0.0.mca"),
            std::slice::from_ref(&chunk),
        )
        .unwrap();
        std::fs::write(dir.join("icon.png"), b"PNG").unwrap();
        if extra {
            std::fs::write(dir.join("extra.txt"), b"hello").unwrap();
        }
        if old {
            std::fs::write(dir.join("old.txt"), b"bye").unwrap();
        }
    }

    #[test]
    fn forward_roundtrip_and_reverse() {
        let d = tempfile::tempdir().unwrap();
        let a = d.path().join("a");
        let b = d.path().join("b");
        world(&a, 20, false, true); // A: hp 20, old.txt, no extra
        world(&b, 18, true, false); // B: hp 18, extra.txt, no old.txt

        let patch = extract(&a, &b).unwrap();

        // forward: apply to A == B
        let fwd = d.path().join("fwd");
        let r = apply(&patch, &a, &fwd, false, false).unwrap();
        assert!(r.conflicts.is_empty(), "conflicts: {:?}", r.conflicts);
        assert!(mca_diff::world::diff(&fwd, &b).unwrap().is_empty());

        // reverse: apply to B == A
        let rev = d.path().join("rev");
        let r = apply(&patch, &b, &rev, true, false).unwrap();
        assert!(r.conflicts.is_empty(), "conflicts: {:?}", r.conflicts);
        assert!(mca_diff::world::diff(&rev, &a).unwrap().is_empty());
    }

    #[test]
    fn copy_world_reproduces_nested_tree_byte_for_byte() {
        // copy_world is the base→output copy that reflinks when the filesystem
        // supports it and falls back to a byte copy otherwise; either way the
        // output must be identical, including nested dirs and a 0-byte file.
        let d = tempfile::tempdir().unwrap();
        let src = d.path().join("src");
        std::fs::create_dir_all(src.join("region")).unwrap();
        std::fs::create_dir_all(src.join("data/nested")).unwrap();
        std::fs::write(src.join("level.dat"), b"LEVEL").unwrap();
        std::fs::write(src.join("region/r.0.0.mca"), vec![0xABu8; 9000]).unwrap();
        std::fs::write(src.join("data/nested/deep.bin"), b"deep").unwrap();
        std::fs::write(src.join("data/empty"), b"").unwrap();

        let dst = d.path().join("dst");
        copy_world(&src, &dst).unwrap();

        for rel in [
            "level.dat",
            "region/r.0.0.mca",
            "data/nested/deep.bin",
            "data/empty",
        ] {
            assert_eq!(
                std::fs::read(src.join(rel)).unwrap(),
                std::fs::read(dst.join(rel)).unwrap(),
                "{rel} must copy identically"
            );
        }
    }

    #[test]
    fn copy_world_overwrites_existing_target() {
        // reflink_or_copy refuses to clobber an existing file; copy_world must
        // still overwrite (the old fs::copy did), so apply into a pre-existing
        // output dir keeps working.
        let d = tempfile::tempdir().unwrap();
        let src = d.path().join("src");
        std::fs::create_dir_all(&src).unwrap();
        std::fs::write(src.join("level.dat"), b"NEW").unwrap();

        let dst = d.path().join("dst");
        std::fs::create_dir_all(&dst).unwrap();
        std::fs::write(dst.join("level.dat"), b"STALE-AND-LONGER").unwrap();

        copy_world(&src, &dst).unwrap();
        assert_eq!(std::fs::read(dst.join("level.dat")).unwrap(), b"NEW");
    }

    #[test]
    fn conflict_when_base_mismatch() {
        let d = tempfile::tempdir().unwrap();
        let a = d.path().join("a");
        let b = d.path().join("b");
        world(&a, 20, false, false);
        world(&b, 18, false, false);
        let patch = extract(&a, &b).unwrap();

        // apply to a world whose chunk doesn't match the patch base (hp 99)
        let c = d.path().join("c");
        world(&c, 99, false, false);
        let out = d.path().join("out");
        let r = apply(&patch, &c, &out, false, false).unwrap();
        assert!(!r.conflicts.is_empty());
    }
}
