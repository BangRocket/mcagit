//! Garbage collection: consolidate all reachable objects into one packfile and
//! drop everything unreachable (loose files + old packs).

use crate::fsck::{manifest_ids, tips};
use crate::pack::PackWriter;
use crate::repository::Repository;
use crate::Result;
use std::collections::HashSet;
use std::path::Path;

#[derive(Debug, Default)]
pub struct GcReport {
    pub kept: usize,
    pub pruned: usize,
}

pub fn gc(repo: &Repository) -> Result<GcReport> {
    let store = repo.objects();
    let total_before = store.all_ids().len();

    // Reachable closure: commits + their trees + all manifest objects.
    let mut keep: Vec<String> = Vec::new();
    let mut seen: HashSet<String> = HashSet::new();
    let mut stack = tips(repo);
    while let Some(c) = stack.pop() {
        if !seen.insert(c.clone()) {
            continue;
        }
        if !store.exists(&c) {
            continue;
        }
        keep.push(c.clone());
        if let Ok(commit) = repo.read_commit(&c) {
            if seen.insert(commit.tree.clone()) {
                keep.push(commit.tree.clone());
            }
            if let Ok(m) = repo.read_manifest(&commit.tree) {
                for id in manifest_ids(&m) {
                    if seen.insert(id.clone()) {
                        keep.push(id);
                    }
                }
            }
            for p in commit.parents {
                stack.push(p);
            }
        }
    }

    let pack_dir = store.pack_dir();
    let old_pack_ids = list_pack_ids(&pack_dir);

    // Write all reachable objects into one consolidated pack.
    let mut writer = PackWriter::new(&pack_dir)?;
    for id in &keep {
        if let Ok(bytes) = store.read(id) {
            writer.add(id, &bytes)?;
        }
    }
    let new_pack = if writer.is_empty() {
        None
    } else {
        Some(writer.finish(&pack_dir)?)
    };

    // Delete old packs (except the freshly written one) and all loose objects.
    for id in old_pack_ids {
        if Some(&id) == new_pack.as_ref() {
            continue;
        }
        let _ = std::fs::remove_file(pack_dir.join(format!("pack-{id}.pack")));
        let _ = std::fs::remove_file(pack_dir.join(format!("pack-{id}.idx")));
    }
    delete_loose(store.objects_dir());
    store.reload_packs();

    let pruned = total_before.saturating_sub(keep.len());
    Ok(GcReport {
        kept: keep.len(),
        pruned,
    })
}

fn list_pack_ids(pack_dir: &Path) -> Vec<String> {
    let mut ids = Vec::new();
    if let Ok(entries) = std::fs::read_dir(pack_dir) {
        for e in entries.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if let Some(rest) = name.strip_prefix("pack-") {
                if let Some(id) = rest.strip_suffix(".pack") {
                    ids.push(id.to_string());
                }
            }
        }
    }
    ids
}

fn delete_loose(objects_dir: &Path) {
    if let Ok(subs) = std::fs::read_dir(objects_dir) {
        for sub in subs.flatten() {
            let name = sub.file_name().to_string_lossy().to_string();
            if name.len() == 2 && sub.path().is_dir() {
                let _ = std::fs::remove_dir_all(sub.path());
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{checkout, snapshot};
    use std::path::Path;

    fn tiny_world(world: &Path) {
        std::fs::create_dir_all(world).unwrap();
        std::fs::write(world.join("icon.png"), b"PNGDATA").unwrap();
        std::fs::write(world.join("level.dat"), b"not really nbt").unwrap();
    }

    #[test]
    fn gc_prunes_unreachable_and_keeps_checkout() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        tiny_world(&world);

        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "x", "me", "t").unwrap();
        repo.write_branch("main", &c).unwrap();

        // an unreachable object
        let junk = repo.objects().write(b"unreachable junk").unwrap();
        assert!(repo.objects().exists(&junk));

        let report = gc(&repo).unwrap();
        assert!(report.pruned >= 1, "expected to prune junk");
        assert!(!repo.objects().exists(&junk), "junk should be gone");

        // checkout still reproduces the committed world (now from the pack)
        let out = d.path().join("out");
        checkout(&repo, &m, &out, false).unwrap();
        let m2 = snapshot::snapshot(&repo, &out).unwrap();
        assert_eq!(repo.write_manifest(&m2).unwrap(), tree);
    }
}
