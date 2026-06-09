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

/// Build a world whose single chunk holds `v`, distinct per commit.
fn commit_value(repo: &Path, world: &Path, v: i32) -> String {
    region_world(world, v);
    let out = mcagit()
        .args([
            "-C",
            repo.to_str().unwrap(),
            "commit",
            "-m",
            &format!("v{v}"),
        ])
        .output()
        .unwrap();
    assert!(out.status.success());
    String::from_utf8(out.stdout).unwrap().trim().to_string()
}

#[test]
fn reflog_and_head_at_n() {
    let d = tempfile::tempdir().unwrap();
    let repo = d.path().join("repo");
    let world = d.path().join("world");
    region_world(&world, 0);
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
    let c1 = commit_value(&repo, &world, 1);
    let _c2 = commit_value(&repo, &world, 2);

    let rl = mcagit()
        .args(["-C", repo.to_str().unwrap(), "reflog"])
        .output()
        .unwrap();
    let text = String::from_utf8(rl.stdout).unwrap();
    assert!(text.contains("HEAD@{0}: commit: v2"), "{text}");
    assert!(text.contains("HEAD@{1}: commit: v1"), "{text}");

    let rp = mcagit()
        .args(["-C", repo.to_str().unwrap(), "rev-parse", "HEAD@{1}"])
        .output()
        .unwrap();
    assert_eq!(String::from_utf8(rp.stdout).unwrap().trim(), c1);
}

#[test]
fn bisect_finds_first_bad_commit() {
    let d = tempfile::tempdir().unwrap();
    let repo = d.path().join("repo");
    let world = d.path().join("world");
    region_world(&world, 0);
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

    // 6 commits; the "regression" is v >= 4 (commit index 3)
    let mut commits = Vec::new();
    for v in 1..=6 {
        commits.push(commit_value(&repo, &world, v));
    }
    let run = |args: &[&str]| {
        let mut full = vec!["-C", repo.to_str().unwrap(), "bisect"];
        full.extend_from_slice(args);
        mcagit().args(&full).output().unwrap()
    };

    let out = run(&["start", &commits[5], &commits[0]]);
    assert!(out.status.success());

    // Drive: the worktree at each step holds the checked-out suspect; decide
    // by reading which commit HEAD is on.
    let mut answer = String::new();
    for _ in 0..10 {
        let head = mcagit()
            .args(["-C", repo.to_str().unwrap(), "rev-parse", "HEAD"])
            .output()
            .unwrap();
        let h = String::from_utf8(head.stdout).unwrap().trim().to_string();
        let idx = commits.iter().position(|c| *c == h).unwrap();
        let verdict = if idx >= 3 { "bad" } else { "good" };
        let out = run(&[verdict]);
        assert!(out.status.success());
        let text = String::from_utf8(out.stdout).unwrap();
        if text.contains("is the first bad commit") {
            answer = text;
            break;
        }
    }
    assert!(
        answer.contains(&commits[3]),
        "expected {} in:\n{answer}",
        commits[3]
    );

    // reset returns to main and clears the session
    assert!(run(&["reset"]).status.success());
    let head = mcagit()
        .args(["-C", repo.to_str().unwrap(), "rev-parse", "HEAD"])
        .output()
        .unwrap();
    assert_eq!(
        String::from_utf8(head.stdout).unwrap().trim(),
        commits[5],
        "back on main's tip"
    );
}

#[test]
fn verify_remote_detects_corruption() {
    let d = tempfile::tempdir().unwrap();
    let repo = d.path().join("repo");
    let world = d.path().join("world");
    region_world(&world, 3);
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
    commit_value(&repo, &world, 4);

    // clone to a "remote" path, then verify-remote against it: ok
    let remote = d.path().join("remote");
    assert!(mcagit()
        .args(["clone", repo.to_str().unwrap(), remote.to_str().unwrap()])
        .status()
        .unwrap()
        .success());
    assert!(mcagit()
        .args([
            "-C",
            repo.to_str().unwrap(),
            "verify-remote",
            remote.to_str().unwrap(),
        ])
        .status()
        .unwrap()
        .success());

    // vandalize one loose object on the remote → verify-remote --deep exits 1
    let mut corrupted = false;
    for sub in std::fs::read_dir(remote.join("objects")).unwrap().flatten() {
        if sub.file_name().to_string_lossy().len() == 2 && sub.path().is_dir() {
            if let Some(f) = std::fs::read_dir(sub.path()).unwrap().flatten().next() {
                std::fs::write(f.path(), b"vandalized").unwrap();
                corrupted = true;
            }
        }
        if corrupted {
            break;
        }
    }
    assert!(corrupted, "expected a loose object to corrupt");
    let st = mcagit()
        .args([
            "-C",
            repo.to_str().unwrap(),
            "verify-remote",
            remote.to_str().unwrap(),
            "--deep",
        ])
        .status()
        .unwrap();
    assert_eq!(st.code(), Some(1), "corruption must be detected");
}
