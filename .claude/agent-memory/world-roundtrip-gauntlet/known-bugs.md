---
name: known-bugs
description: Confirmed bugs found during gauntlet and adversarial code review, with severity and file locations
metadata:
  type: project
---

# Known Bugs (from 2026-06-03 audit)

## BLOCKER: Worktree == RepoDir Destroys Repository
- **File**: `src/McaGit/Repo/Checkout.cs`, `PruneStray()`
- If a user (or attacker) sets `config worktree = <repoDir>`, a full checkout/reset --hard deletes `objects/`, `refs/`, `HEAD`, `config` etc.
- `PruneStray` enumerates all files in worldOut, keeps only those in the manifest and .mcaignore. The repo's own files aren't in the manifest → deleted.
- **Fix**: Validate at checkout time that worldOut is not the repo dir or an ancestor of it. Or protect specific repo-internal dirs in PruneStray.

## HIGH: Packfile Write Non-Atomic (crash during gc loses index)
- **File**: `src/McaGit/Repo/Packfile.cs`, `Write()`, lines 173-175
- `File.Move(tmp, packPath)` succeeds, then `File.Move(tmp.idx, packPath.idx)` → if crash between, .pack exists but .idx doesn't.
- Next open: `ReadIndex(idxPath)` → `FileNotFoundException` → all packed objects inaccessible.
- **Fix**: Move .idx first, then .pack (git's approach). Or detect orphaned .pack without .idx in `OpenAll` and skip/repair.

## HIGH: GC Non-Idempotent (always rewrites pack on 2nd run)
- **File**: `src/McaGit/Repo/ObjectStore.cs` (`StoredSize`), `src/McaGit/Repo/Gc.cs` (`Repack`)
- After 1st GC, `StoredSize` returns 0 for all objects (only checks loose file). Sort order changes → different PackId → pack rewritten every 2nd GC.
- Confirmed: 1st GC produces pack-35e18973ca, 2nd GC produces pack-733fd84319, 3rd GC is stable.
- **Fix**: `StoredSize` should also consider packed size (query packfile), OR sort consistently (always by hash only) from the start.

## MED: Delta.Apply Sign Extension for Large Offsets
- **File**: `src/McaGit/Repo/Delta.cs`, `Apply()`, line 71
- `if ((cmd & 0x08) != 0) offset |= delta[dp++] << 24;` — if byte >= 0x80, `(int)0x80 << 24 = -2147483648`, making offset negative.
- `Array.Copy(baseBuf, offset, ...)` with negative offset → `IndexOutOfRangeException`.
- Only triggers for copy offsets >= 2^31 bytes, which doesn't occur for Minecraft chunks (< 1 MiB). But is a latent correctness hazard.
- **Fix**: `offset |= (uint)(delta[dp++]) << 24; ... Array.Copy(baseBuf, (int)offset, ...)` using unsigned arithmetic.

## MED: .mcaignore Used Before Written During Checkout
- **File**: `src/McaGit/Repo/Checkout.cs`, `Materialize()`, line 13
- `PruneStray` is called before blobs (including .mcaignore) are written. So the .mcaignore from the snapshot isn't loaded when deciding what to prune.
- Impact: if .mcaignore changes between commits, the OLD .mcaignore (existing in worldOut) governs pruning instead of the NEW one being checked out.
- **Fix**: Write .mcaignore (and other blobs) before calling PruneStray, or load .mcaignore content from the manifest directly rather than from the filesystem.

## LOW: IgnoreRules Glob Patterns with '/' Never Match
- **File**: `src/McaGit/Repo/IgnoreRules.cs`, `Rule.Matches()`, line 74
- Glob patterns are matched against `name = segs[^1]` (filename only). A pattern like `data/*.dat` produces regex `^data/.*\.dat$` but is tested against just the filename `foo.dat` → never matches.
- **Fix**: For glob patterns containing '/', match against `rel` (full path) instead of `name`.

## LOW: Stale .tmp Object Files Not Cleaned Up
- **File**: `src/McaGit/Repo/ObjectStore.cs`
- Temp files (`hash.GUID.tmp`) accumulate after process crashes. `LooseHashes()` correctly skips them, but `fsck` doesn't report them and gc doesn't clean them.
- **Fix**: Add `fsck --fix` or `gc` cleanup of `.tmp` files older than N minutes.

## LOW: Chunk Timestamp Loss on Checkout
- **File**: `src/McaGit/Repo/Checkout.cs`, line 26
- All chunks are written with `timestamp: 0`. Minecraft may re-save all chunks immediately on first load, making `mcagit status` show spurious modifications.
- This is a known design limitation (timestamps not stored in manifest).
- **Fix**: Store timestamps per-chunk in manifest (breaking format change), or write current time as timestamp.

## LOW: 0-byte .mca → 8192-byte on checkout (empty region expansion)
- **File**: `src/McaGit/Repo/Checkout.cs:28`, `src/McaGit/Anvil/RegionWriter.cs:58-60`
- RegionWriter.Write always writes the 8KiB header even when the chunk list is empty.
- 0-byte entity .mca files (valid in Minecraft) become 8192-byte all-zeros regions after checkout.
- Semantic diff reports "No differences" (both have 0 chunks). Byte-different but functionally equivalent.
- **Fix**: If chunk list is empty, write 0 bytes (or skip writing the file entirely).

## NOTE: ChunkCache.Save Not Atomic
- **File**: `src/McaGit/Repo/ChunkCache.cs`, `Save()`
- Two concurrent commit processes can corrupt chunkcache.json. On next load, corrupt cache is silently discarded (performance penalty only).
- No correctness impact.

## NOTE: ChunkCache Unbounded Growth
- **File**: `src/McaGit/Repo/ChunkCache.cs`
- Cache keys are `"compressionType:sha256(payload)"` → content-derived. Entries never evicted. For long-lived repos with many commits the file can grow large.
- No correctness impact, just disk waste over time.
