---
name: codebase-architecture
description: End-to-end walk of the one-walk-two-sinks invariant, all key file locations, and the IDiffSink event contract (namespace is McaGit as of 2026-06-06 rename from McaDiff)
metadata:
  type: project
---

## The Load-Bearing Invariant

`NbtComparer.Walk(a, b, sink)` drives a recursive tree walk. It emits exactly five events on IDiffSink:
- `Added(path, value)` — key/element in B only; whole subtree passed
- `Removed(path, value)` — key/element in A only; whole subtree passed
- `Modified(path, a, b)` — scalar leaf changed (same type); called only for non-compound, non-list, non-array tags
- `TypeChanged(path, a, b)` — same key, different tag type; walk stops (no recursion into children)
- `ArrayChanged(path, a, b)` — ByteArray/IntArray/LongArray differs; whole array passed

Two sinks consume these events:
1. `NbtChangeSink` (display) — flattens Added/Removed subtrees to one row per leaf, summarizes arrays
2. `PatchOpSink` (patch) — emits one PatchOp per event; added/removed store whole subtree via NbtJson

## Namespace Note (updated 2026-06-06)

All source files were renamed from `McaDiff.*` to `McaGit.*` (namespace + directory) in the `chore/namespace-mcagit` branch. The move was a mechanical 1:1 token swap — zero semantic changes. AssemblyName remains `mcagit`. All paths below reference the new `src/McaGit/` tree.

## Key File Locations

### Diff/
- `IDiffSink.cs` — the five-method event interface
- `NbtComparer.cs` — the recursive walk; `Walk()` is the public entry point; `Compare()` is convenience wrapper for display
- `NbtChangeSink.cs` — display sink; flattens subtrees; has ExpandArrays logic
- `PatchOpSink.cs` — patch sink; one PatchOp per event, lossless NbtJson encoding
- `ListMatcher.cs` — derives stable keys for list elements (delegates to NbtIdentity)
- `DiffModels.cs` — WorldUnit, ChunkDiff, FileDiff, WorldDiff, DiffRunOptions record types
- `NbtChange.cs` — NbtChange record + ChangeKind enum + NbtDiffOptions
- `ValueRepr.cs` — human-readable string forms; ScalarEquals used by comparer
- `WorldDiffer.cs` — top-level orchestration; parallelizes file diffing

### Nbt/
- `NbtIdentity.cs` — KeyOf(NbtCompound): priority order: xyz coords → UUID IntArray → UUIDMost/Least → Slot byte → id string
- `NbtPath.cs` — parses dotted/bracketed paths; Get/Set/TerminalName; identity resolution via NbtIdentity.KeyOf
- `NbtJson.cs` — lossless NBT↔JSON; type-tagged single-key objects; longs/long-arrays as strings
- `NbtEquality.cs` — recursive DeepEquals; used by patch 3-way guard; float uses .Equals() (NaN==NaN)
- `NbtCanonical.cs` — deterministic binary serialization for repo object hashing; sorts compound keys recursively

### Patch/
- `PatchModels.cs` — PatchOp, ChunkPatch, PatchFileEntry, WorldPatch; JSON serialization options
- `PatchOpSink.cs` — IDiffSink→PatchOp; TypeChanged and Modified both emit Base+Value (correct parity)
- `PatchExtractor.cs` — builds WorldPatch; reuses NbtComparer.Walk via PatchOpSink
- `PatchApplier.cs` — applies WorldPatch; 3-way guard (NbtEquality.DeepEquals); supports --reverse/--force/--dry-run

## Sink Parity Status (as of 2026-06-03 audit)

TypeChanged: both sinks emit Base+Value — PARITY HOLDS
Modified: both sinks emit Base+Value — PARITY HOLDS
ArrayChanged: PatchOpSink emits whole array; NbtChangeSink summarizes or expands — PARITY HOLDS (by design, different representation)
Added: both handle whole subtree — PARITY HOLDS
Removed: both handle whole subtree — PARITY HOLDS

Key asymmetry that is BY DESIGN: NbtChangeSink flattens Added/Removed subtrees to leaf rows; PatchOpSink stores the whole subtree. This is intentional and correct — the patch doesn't need to address each leaf, it replaces the whole subtree atomically.

## Versioning

WorldPatch has `Version: 1`. No migration logic exists. Identity changes or path format changes silently break existing .mcapatch files.
