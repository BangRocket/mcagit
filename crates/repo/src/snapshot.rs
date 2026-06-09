//! Turn a world directory into a [`Manifest`], storing each unique chunk /
//! loose-NBT / file as a content-addressed object. Region (`.mca` under
//! `region/`|`entities/`|`poi/`) → per-chunk canonical NBT objects; `.dat` →
//! canonical NBT object; everything else → raw blob. Parallel over files.

use crate::chunk_cache::ChunkCache;
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
/// A persistent compressed-payload → object-id cache skips decoding chunks
/// whose raw bytes are unchanged (the common incremental-backup case).
pub fn snapshot(repo: &Repository, world_dir: &Path) -> Result<Manifest> {
    let pack_dir = repo.objects().pack_dir();
    let writer = Mutex::new(PackWriter::new(&pack_dir)?);
    let cache = ChunkCache::load(repo.dir());
    let manifest = build(
        world_dir,
        Sink::Pack(repo.objects(), &writer),
        Some(repo.dir()),
        Some(&cache),
    )?;
    let writer = writer.into_inner().unwrap();
    if !writer.is_empty() {
        writer.finish(&pack_dir)?;
        repo.objects().reload_packs(); // make the new pack visible in-process
    }
    cache.save()?;
    Ok(manifest)
}

/// Compute the manifest (and object ids) WITHOUT writing — used by status.
/// Reads the chunk cache (ids are content-derived either way) but never
/// persists it: status must not write repo state.
pub fn hash_only(repo: &Repository, world_dir: &Path) -> Result<Manifest> {
    let cache = ChunkCache::load(repo.dir());
    build(world_dir, Sink::HashOnly, Some(repo.dir()), Some(&cache))
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

fn build(
    world_dir: &Path,
    sink: Sink,
    repo_dir: Option<&Path>,
    cache: Option<&ChunkCache>,
) -> Result<Manifest> {
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
        .map(|f| classify(f, &root, sink, cache))
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

fn classify(path: &Path, root: &Path, sink: Sink, cache: Option<&ChunkCache>) -> Result<Entry> {
    let rel = rel_path(root, path);

    if is_region(&rel) {
        if let Some(map) = try_chunks(path, sink, cache)? {
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
    cache: Option<&ChunkCache>,
) -> Result<Option<BTreeMap<String, String>>> {
    let region = match RegionFile::open(path) {
        Ok(r) => r,
        Err(_) => return Ok(None),
    };
    let mut map = BTreeMap::new();
    for rc in region.chunks() {
        let key = cache.map(|_| ChunkCache::key(rc.compression.to_byte(), &rc.payload));
        // Unchanged raw bytes → reuse the cached object id without decoding.
        // When committing, only trust a hit whose object actually exists.
        if let (Some(cache), Some(key)) = (cache, key.as_deref()) {
            if let Some(id) = cache.get(key) {
                let trusted = match sink {
                    Sink::Pack(store, _) => store.exists(&id),
                    Sink::HashOnly => true, // ids are content-derived
                };
                if trusted {
                    map.insert(format!("{},{}", rc.pos.x, rc.pos.z), id);
                    continue;
                }
            }
        }
        let value = match codec::decode(rc) {
            Ok(v) => v,
            Err(_) => return Ok(None), // undecodable chunk (e.g. Custom) → blob fallback
        };
        let canon = mca_nbt::canonical_bytes(&value);
        let id = put(&canon, sink)?;
        if let (Some(cache), Some(key)) = (cache, key) {
            cache.set(key, id.clone());
        }
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

    #[test]
    fn chunk_cache_fast_path_is_used_and_persisted() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        build_world(&world);

        let m1 = snapshot(&repo, &world).unwrap();
        let cache_file = repo.dir().join("chunkcache.json");
        assert!(cache_file.is_file(), "snapshot persists the chunk cache");

        // Re-snapshot is identical (now served from the cache fast path).
        assert_eq!(snapshot(&repo, &world).unwrap(), m1);

        // Prove the fast path actually fires: poison the cached id for the
        // chunk with another existing object and watch the manifest use it.
        let decoy = repo.objects().write(b"decoy object").unwrap();
        let text = std::fs::read_to_string(&cache_file).unwrap();
        let mut map: std::collections::HashMap<String, String> =
            serde_json::from_str(&text).unwrap();
        assert_eq!(map.len(), 1, "one cached chunk");
        for v in map.values_mut() {
            *v = decoy.clone();
        }
        std::fs::write(&cache_file, serde_json::to_vec(&map).unwrap()).unwrap();
        let m3 = snapshot(&repo, &world).unwrap();
        assert_eq!(
            m3.regions["region/r.0.0.mca"]["0,0"], decoy,
            "cache hit must skip the decode path"
        );

        // A hit whose object does NOT exist is not trusted: falls back to
        // decoding and self-heals to the true id.
        let mut map2 = map.clone();
        for v in map2.values_mut() {
            *v = "f".repeat(64);
        }
        std::fs::write(&cache_file, serde_json::to_vec(&map2).unwrap()).unwrap();
        let m4 = snapshot(&repo, &world).unwrap();
        assert_eq!(m4, m1, "missing object → decode fallback");
    }
}
