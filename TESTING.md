# Testing guide

All tests are synthetic — there are **no binary fixtures**. Inputs are built in-memory by the
in-crate test helpers and written to temp dirs (`tempfile`). This keeps the suite fast,
deterministic, and reviewable. Tests live in `#[cfg(test)]` modules next to the code they
exercise. When adding a test, pick the narrowest type that proves the behavior.

## Test types

### Unit — NBT and core logic

Exercise one module with hand-built NBT values. No region files, no repo. Reach for this for
the comparer, identity, path, JSON, canonical encoding, the object store, merge, and
formatters. Build two NBT compounds and assert the list of changes.

### Wire-format — the bytes on disk

Round-trip through the real container or encoding and assert the bytes / decoded tree survive.
Reach for this for `RegionWriter`, the chunk codecs (incl. LZ4), packfiles, canonical
determinism, and JSON losslessness (especially longs beyond 2^53). Write a region, read it
back, assert chunk identity.

### Integration — patch apply and repo ops

Drive a multi-step flow against synthetic worlds: extract a patch and apply it, or commit and
check out. Assert the result reproduces the expected world (diff reports no differences, or
`verify` matches the commit tree).

### End-to-end — the round-trip gauntlet

The CI `e2e` job runs the release binary against the real sample worlds in `compare-worlds/`
(`New_World_Older` / `New_World_Newer`): cross-diff finds changes, forward/reverse patch
round-trips, and commit → checkout → `verify` reproduces. Use stable refs, never `HEAD~1`
(checkout detaches HEAD and later relative refs drift) — capture explicit commit hashes.

### Invariant — the properties that must always hold

Guard the load-bearing properties directly:

- `commit` → `checkout` reproduces a world exactly (and `verify` confirms the tree hash).
- The canonical encoding is deterministic regardless of on-disk compression.
- Identity-based list matching survives reorders.
- A diff-only normalization never changes a stored object's hash.

## Conventions

- Build inputs programmatically via the in-crate test helpers; allocate scratch dirs with
  `tempfile::tempdir()`.
- Prefer deterministic concurrency primitives over sleeps.
- A test that depends on an unavailable environment should be `#[ignore]`d with a reason rather
  than silently returning.

## Running

```sh
cargo test --all                       # everything
cargo test -p mca-repo                  # one crate
cargo test -p mca-diff comparer         # by name fragment
```
