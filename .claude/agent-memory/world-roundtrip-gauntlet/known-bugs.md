---
name: known-bugs
description: Confirmed bugs found during gauntlet and adversarial code review, with severity and file locations
metadata:
  type: project
---

# Known Bugs

## Note: .NET-era bugs (pre-Rust port) — listed for historical context only
The bugs below were found during the .NET implementation audit (2026-06-03). The Rust port
is the sole active implementation. These are retained as reference for whether equivalent
issues exist in the Rust codebase.

### [.NET] BLOCKER: Worktree == RepoDir Destroys Repository
- `src/McaGit/Repo/Checkout.cs`, `PruneStray()` — if worktree == repoDir, checkout deletes repo internals.

### [.NET] HIGH: Packfile Write Non-Atomic (crash during gc loses index)
- `src/McaGit/Repo/Packfile.cs` — .pack moved before .idx; crash between = inaccessible objects.

### [.NET] HIGH: GC Non-Idempotent (always rewrites pack on 2nd run)
- `src/McaGit/Repo/ObjectStore.cs` / `Gc.cs` — StoredSize returns 0 for packed objects, causing re-sort and re-pack on every even gc run.

### [.NET] MED: Delta.Apply Sign Extension for Large Offsets
- `src/McaGit/Repo/Delta.cs` — latent, only triggers for offsets >= 2^31 bytes (not possible for Minecraft chunks).

### [.NET] MED: .mcaignore Used Before Written During Checkout
- `src/McaGit/Repo/Checkout.cs` — PruneStray called before blobs written.

### [.NET] LOW: IgnoreRules Glob Patterns with '/' Never Match
- `src/McaGit/Repo/IgnoreRules.cs` — patterns matched against filename only, not full path.

### [.NET] LOW: Stale .tmp Object Files Not Cleaned Up
- `src/McaGit/Repo/ObjectStore.cs` — temp files accumulate after crash.

### [.NET] LOW: Chunk Timestamp Loss on Checkout
- `src/McaGit/Repo/Checkout.cs` — all chunks written with timestamp=0.

### [.NET] LOW: 0-byte .mca → 8192-byte on checkout (empty region expansion)
- `src/McaGit/Repo/Checkout.cs` / `Anvil/RegionWriter.cs` — RegionWriter always writes 8KiB header.

## Rust Implementation — Gauntlet Results (2026-06-09, commit 85e7ea6)

All 5 gauntlet cases PASSED. No bugs found in:
- Extract → apply → diff (forward)
- Apply --reverse → diff (reverse)
- Commit → checkout → verify (including chunk cache fast path and partial hit path)
- GC → checkout → verify
- Chunk cache resilience (delete / corrupt)

### Note: ChunkCache.Save Not Atomic (Rust, low severity)
- `crates/repo/src/chunk_cache.rs` — concurrent commit processes could corrupt chunkcache.json.
- On next load, corrupt cache is silently discarded (performance penalty only, no correctness impact).
- Verified in gauntlet: corrupt cache is handled gracefully, cache regenerated correctly.

### Note: ChunkCache Unbounded Growth (Rust, low severity)
- `crates/repo/src/chunk_cache.rs` — entries never evicted; keys are content-derived hashes.
- For long-lived repos with many commits the file can grow large (593KB observed for 2652-chunk world).
- No correctness impact.
