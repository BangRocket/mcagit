//! Turn a world directory into a [`Manifest`], storing each unique chunk /
//! loose-NBT / file as a content-addressed object. Region (`.mca` under
//! `region/`|`entities/`|`poi/`) → per-chunk canonical NBT objects; `.dat` →
//! canonical NBT object; everything else → raw blob. Parallel over files.

use crate::object_store::ObjectStore;
use crate::pack::PackWriter;
use crate::repository::Repository;
use crate::{Manifest, RepoError, Result};
use mca_anvil::{codec, RegionFile};
use rayon::prelude::*;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

const SKIP_NAMES: &[&str] = &["session.lock"];

/// Where snapshot objects go: streamed into one pack, or just hashed (status).
#[derive(Clone, Copy)]
enum Sink<'a> {
    Pack(&'a ObjectStore, &'a Mutex<PackWriter>),
    HashOnly,
}

/// Snapshot `world_dir`, streaming all new objects into a single packfile.
pub fn snapshot(repo: &Repository, world_dir: &Path) -> Result<Manifest> {
    let pack_dir = repo.objects().pack_dir();
    let writer = Mutex::new(PackWriter::new(&pack_dir)?);
    let manifest = build(
        world_dir,
        Sink::Pack(repo.objects(), &writer),
        Some(repo.dir()),
    )?;
    let writer = writer.into_inner().unwrap();
    if !writer.is_empty() {
        writer.finish(&pack_dir)?;
        repo.objects().reload_packs(); // make the new pack visible in-process
    }
    Ok(manifest)
}

/// Compute the manifest (and object ids) WITHOUT writing — used by status.
pub fn hash_only(repo: &Repository, world_dir: &Path) -> Result<Manifest> {
    build(world_dir, Sink::HashOnly, Some(repo.dir()))
}

enum Kind {
    Region(BTreeMap<String, String>),
    Nbt(String),
    Blob(String),
}
struct Entry {
    rel: String,
    kind: Kind,
}

fn build(world_dir: &Path, sink: Sink, repo_dir: Option<&Path>) -> Result<Manifest> {
    let root = std::fs::canonicalize(world_dir).unwrap_or_else(|_| world_dir.to_path_buf());
    let repo_prefix = repo_dir.and_then(|d| std::fs::canonicalize(d).ok());

    let mut files: Vec<PathBuf> = Vec::new();
    let mut dirs: Vec<PathBuf> = Vec::new();
    for entry in walkdir::WalkDir::new(&root)
        .into_iter()
        .filter_map(|e| e.ok())
    {
        let p = entry.path();
        if let Some(rp) = &repo_prefix {
            if p.starts_with(rp) {
                continue; // never capture the repo's own metadata
            }
        }
        if entry.file_type().is_file() {
            let name = entry.file_name().to_string_lossy();
            if SKIP_NAMES.contains(&name.as_ref()) {
                continue;
            }
            files.push(p.to_path_buf());
        } else if entry.file_type().is_dir() {
            dirs.push(p.to_path_buf());
        }
    }

    let entries: Vec<Entry> = files
        .par_iter()
        .map(|f| classify(f, &root, sink))
        .collect::<Result<Vec<_>>>()?;

    let mut m = Manifest::default();
    for e in entries {
        match e.kind {
            Kind::Region(map) => {
                m.regions.insert(e.rel, map);
            }
            Kind::Nbt(h) => {
                m.nbt.insert(e.rel, h);
            }
            Kind::Blob(h) => {
                m.blobs.insert(e.rel, h);
            }
        }
    }

    for d in dirs {
        let empty = std::fs::read_dir(&d)
            .map(|mut it| it.next().is_none())
            .unwrap_or(false);
        if empty {
            let rel = rel_path(&root, &d);
            if !rel.is_empty() {
                m.empty_dirs.push(rel);
            }
        }
    }
    m.empty_dirs.sort();
    Ok(m)
}

fn rel_path(root: &Path, p: &Path) -> String {
    p.strip_prefix(root)
        .unwrap_or(p)
        .to_string_lossy()
        .replace('\\', "/")
}

fn put(content: &[u8], sink: Sink) -> Result<String> {
    let id = blake3::hash(content).to_hex().to_string();
    if let Sink::Pack(store, writer) = sink {
        if !store.exists(&id) {
            let packed = zstd::encode_all(content, 0)?;
            writer
                .lock()
                .map_err(|_| RepoError::Other("pack writer poisoned".into()))?
                .push_packed(&id, &packed)?;
        }
    }
    Ok(id)
}

fn is_region(rel: &str) -> bool {
    if !rel.to_ascii_lowercase().ends_with(".mca") {
        return false;
    }
    let p = format!("/{rel}");
    p.contains("/region/") || p.contains("/entities/") || p.contains("/poi/")
}

fn classify(path: &Path, root: &Path, sink: Sink) -> Result<Entry> {
    let rel = rel_path(root, path);

    if is_region(&rel) {
        if let Some(map) = try_chunks(path, sink)? {
            return Ok(Entry {
                rel,
                kind: Kind::Region(map),
            });
        }
    }
    if rel.to_ascii_lowercase().ends_with(".dat") {
        if let Some(h) = try_nbt(path, sink)? {
            return Ok(Entry {
                rel,
                kind: Kind::Nbt(h),
            });
        }
    }
    let bytes = std::fs::read(path)?;
    let h = put(&bytes, sink)?;
    Ok(Entry {
        rel,
        kind: Kind::Blob(h),
    })
}

/// Parse a region into per-chunk canonical objects. `Ok(None)` means "not a
/// decodable region" → caller stores it as a raw blob.
fn try_chunks(
    path: &Path,
    sink: Sink,
) -> Result<Option<BTreeMap<String, String>>> {
    let region = match RegionFile::open(path) {
        Ok(r) => r,
        Err(_) => return Ok(None),
    };
    let mut map = BTreeMap::new();
    for rc in region.chunks() {
        let value = match codec::decode(rc) {
            Ok(v) => v,
            Err(_) => return Ok(None), // undecodable chunk (e.g. Custom) → blob fallback
        };
        let canon = mca_nbt::canonical_bytes(&value);
        let id = put(&canon, sink)?;
        map.insert(format!("{},{}", rc.pos.x, rc.pos.z), id);
    }
    Ok(Some(map))
}

fn try_nbt(path: &Path, sink: Sink) -> Result<Option<String>> {
    match codec::load_nbt_file(path) {
        Ok(v) => Ok(Some(put(&mca_nbt::canonical_bytes(&v), sink)?)),
        Err(_) => Ok(None),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_anvil::{codec, ChunkCompression, ChunkPos, RawChunk, RegionWriter};
    use mca_nbt::{Compound, NbtValue};

    fn build_world(world: &Path) {
        std::fs::create_dir_all(world.join("region")).unwrap();
        let mut c = Compound::new();
        c.insert("Status".into(), NbtValue::String("full".into()));
        let payload = codec::encode(&NbtValue::Compound(c), ChunkCompression::ZLib).unwrap();
        let chunk = RawChunk {
            pos: ChunkPos::new(0, 0),
            compression: ChunkCompression::ZLib,
            payload,
            external: false,
            timestamp: 0,
        };
        RegionWriter::write(
            &world.join("region").join("r.0.0.mca"),
            std::slice::from_ref(&chunk),
        )
        .unwrap();

        let mut lvl = Compound::new();
        lvl.insert("Version".into(), NbtValue::Int(3));
        codec::save_nbt_file(
            &world.join("level.dat"),
            &NbtValue::Compound(lvl),
            ChunkCompression::GZip,
        )
        .unwrap();

        std::fs::write(world.join("icon.png"), b"PNGDATA").unwrap();
        std::fs::create_dir_all(world.join("playerdata")).unwrap();
    }

    #[test]
    fn snapshots_a_synthetic_world_deterministically() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        build_world(&world);

        let m = snapshot(&repo, &world).unwrap();
        assert_eq!(m.regions["region/r.0.0.mca"].len(), 1);
        assert!(m.regions["region/r.0.0.mca"].contains_key("0,0"));
        assert!(m.nbt.contains_key("level.dat"));
        assert!(m.blobs.contains_key("icon.png"));
        assert!(m.empty_dirs.contains(&"playerdata".to_string()));

        // Re-snapshotting the same world yields an identical manifest (determinism).
        let m2 = snapshot(&repo, &world).unwrap();
        assert_eq!(m, m2);
    }
}
