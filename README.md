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
forward-porting (see [Patch & restore](#patch--restore)), and it includes a
**content-addressed, deduplicating version-control system** for worlds —
`init`/`commit`/`log`/`checkout`/`branch` plus a true 3-way `merge`
(see [Version control](#version-control)).

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

## Version control

A semantic VCS for worlds — like git, but it understands chunks. The repository is
a **content-addressed object store**: each chunk is hashed by its *decoded NBT*,
so an unchanged chunk is stored **once** no matter how many snapshots reference it
(the thing fastback + git-LFS can't do — they store whole region blobs per snapshot).

The CLI mirrors git: the repo is the current directory (or nearest ancestor), or
you pass `-C <repo>`; a **bound worktree** (the world) lets `commit`/`status`/
`diff`/`checkout` take no path — just like git's working tree.

```sh
mcadiff init <repo> --worktree <world>     # create a repo, bind a world
cd <repo>                                   # …or prefix commands with -C <repo>

mcadiff commit -m "before raid"             # snapshot the bound worktree
# …play, then:
mcadiff commit -m "after raid"              # only changed chunks add objects

mcadiff status                              # changes vs HEAD (by hash; fast)
mcadiff diff                                # worktree vs HEAD
mcadiff diff <refA> <refB>                  # any two snapshots (branch/commit/HEAD/path)
mcadiff log [--oneline|-p|--stat]           # history (optionally with diffs)
mcadiff show <ref>                          # a commit's metadata + diff
mcadiff checkout <ref> [<world-out>]        # materialize a snapshot
mcadiff reset <ref> [--hard]                # move the branch (─ worktree with --hard)
mcadiff restore <ref> <path>...             # restore specific files from a snapshot
mcadiff revert <commit>                     # new commit that undoes a commit
mcadiff branch [name]                       # list / create branches
mcadiff tag [name [<ref>]]                  # list / create tags (-d to delete)
mcadiff merge <other-branch>               # true 3-way merge
```

Revisions accept `HEAD`, branch/tag names, abbreviated hashes, and `~n` / `^n`
suffixes (e.g. `diff HEAD~2 HEAD`, `checkout main~1`). A `.mcaignore` in the world
(gitignore-lite: `*.ext`, `dir/`, `name`, `/anchored/path`) excludes files from
commits.

`cherry-pick <commit>` applies one commit onto HEAD via the 3-way engine.

### Remotes & maintenance (filesystem)

Sync history between repositories — e.g. push world backups to another drive or
NAS. No network yet; remotes are repository directories. Because objects are
content-addressed, transfer only copies what the other side lacks.

```sh
mcadiff clone <src-repo> <dest-repo>        # copy a repo + set origin
mcadiff remote add <name> <path>            # register a remote
mcadiff push  [<remote> [<branch>]] [--force]   # fast-forward-checked
mcadiff fetch [<remote> [<branch>]]         # into refs/remotes/<remote>/* (e.g. origin/main)
mcadiff reflog                              # HEAD movement history
mcadiff gc                                  # prune objects unreachable from any ref
```

- **Whole-world snapshots:** region/entities/poi as deduped per-chunk objects,
  loose NBT as canonical objects, everything else (datapacks, stats, advancements)
  as raw blobs — so `checkout` restores a faithful, playable world.
- **True 3-way merge:** finds the common ancestor and merges **per NBT node** —
  changes from both sides that touch different nodes both land; only a genuine
  same-node clash is a conflict (kept *ours*, or *theirs* with `--theirs`, and
  reported). Far finer than git's line-based merge on these files.
- Verified on the example worlds: committing Older then Newer reproduces each
  exactly on `checkout` (and loads in real Minecraft), with shared chunks stored once.

> Stop the server before `checkout` (it rewrites world files). The repo lives
> outside the world directory; bind the world with `--worktree` / `config worktree`.

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
  Nbt/       NbtIdentity, NbtEquality, NbtJson, NbtPath,    — NBT identity, equality,
             NbtCanonical                                    lossless JSON, paths, canonical form
  Diff/      NbtComparer + IDiffSink (NbtChangeSink /       — one tree walk, two outputs
             PatchOpSink), ListMatcher, ValueRepr,
             WorldDiffer, DiffModels                        — file/chunk/world orchestration
  Patch/     WorldPatch/PatchModels, PatchExtractor,        — extract & apply patches
             PatchApplier
  Repo/      ObjectStore, Repository, Manifest, Snapshotter,— content-addressed VCS:
             Checkout, StatusCalc, MergeBase, Merger          commit/log/status/checkout/merge
  Model/     WorldSource                                    — world layout discovery
  Output/    TextDiffFormatter, JsonDiffFormatter, Ansi     — rendering
  Cli/       Diff/Extract/ApplyOptions, RepoCommands,       — subcommand dispatch
             + Program.cs
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

55 tests covering: the NBT comparer (add/remove/modify/type-change, identity-list
matching, array summarize/expand); the lossless `NbtJson` codec (incl. longs
beyond 2^53); `NbtPath` get/set/remove by key, index and identity; `RegionWriter`
round-trip; the full patch pipeline (forward/reverse round-trips, conflict guard,
`--force`, `--dry-run`); and the VCS — object-store dedup, canonical-form
determinism, commit→checkout reproduces a world, merge-base, and 3-way merge
(non-overlapping node changes combine; same-node clashes conflict, keeping ours or
theirs; fast-forward). One test additionally parses a real region file when
`MCADIFF_TEST_REGION` points at one (auto-skipped otherwise).

## Limitations

- **LZ4-compressed chunks** (compression type 4, an opt-in server setting) are
  detected and reported as unsupported — neither decoded, patched, nor committed
  semantically (such a region is stored as a raw blob instead).
- No block-coordinate-level decode of palettes (a changed `block_states` array is
  reported as an array diff, not as `(x,y,z): stone → air`).
- `diff`/`extract`/`commit`/`status` never modify a world. `apply` and `checkout`
  only write to the fresh output directory they create.
- Objects are whole (zlib-compressed), not delta-packed; `apply` copies the whole
  target world before editing (no hardlink/reflink yet). The first commit decodes
  every chunk (parallelized, a few seconds on a large world); re-commits reuse a
  per-repo decode cache, so an unchanged world re-commits in a fraction of the time.
- `merge` uses the nearest common ancestor (fine for linear + single-merge
  histories; no criss-cross LCA), and conflicts are reported + kept-ours/theirs
  with no in-place marker/resolve workflow.
- Remotes are filesystem-only (no ssh/http transport), and there's no staging
  index — `commit` snapshots the whole worktree.
- A compound key containing a literal `.` or `[` isn't addressable by patch/merge
  paths (real Minecraft keys don't use them).
