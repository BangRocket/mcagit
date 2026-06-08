# Contributing to mcagit

Thanks for helping. mcagit is a **Rust** cargo workspace of six crates
(`nbt → anvil → {diff, repo} → patch → cli`) producing one binary, `mcagit`. Key deps:
`flate2` (zlib/gzip), `lz4_flex`, `blake3`, `zstd`, `rayon`, `memmap2`, `arc-swap`, `serde`,
`clap`.

## Build and test

Requires a stable Rust toolchain (see `rust-toolchain.toml`).

```sh
cargo build --release                       # binary: target/release/mcagit
cargo test --all                            # full suite (all synthetic — no fixtures)
cargo test -p mca-repo                       # one crate
cargo test -p mca-diff comparer              # by name fragment
cargo run -p mcagit -- <args>                # run the CLI
```

## Before you push

1. `cargo test --all` — the full suite must be green locally.
2. `cargo fmt --all -- --check` and `cargo clippy --all-targets -- -D warnings` — CI gates on
   both.
3. If you changed user-facing behavior, update `README.md` (and the relevant `docs/` spec).

## Writing tests

Tests build synthetic worlds and regions through the in-crate test helpers (see the test
modules in `crates/anvil` and `crates/repo`) — there are **no binary fixtures**. Construct
inputs programmatically; assert exact world reproduction.

## Architecture in one paragraph

Three layers share one core: **diff** (display), **patch** (extract/apply), and **repo** (VCS)
all sit on the same NBT comparison walk (`crates/diff` comparer + `DiffSink`) and canonical
encoding (`crates/nbt`). Any change to diff semantics must go through the comparer/sink so
display and patch cannot drift. The region container lives in `crates/anvil`, the NBT semantics
in `crates/nbt`, and the VCS in `crates/repo`. See [`CLAUDE.md`](CLAUDE.md) for the full
architecture and the load-bearing invariants.

## Invariants you must preserve

- `commit` → `checkout` reproduces a world faithfully (playable in Minecraft); tests assert
  exact reproduction, and `verify` re-hashes a world's canonical tree against a commit.
- The canonical NBT encoding is deterministic and version-independent — never make it depend
  on map ordering or a diff-path normalization.
- Diff-only normalizations belong in the diff path, never on the canonical/storage path.
- `diff` / `extract` / `status` / `verify` must never modify a world.

## Pull requests

CI re-runs `cargo fmt`, `clippy`, `cargo test --all`, an end-to-end round-trip on the sample
worlds, and markdownlint. Keep them green. Describe what you changed and why; if you touched
diff/patch/repo semantics, note how you verified the round-trip still holds.

## License

By contributing you agree your contributions are licensed under the project's
[GPL-3.0](LICENSE).
