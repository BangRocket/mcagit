# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`mcadiff` — a semantic, git-style diff/patch/version-control tool for Anvil-format Minecraft (Java Edition) worlds. Single .NET 9 console app (`src/McaDiff`, assembly name `mcadiff`) + xUnit test project. Only external dependency: `fNbt` (NBT tag tree parsing).

## Commands

```sh
dotnet build -c Release
dotnet test                                            # full suite (~122 tests, all synthetic — no fixtures needed)
dotnet test --filter "FullyQualifiedName~NbtComparer"  # one test class
dotnet test --filter "DisplayName~merge"               # by name fragment
dotnet run --project src/McaDiff -- <args>             # run the CLI
```

- One test (`RegionFileTests`) optionally parses a real region file when `MCADIFF_TEST_REGION` points at one; auto-skipped otherwise.
- `compare-worlds/New_World_Older` and `New_World_Newer` are real sample worlds for manual end-to-end checks (e.g. `dotnet run --project src/McaDiff -- compare-worlds/New_World_Older compare-worlds/New_World_Newer`).
- Tests build synthetic worlds/regions via `tests/McaDiff.Tests/TestAnvil.cs` — use it when adding tests.

## CLI shape

`Program.cs` is a top-level switch over subcommands (git-style, with optional leading `-C <repo>`). `mcadiff <A> <B>` with no subcommand falls through to `diff`. Exit codes follow git: `0` = identical/clean, `1` = differences/conflicts, `2` = error. Repo subcommands live in `Cli/RepoCommands.cs`; diff/extract/apply have their own option parsers in `Cli/`.

## Architecture

Three layers share one core: **diff** (display), **patch** (extract/apply), and **repo** (VCS) all sit on the same NBT comparison walk and canonical encoding — keep them in sync by construction, not duplication.

- **`Anvil/`** — the region container format itself: `RegionFile` (read), `RegionWriter` (write), `RawChunk` (compressed payload + metadata), `ChunkCodec` (payload ↔ fNbt tree). Parsed/written directly; no library.
- **`Nbt/`** — the semantic foundation:
  - `NbtIdentity` — how list elements are matched across versions (block coords, entity UUID, `Slot`, string `id`; index fallback). This is what makes reorders not look like rewrites.
  - `NbtPath` — the path language used everywhere (`Data.Player.Pos[0]`, `Entities[uuid:…].Pos[1]`); get/set/remove by key, index, or identity. Note: keys containing literal `.` or `[` are not addressable.
  - `NbtJson` — *lossless* type-tagged JSON encoding (`{"long":"383"}`), used by patches and repo objects; longs beyond 2^53 must survive round-trips.
  - `NbtCanonical` — deterministic canonical byte form; the basis of content hashing in the object store (an unchanged chunk hashes identically regardless of on-disk compression).
- **`Diff/`** — `NbtComparer` does one tree walk and reports leaf decisions to an `IDiffSink`. Two sinks exist: `NbtChangeSink` (display rows) and `Patch/PatchOpSink` (applyable ops). **Any change to diff semantics must go through the comparer/sink so display and patch can't drift.** `WorldDiffer` orchestrates file/chunk/world level, with fast paths (byte-identical files skipped unparsed; byte-identical compressed chunk payloads skipped undecompressed) and per-region parallelism.
- **`Patch/`** — `.mcapatch` is JSON; every op records both `base` and `value`, making patches invertible (`apply --reverse`). Apply is 3-way guarded: a node is only changed if the target matches the patch's expected base, otherwise it's a reported conflict (skipped unless `--force`). `apply` never mutates the target — it copies to a fresh output world.
- **`Repo/`** — a git work-alike whose unit of dedup is the *chunk*, hashed by decoded NBT:
  - `ObjectStore` — SHA-256 content-addressed, zlib loose objects at `objects/aa/rest…`, plus packfiles (`Packfile`, `Delta`) created by `gc`.
  - `Manifest` ≈ git tree: regions map chunk position → chunk-object hash; loose NBT and other files map path → object hash. `CommitObject`/`TagObject` are JSON with an author/committer split and optional SSH signatures (`SshSigner`).
  - The repo is **bare and external to the world**; a bound worktree (the world dir) is stored in the repo `config`. `Repository.Discover` walks up from cwd, git-style.
  - `Snapshotter` (commit), `Checkout`, `StatusCalc`, `Staging` (index), `MergeBase` (recursive, criss-cross-safe) + `Merger` (per-NBT-node 3-way), `Rebase`, `Stash`, `Bisect`, `Fsck`, `Hooks`.
  - Remotes: `Transports.cs` dispatches path / `http(s)://` / `ssh://` to `HttpTransport` / `SshTransport` (+ `StdioTransport` for `serve-stdio`); `Transfer` copies only missing objects. Network transfer is per-object (no packs on the wire yet).
- **`Output/`** — `TextDiffFormatter` (ANSI, honors `NO_COLOR`/pipe detection) and `JsonDiffFormatter` render the same `DiffModels`.

## Agent delegation rules

Project agents live in `.claude/agents/` (their descriptions say what they do). Standing rules:

- Before declaring a change to `Diff/`, `Nbt/`, or `Patch/` done, have `nbt-diff-invariant-reviewer` review it.
- Before declaring a substantive change to `Anvil/`, `Patch/`, or `Repo/` done, run `world-roundtrip-gauntlet`.
- When implementing a new git-like command, consult `git-fidelity-researcher` for real-git semantics first.
- New tests go through `testanvil-test-author` conventions (synthetic worlds via `TestAnvil.cs`, no fixtures).
- Changes touching `RepoServer`, transports, `PatchApplier` path handling, or hooks get a `trust-boundary-exploit-hunter` pass.

### Pre-PR checklist

Before opening a PR, in this order:

1. `dotnet test` — full suite green locally. (Note: local machines without the .NET 9 runtime need `DOTNET_ROLL_FORWARD=LatestMajor`.)
2. Map the branch diff (`git diff main...HEAD --stat`) against the delegation rules above and run **every** agent whose paths are touched — a PR spanning `Diff/` and `Repo/` gets both the invariant review and the gauntlet.
3. `trust-boundary-exploit-hunter` additionally runs if the diff touches anything reachable from untrusted input (network, patch apply, checkout path handling), even if the rule paths above don't match.
4. Surface agent findings in the PR description: BLOCKERs must be fixed before opening; WARNs may ship but get listed.

CI (`.github/workflows/ci.yml`) re-runs build + tests (ubuntu/windows), coverage, and the e2e round-trip gauntlet on the real sample worlds. The `lint` job (`dotnet format --verify-no-changes`) is now green — run `dotnet format McaGit.sln` before pushing to keep it that way (note: it expands compact multi-member object initializers to one member per line and indents `case: {}` block bodies a level deeper).

## Invariants worth preserving

- `diff`/`extract`/`status` never modify a world; `apply` only writes its fresh output dir; only `checkout`/`reset --hard`/`merge`/`rebase`/`bisect`/`clean`/`stash` touch the bound worktree.
- Commit → checkout must reproduce a world faithfully (playable in Minecraft); tests assert exact reproduction.
- LZ4 chunks (compression type 4) are detected and handled as unsupported — stored as raw blobs, never decoded.
- README documents behavior in detail (options, repo layout, limitations) — update it when changing user-facing behavior; recent commits follow a "git-likeness tier" naming convention with matching `GitLikeTierNTests.cs` files.
