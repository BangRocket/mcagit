# .NET Parity Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining .NET-parity gaps in the Rust mcagit: (1) hooks + SSH signing + annotated tags, (2) incremental commit chunk cache, (3) reflog + bisect + stash drop + verify-remote, (4) pack-on-the-wire push + shallow clone, (5) S3/Azure bucket remotes. Update CLAUDE.md + README.md per item.

**Architecture:** Each feature ports the .NET reference semantics (preserved at git rev `eeacf5a^`, extracted to `/tmp/mcagit-dotnet-ref/`) onto the existing Rust crate layout. All repo-level logic lands in `crates/repo`; the CLI only wires flags. New untrusted-input surfaces (pack ingest, bucket manifests) follow the existing confinement invariants (id validation before paths, bounded inflate).

**Tech Stack:** Existing deps (ureq, zstd, blake3, serde, tiny_http) plus — item 5 only — `sha2`/`hmac`/`base64` for AWS SigV4 / Azure SharedKey signing (no async SDKs; cloud REST over ureq).

**Branch:** `feat/dotnet-parity`, one commit per item (verified: tests + fmt + clippy before each commit).

---

## Task 1: Hooks + SSH signing + annotated tags

**Files:**
- Create: `crates/repo/src/hooks.rs` — `pub fn run(repo: &Repository, name: &str) -> i32`. Path `repo.dir()/hooks/<name>`; missing → 0. Try direct exec; fall back to `/bin/sh <script>` (plus Windows Git-for-Windows sh candidates, mirroring .NET `Hooks.cs`). Env `MCAGIT_DIR`, `MCAGIT_WORKTREE`; cwd = worktree else repo dir.
- Create: `crates/repo/src/sign.rs` — port `SshSigner.cs`: `available()` (PATH probe incl. `.exe` on Windows), `sign(payload, keyfile) -> Result<String>` via `ssh-keygen -Y sign -f <key> -n mcagit <tmpfile>`; `verify(payload, sig, allowed_signers: Option<&str>) -> VerifyResult{valid, signer_verified, identity, detail}` via `find-principals` + `-Y verify`, falling back to `check-novalidate`. `~` expansion confined to home.
- Modify: `crates/repo/src/manifest.rs` — add `signature: Option<String>` (skip-if-none) to `CommitObject`; add `signable_payload()` (clone w/ signature=None → to_json). New `TagObject {object, type, tag, tagger, time, message, signature}` with `signable_payload()` and `try_from_json` (must have non-empty object/tag/tagger; non-`{` or other objects → None).
- Modify: `crates/repo/src/repository.rs` — `write_annotated_tag(&TagObject) -> Result<String>` (store object, point refs/tags/<name> at it); `read_annotated_tag(name)`; `peel_to_commit(hash)` (follow tag chain, cap 100); `resolve_base` peels tag refs. `create_commit` gains `sign: Option<&dyn Fn(&str) -> Result<String>>`-style path (add `create_commit_signed` to avoid breaking call sites).
- Modify: peeling at tag consumers: `crates/repo/src/transfer.rs::reachable`, `crates/repo/src/remote.rs::fetch_reachable`, `crates/repo/src/fsck.rs` + `crates/repo/src/gc.rs` reachability — when an object parses as a TagObject, also walk its `object` target.
- Modify: `crates/cli/src/main.rs` — commit: run `pre-commit` (non-zero aborts), `post-commit` after; `-S/--sign` flag + `commit.gpgsign` config; tag: `-a`, `-s`, `-m`, `-v`, `-f`, `-n` (tag -v exit 0 ONLY when signer verified against `gpg.ssh.allowedSignersFile` — check-novalidate alone exits 1, per .NET issue #24); new `verify-commit <rev>`.
- Test: in-crate tests (hooks via a tmp script; signing via generated ed25519 key when `ssh-keygen` present, else skipped; annotated tag flow incl. peeling through clone/gc).

**Steps:**
- [ ] hooks.rs + tests (failing first), wire commit
- [ ] manifest.rs TagObject + signature fields + tests
- [ ] repository.rs annotated tag APIs + peeling + tests
- [ ] tag-aware reachability (transfer/remote/fsck/gc) + clone-with-annotated-tag test
- [ ] sign.rs + commit -S / tag -s / tag -v / verify-commit + tests
- [ ] README.md + CLAUDE.md; fmt/clippy/test; commit

## Task 2: Incremental commit chunk cache

**Files:**
- Create: `crates/repo/src/chunk_cache.rs` — `ChunkCache::load(repo_dir)` from `<repo>/chunkcache.json` (corrupt → empty); `get(key) -> Option<String>`, `set(key, id)`, `save()` atomic (tmp + rename). Key = `"{compression}:{blake3(compressed payload)}"`. Backed by `Mutex<HashMap>`.
- Modify: `crates/repo/src/snapshot.rs` — in `try_chunks`, before decode: cache hit AND `store.exists(id)` → reuse id; miss → decode + canonicalize + put + record. `snapshot()` loads + saves the cache; `hash_only` uses it read-only.
- Test: commit world twice; assert second snapshot produces identical manifest with cache file present; corrupt-cache tolerance; cache never trusted without `exists()`.

**Steps:**
- [ ] chunk_cache.rs + tests
- [ ] snapshot.rs integration + determinism tests
- [ ] world-roundtrip-gauntlet agent on compare-worlds
- [ ] docs; fmt/clippy/test; commit

## Task 3: reflog + bisect + stash drop + verify-remote

**Files:**
- Modify: `crates/repo/src/repository.rs` — `record_head(from: Option<&str>, to, message)` appends `"<from|64 zeros> <to> <msg>"` to `logs/HEAD`; `reflog() -> Vec<String>` (most recent first); `reflog_commit_at(n)`; `resolve_ref` handles `HEAD@{n}` / `@{n}`. `parents_of(hash)` helper (also used by Task 4 shallow grafting).
- Create: `crates/repo/src/bisect.rs` — port `Bisect.cs`: state files `BISECT_START/BAD/GOOD/SKIP/LOG` in repo dir; `compute()` → {need_marks, done(first_bad), next, remaining}; suspects = ancestors(bad) − ∪ancestors(good); next = candidate whose suspect-ancestor count is closest to half.
- Modify: `crates/repo/src/stash.rs` — `drop_top(repo) -> Option<String>` (pop the stack without checkout).
- Modify: `crates/repo/src/remote.rs` — `verify_remote(t, deep) -> VerifyReport{branches, commits, objects, missing, corrupt}`: walk advertised branch tips, hash-check + parse each commit + tree, collect leaves; deep → fetch+hash each leaf, else batched `missing()`.
- Modify: `crates/cli/src/main.rs` — `reflog` command; `record_head` at every HEAD/branch move (commit, checkout, merge, reset, revert/cherry-pick/rebase advance, pull, bisect); `bisect start|bad|good|skip|reset|log` (checkout next suspect detached; reset restores original); `stash drop`; `verify-remote [remote] [--deep]` (exit 1 on missing/corrupt).

**Steps:**
- [ ] reflog APIs + @{n} resolution + record_head call sites + tests
- [ ] bisect.rs + CLI + synthetic-history test (plant regression, drive bisect to the first bad commit)
- [ ] stash drop + test
- [ ] verify_remote + CLI + test (in-process transport; corrupt an object, assert detection)
- [ ] docs; fmt/clippy/test; commit

## Task 4: pack-on-the-wire push + shallow clone

**Files:**
- Modify: `crates/repo/src/remote.rs` — `Transport::put_objects(&self, objects: Vec<(String, Vec<u8>)>)` default = per-object `put_object`; HTTP + stdio overrides build one pack: zstd-per-object concat + JSON idx `{id: [off, len]}`, framed `[u32 BE idx len][idx][pack]` (HTTP `POST /pack`; stdio verb `put-pack`). `push()` batches missing objects by ≤64 MiB compressed per pack.
- Modify: `crates/repo/src/serve.rs` — `op_put_pack(dir, body)`: unframe, parse idx, per entry slice + **bounded** zstd decode + blake3 verify before store (tampered/oversized pack rejected); route in HTTP handler + stdio dispatch.
- Modify: `crates/repo/src/repository.rs` — `shallow` file: `shallow_boundary() -> HashSet<String>`, `write_shallow(iter)`, `is_shallow()`; `parents_of()` grafts to `[]` at the boundary.
- Modify: callers that walk parents use `parents_of`: CLI log/show, `transfer.rs::reachable`, `remote.rs::fetch_reachable`, `merge.rs` ancestor walks, `fsck.rs`/`gc.rs`.
- Modify: `crates/repo/src/remote.rs::clone` + CLI `clone --depth <n>` — BFS to depth, record boundary commits (parents pruned), skip tags when shallow.
- Test: pack round-trip over in-process stdio pair + HTTP; corrupt-pack rejection; shallow clone of a 5-commit chain at depth 2 → 2 commits, boundary grafted, log terminates, checkout works.

**Steps:**
- [ ] put_objects + pack framing + server ingest + tests (incl. hostile pack)
- [ ] trust-boundary-exploit-hunter agent pass on pack ingest
- [ ] shallow file + parents_of sweep + clone --depth + tests
- [ ] docs; fmt/clippy/test; commit

## Task 5: S3/Azure bucket remotes

**Files:**
- Create: `crates/repo/src/bucket.rs` — `trait Bucket { get(key) -> Result<Option<(Vec<u8>, String)>>; put(key, data); put_if_match(key, data, Option<&str>) -> Result<bool>; list(prefix) -> Result<Vec<String>>; }` + `InMemoryBucket` (ETag seq) for tests. `BucketTransport` (port of `BucketTransport.cs`) implementing `Transport` + batch `put_objects`: layout `<prefix>/HEAD`, `refs/heads/<b>`, `refs/tags/<t>`, `packs/<id>`, `packs/<id>.idx`, `packs/manifest` (CAS append, 20 retries); pack ids validated as 64-hex before any local path use; ref updates ETag-CAS'd.
- Create: `crates/repo/src/cloud.rs` — `S3Bucket` (AWS SigV4 over ureq; env: standard AWS vars + `S3_ENDPOINT_URL` path-style for R2/MinIO) and `AzureBucket` (SharedKey over ureq; env: `AZURE_STORAGE_ACCOUNT`/`AZURE_STORAGE_KEY` or connection string). Signing unit-tested against published test vectors; network paths smoke-only.
- Modify: `crates/repo/src/remote.rs::connect` — `s3://bucket[/prefix]`, `azure://account/container[/prefix]` dispatch (replacing the stub error).
- Modify: `crates/repo/Cargo.toml` — add `sha2`, `hmac`, `base64`.
- Test: full clone/push/pull/verify-remote protocol against `InMemoryBucket`; concurrent CAS conflict; malformed pack-id rejection; SigV4/SharedKey signature vectors.

**Steps:**
- [ ] bucket.rs trait + InMemoryBucket + BucketTransport + protocol tests
- [ ] cloud.rs S3 SigV4 + Azure SharedKey + signing tests
- [ ] connect() dispatch + CLI docs
- [ ] trust-boundary-exploit-hunter agent pass (bucket manifest/pack ids are attacker-controlled)
- [ ] docs (README transports table, CLAUDE.md); fmt/clippy/test; commit

---

**Per-item gate:** `cargo test --all` + `cargo fmt --all -- --check` + `cargo clippy --all-targets -- -D warnings`; delegation agents as listed; e2e round-trip on `compare-worlds/` for items touching snapshot/checkout/transfer (2, 4).
