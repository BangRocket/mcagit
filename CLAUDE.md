# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`mcagit` — a semantic, git-style diff/patch/version-control tool for Anvil-format Minecraft
(Java Edition) worlds, built in **Rust** for speed (parallel, streaming, native). It began as
a .NET proof-of-concept; the Rust implementation (validated against that original during the
port) is now the sole, primary version. A cargo workspace of six crates produces one binary,
`mcagit`. Key deps: `flate2` (zlib/gzip, `zlib-rs` backend), `lz4_flex` (LZ4 chunks), `blake3`
(object ids), `zstd` (loose-object + pack compression), `rayon` (parallelism), `memmap2`
(pack reads), `arc-swap` (lock-free pack list), `serde`/`serde_json`, `clap`.

The repo's internal formats are **clean-slate** — behaviorally equivalent to the original but
with their own (faster) on-disk encodings; not wire/disk compatible with the old .NET repos.

## Commands

```sh
cargo build --release                       # binary: target/release/mcagit
cargo test --all                            # full suite (synthetic worlds — no fixtures)
cargo test -p mca-repo                       # one crate
cargo test -p mca-diff comparer              # by name fragment
cargo fmt --all -- --check                   # formatting gate
cargo clippy --all-targets -- -D warnings    # lint gate
cargo run -p mcagit -- <args>                # run the CLI
```

- The `compare-worlds/New_World_Older` and `New_World_Newer` directories are real sample
  worlds (Anvil format) for manual + CI end-to-end round-trip checks (e.g.
  `cargo run -p mcagit -- diff compare-worlds/New_World_Older compare-worlds/New_World_Newer`).
- Tests build synthetic worlds/regions via the in-crate test helpers (see `mca-anvil` /
  `mca-repo` test modules) — use them when adding tests; do not add binary fixtures.

## CLI shape

`crates/cli/src/main.rs` is a `clap` derive over subcommands (git-style, with an optional
leading `-C <repo>`). Exit codes follow git: `0` = identical/clean, `1` = differences/conflicts,
`2` = error. `commit` prints the new commit hash to stdout (scriptable). Subcommands:
`init · commit [-S] · checkout · status · log · diff [--json] · extract · apply [--reverse] ·
verify · branch · merge · fsck · gc · revert · cherry-pick · rebase · stash [drop] · reflog ·
bisect · rev-parse · cat-file · ls-tree · tag [-a/-s/-m/-v/-f/-n] · verify-commit ·
reset [--hard] · restore · clean · clone · push · pull · fetch · ls-remote · remote ·
verify-remote [--deep] · serve · serve-stdio · config · players · find · inspect ·
where-changed · region · poi · render`.
Every HEAD move records a reflog entry (`logs/HEAD`; `HEAD@{n}` resolves against it).
Hooks (`<repo>/hooks/pre-commit`, `post-commit`) gate/follow `commit`; SSH signing
(`crates/repo/src/sign.rs`, `ssh-keygen -Y`, namespace `mcagit`) covers commits and
annotated tags — `verify-commit`/`tag -v` exit 0 only on an allowed-signers match.

## Architecture

Three layers share one core: **diff** (display), **patch** (extract/apply), and **repo** (VCS)
all sit on the same NBT comparison walk and canonical encoding — keep them in sync by
construction, not duplication. Workspace deps point downward: `nbt → anvil → {diff, repo} →
patch → cli`.

- **`crates/nbt` (`mca-nbt`)** — the semantic foundation: the NBT value model + big-endian
  read/write (Java modified-UTF8 strings); **canonical** (key-sorted) deterministic byte form
  that is the basis of content hashing (an unchanged chunk hashes identically regardless of
  on-disk compression); list-element **identity** (block coords, entity UUID, `Slot`, string
  `id`; index fallback) so reorders aren't rewrites; the **path** language (`Data.Player.Pos[0]`,
  `Entities[uuid:…].Pos[1]`); and **lossless type-tagged JSON** (`{"long":"383"}`) where longs
  beyond 2^53 and float/double NaN/Inf are string-encoded and must survive round-trips.
- **`crates/anvil` (`mca-anvil`)** — the region container: `RegionFile` (read), `RegionWriter`
  (write), `RawChunk` (compressed payload + metadata), chunk codecs (zlib/gzip/none/lz4 with
  bounded inflate), external `.mcc`, standalone `.dat` load/save. `compress`/`compress_level`
  for chunk payloads. Parsed/written directly; no library.
- **`crates/diff` (`mca-diff`)** — one comparer walk reports leaf decisions to a `DiffSink`
  trait. `ChangeSink` produces display rows; the patch op sink produces applyable ops. **Any
  change to diff semantics must go through the comparer/sink so display and patch can't drift.**
  `WorldDiffer` orchestrates file/chunk/world level with fast paths (byte-identical files
  skipped unparsed; byte-identical compressed chunk payloads skipped undecompressed) and
  per-region parallelism. Text + JSON output.
- **`crates/patch` (`mca-patch`)** — `.mcapatch` is JSON; every op records both `base` and
  `value`, making patches invertible (`apply --reverse`). Apply is 3-way guarded: a node is
  only changed if the target matches the patch's expected base, otherwise it's a reported
  conflict (skipped unless `--force`). `apply` never mutates the target — it writes a fresh
  output world.
- **`crates/repo` (`mca-repo`)** — a git work-alike whose unit of dedup is the *chunk*, hashed
  by decoded NBT:
  - `ObjectStore` — blake3 content ids over uncompressed canonical content; `zstd` loose
    objects at `objects/aa/rest`; mmap'd packfiles (`pack.rs`) with **pack-at-commit**. Pack
    list is read lock-free via `ArcSwap` (checkout/diff read objects hundreds of thousands of
    times across threads).
  - `Manifest` ≈ git tree (regions map chunk pos → chunk-object id; loose nbt + blobs map path
    → id); `CommitObject` JSON. `Repository` is **bare and external** to the world; the bound
    worktree is stored in the repo `config`; `Repository::discover` walks up from cwd.
  - `chunk_cache` — persistent compressed-payload-hash → chunk-object-id map
    (`chunkcache.json`) so incremental commits skip decode+canonicalize for unchanged raw
    chunk bytes; hits are trusted only after an `exists()` check.
  - `snapshot` (commit, parallel), `checkout` (parallel — one flat per-chunk rayon job list,
    then parallel region writes), `verify` (fast single-sided tree-hash accuracy check),
    `status`, `fsck`, `gc`, `merge_base` + 3-way `merge`, `replay` (cherry-pick/revert/rebase),
    `stash`, `transfer` (path-transport clone/push/pull — copies only missing objects),
    `wirepack` (batched push bodies: one zstd-per-object pack per request, hash-verified +
    inflate-bounded on ingest), shallow clones (`clone --depth`; `<repo>/shallow` boundary,
    all graph walks go through `Repository::parents_of` which grafts to empty there).

## Invariants worth preserving

- **One comparison walk.** Any change to diff semantics goes through the comparer + `DiffSink`
  so the display diff and extracted patch cannot drift. Do not write a second comparison.
- **Identity-based list matching.** Lists that behave as sets are matched by identity, not
  position (block coords → entity UUID → `Slot` → string `id` → index fallback).
- **Canonical determinism.** Canonical bytes are independent of on-disk compression and map
  ordering; they are the basis of content hashing. An unchanged chunk must hash identically.
  Diff-only normalizations stay out of the canonical/storage path.
- **Lossless type-tagged JSON.** longs beyond 2^53 and float/double NaN/Inf are string-encoded
  and must survive round-trips — never encode them as JSON numbers.
- **Never mutate in place.** `diff`/`extract`/`status`/`verify` never modify a world. `apply`
  only writes a fresh output dir. Only `checkout`/`reset --hard`/`merge`/`rebase`/`clean`/
  `stash` touch the bound worktree.
- **Reproduction.** `commit` → `checkout` must reproduce a playable world; tests assert exact
  reproduction, and `verify` re-hashes a world's canonical tree against a commit's tree.
- **LZ4 is decoded; Custom is not.** Compression type 4 (LZ4) is fully decoded/re-encoded.
  Only type 127 (Custom) is opaque — a region containing one falls back to a raw blob.
- **Untrusted input is confined.** Manifest keys / patch paths / network-supplied names are
  path-confined before being materialized to disk; thread depth limits through recursive NBT
  walks; size-bound every inflate of untrusted bytes.

## Agent delegation rules

Project agents live in `.claude/agents/`. Standing rules (paths are the Rust crates):

- Before declaring a change to `crates/diff`, `crates/nbt`, or `crates/patch` done, have
  `nbt-diff-invariant-reviewer` review it (comparer/sink parity, canonical determinism,
  lossless JSON, identity stability).
- Before declaring a substantive change to `crates/anvil`, `crates/patch`, or `crates/repo`
  done, run `world-roundtrip-gauntlet` — extract→apply→diff, `apply --reverse`, and
  commit→checkout→`verify` round-trips against the `compare-worlds/` sample worlds (mcagit's
  own diff/verify is the equivalence check — there is no external oracle).
- Changes touching transports, checkout/apply path handling, packfile parsing, or hooks get a
  `trust-boundary-exploit-hunter` pass.
- New tests follow the synthetic-world conventions (no binary fixtures).

> The agent definitions and their memory under `.claude/` still carry .NET-era context from the
> port and are being migrated to the Rust layout; treat their path references as the Rust
> crates above.

### Pre-PR checklist

1. `cargo test --all` green; `cargo fmt --all -- --check` and `cargo clippy --all-targets --
   -D warnings` clean.
2. Map the branch diff against the delegation rules and run every agent whose paths are touched.
3. End-to-end: a commit→checkout→`verify` (and diff) round-trip on the `compare-worlds/`
   worlds is clean.
4. Surface agent findings in the PR description: BLOCKERs fixed before opening; WARNs listed.

CI (`.github/workflows/`) re-runs fmt + clippy + `cargo test --all` and an e2e round-trip on
the sample worlds; `markdownlint.yml` gates docs. Update this file and `README.md` when
changing user-facing behavior.
