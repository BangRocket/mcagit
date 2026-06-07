# Copilot / AI agent instructions for mcagit

mcagit is a semantic, git-style diff / patch / version-control tool for Anvil-format Minecraft (Java Edition) worlds. One .NET 10 (LTS) console app (`src/McaDiff`) plus an xUnit test project. Dependencies: `fNbt` (NBT tree), `K4os.Compression.LZ4` (LZ4 chunks), `Azure.Storage.Blobs` / `AWSSDK.S3` (cloud remotes).

When suggesting code, hold these load-bearing invariants. They are correctness, not style.

## Architecture

Three layers share one core. **Diff** (display), **patch** (extract / apply), and **repo** (VCS) all sit on the same NBT comparison walk and canonical encoding. Keep them in sync by construction, not duplication.

- `Anvil/` — the region container: `RegionFile`, `RegionWriter`, `RawChunk`, `ChunkCodec` (payload ↔ fNbt tree).
- `Nbt/` — the semantic foundation: `NbtIdentity`, `NbtPath`, `NbtJson`, `NbtCanonical`.
- `Diff/` — `NbtComparer` does one tree walk and reports leaf decisions to an `IDiffSink`. Two sinks: `NbtChangeSink` (display) and `Patch/PatchOpSink` (applyable ops).
- `Patch/` — `.mcapatch` JSON; invertible, 3-way guarded.
- `Repo/` — content-addressed VCS, dedup unit is the chunk.
- `Output/` — `TextDiffFormatter` / `JsonDiffFormatter` render the same `DiffModels`.

## Invariants

- **One comparison walk.** Any change to diff semantics goes through `NbtComparer` / `IDiffSink` so the display diff and the extracted patch cannot drift. Do not write a second comparison.
- **Identity-based list matching.** Lists that behave as sets are matched by identity, not position, so a reorder is not a rewrite. Identity preference: block coords `(x,y,z)`, entity UUID, inventory `Slot`, string `id`, then index fallback (`NbtIdentity` / `NbtPath`).
- **Canonical determinism.** `NbtCanonical` produces deterministic bytes independent of on-disk compression or fNbt ordering; it is the basis of content hashing. Never make it depend on fNbt internals, iteration order, or a diff-path normalization. An unchanged chunk must hash identically.
- **Diff-only normalizations stay out of canonical.** Representation differences Minecraft treats as equal (e.g. a redundant single-entry-palette `data` array) are cancelled in the diff path (`Diff/ChunkNormalize`), applied to decoded roots before comparing — never on the storage / canonical path.
- **Lossless type-tagged JSON.** `NbtJson` encodes types (`{"long":"383"}`); longs beyond 2^53 and float/double NaN/Inf are string-encoded and must survive round-trips. Do not encode them as JSON numbers.
- **Never mutate in place.** `diff` / `extract` / `status` never modify a world. `apply` only writes a fresh output dir. Only `checkout` / `reset --hard` / `merge` / `rebase` / `bisect` / `clean` / `stash` touch the bound worktree.
- **Reproduction.** `commit` → `checkout` must reproduce a playable world; tests assert exact reproduction. Patches are 3-way guarded (a node changes only if the target matches the patch's recorded base) and invertible (`apply --reverse`).
- **LZ4 is decoded; Custom is not.** Compression type 4 (LZ4) is fully decoded / re-encoded. Only type 127 (Custom) is unsupported — a region containing one falls back to a raw blob (do not claim type 4 is unsupported; that is stale).
- **Untrusted input is confined.** Manifest keys, patch paths, and network-supplied ref names go through `PathGuard.Confine`; object ids must pass `ObjectStore.IsValidHash`; thread `depth` through any new recursive NBT walk and respect `NbtCanonical.MaxDepth`.

## Conventions

- Exit codes follow git: `0` identical / clean, `1` differences / conflicts, `2` error. (`commit` deliberately exits `0` on "nothing to commit" — the `--json` `committed` flag is the signal; do not change this.)
- Tests are synthetic via `tests/.../TestAnvil.cs` — no binary fixtures. See `TESTING.md`.
- Run `dotnet format McaGit.sln` before committing (CI gates on `--verify-no-changes`).
- Format specs: `docs/repo-format.md`, `docs/mcapatch-format.md`, `docs/cloud-backend.md`. Project guidance: `CLAUDE.md`.
