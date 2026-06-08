//! Fast, single-sided accuracy check: re-hash a world and compare the resulting
//! tree id to a commit's tree. Cheaper than a semantic world-diff — it decodes
//! the world *once* (no second side, no tree-walk); equal tree id means every
//! chunk's canonical NBT is bit-identical to what was committed.

use crate::repository::Repository;
use crate::{snapshot, Result};
use std::path::Path;

/// The tree id `world_dir` would produce if committed (no objects are written).
pub fn world_tree(repo: &Repository, world_dir: &Path) -> Result<String> {
    let m = snapshot::hash_only(repo, world_dir)?;
    Ok(blake3::hash(m.to_json()?.as_bytes()).to_hex().to_string())
}

/// Verify `world_dir` reproduces `commit`. Returns (matches, world_tree, commit_tree).
pub fn verify_commit(
    repo: &Repository,
    world_dir: &Path,
    commit: &str,
) -> Result<(bool, String, String)> {
    let target = repo.read_commit(commit)?.tree;
    let candidate = world_tree(repo, world_dir)?;
    Ok((candidate == target, candidate, target))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{checkout, snapshot};

    #[test]
    fn verify_matches_checkout_and_catches_corruption() {
        let d = tempfile::tempdir().unwrap();
        let repo = Repository::init(&d.path().join("repo")).unwrap();
        let world = d.path().join("world");
        std::fs::create_dir_all(&world).unwrap();
        std::fs::write(world.join("icon.png"), b"PNGDATA").unwrap();

        let m = snapshot::snapshot(&repo, &world).unwrap();
        let tree = repo.write_manifest(&m).unwrap();
        let c = repo.create_commit(&tree, vec![], "v1", "me", "t").unwrap();

        let out = d.path().join("out");
        checkout(&repo, &m, &out, false).unwrap();

        let (ok, cand, target) = verify_commit(&repo, &out, &c).unwrap();
        assert!(ok, "checkout should verify: {cand} vs {target}");

        // corrupt the checkout
        std::fs::write(out.join("icon.png"), b"TAMPERED").unwrap();
        let (ok2, _, _) = verify_commit(&repo, &out, &c).unwrap();
        assert!(!ok2, "tampered world must fail verify");
    }
}
