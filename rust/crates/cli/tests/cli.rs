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
