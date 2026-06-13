# Staging index (`mcagit add`) — design

**Date:** 2026-06-13
**Status:** Approved (pending spec review)
**Scope:** Add a git-style staging area to mcagit: an `add` command, a persistent
index, and the `commit`/`status`/`reset`/`restore` changes that follow from it.

## Problem

mcagit today has **no staging index**. `commit` snapshots the *entire* bound
worktree (or a given path) into a `Manifest` (≈ a git tree) and commits it
atomically. There is no way to build up a partial next commit — no `git add`
workflow. This spec introduces one.

## Decisions (locked during brainstorming)

1. **Persistent index.** A real `<repo>/index` file holds the staged tree.
   `add` stages paths into it; `commit` commits the index; `status` becomes
   three-way; `reset`/`restore --staged` unstage.
2. **File/path-level granularity.** The unit of staging is a whole file — region
   files (`r.X.Z.mca`, staged all-or-nothing including all their chunks),
   `level.dat`, `playerdata/*.dat`, blobs — plus directories (recursive) and
   globs. No chunk-coordinate-level staging.
3. **Git-faithful commit + guardrail.** Bare `commit` commits the **index**.
   `commit -a/--all` snapshots the whole worktree (today's behavior). If nothing
   is staged but the worktree is dirty, bare `commit` errors with a hint instead
   of committing a no-op. A `commit.autoStageAll` repo-config flag makes bare
   `commit` behave like `-a` (for existing automated/backup commits).
4. **Index representation: full materialized `Manifest`.** `<repo>/index` is a
   complete `Manifest` JSON — the same type `commit` already produces. A missing
   index file means "index ≡ HEAD's tree". `commit` is just
   `write_manifest(index)`; no overlay/merge logic. (Rejected alternative: a
   sparse delta-vs-HEAD overlay — smaller on disk but requires reconstructing the
   effective tree on every read and invalidates when HEAD moves.)

## Background: the current model (what we build on)

- `Repository` is **bare and external**; the bound worktree path is stored in the
  repo `config`. `commit`/`status` resolve the worktree from there.
- `Manifest` (`crates/repo/src/manifest.rs`): `regions: BTreeMap<relpath,
  BTreeMap<"x,z", chunkObjId>>`, `nbt: BTreeMap<relpath, objId>`, `blobs:
  BTreeMap<relpath, objId>`, `empty_dirs: Vec<String>`.
- `snapshot::build` (`snapshot.rs`) walks the whole worktree in parallel,
  classifies each file (region → per-chunk canonical objects; `.dat` → canonical
  NBT object; else → raw blob), and writes objects via a `Sink` (`Pack` for
  commit, `HashOnly` for status). `hash_only` computes a `Manifest` without
  writing.
- `status::status` (`status.rs`) = `diff(HEAD.tree, hash_only(worktree))`, where
  `flatten` folds each region's chunk ids into one signature (so a changed chunk
  marks its region modified).
- `commit` (`cli/src/main.rs`): `snapshot` worktree → `write_manifest` → tree
  hash → `create_commit_signed` → move branch → reflog. Has a `tree == HEAD.tree`
  short-circuit that prints "nothing to commit".

## Design

### 1. Index module (`crates/repo/src/index.rs`, new)

- On-disk: `<repo>/index`, a `Manifest` serialized as JSON, written **atomically**
  (write to `index.tmp`, `rename` into place).
- API:
  - `read(repo) -> Result<Option<Manifest>>` — `None` if the file is absent.
  - `write(repo, &Manifest) -> Result<()>` — atomic.
  - `clear(repo) -> Result<()>` — remove the file (→ "index ≡ HEAD").
  - `effective(repo) -> Result<Manifest>` — the staged tree to commit/diff
    against: the index if present; else HEAD's tree; else `Manifest::default()`
    (unborn HEAD).
- **Invariant: a clean index is represented by the file's *absence*, not by a
  copy of HEAD.** Every HEAD-moving / worktree-rewriting op ends by calling
  `clear`, so the index file exists only when there is genuinely staged content.

### 2. `add` command (`crates/cli/src/main.rs` + `crates/repo`)

CLI: `mcagit add <pathspec>...` with `-A/--all` (and `add .`) for the whole
worktree.

Semantics:
- Pathspecs resolve **relative to the bound worktree root** (mcagit is
  bare/external; this avoids cwd ambiguity). A pathspec may name a file, a
  directory (recursive), or a glob.
- Start from `index::effective(repo)` (so successive `add`s accumulate).
- Snapshot just the matched worktree files through the existing pack `Sink`
  (writes objects into a pack, updates `chunkcache.json`), producing a partial
  `Manifest`; merge its entries over the index manifest.
- **Deletions:** for every index entry whose path falls within a pathspec's scope
  but no longer exists in the worktree, remove that entry (stage the removal).
  So `add playerdata/` stages deletions under `playerdata/` as well as
  modifications.
- `empty_dirs`: recomputed for directories within scope.
- Write the result with `index::write`.
- A pathspec matching nothing → error `pathspec '<x>' did not match any files`.

### 3. Snapshot refactor (`crates/repo/src/snapshot.rs`)

`build` currently always walks the entire worktree. Add a **path filter** so a
subset can be snapshotted and merged:

- Introduce a scope/predicate parameter (accept-all for full snapshot;
  pathspec-scoped for `add`). The filter decides which walked files are
  classified, and reports the set of in-scope rel-paths so `add` can compute
  deletions (in-scope index entries absent from the walk).
- `snapshot`/`snapshot_with_progress`/`hash_only` pass an accept-all filter →
  behavior unchanged. `add` passes its pathspec scope.
- Keep it **one walk function** so commit/status/add hash identically by
  construction (per the "one comparison walk" / reproduction invariants).

### 4. `commit` (changed)

- Resolve the tree from `index::effective(repo)` instead of always snapshotting
  the worktree.
- **Guardrail** (replaces the bare `tree == HEAD.tree` short-circuit):
  - index tree `==` HEAD tree **and** worktree dirty → exit 2,
    `error: nothing staged for commit. use \`mcagit add <path>\` or \`commit -a\`.`
  - index tree `==` HEAD tree **and** worktree clean → existing
    `nothing to commit — world matches HEAD` (exit 0).
  - otherwise commit the index tree.
- `-a/--all`: snapshot the whole worktree (today's path), commit it.
- `commit.autoStageAll=true` in repo config → bare `commit` behaves as `-a`.
- On success: `index::clear(repo)`. Hooks (`pre-commit`/`post-commit`), signing,
  reflog, and the stdout commit-hash contract are all unchanged.

### 5. `status` (changed → three-way)

Reuse `flatten`/`diff` from `status.rs`:
- **Changes staged for commit:** `diff(HEAD.tree, index::effective)`.
- **Changes not staged for commit:** tracked paths (in the index) that differ
  between `index::effective` and `hash_only(worktree)` → modified/deleted.
- **Untracked:** worktree paths absent from the index.

CLI prints the three groups git-style; clean worktree + clean index → "nothing to
commit, working tree clean". Exit code conventions unchanged.

### 6. `reset` / `restore` / `clean` (changed)

- `reset` **mixed** (default): move the branch to `<rev>` **and** reset the index
  to match. Since the new HEAD *is* `<rev>`, "index ≡ HEAD" — so this is an
  `index::clear` (per the section-1 invariant that a clean index is the file's
  absence), not a written copy. `--soft`: move the ref only, leave the index.
  `--hard`: move ref + clear index + reset worktree.
- `restore --staged <paths>` (new flag): copy those paths' entries from HEAD (or
  `--source <rev>`) into the index — i.e. unstage. Existing
  `restore <rev> <paths>` (worktree restore) is unchanged.
- `clean`: a path staged in the index counts as tracked, so `clean` must **not**
  remove it (today "untracked" = not-in-HEAD; it becomes not-in-index).

### 7. Index as a GC/fsck reachability root (`gc.rs`, `fsck.rs`)

The index's object ids (chunk ids, nbt ids, blob ids) become a **root** in the
reachability walk, alongside refs and reflogs. Without this, `add` followed by
`gc` would prune objects referenced only by the index and corrupt it. `fsck`
likewise reports the index's objects as reachable and flags any that are missing.

### 8. Index reset points (avoid stale-index bugs)

Operations that move HEAD or rewrite the worktree end by clearing/refreshing the
index to the resulting HEAD: `commit`, `checkout`, `reset` (mixed/hard),
`merge`, `rebase`, `cherry-pick`, `revert`, and `stash` apply/pop. Rationale:
preserving partial staged state across a three-way merge or replay is fiddly and
error-prone; a clean post-operation index is predictable and safe. (Git preserves
more here; we deliberately don't, for simplicity. Revisit only if a need shows
up.)

## Edge cases

- **Unborn HEAD (no commits yet):** `effective` is `Manifest::default()`; `add`
  builds it up; the first `commit` may commit a *partial* world. This is allowed
  and intentional — you're choosing what to snapshot; `checkout` materializes
  exactly the staged tree (which may be an incomplete, not-yet-playable world).
- **Pathspec escaping the worktree** (`..`, absolute paths outside root): rejected
  via the existing path-confinement used by checkout/apply.
- **Objects written by `add` but never committed:** loose pack objects, kept
  alive by the index root (section 7), reclaimed by `gc` once unstaged — exactly
  like git.
- **Concurrent runs:** index writes are atomic (temp + rename); the existing
  `mcagit.lock` continues to serialize mutating commands.

## Testing

Synthetic worlds only — no binary fixtures (per CLAUDE.md). All gates: `cargo test
--all`, `cargo fmt --all -- --check`, `cargo clippy --all-targets -- -D warnings`.

- **`index.rs`:** read/write/clear round-trip; `effective` fallback for present /
  unborn / post-commit states; atomic write leaves no partial file.
- **`add`:** single file; directory (recursive); glob; deletion staging within a
  pathspec scope; pathspec-no-match error; objects actually land in a pack.
- **`commit`:** index-only commit; guardrail error when nothing staged + dirty
  worktree (exit 2); clean/clean → "nothing to commit"; `-a` whole-worktree;
  `commit.autoStageAll`.
- **`status`:** three-way — a path staged, a different path modified-unstaged, a
  third untracked, all classified correctly.
- **`reset`/`restore --staged`:** mixed reset clears/refreshes the index; `--soft`
  leaves it; `--hard` resets index + worktree; `restore --staged` unstages a path.
- **`gc` root:** `add` a file, `gc`, assert the object survives and the index is
  still valid/checkout-able.
- **End-to-end** on `compare-worlds/`: `add` a subset → `commit` → `checkout` →
  `verify` reproduces exactly the staged tree.

## Out of scope (YAGNI for now)

- Chunk-coordinate-level staging (decided against).
- `diff --staged` / `diff --cached` (could follow naturally later).
- `.mcagitignore` / ignore rules and a distinct "assume-unchanged" bit.
- Preserving staged state across merge/rebase/stash.

## Affected files

- New: `crates/repo/src/index.rs`.
- Changed: `crates/repo/src/snapshot.rs` (path filter), `status.rs` (three-way),
  `gc.rs` + `fsck.rs` (index root), `lib.rs` (export `index`),
  `repository.rs` (config helpers for `commit.autoStageAll` if not already
  generic), `crates/cli/src/main.rs` (`add`, `commit -a`/guardrail,
  `status` output, `reset` index handling, `restore --staged/--source`, `clean`).
- Docs: `README.md` + `CLAUDE.md` CLI shape / invariants updated for the index.

## Invariant check

- **Never mutate in place:** `add`/`status` never modify the worktree; `add` only
  writes repo state (objects + index). Unchanged.
- **Reproduction:** `commit → checkout` reproduces the committed (now possibly
  partial) tree; `verify` re-hashes against it. Tests assert this.
- **Canonical determinism / one walk:** `add` reuses the same `build`/`classify`
  path as commit/status — no second comparison or hashing path introduced.
