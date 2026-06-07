# gc: parallel delta-packing + progress reporting

**Date:** 2026-06-06
**Status:** Approved design — ready for implementation plan
**Branch:** `feat/gc-parallel-pack` (stacked on `chore/namespace-mcagit`)

## Problem

`mcagit gc` consolidates every reachable object into one delta-compressed pack
(`Gc.Repack` → `Packfile.Write`). On a real repo (the dobbscraft 8-snapshot series:
388,510 objects, ~2.1 GB across 8 per-commit packs) this:

1. **Looks hung** — `Gc`/`Packfile.Write` emit no progress output. With piped (non-TTY)
   output and a long single-threaded compute, it is indistinguishable from a hang.
2. **Is slow** — `Packfile.Write` is a single `foreach` over every object
   (`Packfile.cs:153`). Each object is decompressed (`load`), deflated, and diffed
   against a sliding `Window = 10` of just-written objects (each candidate also deflated).
   That is ~388K × up-to-10 decompress+delta operations on **one core**. Measured: the
   output pack grew ~10 MB/min; a full run projected to well over an hour.

The commit path already has a `Progress` reporter and parallel snapshotting; gc has neither.

## Goals

- gc shows live, TTY-aware progress (silent when piped) — never looks hung.
- gc delta-compression uses all cores.
- Preserve every existing invariant: idempotent pack id, bounded memory
  ("peak memory = the delta window, not the whole set"), byte-faithful object round-trip,
  pack readability by the unchanged reader and `PackTransfer`.

## Non-goals

- Incremental gc / pack-retention policy (not re-deltifying the consolidated base each run).
  A worthy separate effort; out of scope here.
- Changing the pack file format, the delta format, or the reader.

## Key insight (why parallelism is correct by construction)

A delta entry encodes a **relative** back-offset: `WriteVarint(entryOff - baseOffset)`
(`Packfile.cs:177`). If each object's delta base is restricted to **its own segment**, a
segment's bytes are position-independent: the relative distance between a delta and its base
is invariant under a global shift. So segments can be compressed **in parallel** to separate
temp files with segment-local offsets, then **concatenated** into the final pack with **no
offset fixup** — only the `.idx` needs global offsets (`segmentStart + localOffset`).

Delta quality loss is confined to the `threads − 1` segment boundaries (a base that would
have been chosen across a boundary is unavailable) — negligible on hundreds of thousands of
objects, and never incorrect, only marginally larger than the serial pack.

## Design

### `Packfile` refactor

Extract the existing per-object loop into a reusable unit and add a parallel driver. Three
methods:

1. **`WriteSegment(Stream out, IReadOnlyList<string> hashes, Func<string,byte[]> load,
   Action onObject) → List<(string Hash, long LocalOffset)>`**
   The *current* loop body (load → deflate → window delta-search → append type-0/type-1
   entry), writing to `out` with stream-local offsets starting at 0, **minus** the `MCAP`
   header and index/install. Calls `onObject()` once per object (for progress). Peak memory =
   the window. This is the single reusable unit of work.

2. **`Write(objectsDir, ordered, load)`** — serial path, behavior identical to today:
   create file → `MCAP` header → `WriteSegment` → `WriteIndex` → atomic install. Used as the
   fallback when `threads <= 1 || ordered.Count < threads` (nothing to parallelize / avoids
   temp-file overhead). This is exactly one segment, so it shares the `WriteSegment` body.

3. **`WriteParallel(objectsDir, ordered, load, threads, progress) → string? packId`**
   - **Partition** `ordered` (already sorted by `StoredSize` desc, then hash) into `threads`
     **contiguous** segments balanced by cumulative `StoredSize` (greedy: walk the sorted
     list, cut a segment when its running byte-sum crosses `totalBytes / threads`). Contiguous
     preserves size-adjacency (delta quality); byte-balancing prevents the large-object
     segment from being a straggler.
   - **Parallel phase**: `Parallel.For`/`Parallel.ForEach` over segments; each runs
     `WriteSegment` into its own temp file `objects/pack/incoming-seg-{k}-{guid}.pack.tmp`
     (the `.pack.tmp` suffix is ignored by the reader's `*.pack` glob — matching the existing
     `Appender` convention — and matches gc's recursive `*.tmp` sweep, so a crash leaves nothing
     readable behind).
     `onObject` does `progress.Update(Interlocked.Increment(ref done), total)` — the same
     thread-safe pattern `Snapshotter` already uses.
   - **Serial concat phase**: create the final temp pack → `MCAP` header; for `k` in `0..S`:
     record `Pk = fs.Position`, stream-copy `seg-{k}.tmp` into `fs`, append
     `(hash, Pk + localOffset)` for each of its entries to the global index, delete the temp.
     `WriteIndex` (global offsets) → atomic `File.Move` of `.pack` and `.idx`. `progress.Done`.
   - Returns the same `PackId` as the serial path (see invariants).

`PackId` is unchanged — it hashes the **sorted set** of hashes, independent of segmentation
or write order, so a parallel pack and a serial pack of the same object set share an id, and a
second gc is a no-op (`pack-{id}.pack` already exists → early return).

### `Gc.Repack`

Add `int threads` and `Progress? progress` parameters; pass them to `Packfile.WriteParallel`
(or `Write` when `threads == 1`). The stale-`*.tmp` sweep it already performs (`Gc.cs:38`)
cleans orphaned `seg-*.tmp` from a crashed/killed gc — no new crash-recovery code needed.

### `GcCmd` (CLI)

- Create `new Progress(Progress.ShouldShow())` (TTY-aware; silent when piped — directly fixes
  "looks hung"). Drive it through `Gc.Repack`.
- Parse `--threads N` (default `Environment.ProcessorCount`). `--threads 1` forces the serial
  `Packfile.Write` path — also the equivalence-test lever and an escape hatch.
- Progress label e.g. `gc: packing` with `Update(done, total)`; the serial concat is a short
  trailing phase (optionally a second `Begin("gc: writing pack")`).

## Invariants preserved

| Invariant | How |
|---|---|
| Idempotent pack id | `PackId` hashes the sorted object set; unaffected by segmentation/order. |
| Bounded memory | Each worker's peak memory is its window (temp-file staging, not in-RAM). |
| Byte-faithful round-trip | Reader unchanged; deltas are within-segment and concatenation preserves relative offsets. |
| Network pack path | Output is a normal pack; `PackTransfer`/clone/fetch read it unchanged. |
| Crash safety | `*.pack.tmp` segments ignored by reader and swept by gc; final install is atomic `File.Move`. |

## Testing (synthetic worlds via `TestAnvil`, house style — no fixtures)

- **Round-trip**: commit a world with enough chunks to span multiple segments and exceed the
  window; `gc`; assert `fsck` clean, **every object `Read()` returns byte-identical content**,
  and checkout reproduces the world byte-faithfully.
- **Equivalence**: gc with `--threads 1` vs `--threads 8` → identical reconstructed object
  contents and identical pack id.
- **Idempotence**: a second `gc` is a no-op (same pack id, nothing rewritten).
- **Edges**: object count < threads; single object; empty reachable set; delta chains at
  `MaxDepth`; a segment whose objects all delta vs a within-segment base.
- **Progress**: `Progress.ShouldShow()` false under pipe → no stray output (assert clean
  stderr in the existing UX-guard style).

## Review gate (per CLAUDE.md)

- `Repo/` + `Packfile` change → **world-roundtrip-gauntlet**.
- `Packfile` is reachable from untrusted network input (clone/fetch via `PackTransfer`) →
  **trust-boundary-exploit-hunter** (writer change; confirm no reader/parse regression and no
  resource-exhaustion surface from `--threads`).
- No `Diff/`/`Nbt/`/`Patch/` change → invariant-reviewer not required.
- Then the `pre-pr` skill before opening the PR.

## Rollout notes

- Stacked on `chore/namespace-mcagit` (PR #45). That rename PR should merge first; this branch
  then rebases onto `main`. Targets `net10.0` (build/test via `/usr/local/share/dotnet/dotnet`).
- README `gc` section updated for `--threads` and progress behavior.
