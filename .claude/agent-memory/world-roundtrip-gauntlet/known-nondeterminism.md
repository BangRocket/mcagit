---
name: known-nondeterminism
description: Byte differences between checkout output and originals that are expected and correct (not bugs)
metadata:
  type: project
---

# Known Non-Determinism / Expected Byte Differences

## Checkout vs Original: Byte-Level Differences Are Expected

When comparing `checkout_newer` vs `New_World_Newer` at byte level, 23 of 36 files differ. This is CORRECT and expected. The diff tool reports "No differences" (semantic equivalence).

### Why bytes differ:
1. **Region files (.mca)**: chunks are re-encoded with ZLib at Optimal compression. The resulting ZLib stream differs from Minecraft's original stream even for identical NBT content.
2. **Chunk timestamps**: checkout always writes `timestamp=0` for all chunks. Original files have real Unix timestamps from when Minecraft generated/saved the chunk.
3. **Sector ordering/packing**: RegionWriter packs chunks contiguously sorted by RegionIndex. Original Minecraft files may have gaps and arbitrary ordering.
4. **NBT .dat files**: re-saved via GZip with canonical key-sorted NBT. Minecraft's output has different key ordering and GZip parameters.
5. **Chunk ordering within region**: chunks are sorted by RegionIndex (x*32+z offset in header).

### Files that ARE byte-identical after checkout:
- Icon files, datapacks, advancements, stats (raw blobs) — stored as-is.

### Authoritative test: semantic diff
The `mcadiff` diff tool is the authoritative check. "No differences" exit 0 = pass.
Byte-level `Get-FileHash` comparison is informational only.

## GC Idempotency: Second Run Rewrites Pack

After 1st GC, `ObjectStore.StoredSize(hash)` returns 0 for all objects (they're in the pack, not loose). The second GC sorts by `StoredSize` (all 0) then by hash, producing a different sort order than the 1st GC's size-based sort. This produces a different PackId → second GC writes a new pack even though content is identical. Third GC is idempotent (pack already exists at the hash-sorted PackId).

This is a known inefficiency, not a data loss bug.
