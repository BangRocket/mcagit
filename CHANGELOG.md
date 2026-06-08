# Changelog

All notable changes to mcagit are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project uses semantic-ish `v0.x`
tags. Releases `v0.2.0`–`v0.7.0` were the original .NET implementation — see the git tag history.

## [0.8.0] - 2026-06-08

### Changed

- **mcagit is now a Rust implementation and the sole, primary version.** The tool was rewritten
  in Rust (parallel, streaming, native) — checkout is ~4.7× faster than the .NET serial baseline
  on a full world, with on-par commit and ~2.8× faster diff. Internal repo and patch formats are
  **clean-slate** and are **not** compatible with repositories created by the .NET v0.x releases.
- The cargo workspace was promoted to the repository root (`Cargo.toml` + `crates/` at top level);
  build with `cargo build --release`.

### Added

- **`verify`** — fast single-sided tree-hash accuracy check: re-hashes a world's canonical tree
  and compares it to a commit's tree (no second decode, no tree-walk).
- `--version` on the CLI.

### Removed

- The .NET implementation (`src/`, `tests/`, `McaGit.sln`) and its .NET build / release / CodeQL CI.
- Capabilities that existed in the .NET v0.x line but are **not yet ported** to Rust: network &
  cloud remotes (http/ssh/stdio transports, `serve`, S3/Azure), shallow clone, pack-on-the-wire,
  coordinate-level block/biome diff, world-state inspection (`inspect`/`find`/`players`/`poi`/
  `where-changed`), `log` metadata filters, `verify-remote`, reflog (`HEAD@{n}`), `bisect`, and
  SSH commit/tag signing. The object-copy core ships via local path-transport clone/push/pull.

### Fixed

- **Patch `extract`/`apply` now reproduces real worlds round-trip** — forward and `--reverse`,
  verified on the sample worlds and gated in CI. Two bugs: (1) floats/doubles were JSON-number
  encoded and lost up to 1 ULP through serde_json's default parser — now string-encoded like
  longs (also fixes NaN/Inf); (2) `NbtPath::set` silently no-op'd when appending a new trailing
  list element (e.g. a growing block-state palette) — it now appends at `index == len`.
