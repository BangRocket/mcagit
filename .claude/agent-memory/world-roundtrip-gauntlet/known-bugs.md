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

## Rust Implementation — Gauntlet Results (2026-06-09, branch fix/issue-43-41-small-gaps, commit after 4073347)

All checks PASSED for branch fix/issue-43-41-small-gaps:
- Invariant 1: Extract → apply → diff (forward) — PASS (18 file entries, 4712 ops, exit 0)
- Invariant 2: Apply --reverse → diff (reverse) — PASS (4712 ops, exit 0)
- Invariant 3: Commit → checkout → verify (2-commit, Older then Newer) — PASS
  - stdout last line is valid 64-hex hash in all commits (progress callback does not corrupt stdout)
  - "nothing to commit" fast path works correctly (cache hit on same-world second commit attempt)
- Invariant 4 (GC → checkout → verify): PASS — gc kept 4389 objects, pruned 0; fsck 0 corrupt/missing/unreachable
- Branch reflog smoke test: PASS
  - write_branch appends to logs/refs/heads/<branch> correctly on each commit
  - `reflog test-branch` shows 2 entries (indices 0 and 1)
  - `rev-parse test-branch@{1}` resolves to prior tip (TB1 hash) correctly
  - `@{2}` correctly errors when only 2 entries exist (expected behavior)
- cargo test --all: 157 tests, 0 failed (all crates green)

### Note: Reflog @{n} counting
Branch reflog starts counting from the FIRST UPDATE (not from branch creation). If a branch is
created pointing at commit X and then 2 commits are made: @{0}=tip, @{1}=prior tip.
The creation point (X) is NOT in the reflog for the branch — consistent with git behavior.

## Rust Implementation — Transport Gauntlet Results (2026-06-09, commit ce73f40, branch fix/issue-43-41-small-gaps)

All transport checks PASSED. Batched-fetch change (ce73f40) does not break world reproduction:
- Path clone (2 commits, Older+Newer): PASS — checkout each commit, diff vs original = exit 0; fsck 0 corrupt/missing
- Path fetch (3rd commit added to origin, fetched into clone1): PASS — "1 objects" fetched; checkout + diff exit 0; fsck 0 corrupt/missing (4390 objects)
- SSH/stdio transport (MCAGIT_SSH=fake-ssh wrapper, MCAGIT_REMOTE_BIN=mcagit): PASS — clone of 3-commit origin; all 3 commits checkout cleanly + diff exit 0; fsck 0 corrupt/missing
- HTTP transport (serve + push + clone + fetch): PASS — 4390 objects pushed; clone checked out HEAD and commit 2, both diff exit 0; fetch of 4th commit + checkout + diff exit 0; fsck 0 corrupt/missing (4391 objects after 4th commit)
- cargo test --all: 160 tests, 0 failed

Note on "1 objects" in fetch output: when the 3rd commit re-uses chunks already in the clone (Older content is same as commit 1), fetch only transfers the new commit object itself (not leaves that already exist). Correct behavior — dedup working.

## Rust Implementation — Extended Gauntlet Results (2026-06-09, commit 4073347, branch feat/dotnet-parity)

All 5 cases PASSED. No bugs found in new transport/snapshot/gc features:
- Case 1: Extract → apply → diff (forward + reverse) — PASS
- Case 2: Commit → checkout → verify (2-commit, Older then Newer) — PASS
- Case 3: HTTP transport round-trip (serve/push/clone/checkout/verify) — PASS
  - streaming wire-pack ingest: 4389 objects pushed and cloned correctly
- Case 4: Shallow clone --depth 1 (3-commit repo, log terminates, checkout reproduces tip) — PASS
- Case 5: GC with annotated tag reachability (tag -a on HEAD~1, gc, checkout v1, verify + diff) — PASS
  - gc kept 4390 objects (tag object counted, not pruned), pruned 0
  - fsck post-gc: 0 corrupt, 0 missing, 0 unreachable

### Note: ChunkCache.Save Not Atomic (Rust, low severity)
- `crates/repo/src/chunk_cache.rs` — concurrent commit processes could corrupt chunkcache.json.
- On next load, corrupt cache is silently discarded (performance penalty only, no correctness impact).
- Verified in gauntlet: corrupt cache is handled gracefully, cache regenerated correctly.

### Note: ChunkCache Unbounded Growth (Rust, low severity)
- `crates/repo/src/chunk_cache.rs` — entries never evicted; keys are content-derived hashes.
- For long-lived repos with many commits the file can grow large (593KB observed for 2652-chunk world).
- No correctness impact.
