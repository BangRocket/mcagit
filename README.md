# mcadiff

A semantic, **git-style diff for Anvil-format Minecraft (Java Edition) worlds**.

Git only sees `.mca` region files as opaque binary blobs — `git diff` reports
"binary files differ" and nothing more. `mcadiff` opens both worlds, parses every
region → chunk → NBT element, and prints what actually changed:

```text
diff --mca entities/r.0.0.mca
  @@ chunk (0, 0) @@
    ~ DataVersion: 3955 → 3956
    ~ Entities[uuid:641b80dde18a476d8dcb56cb10f440c5].Pos[1]: -30.9d → -22.9d
    + mcadiff_demo: "hello from mcadiff"

diff --nbt level.dat
  ~ Data.DayTime: 6121995L → 6127995L
  ~ Data.Time: 25105491L → 25117836L

2 files changed (2 modified, 0 added, 0 deleted), 1 chunks, 5 nbt changes
```

It can also **extract those changes into a portable patch and apply them to
another save** — surgically and non-destructively — for restore/rollback or
forward-porting. See [Patch & restore](#patch--restore).

## Requirements

- .NET 9 SDK
- [`fNbt`](https://www.nuget.org/packages/fNbt) (restored automatically)

## Build & run

```sh
dotnet build -c Release
dotnet run --project src/McaDiff -- <A> <B>     # or run the built mcadiff binary
```

`<A>` and `<B>` are either **two world folders** or **two single files**
(`.mca` region files or `.dat` loose NBT files).

```sh
mcadiff ~/backups/world-monday ~/backups/world-tuesday
mcadiff old/region/r.0.0.mca new/region/r.0.0.mca
```

## Example

Diffing two saves of the same world a minute apart — the loose-NBT view
(`--only nbt`) reads like a play-by-play of what the player did:

```sh
mcadiff --only nbt New_World_Older New_World_Newer
```
```text
diff --nbt level.dat
  ~ Data.DayTime: 383L → 1574L
  ~ Data.Player.Pos[0]: 8.5d → -1.8179372026061453d
  ~ Data.Player.Pos[2]: -3.5d → -7.064525711408038d
  ~ Data.Player.Rotation[0]: 5.8904247f → 0.5203297f
  ~ Data.Player.XpSeed: 0 → 1815296106
  ~ Data.Time: 383L → 1574L
  ~ Data.rainTime: 41203 → 40012

diff --nbt playerdata/0bde0058-…-470118f9a8c7.dat
  ~ Pos[0]: 8.5d → -1.8179372026061453d
  ~ warden_spawn_tracker.ticks_since_last_warning: 370 → 1553

5 files changed (5 modified, 0 added, 0 deleted), 0 chunks, 20 nbt changes
```

The full world diff (terrain included) for the same pair touches 1,762 chunks
across the four `region/` files — mostly `InhabitedTime` / `LastUpdate` ticking up
on the chunks nearest the player. Use `--summary` for a per-file overview.

## Options

| Flag | Effect |
|------|--------|
| `--json` | Emit a structured JSON change list instead of colored text. |
| `--expand` | Show every changed array index (default: summarize as `long[37] — 3 of 37 entries differ`). |
| `--only <cats>` | Limit to categories: `region,entities,poi,nbt` (comma-separated, repeatable). |
| `--summary` | Per-file status and totals only; omit per-change detail. |
| `--no-color` | Disable ANSI color (also honors `NO_COLOR`; auto-off when piped). |
| `-h`, `--help` | Show help. |

**Exit codes** (git convention): `0` = identical, `1` = differences found, `2` = error.

## What gets compared

For a world folder, across the overworld and the `DIM-1` / `DIM1` dimensions:

- **Chunk data** — `region/`, `entities/`, `poi/` (`r.X.Z.mca`), diffed
  chunk-by-chunk and then NBT element-by-element.
- **Loose NBT** — `level.dat`, `playerdata/*.dat`, `data/*.dat` (and per-dimension
  `data/*.dat`).

### How the NBT diff reads

- **Compounds** are matched by key → `Added` / `Removed` / `Modified` / `TypeChanged`.
- **Lists** that behave as sets are matched by identity rather than position, so a
  reorder isn't reported as a rewrite. The identity used, in order of preference:
  block coordinates `(x,y,z)` (`block_entities[@5,63,8]`), entity UUID
  (`Entities[uuid:…]`), inventory `Slot` (`Items[slot:3]`), or a string `id`
  (`attributes[id:minecraft:generic.movement_speed]`). When elements have no
  unique identity, lists fall back to index matching (`list[3]`).
- **Packed arrays** (block states, heightmaps, biomes) are summarized by default;
  `--expand` shows each changed index.

## Patch & restore

`mcadiff` can turn a diff into a portable, **bidirectional** patch and apply it
to another world. Use it to roll a save forward, or — applied in reverse — to
**restore** old, known-good state (undo griefing, corruption, accidental edits)
without overwriting everything else.

```sh
# 1. Capture the changes from <old> to <new> as a patch file
mcadiff extract <old> <new> -o changes.mcapatch

# 2. Apply forward onto a base (writes a NEW world; never mutates the target)
mcadiff apply changes.mcapatch <base-world> -o <output-world>

# 3. …or apply in reverse onto the newer world to restore the old state
mcadiff apply --reverse changes.mcapatch <new-world> -o <restored-world>
```

**Non-destructive by design:**
- `apply` copies the target to a fresh `--output` world and only rewrites the
  patched nodes — everything else is preserved byte-for-byte.
- Every node is **guarded** (3-way): it's only changed if the target's current
  value matches what the patch expects. A mismatch is reported as a *conflict*
  and skipped — your data is never clobbered. `--force` overrides the guard;
  `--dry-run` reports what would happen and writes nothing.

The patch (`*.mcapatch`) is human-readable JSON. Each op records both the old and
new value (losslessly type-encoded), which is what makes it invertible:

```jsonc
{ "path": "Data.Time", "base": {"long":"383"}, "value": {"long":"1574"} }
```

`extract` flags: `--only <cats>`, and `--whole-chunk` / `--whole-file` to store
whole roots instead of node-level ops. `apply` flags: `--reverse`, `--force`,
`--dry-run`, `--only`. Apply exits `0` clean, `1` if any conflicts were skipped.

### Worked example

Advance the older save to the newer one by extracting the changes and applying
them onto **old** (`extract <base> <target>` — base first):

```sh
# 1. Capture old → new as a patch
mcadiff extract New_World_Older New_World_Newer -o changes.mcapatch
#   Wrote changes.mcapatch — 13 files, 4707 ops.

# 2. Apply onto old → a fresh updated world (New_World_Older is never modified)
mcadiff apply changes.mcapatch New_World_Older -o Old_Updated
#   Applied 4711 ops across 13 files; 0 conflicts.

# 3. Verify it now matches new
mcadiff Old_Updated New_World_Newer
#   No differences.
```

The reverse direction restores instead: keep the same patch and run
`mcadiff apply --reverse changes.mcapatch New_World_Newer -o Old_Restored` to
rebuild the older state from the newer world. Both directions are verified
end-to-end against these worlds in the test suite.

## Performance

Two fast paths keep whole-world diffs cheap:

1. Files with byte-identical contents are skipped without parsing.
2. Within a region, chunks whose **compressed** payloads are byte-identical are
   skipped without decompressing or parsing NBT.

Region files are diffed in parallel. A self-diff of a ~2,900-file world completes
in a couple of seconds.

## Architecture

```
src/McaDiff/
  Anvil/     RegionFile, RegionWriter, RawChunk, ChunkPos, — region container
             ChunkCodec                                      read + write
  Nbt/       NbtIdentity, NbtEquality, NbtJson, NbtPath     — NBT identity, equality,
                                                              lossless JSON, path resolve
  Diff/      NbtComparer + IDiffSink (NbtChangeSink /       — one tree walk, two outputs
             PatchOpSink), ListMatcher, ValueRepr,
             WorldDiffer, DiffModels                        — file/chunk/world orchestration
  Patch/     WorldPatch/PatchModels, PatchExtractor,        — extract & apply patches
             PatchApplier
  Model/     WorldSource                                    — world layout discovery
  Output/    TextDiffFormatter, JsonDiffFormatter, Ansi     — rendering
  Cli/       Diff/Extract/ApplyOptions + Program.cs         — subcommand dispatch
tests/McaDiff.Tests/  xUnit suite (synthetic + real-region parse)
```

The Anvil region container (8 KiB sector header + per-chunk compression) is parsed
and written directly; `fNbt` handles the NBT tag tree (GZip/ZLib/uncompressed). A
single tree walk (`NbtComparer` + `IDiffSink`) feeds both the display diff and the
patch extractor, so they can never drift.

## Tests

```sh
dotnet test
```

48 tests covering: the NBT comparer (add/remove/modify/type-change, identity-list
matching, array summarize/expand); the lossless `NbtJson` codec (incl. longs
beyond 2^53); `NbtPath` get/set/remove by key, index and identity; `RegionWriter`
round-trip; and the full patch pipeline — forward/reverse round-trips reproduce
the target/base, conflicts are reported and the target is not clobbered, `--force`
and `--dry-run` behave. One test additionally parses a real region file when
`MCADIFF_TEST_REGION` points at one (auto-skipped otherwise).

## Limitations (v1)

- **LZ4-compressed chunks** (compression type 4, an opt-in server setting) are
  detected and reported as unsupported — neither decoded nor patched.
- No block-coordinate-level decode of palettes (a changed `block_states` array is
  reported as an array diff, not as `(x,y,z): stone → air`).
- `diff` and `extract` never modify either world. `apply` only writes to the fresh
  `--output` directory it creates; the target is copied, never mutated in place.
- Patches store full arrays and whole added units (exact, not delta-compressed);
  `apply` copies the whole target world before editing (no hardlink/reflink yet).
- No automatic merge resolution — conflicts are reported and skipped (or `--force`d).
- A compound key containing a literal `.` or `[` isn't addressable by patch paths
  (real Minecraft keys don't use them).
