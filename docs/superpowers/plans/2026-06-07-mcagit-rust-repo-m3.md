# mcagit Rust — M3: object store + commit + parallel checkout (speed proof)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline). Steps use `- [ ]`.

**Goal:** A working vertical slice — `init` / `commit` / `checkout` / `status` — proving the headline win: **parallel checkout** on real worlds, benchmarked against the .NET baselines (checkout 268 s serial, commit 43 s) with the .NET tool as correctness oracle ("No differences").

**Architecture:** New crate `mca-repo` (depends on `mca-nbt` + `mca-anvil`) + a minimal `mcagit` binary crate. Clean-slate repo: blake3 object IDs over *uncompressed canonical content* (so identical content dedups regardless of on-disk compression), zstd-compressed loose objects at `objects/aa/rest`. Commit walks the world in parallel (rayon); **checkout materializes regions in parallel (rayon)** — the one loop the .NET version left serial.

**Tech Stack:** `mca-nbt`, `mca-anvil`, `blake3`, `zstd`, `serde`/`serde_json`, `rayon`, `thiserror`; bin: `clap`, `anyhow`. Dev: `tempfile`.

**Reference:** .NET `src/McaGit/Repo/{Manifest,Snapshotter,ObjectStore,Repository,Checkout,StatusCalc}.cs`.

## Crate layout

```text
crates/repo/
  Cargo.toml
  src/
    lib.rs            RepoError/Result + re-exports
    object_store.rs   ObjectStore: write/read/exists (blake3 + zstd loose objects)
    manifest.rs       Manifest, CommitObject (serde JSON)
    repository.rs     Repository: init/open/discover, config, refs/HEAD, commit r/w, manifest r/w, resolve_ref
    snapshot.rs       snapshot()/hash_only(): world dir -> Manifest (rayon, region/nbt/blob classify)
    checkout.rs       checkout(): Manifest -> world dir (rayon over regions)  ← the speed win
    status.rs         status(): worktree vs a commit (added/modified/removed)
crates/cli/
  Cargo.toml
  src/main.rs         clap: init, commit, checkout, status, log  (exit codes 0/1/2)
```

## Object & repo formats (clean-slate)

- **Object id:** `blake3::hash(content)` hex (content = uncompressed canonical bytes / blob bytes). Loose file: `objects/<aa>/<rest>`, body = `zstd(content)`.
- **Repo dir:** `objects/`, `refs/heads/<branch>`, `HEAD` (`ref: refs/heads/main\n` or a 64-hex detached id), `config` (TOML-ish `key = value` lines; we only need `worktree`). `Repository::discover` walks up looking for a dir containing `HEAD` + `objects/`.
- **Manifest / commit:** serde_json, pretty, key-sorted (BTreeMap). Commit: `{tree, parents[], message, author, time, committer?, commitTime?}`.

## Tasks (module-level; each ends green + committed)

### Task 1: crate scaffold + ObjectStore

- `crates/repo/Cargo.toml`; add `crates/repo` to workspace members; workspace deps `blake3="1"`, `zstd="0.13"`, `rayon="1"`, `serde`, `serde_json`.
- `object_store.rs`: `ObjectStore { dir: PathBuf }`; `write(&[u8]) -> String` (hex id; writes `objects/aa/rest` = zstd, idempotent), `read(&str) -> Result<Vec<u8>>` (zstd-decompress), `exists(&str) -> bool`.
- Tests: write→read round-trip; same content twice → same id + single file; `exists`.
- Commit `feat(repo): scaffold + content-addressed object store (blake3+zstd)`.

### Task 2: Manifest + CommitObject

- `manifest.rs`: `Manifest { regions: BTreeMap<String, BTreeMap<String,String>>, nbt: BTreeMap<String,String>, blobs: BTreeMap<String,String>, empty_dirs: Vec<String> }` with serde (`#[serde(rename_all="camelCase")]`, `emptyDirs`); `to_json`/`from_json`. `CommitObject { tree, parents, message, author, time, committer: Option, commit_time: Option }`.
- Tests: manifest JSON round-trip; commit JSON round-trip.
- Commit `feat(repo): Manifest + CommitObject (serde JSON)`.

### Task 3: Repository (init/open/discover, config, refs, commit/manifest r/w)

- `repository.rs`: `Repository { dir: PathBuf }`.
  - `init(dir)` (creates `objects/`, `refs/heads/`, `HEAD=ref: refs/heads/main`), `open(dir)`, `is_repository(dir)`, `discover(start)`.
  - `config_get(key)`/`config_set(key,val)` (worktree); `worktree()`.
  - `objects()` -> &ObjectStore.
  - `head_commit() -> Option<String>` (resolve HEAD → branch tip or detached), `current_branch() -> Option<String>`, `read_branch(name)`, `write_branch(name, hash)`, `set_head_to_branch(name)`, `set_head_detached(hash)`.
  - `write_manifest(&Manifest) -> String` (store as object), `read_manifest(treehash) -> Manifest`.
  - `read_commit(hash) -> CommitObject`, `create_commit(tree, parents, message, author, time) -> String`.
  - `resolve_ref(spec) -> Result<String>`: HEAD, branch name, full/abbrev hex id, `~n`, `^`.
- Tests: init then open; config round-trip; create_commit then head_commit/read_commit; branch create + resolve; `~1` walks parent.
- Commit `feat(repo): Repository — init/discover, config, refs, commit & manifest store`.

### Task 4: Snapshotter (commit walk)

- `snapshot.rs`: `snapshot(&Repository, world_dir) -> Result<Manifest>` and `hash_only(&Repository, world_dir) -> Result<Manifest>`.
  - Walk files (`walkdir` or std recursion), skip `session.lock` and the repo dir, respect a simple `.mcaignore` (defer full gitignore — basic name/dir/`*.ext`).
  - Classify: `.mca` under `region/`|`entities/`|`poi/` → per-chunk canonical objects (`pos "x,z" → blake3(canonical_bytes(decode(chunk)))`), key map; `.dat` → canonical NBT object; else raw blob. Region/NBT parse failure → blob fallback.
  - Parallel over files with `rayon` (`par_iter` + collect).
  - Record empty dirs.
- Tests (synthetic world via mca-anvil writer + tempfile): commit a tiny world → manifest has the region's chunks + a blob; re-snapshot identical world → identical tree hash (determinism).
- Commit `feat(repo): Snapshotter — parallel world→manifest`.

### Task 5: Checkout (PARALLEL) + status

- `checkout.rs`: `checkout(&Repository, &Manifest, out_dir, prune: bool) -> Result<()>`.
  - **`regions.par_iter()`** (rayon): for each region, decode each object (`mca_nbt::read`) → `codec::encode(value, ZLib)` → `RawChunk` (pos from "x,z") → `RegionWriter::write`. Each region is an independent file → no shared state.
  - nbt: `save_nbt_file` (gzip); blobs: write bytes; empty_dirs: create. prune: remove tracked files not in manifest.
- `status.rs`: `status(&Repository, world_dir, commit) -> Vec<Change>` via `hash_only` vs the commit's manifest (added/modified/removed by hash).
- Tests: commit a synthetic world → checkout to new dir → re-snapshot the checkout → tree hash equals the commit's tree (round-trip at canonical level).
- Commit `feat(repo): parallel checkout (rayon over regions) + status`.

### Task 6: minimal `mcagit` CLI

- `crates/cli` bin: `clap` subcommands `init <dir> [--worktree W]`, `-C <repo> commit -m <msg> [<world>]`, `-C <repo> checkout <ref> [<out>]`, `-C <repo> status`, `-C <repo> log [--oneline]`. Exit codes 0/1/2; `commit` exits 0 on nothing-to-commit.
- Test: an integration test (`tests/`) that inits a repo, commits a synthetic world, checks out, and asserts the output reproduces (re-snapshot tree equality).
- Commit `feat(cli): minimal mcagit binary (init/commit/checkout/status/log)`.

### Task 7: M3 gate — benchmark + .NET oracle

- Build release. Against the kept scratch worlds (`/Volumes/Storage/Code/minecraft/dobbscraft-snapshots`):
  - `mcagit init` a fresh Rust repo; commit all 8 snapshots (time each); `checkout` newest + oldest to dirs (time each).
  - **Speed:** record checkout/commit wall-clock vs .NET (checkout 268 s, commit 43 s/11 s).
  - **Correctness (oracle):** run the **.NET** `mcagit <rust_checkout> <source_snapshot>` → expect "No differences" / exit 0 for newest and oldest.
- `cargo test --all` + `fmt --check` + `clippy -D warnings` green.
- Commit `test(repo): M3 gate — dobbscraft benchmark + .NET-oracle no-diff`.

## Deferred (tracked)

- Incremental commit cache (persistent `compression:hash → id`) — M3 commits decode every chunk (parallel) for now; cache is a fast-follow.
- Packfiles/gc (M5), full `.mcaignore`/hooks/signing (M5), merge/rebase/etc. (M5), transports/cloud (M6), full 35-command CLI + output formatters (M7), mmap + zlib-ng + fast checkout level (perf pass).

## Done criteria

- `init`/`commit`/`checkout`/`status` work end-to-end; checkout is rayon-parallel.
- M3 gate: 8 dobbscraft snapshots commit + checkout; .NET oracle reports "No differences"; checkout wall-clock recorded vs the 268 s baseline.
- `cargo test --all` + clippy + fmt green.
