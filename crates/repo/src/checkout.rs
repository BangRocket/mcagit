//! Materialize a [`Manifest`] back into a playable world directory.
//!
//! Regions are written **in parallel** (rayon) — each region is an independent
//! file, so there is no shared mutable state. This is the loop the .NET version
//! left serial and the headline speed win of the port.

use crate::repository::Repository;
use crate::{Manifest, RepoError, Result};
use mca_anvil::{codec, ChunkCompression, ChunkPos, RawChunk, RegionWriter};
use rayon::prelude::*;
use std::collections::BTreeMap;
use std::collections::HashSet;
use std::path::{Component, Path, PathBuf};

/// Materialize `manifest` into `out_dir`. When `prune`, tracked files in
/// `out_dir` that are absent from the manifest are removed.
pub fn checkout(repo: &Repository, manifest: &Manifest, out_dir: &Path, prune: bool) -> Result<()> {
    std::fs::create_dir_all(out_dir)?;
    let store = repo.objects();

    // Regions, in parallel (the .NET serial bottleneck).
    let regions: Vec<(&String, &BTreeMap<String, String>)> = manifest.regions.iter().collect();

    // Flatten every chunk across every region into one job list, so a single
    // rayon pass splits the work perfectly across cores (no nested/BTreeMap
    // iteration). The stored object IS the chunk's canonical NBT bytes, so we
    // re-compress it directly — no NBT parse/reserialize round-trip.
    let jobs: Vec<(usize, &String, &String)> = regions
        .iter()
        .enumerate()
        .flat_map(|(ri, (_rel, chunks))| chunks.iter().map(move |(pos, id)| (ri, pos, id)))
        .collect();
    let built: Vec<(usize, RawChunk)> = jobs
        .par_iter()
        .map(|&(ri, pos, id)| {
            let canon = store.read(id)?;
            let payload = mca_anvil::compression::compress(ChunkCompression::ZLib, &canon)?;
            Ok::<(usize, RawChunk), RepoError>((
                ri,
                RawChunk {
                    pos: parse_pos(pos)?,
                    compression: ChunkCompression::ZLib,
                    payload,
                    external: false,
                    timestamp: 0,
                },
            ))
        })
        .collect::<Result<Vec<_>>>()?;

    // Group by region, then write region files in parallel.
    let mut by_region: Vec<Vec<RawChunk>> = (0..regions.len()).map(|_| Vec::new()).collect();
    for (ri, rc) in built {
        by_region[ri].push(rc);
    }
    by_region
        .into_par_iter()
        .enumerate()
        .try_for_each(|(ri, raws)| -> Result<()> {
            let path = confine(out_dir, regions[ri].0)?;
            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            RegionWriter::write(&path, &raws)?;
            Ok(())
        })?;

    // Loose NBT files (re-saved as gzip, like Minecraft).
    for (rel, id) in &manifest.nbt {
        let path = confine(out_dir, rel)?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let (_n, value) = mca_nbt::read(&store.read(id)?)?;
        codec::save_nbt_file(&path, &value, ChunkCompression::GZip)?;
    }

    // Raw blobs.
    for (rel, id) in &manifest.blobs {
        let path = confine(out_dir, rel)?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(&path, store.read(id)?)?;
    }

    // Empty directories.
    for rel in &manifest.empty_dirs {
        std::fs::create_dir_all(confine(out_dir, rel)?)?;
    }

    if prune {
        prune_extra(out_dir, manifest, Some(repo.dir()))?;
    }
    Ok(())
}

/// Join `rel` under `base`, rejecting any `..`/root component so a hostile
/// manifest can't escape the output directory.
fn confine(base: &Path, rel: &str) -> Result<PathBuf> {
    let mut p = base.to_path_buf();
    for comp in Path::new(rel).components() {
        match comp {
            Component::Normal(c) => p.push(c),
            Component::CurDir => {}
            _ => return Err(RepoError::Other(format!("unsafe path in manifest: {rel}"))),
        }
    }
    Ok(p)
}

fn parse_pos(s: &str) -> Result<ChunkPos> {
    let (x, z) = s
        .split_once(',')
        .ok_or_else(|| RepoError::Other(format!("bad chunk pos {s:?}")))?;
    let x = x
        .parse()
        .map_err(|_| RepoError::Other(format!("bad chunk pos {s:?}")))?;
    let z = z
        .parse()
        .map_err(|_| RepoError::Other(format!("bad chunk pos {s:?}")))?;
    Ok(ChunkPos::new(x, z))
}

fn prune_extra(out_dir: &Path, m: &Manifest, repo_dir: Option<&Path>) -> Result<()> {
    let mut keep: HashSet<&String> = HashSet::new();
    keep.extend(m.regions.keys());
    keep.extend(m.nbt.keys());
    keep.extend(m.blobs.keys());
    let root = std::fs::canonicalize(out_dir).unwrap_or_else(|_| out_dir.to_path_buf());
    // Never delete the repo's own metadata when it lives inside `out_dir`
    // (embedded `.mcagit/` layout).
    let repo_prefix =
        repo_dir.map(|d| std::fs::canonicalize(d).unwrap_or_else(|_| d.to_path_buf()));
    for entry in walkdir::WalkDir::new(&root)
        .into_iter()
        .filter_map(|e| e.ok())
    {
        if let Some(rp) = &repo_prefix {
            if entry.path().starts_with(rp) {
                continue;
            }
        }
        if entry.file_type().is_file() {
            let rel = entry
                .path()
                .strip_prefix(&root)
                .unwrap_or(entry.path())
                .to_string_lossy()
                .replace('\\', "/");
            if !keep.contains(&rel) {
                let _ = std::fs::remove_file(entry.path());
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::snapshot;
    use mca_anvil::{codec, ChunkCompression, ChunkPos, RawChunk, RegionWriter};
    use mca_nbt::{Compound, NbtValue};

    fn build_world(world: &Path) {
        std::fs::create_dir_all(world.join("region")).unwrap();
        let mut c = Compound::new();
        c.insert("Status".into(), NbtValue::String("full".into()));
        c.insert("Y".into(), NbtValue::Byte(-4));
        let payload = codec::encode(&NbtValue::Compound(c), ChunkCompression::ZLib).unwrap();
        let chunk = RawChunk {
            pos: ChunkPos::new(3, -7),
            compression: ChunkCompression::ZLib,
            payload,
            external: false,
            timestamp: 0,
        };
        RegionWriter::write(
            &world.join("region").join("r.0.-1.mca"),
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
    fn checkout_reproduces_world_at_canonical_level() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        build_world(&world);

        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();

        let out = d.path().join("out");
        checkout(&repo, &m, &out, false).unwrap();

        // Re-snapshotting the checkout must yield the identical tree.
        let m2 = snapshot::snapshot(&repo, &out).unwrap();
        let tree2 = repo.write_manifest(&m2).unwrap();
        assert_eq!(tree, tree2);
    }

    #[test]
    fn confine_rejects_traversal() {
        let base = Path::new("/tmp/out");
        assert!(confine(base, "../escape").is_err());
        assert!(confine(base, "region/r.0.0.mca").is_ok());
    }

    #[test]
    fn prune_preserves_embedded_repo_dir() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        // embedded repo: metadata at world/.mcagit, worktree = world
        let repo = Repository::init_embedded(&world).unwrap();
        std::fs::write(world.join("keep.bin"), b"keep").unwrap();

        // snapshot excludes .mcagit, so the manifest holds only keep.bin
        let m = snapshot::snapshot(&repo, &world).unwrap();

        // an untracked extra file that prune SHOULD remove
        std::fs::write(world.join("extra.bin"), b"extra").unwrap();

        // checkout with prune INTO the worktree (the dangerous case)
        checkout(&repo, &m, &world, true).unwrap();

        assert!(
            world.join(".mcagit").join("HEAD").is_file(),
            ".mcagit/HEAD must survive prune"
        );
        assert!(
            world.join(".mcagit").join("objects").is_dir(),
            ".mcagit/objects must survive prune"
        );
        assert!(
            Repository::open(&world).is_ok(),
            "repo still opens after prune"
        );
        assert!(
            !world.join("extra.bin").exists(),
            "untracked extra is pruned"
        );
        assert!(world.join("keep.bin").exists(), "tracked file is kept");
    }
}
