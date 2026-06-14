# Embedded `.mcagit/` Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `mcagit init` and `clone` create a git-style `<folder>/.mcagit/` holding the repo metadata, with `<folder>` bound as the worktree — while keeping the bare/external model behind flags and never letting prune delete the embedded `.mcagit/`.

**Architecture:** `Repository::dir()` keeps meaning "the directory that directly holds HEAD/objects/refs/config/logs"; it becomes `<folder>/.mcagit` for embedded repos and the dir itself for bare repos. Only construction (`init_embedded`, clone), detection (`is_repository`/`open`/`discover`), and prune-protection change. The existing `snapshot::build` repo-dir exclusion is extended to `checkout::prune_extra` so an embedded `.mcagit/` is never captured *or* deleted.

**Tech Stack:** Rust (cargo workspace, crates `mca-repo` + `mcagit` CLI), `walkdir`, `clap`. Tests: in-crate `#[cfg(test)]` (synthetic worlds) + CLI integration tests driving the real binary.

**Spec:** `docs/superpowers/specs/2026-06-13-embedded-mcagit-layout-design.md`

**Branch:** `feat/embedded-layout` (off `main`; does NOT contain the staging-index work, so bare `commit` snapshots the whole worktree — no `-a` needed in tests).

**Gates (run before every commit):** `cargo test --all`, `cargo fmt --all -- --check`, `cargo clippy --all-targets -- -D warnings`.

## File Structure

- **Modify** `crates/repo/src/repository.rs` — add `init_embedded`, a shared `is_flat_repo` detector, and make `is_repository`/`open`/`discover` embedded-aware. (`init` stays unchanged so its ~40 callers keep producing bare repos.)
- **Modify** `crates/repo/src/checkout.rs` — `prune_extra` gains a `repo_dir` exclusion; `checkout` passes `repo.dir()`.
- **Modify** `crates/repo/src/transfer.rs` + `crates/repo/src/remote.rs` — `clone_local`/`clone_partial`/`clone_depth` gain an `embedded: bool` param (embedded → metadata in `dst/.mcagit` + worktree bound).
- **Modify** `crates/cli/src/main.rs` — `init` gains `--bare` (keeps `--worktree`) and defaults to embedded; `clone` gains `--bare`/`--worktree`, defaults to embedded, and auto-checks-out HEAD (except partial clones).
- **Modify** `crates/cli/tests/cli.rs` — integration tests.
- **Modify** `README.md` + `CLAUDE.md` — document the layout, flags, and discovery.

Order: repo primitives (Task 1) → safety (Task 2) → CLI init (Task 3) → CLI clone (Task 4) → docs/e2e (Task 5). Task 2 depends on Task 1 (`init_embedded`).

---

## Task 1: Repository layout primitives

**Files:**
- Modify: `crates/repo/src/repository.rs` (`is_repository` ~40-43, `init` ~45-54, `open` ~56-64, `discover` ~66-78; add `init_embedded`)

- [ ] **Step 1: Write failing tests**

Add to the `#[cfg(test)] mod tests` block in `crates/repo/src/repository.rs`:

```rust
    #[test]
    fn init_embedded_creates_dotmcagit_and_binds_worktree() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        let repo = Repository::init_embedded(&world).unwrap();
        assert!(world.join(".mcagit").join("HEAD").is_file());
        assert!(world.join(".mcagit").join("objects").is_dir());
        assert!(repo.dir().ends_with(".mcagit"), "dir() points at .mcagit");
        // worktree is bound to the folder (canonicalized)
        let wt = repo.worktree().unwrap();
        assert_eq!(
            std::fs::canonicalize(&wt).unwrap(),
            std::fs::canonicalize(&world).unwrap()
        );
    }

    #[test]
    fn open_detects_embedded_and_bare() {
        let d = tempfile::tempdir().unwrap();
        // embedded
        let world = d.path().join("world");
        Repository::init_embedded(&world).unwrap();
        let e = Repository::open(&world).unwrap();
        assert!(e.dir().ends_with(".mcagit"));
        // bare
        let bare = d.path().join("bare");
        Repository::init(&bare).unwrap();
        let b = Repository::open(&bare).unwrap();
        assert_eq!(b.dir(), bare.as_path());
        // neither
        assert!(Repository::open(&d.path().join("nope")).is_err());
    }

    #[test]
    fn discover_finds_embedded_from_nested_subdir() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        Repository::init_embedded(&world).unwrap();
        let nested = world.join("region").join("sub");
        std::fs::create_dir_all(&nested).unwrap();
        let r = Repository::discover(&nested).unwrap();
        assert!(r.dir().ends_with(".mcagit"));
    }

    #[test]
    fn init_embedded_on_existing_bare_stays_bare() {
        let d = tempfile::tempdir().unwrap();
        let dir = d.path().join("repo");
        Repository::init(&dir).unwrap(); // bare: HEAD/objects at top
        let r = Repository::init_embedded(&dir).unwrap();
        assert_eq!(r.dir(), dir.as_path(), "re-init keeps the existing bare layout");
        assert!(!dir.join(".mcagit").exists(), "must not nest a .mcagit");
    }

    #[test]
    fn is_repository_recognizes_both_layouts() {
        let d = tempfile::tempdir().unwrap();
        let world = d.path().join("world");
        Repository::init_embedded(&world).unwrap();
        let bare = d.path().join("bare");
        Repository::init(&bare).unwrap();
        assert!(Repository::is_repository(&world));
        assert!(Repository::is_repository(&bare));
        assert!(!Repository::is_repository(&d.path().join("nope")));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cargo test -p mca-repo init_embedded open_detects discover_finds is_repository_recognizes`
Expected: FAIL — `init_embedded` not found.

- [ ] **Step 3: Add the shared detector and make detection embedded-aware**

In `crates/repo/src/repository.rs`, replace the existing `is_repository` and `open` functions:

```rust
    /// A directory is a repo if it has both `HEAD` and `objects/`.
    pub fn is_repository(dir: &Path) -> bool {
        dir.join("HEAD").is_file() && dir.join("objects").is_dir()
    }
```

becomes (note: this introduces the private `is_flat_repo` and makes `is_repository` recognize an embedded `.mcagit/`):

```rust
    /// True if `dir` *directly* holds the repo metadata (flat / bare layout).
    fn is_flat_repo(dir: &Path) -> bool {
        dir.join("HEAD").is_file() && dir.join("objects").is_dir()
    }

    /// A path is (or contains) a repo if it is a flat repo or has an embedded
    /// `.mcagit/` flat repo inside it.
    pub fn is_repository(dir: &Path) -> bool {
        Self::is_flat_repo(dir) || Self::is_flat_repo(&dir.join(".mcagit"))
    }
```

and replace `open`:

```rust
    pub fn open(dir: &Path) -> Result<Self> {
        if !Self::is_repository(dir) {
            return Err(RepoError::NotARepository(dir.display().to_string()));
        }
        Ok(Self {
            dir: dir.to_path_buf(),
            objects: ObjectStore::new(dir.join("objects")),
        })
    }
```

with (embedded layout takes precedence; falls back to flat/bare):

```rust
    pub fn open(path: &Path) -> Result<Self> {
        // Embedded `<path>/.mcagit` wins; otherwise a flat repo at `path`.
        let dir = if Self::is_flat_repo(&path.join(".mcagit")) {
            path.join(".mcagit")
        } else if Self::is_flat_repo(path) {
            path.to_path_buf()
        } else {
            return Err(RepoError::NotARepository(path.display().to_string()));
        };
        Ok(Self {
            objects: ObjectStore::new(dir.join("objects")),
            dir,
        })
    }
```

Note: `init` ends with `Self::open(dir)`. Since `init(dir)` creates a flat layout at `dir` (and no `dir/.mcagit`), the new `open` resolves to `dir` (bare) — unchanged behavior. `discover` already calls `is_repository` + `open`, so it now finds embedded repos for free; leave `discover` as-is (it already walks up calling `is_repository`/`open`).

- [ ] **Step 4: Add `init_embedded`**

In `crates/repo/src/repository.rs`, immediately after the existing `init` function, add:

```rust
    /// Create an embedded repo: metadata under `<folder>/.mcagit`, with `folder`
    /// bound as the worktree (git-style). Idempotent: if `folder` is already a
    /// *bare* repo, the existing flat layout is kept (no nested `.mcagit/`).
    pub fn init_embedded(folder: &Path) -> Result<Self> {
        std::fs::create_dir_all(folder)?;
        if Self::is_flat_repo(folder) {
            return Self::open(folder); // already bare → keep it bare
        }
        let repo = Self::init(&folder.join(".mcagit"))?;
        let abs = std::fs::canonicalize(folder).unwrap_or_else(|_| folder.to_path_buf());
        repo.set_worktree(&abs.to_string_lossy())?;
        Ok(repo)
    }
```

- [ ] **Step 5: Run the tests**

Run: `cargo test -p mca-repo init_embedded open_detects discover_finds is_repository_recognizes`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the whole repo suite (nothing else should regress)**

Run: `cargo test -p mca-repo`
Expected: PASS — the embedded-aware `open` must not break any existing `init`/`open`/`discover` test.

- [ ] **Step 7: Lint + format**

Run: `cargo fmt --all -- --check && cargo clippy -p mca-repo --all-targets -- -D warnings`
Expected: clean.

- [ ] **Step 8: Commit**

```bash
git add crates/repo/src/repository.rs
git commit -m "feat(repo): embedded .mcagit/ layout — init_embedded + embedded-aware open/discover"
```

---

## Task 2: Protect `.mcagit/` from prune (hard requirement)

**Files:**
- Modify: `crates/repo/src/checkout.rs` (`checkout` prune call ~92-94, `prune_extra` ~125-147)

- [ ] **Step 1: Write the failing safety test**

Add to the `#[cfg(test)] mod tests` block in `crates/repo/src/checkout.rs`:

```rust
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
        assert!(Repository::open(&world).is_ok(), "repo still opens after prune");
        assert!(!world.join("extra.bin").exists(), "untracked extra is pruned");
        assert!(world.join("keep.bin").exists(), "tracked file is kept");
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cargo test -p mca-repo prune_preserves_embedded_repo_dir`
Expected: FAIL — `.mcagit/HEAD` is deleted by prune (assert fails / repo won't open).

- [ ] **Step 3: Add the repo-dir exclusion to `prune_extra`**

In `crates/repo/src/checkout.rs`, replace the `prune_extra` function:

```rust
fn prune_extra(out_dir: &Path, m: &Manifest) -> Result<()> {
    let mut keep: HashSet<&String> = HashSet::new();
    keep.extend(m.regions.keys());
    keep.extend(m.nbt.keys());
    keep.extend(m.blobs.keys());
    for entry in walkdir::WalkDir::new(out_dir)
        .into_iter()
        .filter_map(|e| e.ok())
    {
        if entry.file_type().is_file() {
            let rel = entry
                .path()
                .strip_prefix(out_dir)
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
```

with (canonicalizes the walk root + the excluded repo dir, mirroring `snapshot::build`):

```rust
fn prune_extra(out_dir: &Path, m: &Manifest, repo_dir: Option<&Path>) -> Result<()> {
    let mut keep: HashSet<&String> = HashSet::new();
    keep.extend(m.regions.keys());
    keep.extend(m.nbt.keys());
    keep.extend(m.blobs.keys());
    let root = std::fs::canonicalize(out_dir).unwrap_or_else(|_| out_dir.to_path_buf());
    // Never delete the repo's own metadata when it lives inside `out_dir`
    // (embedded `.mcagit/` layout).
    let repo_prefix = repo_dir.and_then(|d| std::fs::canonicalize(d).ok());
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
```

- [ ] **Step 4: Pass the repo dir from `checkout`**

In `crates/repo/src/checkout.rs`, in the `checkout` function, replace:

```rust
    if prune {
        prune_extra(out_dir, manifest)?;
    }
```

with:

```rust
    if prune {
        prune_extra(out_dir, manifest, Some(repo.dir()))?;
    }
```

- [ ] **Step 5: Run the safety test + repo suite**

Run: `cargo test -p mca-repo prune_preserves_embedded_repo_dir && cargo test -p mca-repo`
Expected: PASS — the safety test passes and the existing checkout/gc tests (which call `prune` on external out-dirs, where the exclusion is a no-op) still pass.

- [ ] **Step 6: Lint + format**

Run: `cargo fmt --all -- --check && cargo clippy -p mca-repo --all-targets -- -D warnings`
Expected: clean.

- [ ] **Step 7: Commit**

```bash
git add crates/repo/src/checkout.rs
git commit -m "fix(repo): checkout prune must never delete the embedded .mcagit/ dir"
```

---

## Task 3: CLI `init` — embedded default + `--bare`

**Files:**
- Modify: `crates/cli/src/main.rs` (`Init` enum variant ~28-32, `Cmd::Init` handler ~366-378)
- Modify: `crates/cli/tests/cli.rs`

- [ ] **Step 1: Write failing integration tests**

Add to `crates/cli/tests/cli.rs` (the `mcagit()` and `build_world` helpers already exist):

```rust
#[test]
fn init_embedded_layout_and_discovery() {
    let d = tempfile::tempdir().unwrap();
    let world = d.path().join("world");
    build_world(&world); // region/r.0.0.mca + level.dat

    // `mcagit init` from inside the world, no -C, no dir arg → embedded .mcagit
    assert!(mcagit()
        .current_dir(&world)
        .args(["init"])
        .status()
        .unwrap()
        .success());
    assert!(world.join(".mcagit").join("HEAD").is_file(), ".mcagit created in the world");

    // commit from inside the world via discovery (no -C); whole-world snapshot
    let out = mcagit()
        .current_dir(&world)
        .args(["commit", "-m", "seed"])
        .output()
        .unwrap();
    assert!(out.status.success(), "stderr: {}", String::from_utf8_lossy(&out.stderr));
    let commit = String::from_utf8(out.stdout).unwrap().trim().to_string();
    assert_eq!(commit.len(), 64);

    // the repo's own metadata must NOT be committed
    let tree = mcagit()
        .current_dir(&world)
        .args(["ls-tree", &commit])
        .output()
        .unwrap();
    let text = String::from_utf8(tree.stdout).unwrap();
    assert!(!text.contains(".mcagit"), ".mcagit must not be committed:\n{text}");
    assert!(text.contains("level.dat"), "world content committed:\n{text}");
}

#[test]
fn init_bare_then_default_reinit_stays_bare() {
    let d = tempfile::tempdir().unwrap();
    let dir = d.path().join("repo");
    let r = dir.to_str().unwrap();
    assert!(mcagit().args(["init", r, "--bare"]).status().unwrap().success());
    assert!(dir.join("HEAD").is_file(), "bare: HEAD at top level");
    assert!(!dir.join(".mcagit").exists());
    // default re-init on an existing bare repo must not nest a .mcagit
    assert!(mcagit().args(["init", r]).status().unwrap().success());
    assert!(!dir.join(".mcagit").exists(), "re-init must not nest .mcagit in a bare repo");
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cargo test -p mcagit init_embedded_layout_and_discovery init_bare_then_default_reinit_stays_bare`
Expected: FAIL — `--bare` is unknown and `init` produces a flat layout (no `.mcagit`).

- [ ] **Step 3: Add `--bare` to the `Init` variant**

In `crates/cli/src/main.rs`, replace the `Init` variant:

```rust
    /// Create a repo, optionally binding a world as the worktree.
    Init {
        dir: Option<PathBuf>,
        #[arg(long)]
        worktree: Option<PathBuf>,
    },
```

with:

```rust
    /// Create a repo. Default: embedded `<dir>/.mcagit/` with the worktree bound
    /// to `<dir>`. `--worktree` binds an external world (bare layout in `<dir>`);
    /// `--bare` makes a bare repo with no worktree.
    Init {
        dir: Option<PathBuf>,
        /// Bind an external worktree (bare layout: metadata directly in <dir>).
        #[arg(long, conflicts_with = "bare")]
        worktree: Option<PathBuf>,
        /// Bare repo: metadata directly in <dir>, no worktree, no `.mcagit/`.
        #[arg(long)]
        bare: bool,
    },
```

- [ ] **Step 4: Replace the `Cmd::Init` handler**

Replace the `Cmd::Init { dir, worktree } => { ... }` arm with:

```rust
        Cmd::Init { dir, worktree, bare } => {
            let dir = dir
                .clone()
                .or_else(|| cli.repo.clone())
                .unwrap_or_else(|| PathBuf::from("."));
            if let Some(w) = worktree {
                // External worktree → bare/flat layout in `dir`.
                let repo = Repository::init(&dir)?;
                let w = std::fs::canonicalize(w).unwrap_or_else(|_| w.clone());
                repo.set_worktree(&w.to_string_lossy())?;
                eprintln!(
                    "Initialized mcagit repository in {} (worktree {})",
                    dir.display(),
                    w.display()
                );
            } else if *bare {
                Repository::init(&dir)?;
                eprintln!("Initialized bare mcagit repository in {}", dir.display());
            } else {
                // Default: embedded `.mcagit/` with the worktree = dir.
                Repository::init_embedded(&dir)?;
                eprintln!(
                    "Initialized mcagit repository in {} (.mcagit/)",
                    dir.display()
                );
            }
            Ok(ExitCode::SUCCESS)
        }
```

- [ ] **Step 5: Run the tests**

Run: `cargo test -p mcagit init_embedded_layout_and_discovery init_bare_then_default_reinit_stays_bare`
Expected: PASS.

- [ ] **Step 6: Run the whole CLI suite**

Run: `cargo test -p mcagit`
Expected: PASS — existing tests that use `init <dir> --worktree <world>` still work (that path is unchanged).

- [ ] **Step 7: Lint + format**

Run: `cargo fmt --all -- --check && cargo clippy -p mcagit --all-targets -- -D warnings`
Expected: clean.

- [ ] **Step 8: Commit**

```bash
git add crates/cli/src/main.rs crates/cli/tests/cli.rs
git commit -m "feat(cli): init defaults to embedded .mcagit/ (--bare/--worktree for the flat layout)"
```

---

## Task 4: `clone` — embedded default + auto-checkout

**Files:**
- Modify: `crates/repo/src/transfer.rs` (`clone_local` ~65-86)
- Modify: `crates/repo/src/remote.rs` (`clone_partial` ~775-804, `clone_depth` ~810-848)
- Modify: `crates/cli/src/main.rs` (`Clone` enum ~199-210, `Cmd::Clone` handler ~1106-1128; add a `checkout_after_clone` helper)
- Modify: `crates/cli/tests/cli.rs`

- [ ] **Step 1: Write a failing integration test**

Add to `crates/cli/tests/cli.rs`:

```rust
#[test]
fn clone_creates_embedded_worktree_and_checks_out() {
    let d = tempfile::tempdir().unwrap();
    let src = d.path().join("src");
    let srcworld = d.path().join("srcworld");
    build_world(&srcworld);

    // a bare source repo bound to an external world, with one commit
    assert!(mcagit()
        .args(["init", src.to_str().unwrap(), "--worktree", srcworld.to_str().unwrap()])
        .status()
        .unwrap()
        .success());
    assert!(mcagit()
        .args(["-C", src.to_str().unwrap(), "commit", "-m", "seed"])
        .status()
        .unwrap()
        .success());

    // clone into a fresh dir → embedded .mcagit + auto-checkout
    let dst = d.path().join("dst");
    assert!(mcagit()
        .args(["clone", src.to_str().unwrap(), dst.to_str().unwrap()])
        .status()
        .unwrap()
        .success());
    assert!(dst.join(".mcagit").join("HEAD").is_file(), "embedded .mcagit at dst");
    assert!(dst.join("level.dat").is_file(), "auto-checkout materialized the world");

    // status works from inside the cloned worktree via discovery
    assert!(mcagit().current_dir(&dst).args(["status"]).status().unwrap().success()
        || mcagit().current_dir(&dst).args(["status"]).status().unwrap().code() == Some(1));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cargo test -p mcagit clone_creates_embedded_worktree_and_checks_out`
Expected: FAIL — clone produces a flat `dst` (no `.mcagit`, no checked-out `level.dat`).

- [ ] **Step 3: Add an `embedded` param to the three clone fns**

In `crates/repo/src/transfer.rs`, change `clone_local`'s signature and dest construction:

```rust
pub fn clone_local(src: &Path, dst: &Path) -> Result<Repository> {
    let source = Repository::open(src)?;
    let dest = Repository::init(dst)?;
```

to:

```rust
pub fn clone_local(src: &Path, dst: &Path, embedded: bool) -> Result<Repository> {
    let source = Repository::open(src)?;
    let dest = if embedded {
        Repository::init_embedded(dst)?
    } else {
        Repository::init(dst)?
    };
```

In `crates/repo/src/remote.rs`, do the same to BOTH `clone_partial` and `clone_depth` — add `embedded: bool` as the last parameter and replace their `let dest = Repository::init(dst)?;` line with the same `if embedded { Repository::init_embedded(dst)? } else { Repository::init(dst)? }` block:

```rust
pub fn clone_partial(url_or_path: &str, dst: &Path, embedded: bool) -> Result<Repository> {
    let t = connect(url_or_path)?;
    let dest = if embedded {
        Repository::init_embedded(dst)?
    } else {
        Repository::init(dst)?
    };
```

```rust
pub fn clone_depth(url_or_path: &str, dst: &Path, depth: usize, embedded: bool) -> Result<Repository> {
    let t = connect(url_or_path)?;
    let dest = if embedded {
        Repository::init_embedded(dst)?
    } else {
        Repository::init(dst)?
    };
```

- [ ] **Step 4: Fix every existing call site (compiler-driven)**

Run: `cargo build -p mca-repo 2>&1 | grep "this function takes"` (or just `cargo build`) to find every caller of the three functions. At each EXISTING call site (the in-crate tests in `transfer.rs` and `remote.rs`), append `, false` (bare — unchanged behavior). For example in `transfer.rs` tests: `clone_local(&srcdir, &dstdir)` → `clone_local(&srcdir, &dstdir, false)`. Do the same for any `clone_partial(...)` / `clone_depth(...)` test calls in `remote.rs`. (The CLI call sites are updated in Step 6.)

- [ ] **Step 5: Verify the repo crate builds + tests pass**

Run: `cargo test -p mca-repo`
Expected: PASS — all clone tests green with the `, false` arg.

- [ ] **Step 6: Update the `Clone` variant + handler + add `checkout_after_clone`**

In `crates/cli/src/main.rs`, replace the `Clone` variant:

```rust
    Clone {
        src: String,
        dst: PathBuf,
        /// Shallow clone: fetch at most this many commits per branch
        /// (records a shallow boundary; tags are skipped).
        #[arg(long)]
        depth: Option<usize>,
        /// Partial clone: `--filter blob:none` fetches the commit/tree skeleton
        /// only; leaf chunks are backfilled on demand (e.g. at checkout).
        #[arg(long)]
        filter: Option<String>,
    },
```

with:

```rust
    Clone {
        src: String,
        dst: PathBuf,
        /// Shallow clone: fetch at most this many commits per branch
        /// (records a shallow boundary; tags are skipped).
        #[arg(long)]
        depth: Option<usize>,
        /// Partial clone: `--filter blob:none` fetches the commit/tree skeleton
        /// only; leaf chunks are backfilled on demand (e.g. at checkout).
        #[arg(long)]
        filter: Option<String>,
        /// Bare clone: metadata in <dst>, no worktree, no checkout.
        #[arg(long, conflicts_with = "worktree")]
        bare: bool,
        /// Bind an external worktree instead of the embedded `.mcagit/` default
        /// (no auto-checkout of <dst>).
        #[arg(long)]
        worktree: Option<PathBuf>,
    },
```

Replace the `Cmd::Clone { src, dst, depth, filter } => { ... }` handler with:

```rust
        Cmd::Clone {
            src,
            dst,
            depth,
            filter,
            bare,
            worktree,
        } => {
            // Embedded (.mcagit/ + worktree=dst) unless --bare or --worktree.
            let embedded = !*bare && worktree.is_none();
            let suffix = match filter.as_deref() {
                Some("blob:none") => {
                    if depth.is_some() {
                        bail!("--filter and --depth cannot be combined");
                    }
                    let repo = mca_repo::remote::clone_partial(src, dst, embedded)?;
                    if let Some(w) = worktree {
                        let w = std::fs::canonicalize(w).unwrap_or_else(|_| w.clone());
                        repo.set_worktree(&w.to_string_lossy())?;
                    }
                    // Partial clones intentionally check out nothing (the leaves
                    // aren't fetched); the worktree stays empty until checkout.
                    " (partial: blob:none)".to_string()
                }
                Some(other) => bail!("unsupported filter: {other} (only blob:none)"),
                None => {
                    let repo = mca_repo::remote::clone_depth(src, dst, depth.unwrap_or(0), embedded)?;
                    if let Some(w) = worktree {
                        let w = std::fs::canonicalize(w).unwrap_or_else(|_| w.clone());
                        repo.set_worktree(&w.to_string_lossy())?;
                    } else if embedded {
                        checkout_after_clone(&repo)?;
                    }
                    depth.map(|d| format!(" (depth {d})")).unwrap_or_default()
                }
            };
            eprintln!("Cloned {src} -> {}{suffix}", dst.display());
            Ok(ExitCode::SUCCESS)
        }
```

Add this helper near the other free functions in `crates/cli/src/main.rs` (e.g. next to `open_repo`):

```rust
/// After an embedded clone, materialize HEAD into the bound worktree (git-style).
/// A no-op for an empty clone (no HEAD) or a repo with no bound worktree.
fn checkout_after_clone(repo: &Repository) -> anyhow::Result<()> {
    let Some(wt) = repo.worktree() else {
        return Ok(());
    };
    let Some(head) = repo.head_commit() else {
        return Ok(());
    };
    let manifest = repo.read_manifest(&repo.read_commit(&head)?.tree)?;
    mca_repo::checkout(repo, &manifest, std::path::Path::new(&wt), false)?;
    eprintln!("Checked out {} into {wt}", &head[..10.min(head.len())]);
    Ok(())
}
```

- [ ] **Step 7: Run the clone test + whole CLI suite**

Run: `cargo test -p mcagit clone_creates_embedded_worktree_and_checks_out`
Expected: PASS.

Run: `cargo test -p mcagit`
Expected: PASS. If a pre-existing test runs `clone <src> <dst>` and then inspects `dst/objects`/`dst/HEAD` (expecting the flat layout), it now needs `dst/.mcagit/...` OR a `--bare` flag. Update such tests: prefer adding `--bare` to keep their intent (a bare clone), or update the path to `dst/.mcagit/...`. Scan `crates/cli/tests/cli.rs` for `"clone"` usages and fix any that assert the old flat dest layout.

- [ ] **Step 8: Lint + format**

Run: `cargo fmt --all -- --check && cargo clippy --all-targets -- -D warnings`
Expected: clean.

- [ ] **Step 9: Commit**

```bash
git add crates/repo/src/transfer.rs crates/repo/src/remote.rs crates/cli/src/main.rs crates/cli/tests/cli.rs
git commit -m "feat(clone): embedded .mcagit/ default + auto-checkout (--bare/--worktree opt out)"
```

---

## Task 5: Docs + full-suite + e2e

**Files:**
- Modify: `README.md`, `CLAUDE.md`

- [ ] **Step 1: Update `CLAUDE.md`**

In `CLAUDE.md`, update the description of the repo model. Find the sentence in the Architecture section stating the repository is "bare and external" (it currently reads roughly: "`Repository` is **bare and external** to the world; the bound worktree is stored in the repo `config`"). Replace it with text conveying:

```
`Repository` supports two layouts: **embedded** (`<world>/.mcagit/` holds the
metadata, worktree = the containing folder — the `init`/`clone` default) and
**bare** (metadata directly in the repo dir, worktree external via `config` or
none — `--bare`/`--worktree`). `Repository::dir()` is the metadata dir in both
cases; `open`/`discover` detect `.mcagit/` (preferred) or a flat layout. The
embedded `.mcagit/` is excluded from snapshots AND protected from `checkout`
prune (it lives inside the worktree).
```

Also in the "CLI shape" subcommand list, update `init` to note the embedded default and add the flags (e.g. `init [--bare|--worktree <path>]`, `clone [--bare|--worktree <path>]`).

- [ ] **Step 2: Update `README.md`**

In `README.md`, in the getting-started / usage section, document the embedded default. Add (adapting to the README's existing voice/format):

```
- `mcagit init [<dir>]` — create a repo embedded as `<dir>/.mcagit/`, with
  `<dir>` (default: the current directory) as the worktree (git-style). Run
  `mcagit` commands from anywhere inside `<dir>` — they discover `.mcagit/`.
  - `--worktree <path>` — bare layout in `<dir>`, bound to an external world.
  - `--bare` — bare repo, no worktree.
- `mcagit clone <src> <dir>` — clone into `<dir>/.mcagit/` and check out HEAD
  into `<dir>` (a usable world). `--bare`/`--worktree <path>` opt out;
  `--filter blob:none` clones the skeleton and checks out nothing.
```

If a README section describes the old bare-only model as the only option, reword it so embedded is the default and bare is the alternative.

- [ ] **Step 3: Full workspace gates**

Run: `cargo test --all`
Expected: PASS — every crate.

Run: `cargo fmt --all -- --check && cargo clippy --all-targets -- -D warnings`
Expected: clean.

If `markdownlint-cli2` is available, run it on README.md/CLAUDE.md and fix violations; else keep the markdown visually consistent.

- [ ] **Step 4: End-to-end (run on temp dirs — do NOT touch the repo's sample worlds)**

```bash
set -e
cargo build --release
BIN="$PWD/target/release/mcagit"
TMP=$(mktemp -d)

# 1) Embedded init in a copy of a sample world; commit + checkout round-trip.
cp -R compare-worlds/New_World_Older "$TMP/world"
( cd "$TMP/world" && "$BIN" init )
test -f "$TMP/world/.mcagit/HEAD" || { echo "FAIL: no .mcagit"; exit 1; }
COMMIT=$(cd "$TMP/world" && "$BIN" commit -m seed)
# .mcagit must not be in the committed tree
( cd "$TMP/world" && "$BIN" ls-tree "$COMMIT" ) | grep -q ".mcagit" && { echo "FAIL: .mcagit committed"; exit 1; }
echo "embedded init OK"

# 2) reset --hard must leave .mcagit intact (the safety property).
( cd "$TMP/world" && "$BIN" reset --hard "$COMMIT" )
test -f "$TMP/world/.mcagit/HEAD" || { echo "FAIL: reset --hard deleted .mcagit"; exit 1; }
( cd "$TMP/world" && "$BIN" status >/dev/null )
echo "prune-safety OK"

# 3) Embedded clone auto-checks-out.
"$BIN" clone "$TMP/world" "$TMP/clone"
test -f "$TMP/clone/.mcagit/HEAD" || { echo "FAIL: clone not embedded"; exit 1; }
test -f "$TMP/clone/level.dat"    || { echo "FAIL: clone did not check out"; exit 1; }
echo "embedded clone OK"

rm -rf "$TMP"
echo "e2e OK"
```
Report the output (must end with `e2e OK`). Adapt the `cp` source if `ls compare-worlds/` shows a different sample-world name.

- [ ] **Step 5: Confirm the repo is clean (no sample-world mutation)**

Run: `git -C /Volumes/Storage/Code/minecraft/mcagit status --short`
Confirm nothing under `compare-worlds/` was modified (the e2e used `mktemp` copies). Only doc edits should be staged.

- [ ] **Step 6: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document the embedded .mcagit/ layout (init/clone defaults, discovery)"
```

---

## Self-Review (completed during planning)

**Spec coverage** — every spec section maps to a task:
- §1 detection & `dir()` → Task 1 (`is_flat_repo`, embedded-aware `is_repository`/`open`/`discover`).
- §2 `init` embedded default + bare via flag + idempotent re-init → Task 1 (`init_embedded`) + Task 3 (CLI flags/handler).
- §3 `clone` embedded + auto-checkout (+ partial checks out nothing) → Task 4.
- §4 prune-protection (hard requirement) → Task 2 (with the headline safety test) + Task 5 e2e (reset --hard).
- §5 worktree resolution via config → Task 1 (`init_embedded` writes worktree) + Task 4 (clone embedded/external binding); `worktree()` unchanged.
- §6 backward compat (flat = bare) → Task 1 (`open`/`is_repository` flat branch) + existing suite staying green.
- §Testing → tests in each task + Task 5 e2e; §Docs → Task 5.

**Placeholder scan:** no TBD/TODO; every code step shows complete code.

**Type consistency:** `Repository::init_embedded(&Path) -> Result<Self>`, the private `is_flat_repo(&Path) -> bool`, `open(&Path)`/`is_repository(&Path)` unchanged signatures, `prune_extra(&Path, &Manifest, Option<&Path>)`, `clone_local(&Path,&Path,bool)` / `clone_partial(&str,&Path,bool)` / `clone_depth(&str,&Path,usize,bool)`, and the CLI `checkout_after_clone(&Repository) -> anyhow::Result<()>` are used consistently across Tasks 1–4. The `init` low-level constructor is deliberately left unchanged so its ~40 callers keep producing bare repos.
