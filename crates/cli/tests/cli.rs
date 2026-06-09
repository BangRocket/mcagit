//! End-to-end: drive the real `mcagit` binary through init → commit → checkout.

use mca_anvil::{codec, ChunkCompression, ChunkPos, RawChunk, RegionWriter};
use mca_nbt::{Compound, NbtValue};
use std::path::Path;
use std::process::Command;

fn mcagit() -> Command {
    Command::new(env!("CARGO_BIN_EXE_mcagit"))
}

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
}

#[test]
fn init_commit_checkout_roundtrip() {
    let d = tempfile::tempdir().unwrap();
    let repo = d.path().join("repo");
    let world = d.path().join("world");
    build_world(&world);

    let ok = mcagit()
        .args([
            "init",
            repo.to_str().unwrap(),
            "--worktree",
            world.to_str().unwrap(),
        ])
        .status()
        .unwrap();
    assert!(ok.success());

    let out = mcagit()
        .args(["-C", repo.to_str().unwrap(), "commit", "-m", "first"])
        .output()
        .unwrap();
    assert!(out.status.success());
    let commit = String::from_utf8(out.stdout).unwrap().trim().to_string();
    assert_eq!(commit.len(), 64, "commit id on stdout");

    let outdir = d.path().join("out");
    let ok = mcagit()
        .args([
            "-C",
            repo.to_str().unwrap(),
            "checkout",
            &commit,
            outdir.to_str().unwrap(),
        ])
        .status()
        .unwrap();
    assert!(ok.success());
    assert!(outdir.join("level.dat").exists());
    assert!(outdir.join("region").join("r.0.0.mca").exists());
}

fn region_world(world: &Path, hp: i32) {
    std::fs::create_dir_all(world.join("region")).unwrap();
    let mut c = Compound::new();
    c.insert("hp".into(), NbtValue::Int(hp));
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
}

#[test]
fn diff_extract_apply_roundtrip() {
    let d = tempfile::tempdir().unwrap();
    let a = d.path().join("a");
    let b = d.path().join("b");
    region_world(&a, 20);
    region_world(&b, 18);

    // diff differs -> exit 1
    let st = mcagit()
        .args(["diff", a.to_str().unwrap(), b.to_str().unwrap()])
        .status()
        .unwrap();
    assert_eq!(st.code(), Some(1));

    // extract A->B
    let patch = d.path().join("p.mcapatch");
    assert!(mcagit()
        .args([
            "extract",
            a.to_str().unwrap(),
            b.to_str().unwrap(),
            "-o",
            patch.to_str().unwrap(),
        ])
        .status()
        .unwrap()
        .success());

    // apply to A -> out
    let out = d.path().join("out");
    assert!(mcagit()
        .args([
            "apply",
            patch.to_str().unwrap(),
            a.to_str().unwrap(),
            "-o",
            out.to_str().unwrap(),
        ])
        .status()
        .unwrap()
        .success());

    // out == B  (diff exit 0)
    assert!(mcagit()
        .args(["diff", out.to_str().unwrap(), b.to_str().unwrap()])
        .status()
        .unwrap()
        .success());
}

#[test]
fn plumbing_tag_revparse_lstree() {
    let d = tempfile::tempdir().unwrap();
    let repo = d.path().join("repo");
    let world = d.path().join("world");
    region_world(&world, 5);
    assert!(mcagit()
        .args([
            "init",
            repo.to_str().unwrap(),
            "--worktree",
            world.to_str().unwrap()
        ])
        .status()
        .unwrap()
        .success());
    let out = mcagit()
        .args(["-C", repo.to_str().unwrap(), "commit", "-m", "c1"])
        .output()
        .unwrap();
    let commit = String::from_utf8(out.stdout).unwrap().trim().to_string();

    assert!(mcagit()
        .args(["-C", repo.to_str().unwrap(), "tag", "v1", &commit])
        .status()
        .unwrap()
        .success());

    let rp = mcagit()
        .args(["-C", repo.to_str().unwrap(), "rev-parse", "v1"])
        .output()
        .unwrap();
    assert_eq!(String::from_utf8(rp.stdout).unwrap().trim(), commit);

    let lt = mcagit()
        .args(["-C", repo.to_str().unwrap(), "ls-tree", "v1"])
        .output()
        .unwrap();
    assert!(String::from_utf8(lt.stdout).unwrap().contains("r.0.0.mca"));
}

#[cfg(unix)]
#[test]
fn hooks_gate_commit() {
    use std::os::unix::fs::PermissionsExt;
    let d = tempfile::tempdir().unwrap();
    let repo = d.path().join("repo");
    let world = d.path().join("world");
    region_world(&world, 1);
    assert!(mcagit()
        .args([
            "init",
            repo.to_str().unwrap(),
            "--worktree",
            world.to_str().unwrap()
        ])
        .status()
        .unwrap()
        .success());

    // a failing pre-commit hook aborts the commit
    let hooks = repo.join("hooks");
    std::fs::create_dir_all(&hooks).unwrap();
    std::fs::write(hooks.join("pre-commit"), "#!/bin/sh\nexit 1\n").unwrap();
    std::fs::set_permissions(
        hooks.join("pre-commit"),
        std::fs::Permissions::from_mode(0o755),
    )
    .unwrap();
    let st = mcagit()
        .args(["-C", repo.to_str().unwrap(), "commit", "-m", "blocked"])
        .status()
        .unwrap();
    assert!(!st.success(), "pre-commit failure must abort the commit");

    // with a passing pre-commit, post-commit observes the new commit
    std::fs::write(hooks.join("pre-commit"), "#!/bin/sh\nexit 0\n").unwrap();
    let marker = d.path().join("post-ran");
    std::fs::write(
        hooks.join("post-commit"),
        format!("#!/bin/sh\necho ran > {}\n", marker.display()),
    )
    .unwrap();
    std::fs::set_permissions(
        hooks.join("post-commit"),
        std::fs::Permissions::from_mode(0o755),
    )
    .unwrap();
    assert!(mcagit()
        .args(["-C", repo.to_str().unwrap(), "commit", "-m", "ok"])
        .status()
        .unwrap()
        .success());
    assert!(marker.exists(), "post-commit hook should have run");
}

#[test]
fn annotated_tag_create_list_peel() {
    let d = tempfile::tempdir().unwrap();
    let repo = d.path().join("repo");
    let world = d.path().join("world");
    region_world(&world, 7);
    assert!(mcagit()
        .args([
            "init",
            repo.to_str().unwrap(),
            "--worktree",
            world.to_str().unwrap()
        ])
        .status()
        .unwrap()
        .success());
    let out = mcagit()
        .args(["-C", repo.to_str().unwrap(), "commit", "-m", "c1"])
        .output()
        .unwrap();
    let commit = String::from_utf8(out.stdout).unwrap().trim().to_string();

    assert!(mcagit()
        .args([
            "-C",
            repo.to_str().unwrap(),
            "tag",
            "-a",
            "-m",
            "first release",
            "v1",
        ])
        .status()
        .unwrap()
        .success());

    // rev-parse peels the annotated tag to its commit
    let rp = mcagit()
        .args(["-C", repo.to_str().unwrap(), "rev-parse", "v1"])
        .output()
        .unwrap();
    assert_eq!(String::from_utf8(rp.stdout).unwrap().trim(), commit);

    // -n lists the message; cat-file shows the tag object itself
    let ls = mcagit()
        .args(["-C", repo.to_str().unwrap(), "tag", "-n"])
        .output()
        .unwrap();
    assert!(String::from_utf8(ls.stdout)
        .unwrap()
        .contains("first release"));
    let cf = mcagit()
        .args(["-C", repo.to_str().unwrap(), "cat-file", "v1"])
        .output()
        .unwrap();
    assert!(String::from_utf8(cf.stdout).unwrap().contains("\"tagger\""));

    // an unsigned tag does not verify
    let st = mcagit()
        .args(["-C", repo.to_str().unwrap(), "tag", "-v", "v1"])
        .status()
        .unwrap();
    assert_eq!(st.code(), Some(1));

    // duplicate without -f refuses; with -f succeeds
    let st = mcagit()
        .args(["-C", repo.to_str().unwrap(), "tag", "v1", &commit])
        .status()
        .unwrap();
    assert_eq!(st.code(), Some(2));
    assert!(mcagit()
        .args(["-C", repo.to_str().unwrap(), "tag", "-f", "v1", &commit])
        .status()
        .unwrap()
        .success());
}
