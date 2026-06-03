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
  Anvil/     RegionFile, RawChunk, ChunkPos, ChunkCodec   — region container + decompression
  Diff/      NbtComparer, ListMatcher, ValueRepr,         — semantic NBT tree diff
             WorldDiffer, DiffModels, NbtChange           — file/chunk/world orchestration
  Model/     WorldSource                                  — world layout discovery
  Output/    TextDiffFormatter, JsonDiffFormatter, Ansi   — rendering
  Cli/       DiffOptions + Program.cs                     — command line
tests/McaDiff.Tests/  xUnit suite (synthetic + real-region parse)
```

The Anvil region container (8 KiB sector header + per-chunk compression) is parsed
directly; `fNbt` handles the NBT tag tree (GZip/ZLib/uncompressed).

## Tests

```sh
dotnet test
```

Covers the NBT comparer (add/remove/modify/type-change, identity-list matching,
array summarize/expand), region round-trip, and full world-directory diffs. One
test additionally parses a real region file when `MCADIFF_TEST_REGION` points at
one (auto-skipped otherwise).

## Limitations (v1)

- **LZ4-compressed chunks** (compression type 4, an opt-in server setting) are
  detected and reported as unsupported rather than decoded.
- No block-coordinate-level decode of palettes (a changed `block_states` array is
  reported as an array diff, not as `(x,y,z): stone → air`).
- Read-only: `mcadiff` never writes to either world.
```
