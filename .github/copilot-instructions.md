# Copilot / AI agent instructions for mcagit

mcagit is a semantic, git-style diff / patch / version-control tool for Anvil-format Minecraft
(Java Edition) worlds. It is a **Rust** cargo workspace of six crates producing one binary,
`mcagit`. Key deps: `flate2` (zlib/gzip), `lz4_flex` (LZ4 chunks), `blake3` (object ids),
`zstd` (object/pack compression), `rayon`, `memmap2`, `arc-swap`, `serde`, `clap`.

When suggesting code, hold these load-bearing invariants. They are correctness, not style.

## Architecture

Three layers share one core. **Diff** (display), **patch** (extract / apply), and **repo**
(VCS) all sit on the same NBT comparison walk and canonical encoding. Keep them in sync by
construction, not duplication. Deps point downward: `nbt → anvil → {diff, repo} → patch → cli`.

- `crates/nbt` (`mca-nbt`) — NBT model, big-endian read/write, canonical bytes, list-element
  identity, the path language, lossless type-tagged JSON.
- `crates/anvil` (`mca-anvil`) — the region container: `RegionFile`, `RegionWriter`,
  `RawChunk`, chunk codecs (zlib/gzip/none/lz4), external `.mcc`, standalone `.dat`.
- `crates/diff` (`mca-diff`) — one comparer walk reports to a `DiffSink` trait; `ChangeSink`
  (display) and the patch op sink (applyable ops). `WorldDiffer` is parallel with
  byte-identical fast paths. Text + JSON output.
- `crates/patch` (`mca-patch`) — `.mcapatch` JSON; invertible, 3-way guarded.
- `crates/repo` (`mca-repo`) — content-addressed VCS; dedup unit is the chunk. blake3 ids,
  zstd loose objects, mmap'd packfiles + pack-at-commit, parallel commit/checkout, verify,
  status, fsck, gc, merge, replay, stash, path-transport.
- `crates/cli` (`mcagit`) — the `clap` binary (28 subcommands).

## Invariants

- **One comparison walk.** Any change to diff semantics goes through the comparer / `DiffSink`
  so the display diff and the extracted patch cannot drift. Do not write a second comparison.
- **Identity-based list matching.** Lists that behave as sets are matched by identity
  (block coords, entity UUID, inventory slot, string `id`, then index fallback), so a reorder
  is not a rewrite.
- **Canonical determinism.** The canonical encoding produces deterministic bytes independent of
  on-disk compression or map ordering; it is the basis of content hashing. An unchanged chunk
  must hash identically. Never make it depend on iteration order or a diff-path normalization.
- **Diff-only normalizations stay out of canonical.** Representation differences Minecraft
  treats as equal are cancelled in the diff path, applied to decoded roots before comparing —
  never on the storage / canonical path.
- **Lossless type-tagged JSON.** Types are encoded (`{"long":"383"}`); longs beyond 2^53 and
  float/double NaN/Inf are string-encoded and must survive round-trips. Never encode them as
  JSON numbers.
- **Never mutate in place.** `diff` / `extract` / `status` / `verify` never modify a world.
  `apply` only writes a fresh output dir. Only `checkout` / `reset --hard` / `merge` / `rebase`
  / `clean` / `stash` touch the bound worktree.
- **Reproduction.** `commit` → `checkout` must reproduce a playable world; tests assert exact
  reproduction. Patches are 3-way guarded and invertible (`apply --reverse`).
- **LZ4 is decoded; Custom is not.** Compression type 4 (LZ4) is fully decoded / re-encoded.
  Only type 127 (Custom) is unsupported — a region containing one falls back to a raw blob.
- **Untrusted input is confined.** Manifest keys, patch paths, and network-supplied names are
  path-confined before being materialized to disk; object ids are validated; size-bound every
  inflate of untrusted bytes and depth-check NBT before parsing.

## Conventions

- Exit codes follow git: `0` identical / clean, `1` differences / conflicts, `2` error.
  `commit` prints the new commit hash to stdout.
- Tests are synthetic via in-crate helpers — no binary fixtures. See `TESTING.md`.
- Run `cargo fmt --all -- --check` and `cargo clippy --all-targets -- -D warnings` before
  committing (CI gates on both). Project guidance: `CLAUDE.md`.
