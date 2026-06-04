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
mcadiff tag -a v1 -m "season 1" [-s]        # annotated (optionally SSH-signed) tags
mcadiff merge <other-branch>               # 3-way merge (stops on conflict)
mcadiff merge --continue | --abort          # finish / undo a conflicted merge

mcadiff add <path>... | restore --staged …  # stage / unstage (the index)
mcadiff stash [push|pop|list|drop|clear]    # shelve / restore the worktree
mcadiff rebase [--onto <base>] <upstream>   # replay commits onto a new base
mcadiff bisect start|good|bad|reset         # binary-search for a bad commit
mcadiff clean [-n|-f]                        # remove untracked worktree files
mcadiff fsck                                 # verify object integrity + reachability
mcadiff rev-parse|cat-file|hash-object|ls-tree   # plumbing
```

Revisions accept `HEAD`, branch/tag names, abbreviated hashes, `~n` / `^n` suffixes,
`HEAD@{n}` reflog positions, and ranges `A..B` / `A...B` for `log` (e.g.
`diff HEAD~2 HEAD`, `checkout main~1`, `log v1..HEAD`). A `.mcaignore` in the world
(gitignore-lite: `*.ext`, `dir/`, `name`, `/anchored/path`) excludes files from
commits. Identity comes from `config user.name` / `user.email`; commits and tags can
be SSH-signed (`commit -S`, `tag -s`, `user.signingkey`) and verified (`tag -v`).

`cherry-pick <commit>` applies one commit onto HEAD via the 3-way engine. A conflicted
`merge` stops without committing, records MERGE_HEAD + a conflict list, and lays the
partial result into the worktree; resolve it and `merge --continue`, or `merge --abort`.
Pre-commit / post-commit **hooks** run from `<repo>/hooks/`.

### Remotes & maintenance

Sync history between repositories — push world backups offsite or pull them down.
A remote is a **path**, an **`http(s)://`** URL, or **`ssh://`**. Because objects
are content-addressed, transfer copies only what the other side lacks.

```sh
mcadiff clone <src> <dest> [--token T]        # src: path | http(s):// | ssh://
mcadiff remote add origin <url>
mcadiff push  [<remote> [<branch>]] [--force] [--token T]   # fast-forward-checked
mcadiff fetch [<remote> [<branch>]] [--token T]            # into refs/remotes/<remote>/*
mcadiff push  [<remote>] --all                # push every branch
mcadiff ls-remote [<remote>]                  # list a remote's refs
mcadiff reflog                                # HEAD movement history
mcadiff gc                                    # repack reachable objects + prune the rest
```

**Serving over the network** (git's model — anonymous read, authenticated push):

```sh
# HTTP: run a daemon. Read is open; push needs --allow-push and (optionally) a token.
mcadiff -C <repo> serve --port 8421 --allow-push --token s3cret
mcadiff clone http://host:8421 ./world.mcagit          # anonymous
mcadiff push origin main --token s3cret                # authenticated

# SSH: no daemon — runs `mcadiff serve-stdio` on the remote over your ssh session.
# Auth & encryption are ssh's job (keys/agent); requires mcadiff on the remote.
mcadiff clone ssh://user@host/path/to/world.mcagit ./world.mcagit
mcadiff push  ssh://user@host/path/to/world.mcagit main
```

- **Whole-world snapshots:** region/entities/poi as deduped per-chunk objects,
  loose NBT as canonical objects, everything else (datapacks, stats, advancements)
  as raw blobs — so `checkout` restores a faithful, playable world (a full checkout
  prunes worktree files absent from the snapshot; `.mcaignore`'d files are kept).
- **Delta-packed storage:** `gc` repacks reachable objects into a single packfile
  with delta compression between similar chunks (a chunk whose only change is a
  ticking `InhabitedTime` packs to a few bytes) and prunes the unreachable rest.
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

122 tests covering: the NBT comparer (add/remove/modify/type-change, identity-list
matching, array summarize/expand); the lossless `NbtJson` codec (incl. longs
beyond 2^53); `NbtPath` get/set/remove by key, index and identity; `RegionWriter`
round-trip; the full patch pipeline (forward/reverse round-trips, conflict guard,
`--force`, `--dry-run`); and the VCS — object-store dedup, canonical-form
determinism, commit→checkout reproduces a world, merge-base, and 3-way merge.
The git-likeness tiers add: fsck integrity/reachability, config + identity, the
author/committer split, annotated & SSH-signed tags, object classification + plumbing;
the binary delta codec, packfile round-trips/compression and gc repacking; the
recursive merge base (incl. criss-cross) and the merge stop/continue/abort workflow;
bisect convergence and the staging index; and HEAD@{n}, stash (+gc survival), rebase
(+`--onto`), clean, and hooks. One test additionally parses a real region file when
`MCADIFF_TEST_REGION` points at one (auto-skipped otherwise).

## Minecraft version support

Targets the **Anvil** format. Safe floor is **DataVersion 2724 (1.17)**; below it the
pre-1.18 `Level`-compound shape and pre-1.16 straddled `block_states` packing make
paths and comparisons unreliable. All chunk compression types are decoded:
GZip (1), Zlib (2), uncompressed (3), and **LZ4 frame (4)** — the latter is the
`region-file-compression=lz4` server setting shipped since 1.20.5, so worlds saved
with it diff/commit at full per-chunk granularity (was previously demoted to an opaque
blob). Only type 127 (Custom, a namespaced mod algorithm) is undecodable and falls back
to a raw blob. Data-pack/mod dimensions under `dimensions/<ns>/<path>/` are included.
Excluded: Alpha/Beta/MCRegion (`.mcr`, pre-1.2.1) — a different container.

## Limitations

- No block-coordinate-level decode of palettes (a changed `block_states` array is
  reported as an array diff, not as `(x,y,z): stone → air`; this also means re-encoded
  sections — e.g. a single-block section's optional `data` array appearing/disappearing —
  can read as a representation change). A semantic block-state decoder is future work.
- `diff`/`extract`/`status` never modify a world. `apply` writes only to the fresh
  output directory it creates; `checkout` / `reset --hard` / `bisect` / `merge` update
  the bound worktree in place (a full checkout prunes files not in the snapshot).
- Loose objects are whole (zlib-compressed); `gc` delta-packs them into a packfile,
  but **network transfer is still per-object** (no pack/delta on the wire yet), and
  `apply` copies the whole target world before editing (no hardlink/reflink yet). The
  first commit decodes every chunk (parallelized, a few seconds on a large world);
  re-commits reuse a per-repo decode cache.
- `merge` uses a recursive merge base (folds multiple bases on a criss-cross), and a
  conflicted merge stops with MERGE_HEAD + a conflict list for `merge --continue` /
  `--abort` (resolution is by re-snapshotting the worktree, not in-file markers — the
  files are binary). `merge --ours`/`--theirs` still auto-resolve in one shot.
- Tag/commit signing is SSH-key based (`ssh-keygen`), not GPG. `rebase` is
  non-interactive (no `-i`); on a conflict it keeps the replayed change and reports.
- The HTTP server is a simple built-in daemon (single token, no TLS — put it behind a
  reverse proxy for `https`); `push --all` and `ls-remote` work, but pushing tag refs
  over the network isn't wired up yet.
- A compound key containing a literal `.` or `[` isn't addressable by patch/merge
  paths (real Minecraft keys don't use them).
