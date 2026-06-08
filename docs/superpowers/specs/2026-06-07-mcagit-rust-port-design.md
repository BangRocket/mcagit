# mcagit → Rust Port — Design

**Date:** 2026-06-07
**Status:** Approved (design); planning sub-project #1 next
**Author:** Joshua Heidorn (with Claude)

## Summary

Reimplement `mcagit` — the semantic, git-style diff/patch/version-control tool for
Anvil-format Minecraft worlds — in Rust, with **speed as the primary goal**. The
existing .NET 10 implementation (~11k lines, 89 files, 35 subcommands) is a proven
proof of concept; this is a full **feature-parity rewrite**, not a partial port or an
FFI hybrid.

The rewrite is **clean-slate**: it must faithfully reproduce Minecraft worlds and pass
equivalent tests, but is free to choose its own object hashing, canonical NBT form,
packfile/delta format, and internal compression. Existing .NET repos are **not** required
to be readable by the Rust tool.

## Goals & Non-Goals

### Goals

- **Full parity** with all 35 .NET subcommands and their git-like semantics (exit codes
  `0`/`1`/`2`, `-C <repo>`, revision syntax, `.mcaignore`, hooks, SSH signing, cloud remotes).
- **Speed on four axes:**
  1. **Multicore wall-clock** — checkout/commit/diff/gc saturate all cores.
  2. **Low memory / streaming** — mmap regions, decode on demand, region-by-region.
  3. **Fast startup** — native binary, no JIT warmup.
  4. **Incremental at scale** — skip unchanged chunks without re-decoding; persistent cache.
- **Faithful Minecraft reproduction** — commit → checkout yields a world playable in
  Minecraft; round-trip invariants hold.

### Non-Goals

- On-disk/wire compatibility with .NET repos (clean-slate; no cross-tool repo interop).
- Matching Minecraft's exact compressed bytes (we choose compression levels for speed).
- Preserving the .NET object-store format, packfile format, or hash algorithm.

## Baseline (.NET, measured 2026-06-07)

Measured on the `dobbscraft-snapshots`: 8 incremental snapshots of one world, ~2.4 GB /
~3,200 files / ~310k chunks each, committed as a linear history. Machine: macOS, 8 cores.

| Operation | .NET time | Notes |
|---|---|---|
| commit (cold, full world) | 43 s | snapshot 1 |
| commit (incremental) | ~11 s | snapshots 2–8 (raw-payload fast path) |
| checkout (cold, loose objects) | **268 s** | full 2.4 GB world; **serial** |
| diff (full world) | 63–91 s | already `Parallel.For` over regions |
| gc (repack ~2 GB) | **1260 s** | serial pack concat dominates |
| fsck (388k objects) | fast | — |
| object-store dedup | 19 GB raw → ~2.1 GB | chunk-level content addressing |

**Root cause of the headline cost:** `Checkout.Materialize` is fully serial (1 of 8
cores) and re-deflates ~310k chunks at zlib Optimal. Only 3 loops in the entire .NET
codebase are parallel (commit/Snapshotter, diff/WorldDiffer, gc/Packfile); checkout,
apply, extract, fsck, and transfer are serial. The Rust rewrite parallelizes by default.

## Architecture

A cargo workspace; dependencies point strictly downward
(`cli → remote → repo → patch → diff → anvil → nbt`). The Rust tree lives in **this
repo** under `rust/` during the port, so the .NET binary can serve as a live correctness
oracle and we can reuse `compare-worlds/` + the dobbscraft snapshots and one PR history.
It may split into its own repo once it reaches parity.

```text
rust/
  Cargo.toml                 (workspace)
  crates/
    nbt/      (mca-nbt)      tag model, parse/write, identity, path, canonical, json
    anvil/    (mca-anvil)    RegionFile, RegionWriter, RawChunk, ChunkCodec
    diff/     (mca-diff)     NbtComparer + sink trait, 2 sinks, WorldDiffer, formatters
    patch/    (mca-patch)    .mcapatch model, extract, apply (3-way, reverse)
    repo/     (mca-repo)     object store, manifest, commit, checkout, status, merge/…, gc
    remote/   (mca-remote)   transports (path/http/ssh/stdio), transfer, serve, S3/Azure
    cli/      (mcagit bin)   clap CLI, 35 subcommands
    testkit/  (mca-testkit)  synthetic-world builder (TestAnvil-equivalent, no fixtures)
```

### Module responsibilities (mirrors the .NET layering)

- **`mca-nbt`** — semantic foundation: `NbtValue`, parse/write, `NbtIdentity` (list-element
  matching), `NbtPath` (path language), `NbtCanonical` (deterministic bytes), `NbtJson`
  (lossless type-tagged JSON).
- **`mca-anvil`** — region container: `RegionFile` (mmap read), `RegionWriter` (write),
  `RawChunk`, `ChunkCodec` (payload ↔ tree; zlib/gzip/lz4/none).
- **`mca-diff`** — `NbtComparer` does one tree walk, reports leaf decisions to an
  `IDiffSink`-equivalent trait. Two sinks (display rows, patch ops). `WorldDiffer`
  orchestrates file/chunk/world level with fast paths and per-region parallelism. **Any
  change to diff semantics goes through the comparer/sink so display and patch can't drift.**
- **`mca-patch`** — `.mcapatch` JSON; every op records `base` + `value` (invertible,
  `apply --reverse`); 3-way guarded apply; `apply` never mutates the target.
- **`mca-repo`** — object store, manifest (≈ tree), commit/checkout/status/staging, refs/
  HEAD/config, discovery, plus merge-base/merge, rebase, stash, bisect, fsck, hooks,
  reflog, revert, cherry-pick, gc.
- **`mca-remote`** — transports, transfer (copy only missing objects), serve, cloud.
- **`cli`** — clap CLI; `mcagit <A> <B>` with no subcommand falls through to `diff`.

## Clean-slate format & algorithm decisions

- **Object IDs:** `blake3` (parallel, ~10× faster than SHA-256). Loose objects at
  `objects/aa/rest`.
- **Internal object & pack compression:** `zstd` (faster + tighter than zlib) — repo-internal only.
- **Packfile:** custom format, **parallel write and parallel concat** (fixes the .NET gc's
  serial 21-min concat). Delta strategy chosen for parallelizability.
- **Canonical NBT:** deterministic key-sorted big-endian serialization → blake3. Stable
  across versions (an unchanged chunk hashes identically regardless of on-disk compression).
- **NBT JSON / `.mcapatch`:** lossless type-tagged JSON (`{"long":"383"}`); longs beyond
  2^53 survive round-trips as strings; invertible patches.
- **Hard constraint — Minecraft output:** region files written on checkout/apply MUST be
  valid Anvil — chunk payloads in zlib (type 2) / gzip (1) / lz4 (4) / none (3); 8 KiB
  header (1024 offsets + 1024 timestamps); 4 KiB sectors; external/oversized chunks via
  `.mcc` + the `0x80` length flag. **Checkout default: a fast zlib level** (clean-slate
  frees us from matching Minecraft's exact bytes — semantic diff still passes).
- **Undecodable chunks:** type 127 (Custom) → opaque blob fallback (a whole region with one
  falls back to a raw blob), matching .NET.

## Performance model (how the four axes are met)

- **Multicore:** `rayon` `par_iter` over regions for checkout/commit/diff/gc; each region →
  an independent output file, no shared mutable state. Per-region chunk work parallelizes
  where it helps.
- **Low memory / streaming:** `memmap2` region files; decode chunks on demand; process
  region-by-region so peak RAM ≈ a few regions, not the whole world; stream object writes.
- **Fast startup:** native binary, no JIT; lazily mmap packs; minimal global init.
- **Incremental at scale:** commit fast-path keys chunks by a cheap hash of the raw
  compressed payload → skips decode+canonical+rehash for unchanged chunks; a persistent
  `region+pos+rawhash → objectid` cache makes repeated backups of slowly-changing worlds
  skip untouched chunks entirely.

## Recommended crates

`rayon` (data parallelism), `flate2` w/ **zlib-ng** backend + `lz4_flex` (compression),
`zstd` (internal), `blake3` (hashing), `memmap2` (region reads), `clap` (CLI),
`serde`/`serde_json`, `anyhow`/`thiserror` (errors), `ssh-key` (signing),
`aws-sdk-s3` / `azure_storage_blobs` + `tokio` (cloud, M6 only).

## Validation strategy

- **Full test-suite port (primary):** re-author the ~5,700 lines of .NET tests as Rust
  `#[test]`s, plus the `mca-testkit` crate that builds synthetic worlds/regions in code
  (mirroring `TestAnvil.cs` conventions — **no fixtures, no mocks**). The
  `GitLikeTierN`-style tier tests map to Rust test modules.
- **E2E oracle cross-check (safety net):** a harness runs the .NET and Rust binaries on the
  same worlds (`compare-worlds/` + dobbscraft) and asserts identical *behavior* — Rust
  checkout produces a world both tools' diff call "No differences"; extract→apply→diff is
  clean; commit→checkout reproduces faithfully. Behavior, not bytes (formats differ by design).
- **CI:** `cargo test` per crate; `clippy` (deny warnings) + `rustfmt --check`; the e2e
  gauntlet on the real sample worlds.

## Invariants to preserve (from the .NET design)

- `diff`/`extract`/`status` never modify a world; `apply` only writes its fresh output dir;
  only `checkout`/`reset --hard`/`merge`/`rebase`/`bisect`/`clean`/`stash` touch the bound worktree.
- Commit → checkout reproduces a playable world; tests assert faithful reproduction.
- LZ4 chunks fully decoded and re-encoded; only type 127 remains opaque.
- The repo is bare and external to the world; a bound worktree is recorded in `config`;
  discovery walks up from cwd.

## Milestones (hot-path-first sequencing)

Each milestone is its own spec → plan → build cycle. **M0–M2 (the engine, sub-project #1)
is planned first.**

| M | Deliverable | Gate |
|---|---|---|
| **M0** | workspace + CI (fmt/clippy/test) + `mca-testkit` skeleton | builds; CI green |
| **M1** | `mca-nbt` complete + ported nbt tests | nbt unit tests green |
| **M2** | `mca-anvil` complete + ported anvil tests | round-trip a real dobbscraft chunk: decode→encode→decode = equal NBT |
| **M3** | hot-path vertical: object store (blake3+zstd) + manifest + commit (incremental fast-path) + **parallel checkout** | **benchmark vs .NET**: commit 8 snapshots + checkout; prove multicore speedup; .NET oracle says "No differences" |
| **M4** | `mca-diff` + `mca-patch` | comparer/patch/reverse tests green |
| **M5** | repo-advanced (status, staging, merge-base/merge, rebase, stash, bisect, fsck, reflog, revert, cherry-pick, gc parallel-concat, signing) | tier tests green |
| **M6** | `mca-remote` (transports/transfer/serve) + cloud (S3/Azure) | transport round-trip tests |
| **M7** | full CLI parity (35 cmds, exit codes, output formats) + e2e gauntlet + README | parity + gauntlet green |

**M3 is the early speed proof** — checkout target: 268 s → well under (multicore + fast
zlib level); commit faster than 43 s/11 s; correctness gated by the .NET oracle.

## Open questions (resolved)

- Scope → **full feature-parity rewrite**.
- Fidelity → **clean-slate (behavioral parity, own formats)**.
- Speed axes → **all four** (multicore, low-mem/streaming, fast startup, incremental).
- Validation → **full test-suite port** (+ e2e oracle as safety net).
- Build sequencing → **layered workspace, hot-path-first**.
- Location → **`rust/` in this repo during the port**.
