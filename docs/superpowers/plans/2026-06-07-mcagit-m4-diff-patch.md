# mcagit Rust — M4: diff + patch

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline). Steps use `- [ ]`.

**Goal:** Port the semantic diff and the invertible `.mcapatch` engine: `mca-diff` (one tree-walk comparer → sink trait; two sinks) and `mca-patch` (extract/apply, 3-way guarded, reversible), wired into the CLI as `diff`/`extract`/`apply`.

**Architecture:** `mca-diff` depends on `mca-nbt` + `mca-anvil`; `mca-patch` depends on `mca-diff`. **Invariant (from CLAUDE.md): all change semantics go through the one comparer + the `DiffSink` trait**, so the display sink and the patch-op sink cannot drift. Worlds diff in parallel (rayon over regions) with byte-identical fast paths.

**Tech:** `mca-nbt`, `mca-anvil`, `rayon`, `serde_json`; reuse `mca-nbt::{NbtValue, NbtPath, identity_key, to_json, from_json}`.

**Reference:** .NET `Diff/{NbtComparer,IDiffSink,ListMatcher,NbtChange,NbtChangeSink,WorldDiffer}.cs`, `Patch/{PatchModels,PatchOpSink,PatchExtractor,PatchApplier}.cs`.

## Crate layout

```text
crates/diff/  (mca-diff)
  comparer.rs   DiffSink trait + walk(a,b,sink) (compound by key, list by identity|index, recursion)
  change.rs     NbtChange{path,kind,old,new} + ChangeSink (display rows)
  world.rs      WorldDiffer: world/region/chunk + nbt/blob, parallel, fast paths -> Vec<FileDiff>
  format.rs     text rendering of a world diff
crates/patch/ (mca-patch)
  model.rs      WorldPatch/PatchFileEntry/ChunkPatch/PatchOp (serde JSON, version=1)
  op_sink.rs    PatchOpSink: DiffSink -> Vec<PatchOp> (base/value via mca_nbt::to_json)
  extract.rs    two worlds -> WorldPatch
  apply.rs      WorldPatch + base world -> fresh output world (3-way guarded; --reverse)
```

## Semantics to mirror (clean-slate, behavior-equivalent)

- **Comparer walk** (`comparer.rs`): if tag types differ → `type_changed`. Compound: key union (removed in B → `removed`; only in B → `added` in sorted key order; common → recurse). List: if **both** sides yield unique identity keys (`identity_key` per element, no collisions) → match by identity (added/removed/recurse by key label `path[key]`), else align by index (`path[i]`, extra → added/removed). Arrays (Byte/Int/Long) compared whole → `array_changed`. Scalars → `modified` if `!=`. Path child = `parent.name` (root "").
- **PatchOp**: `{path, base, value}` (either null = absent). `added` → base null; `removed` → value null; modified/type/array → both. Values are `mca_nbt::to_json` (longs as strings — lossless). Path "" = whole unit root.
- **WorldPatch**: `version=1`, `base?/target?` labels, `files[]`. Region entry → `chunks[]` (each `{x,z,status,ops}`); loose NBT entry → `ops[]`; blobs → status only (added/removed/modified by hash). Status enum {added, removed, modified}.
- **apply (3-way)**: copy base world to output (never mutate input). For each op: resolve node at `path`; if it equals `op.base` → set to `op.value` (remove if value null; insert if base null); else **conflict** → skip + report (unless `--force`). `--reverse` swaps base/value. Region: decode chunk → apply ops → re-encode. Loose NBT: decode → apply → save. Blob status applied by copying target bytes (carried in patch? no — blobs store hashes; for M4 a blob "modified" needs both byte sets → store base/value blob bytes inline base64? .NET stores blob content in the patch? Check: .NET PatchExtractor for blobs). **Decision for M4:** patch carries blob add/remove/modify with the *new* bytes inline (base64) so apply is self-contained; reverse needs old bytes too → store both. (Keep blobs simple: most diffs are chunk/NBT.)

## Tasks

### M4-T1: mca-diff scaffold + comparer

- Crate + `DiffSink` trait (`added/removed/modified/type_changed/array_changed`, taking `&NbtValue`), `walk(&NbtValue,&NbtValue,&mut dyn DiffSink)`, list-identity helper.
- Tests: compound add/remove/modify; nested recurse; list-by-identity (reorder = no change); list-by-index (length change); type change; array change. Use a recording sink in tests.
- Commit `feat(diff): NbtComparer walk + DiffSink trait`.

### M4-T2: ChangeSink + NbtChange

- `NbtChange{path,kind,old:Option<String>,new:Option<String>}`, `ChangeKind{Added,Removed,Modified,TypeChanged}`; `ChangeSink` implements `DiffSink`, renders values via a short repr; sorted by path.
- `compare(a,b) -> Vec<NbtChange>` convenience.
- Tests: a mixed change set yields the expected rows.
- Commit `feat(diff): ChangeSink display rows`.

### M4-T3: WorldDiffer + text format

- `WorldDiffer::diff(world_a, world_b) -> WorldDiff` (Vec of per-file diffs): enumerate union of files; region (`.mca` under region/entities/poi) → per-chunk compare (decode both; **fast path**: equal raw payload bytes → skip); `.dat` → NBT compare; else blob (compare bytes). Parallel over the file union (rayon). Exit-code helper (0 identical / 1 differ).
- `format.rs`: text rendering ("No differences." when empty).
- Tests: two synthetic worlds (one chunk changed, one file added) → expected file/chunk diffs; identical worlds → empty.
- Commit `feat(diff): WorldDiffer (parallel, fast paths) + text format`.

### M4-T4: mca-patch scaffold + model + op sink

- `WorldPatch`/`PatchFileEntry`/`ChunkPatch`/`PatchOp` serde (camelCase, version=1, skip-null); `to_json`/`from_json`.
- `PatchOpSink` implements `DiffSink` → `Vec<PatchOp>`.
- Tests: patch JSON round-trip; op sink records base/value correctly for each change kind.
- Commit `feat(patch): WorldPatch model + PatchOpSink`.

### M4-T5: extract

- `extract(world_a, world_b) -> WorldPatch`: per region, per chunk run comparer+PatchOpSink; per `.dat` likewise; blobs add/remove/modify with inline bytes. Parallel over files.
- Tests: extract two synthetic worlds → patch has the expected entries/ops.
- Commit `feat(patch): extract worlds -> WorldPatch`.

### M4-T6: apply (+ reverse)

- `apply(patch, base_world, out_dir, reverse, force) -> ApplyReport`: copy base→out; for each file entry apply ops (3-way) to a decoded chunk/NBT then re-encode/save; blobs write new bytes; collect conflicts.
- Tests: **extract(A,B) then apply to A == B** (re-diff clean); **apply --reverse to B == A**; a conflicting target reports a conflict and is left unchanged.
- Commit `feat(patch): apply (3-way guarded, reversible)`.

### M4-T7: CLI wiring + gate

- CLI: `mcagit <A> <B>` (fallthrough diff), `diff <A> <B>`, `extract <A> <B> -o p.mcapatch`, `apply [--reverse] [--force] <patch> <world> -o <out>`. Exit codes 0/1/2.
- Gate: `cargo test --all` + fmt + clippy green; e2e (integration test): build two synthetic worlds, `extract` → `apply` → `diff` reports identical; `apply --reverse` restores.
- Commit `feat(cli): diff/extract/apply + M4 gate`; push.

## Done criteria

- Comparer drives both sinks (one walk); world diff parallel with fast paths; patch extract/apply invertible (round-trip + reverse tests green); CLI diff/extract/apply wired. `cargo test --all` + clippy + fmt green.

## Deferred

- `block_entities[@x,y,z]` block-coordinate display sugar, block-state palette decode, `--expand-arrays`, DataVersion warnings (display polish — M7).
