---
name: known-nondeterminism
description: Byte differences between checkout output and originals that are expected and correct (not bugs)
metadata:
  type: project
---

# Known Non-Determinism / Expected Byte Differences

## Checkout vs Original: Byte-Level Differences Are Expected

When comparing `checkout_newer` vs `New_World_Newer` at byte level, many files differ. This is CORRECT and expected. The diff tool reports "No differences" (semantic equivalence) which is the authoritative test.

### Why bytes differ:
1. **Region files (.mca)**: chunks are re-encoded with ZLib at Optimal compression. The resulting ZLib stream differs from Minecraft's original stream even for identical NBT content.
2. **Chunk timestamps**: checkout always writes `timestamp=0` for all chunks. Original files have real Unix timestamps from when Minecraft generated/saved the chunk.
3. **Sector ordering/packing**: RegionWriter packs chunks contiguously sorted by RegionIndex. Original Minecraft files may have gaps and arbitrary ordering.
4. **NBT .dat files**: re-saved via GZip with canonical key-sorted NBT. Minecraft's output has different key ordering and GZip parameters.
5. **Chunk ordering within region**: chunks are sorted by RegionIndex (x*32+z offset in header).

### Files that ARE byte-identical after checkout:
- Icon files, datapacks, advancements, stats (raw blobs) — stored as-is.

### Authoritative test: semantic diff
The `mcagit diff` tool is the authoritative check. "No differences" + exit 0 = PASS.
Byte-level comparison is informational only.

## Second Commit On Unchanged World: "nothing to commit"
When running commit on a world that matches HEAD (same content in worktree), the chunk cache
fast-path kicks in and the tool outputs "nothing to commit — world matches HEAD" with exit 0.
This is correct and expected behavior (not an error).

## Head Detachment After Checkout HEAD~1
After running `checkout HEAD~1`, HEAD becomes a detached hash pointing to the older commit.
Running `status` then correctly shows modifications if the worktree still has newer content.
This is expected. Reattach with `printf "ref: refs/heads/main\n" > repo/HEAD`.

## HEAD~1 Resolution Error on Detached-First-Commit HEAD
When HEAD is a raw detached hash pointing to the FIRST commit (no parent), running
`verify HEAD~1` fails with "bad revision: <hash>: no parent". This is correct behavior.
Always use `HEAD~1` when HEAD is attached to a branch, or use the explicit hash directly.
