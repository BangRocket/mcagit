# Changelog

All notable changes to mcadiff are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project uses semantic-ish `v0.x`
tags. Releases `v0.2.0`–`v0.7.0` predate this file — see the git tag history for those.

## [Unreleased]

### Added

- **Serverless cloud remotes** — `azure://` (Azure Blob) and `s3://` (any S3-compatible store) push/clone
  with no daemon: missing objects bundle into one content-addressed pack per push (~3 bucket ops),
  refs/manifest use ETag compare-and-swap. See `docs/cloud-backend.md`.
- **`verify-remote [--deep]`** — offsite integrity check over any transport (hash-checks the
  commit/tree spine and leaf presence; `--deep` re-hashes everything).
- **Shallow clone** — `clone --depth N`, history grafted at a boundary so log/checkout/gc/fsck terminate.
- **Pack-on-the-wire** — push over path/http/ssh now bundles new objects into one delta-compressed pack
  instead of N round-trips (the bucket backend already did this).
- **Coordinate-level block & biome diff** — `sections[Y].block_states[@x,y,z]: stone → air` instead of
  an opaque `long[]` delta (worldgen-scale sections summarize; `--expand` lists each).
- **World-state inspection** (read-only) — `inspect <x y z>`, `find <entity|block-entity|sign>`,
  `players`, `poi`, and `where-changed <old> <new>` (the grief detector).
- **`log` metadata filters** — `--author`, `--grep`, `--since`/`--until`, `--merges`/`--no-merges`.
- **`serve-stdio --read-only`**, **`remote` remove/rename/set-url/get-url**, **`clean -d`**,
  **`config --global`** outside a repo, **abbreviated hashes** (shortest-unambiguous).
- **GPL-3.0 LICENSE**; install/quickstart; `docs/repo-format.md`, `docs/mcapatch-format.md`,
  `SECURITY.md`, `CONTRIBUTING.md`, `TESTING.md`, `docs/architecture/` ADRs.

### Changed

- Target framework migrated from `net9.0` (STS) to **`net10.0`** (LTS).
- `commit` / `push` take an inter-process repository lock (fail-fast) so concurrent runs can't drop a
  commit; `commit --push` / `commit --json` for backup drivers.
- `clean` / `reset --hard` / force-`checkout` confirm before destroying data (skipped with `-y` or when
  stdin isn't a terminal).
- `checkout` / `reset --hard` refuse a world that's open in Minecraft (`session.lock`) before writing.

### Fixed

- **Single-entry-palette false diffs** — a section going all-stone/all-air no longer diffs as a spurious
  `block_states.data` add/remove.
- Loose-file coverage (`data/*.nbt`; single-file `.json`/`.mcc` byte-compare).
- git-fidelity leftovers: `push --all` exit code, `config --global`, `clean -d`, `remote` subcommands.
- First-run crashes: `init` in a world folder, `checkout` while the server is up; unknown subcommands
  now suggest a correction instead of silently running a diff; no raw stack traces.

### Security

- **Decompression bombs** bounded at every inflate callsite (`SafeInflate`).
- **Deeply-nested NBT** no longer crashes the process (non-recursive `NbtDepthGuard` pre-parse scan).
- **Bucket pack-ID path traversal** blocked (40-hex validation before any path use).
- **`tag -v`** now exits non-zero for an untrusted signer; ssh-keygen works on Windows.
- **NTFS Alternate Data Stream** writes rejected on Windows (`PathGuard`).
