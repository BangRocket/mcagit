# Embedded `.mcagit/` repo layout — design

**Date:** 2026-06-13
**Status:** Approved (pending spec review)
**Branch:** `feat/embedded-layout` (off `main`; independent of the staging-index PR #54)
**Scope:** Make `mcagit init` and `clone` create the repo metadata (HEAD/refs/objects/config/logs/index) inside a `.mcagit/` folder within the target directory — git-style — with that directory bound as the worktree. Keep the current bare/external-worktree model available behind flags, and protect the embedded `.mcagit/` from being deleted by prune.

## Problem

mcagit is **bare + external**: `init` writes HEAD/objects/refs/config directly into the repo directory, and the worktree (the Minecraft world) is a *separate* folder bound via the repo `config`. Users want the familiar git layout: run `mcagit init` in a world folder and get a `.mcagit/` subfolder holding the metadata, with that folder as the worktree.

## Decisions (locked during brainstorming)

1. **Embedded by default, bare kept.** `init <folder>` / `clone <url> <folder>` create `<folder>/.mcagit/` and bind the worktree to `<folder>`. The bare/external model stays available via flags (`--worktree <path>` for an external world, `--bare` for no worktree). Both coexist.
2. **Scope = `init` + `clone`.** Both create embedded repos by default. `serve` auto-init stays **bare** (servers hold bare repos, like git).
3. **Clone auto-checks-out.** After `clone <url> <folder>`, HEAD is checked out into `<folder>` so it's an immediately-usable world (git-like).
4. **Prune-protection is a hard requirement.** `checkout(prune=true)` — and therefore `reset --hard` / `pull` / `merge` / `bisect` into the bound worktree — must NEVER delete the repo's own `.mcagit/` directory.
5. **Backward compatible, no migration.** The flat on-disk format is unchanged and *is* the bare layout, so existing repos (e.g. `dobbscraft.mcagit`) keep opening as bare repos.

## Background: the current layout (what we build on)

- `Repository::dir()` returns the directory that **directly** contains `HEAD`, `objects/`, `refs/`, `config`, `logs/`, `index`. Every metadata path is `dir().join(<name>)` (e.g. `index.rs` uses `repo.dir().join("index")`). This is the single source of truth for layout.
- `Repository::init(dir)` (`repository.rs`) creates `dir/objects`, `dir/refs/heads`, `dir/HEAD` and returns a repo with `dir()=dir`. It is the **only** constructor of the on-disk layout, used by the CLI `init`, by `clone_local`/`clone_partial`/`clone_depth` (`transfer.rs`/`remote.rs`), by `serve` auto-init, and by ~40 tests.
- `is_repository(dir)` = `dir/HEAD` && `dir/objects`. `open(dir)` requires that. `discover(start)` walks up calling `is_repository`.
- The worktree is stored in `config` (`worktree = <path>`) and read by `worktree()`. `init --worktree <path>` binds it; a bare repo may have none.
- `snapshot::build` already takes the repo dir and **excludes any path under it** from the worktree walk (`p.starts_with(repo_prefix) → continue`) — so a `.mcagit/` inside the worktree is never captured in a snapshot. `status`/`hash_only` go through the same walk, so `.mcagit/` never appears as a tracked/untracked change either.
- `checkout::prune_extra` (`checkout.rs`) walks `out_dir` and deletes every file not in the manifest. It has **no** repo-dir exclusion — safe today only because the worktree is external. This is the dangerous spot for the embedded model (Design §4).

## Design

The guiding rule: **`Repository::dir()` keeps meaning "the directory that directly holds the metadata"** — `.mcagit/` for embedded repos, the dir itself for bare repos. All internal layout code is unchanged; only construction, detection, and prune-protection change.

### 1. Detection & resolution (`repository.rs`)

- `is_repository(path) -> bool`: true if `path` is a flat repo (`path/HEAD` && `path/objects`) **or** `path/.mcagit` is a flat repo (`path/.mcagit/HEAD` && `path/.mcagit/objects`).
- `open(path) -> Result<Repository>`: detect and set `dir` accordingly:
  - if `path/.mcagit` is a flat repo → **embedded**: `dir = path/.mcagit`.
  - else if `path` is a flat repo → **bare**: `dir = path`.
  - else → `NotARepository`.
  - So `open(world-folder)`, `open(world-folder/.mcagit)`, and `open(bare-dir)` all succeed, and `-C <x>` accepts any of them. (`open(.mcagit-dir)` hits the second branch — `.mcagit/` is itself a flat repo — which is correct.)
- `discover(start) -> Result<Repository>`: walk up from canonicalized `start`; at each ancestor, prefer `ancestor/.mcagit` (embedded) over a flat `ancestor` (bare); stop at the first match; error at the filesystem root. This makes `mcagit status` etc. work from anywhere inside a world. (The worktree is not inferred here — it comes from `config`, which `init_embedded`/`clone` always populate; see §5.)
- A new private helper centralizes the "is `dir` a flat repo" check so `is_repository`/`open`/`discover` share one definition.

### 2. `init` — embedded by default, bare via flag (`repository.rs` + CLI)

New low-level constructor, keeping `Repository::init` unchanged:

```
Repository::init(dir)            // UNCHANGED — flat layout in `dir`, dir()=dir (bare). All existing callers keep working.
Repository::init_embedded(folder) -> Result<Repository>
    // create `folder` if needed; flat-init `folder/.mcagit`; config worktree = folder; return repo (dir()=folder/.mcagit)
```

CLI `mcagit init [DIR] [--worktree <path>] [--bare]` (DIR defaults to `.`):
- **default** (no flags): `init_embedded(DIR)` → `DIR/.mcagit/`, worktree = DIR.
- `--worktree <path>`: `init(DIR)` (flat) + `set_worktree(path)`. Today's external behavior, preserved.
- `--bare`: `init(DIR)` (flat), no worktree.
- `--worktree` and `--bare` are mutually exclusive (clap `conflicts_with`).
- **Idempotent re-init:** if DIR is already a repo, re-init in its existing layout — do not nest a new `.mcagit/` inside an existing bare repo, and do not flatten an existing embedded one. Concretely: if `init_embedded` is requested but DIR is already a *bare* repo (flat HEAD/objects at top), keep the bare layout (no-op re-init); if DIR already has `.mcagit/`, that flat-init is itself idempotent.

### 3. `clone` — embedded by default + auto-checkout (CLI + `remote.rs`/`transfer.rs`)

`mcagit clone <src> <dst> [--bare] [--worktree <path>] [--depth N] [--filter blob:none]`:
- **default:** build the embedded layout at `dst/.mcagit` (worktree = dst), copy objects + refs as today, then **check out HEAD into `dst`** (materialize the world). After clone, `dst` is a usable world with `dst/.mcagit` inside it.
- `--bare`: today's flat dest, no worktree, no checkout.
- `--worktree <path>`: flat dest, external worktree bound to `path` (no auto-checkout of `dst`; user checks out into `path` if desired) — preserves the current power-user form.
- `--depth` / `--filter blob:none` compose with the above (a partial/shallow embedded clone checks out what it has; partial clones backfill leaves on checkout as today).

Implementation: the clone entry points (`clone_local`, `clone_partial`, `clone_depth`) currently call `Repository::init(dst)`. Factor the "where do the metadata go + bind worktree" decision so the default path uses `init_embedded(dst)` and the `--bare`/`--worktree` paths use `init(dst)`. After a successful default/embedded clone, run `checkout(&repo, &head_manifest, worktree, prune=false)` (prune=false: nothing to prune in a fresh dir, and it avoids any prune interaction during clone).

### 4. Protect `.mcagit/` from prune — HARD REQUIREMENT (`checkout.rs`)

`prune_extra(out_dir, manifest)` must not delete the repo's own directory when it lives inside `out_dir`. Change the signature to take the repo dir to exclude:

```
prune_extra(out_dir, manifest, repo_dir: Option<&Path>)
    // skip any walked path that is under a canonicalized `repo_dir`
```

`checkout()` passes `Some(repo.dir())`. The exclusion mirrors `snapshot::build`'s: canonicalize `repo_dir`, and in the walk `if p.starts_with(repo_prefix) { continue; }` so neither the `.mcagit/` files nor the `.mcagit/` directory itself are removed. For a bare/external checkout (repo dir not under `out_dir`) the exclusion is a no-op, so behavior is unchanged.

Regression test (the headline safety test): an embedded repo, stage/commit, then `reset --hard` (and separately `checkout`) — assert `<worktree>/.mcagit/HEAD` and an objects file still exist afterward and `Repository::open(<worktree>)` still succeeds and reproduces the tree.

### 5. Worktree resolution & config

`worktree()` is unchanged — it reads `config`. `init_embedded` and the embedded `clone` path write `worktree = <folder>` into config, so the rest of the system (commit/status/checkout/clean resolving the worktree) needs no change. The snapshot/prune exclusions use `repo.dir()` (= `<folder>/.mcagit`), which is correctly under the worktree.

### 6. Backward compatibility

No migration and no format change. Existing flat repos satisfy the `open`/`is_repository` "flat repo" branch and open as **bare** repos exactly as before (their `config` worktree, if external, still applies). New repos are embedded. `-C <path>` and discovery accept either.

## Edge cases

- **`init` in a folder that is already a bare repo:** default mode re-inits the existing bare layout (no nested `.mcagit/`). Documented in §2.
- **`open` precedence:** `.mcagit/` (embedded) is preferred over a same-level flat layout if both somehow exist (shouldn't happen normally).
- **Worktree == repo-parent overlap:** the repo dir (`.mcagit`) is inside the worktree; snapshot already excludes it, and §4 makes prune exclude it. `clean` is already safe (driven by the repo-excluding snapshot, so `.mcagit/` never shows as untracked).
- **Symlinks/canonicalization:** exclusions canonicalize both the repo dir and walked paths (as `snapshot::build` does) so a symlinked worktree path still matches.
- **`serve`:** unchanged — auto-init stays bare.

## Testing

Synthetic worlds only; no binary fixtures. Gates: `cargo test --all`, `cargo fmt --all -- --check`, `cargo clippy --all-targets -- -D warnings`.

- `repository.rs`: `init_embedded` creates `.mcagit/` and binds worktree; `open`/`is_repository`/`discover` detect embedded vs bare vs neither (incl. discover from a nested subdir); re-init idempotency for both layouts.
- `checkout.rs`: **prune preserves `.mcagit/`** (the safety test above); a full commit→checkout→re-snapshot round-trip in an embedded repo yields the identical tree.
- `snapshot.rs`: an embedded-layout snapshot excludes `.mcagit/` (assert no `.mcagit/...` keys in the manifest).
- CLI integration (`crates/cli/tests/cli.rs`): `mcagit init world/` (no `-C`) then, from inside `world/`, `commit -a` / `status` work via discovery and `.mcagit/` is not committed; `mcagit clone src dst` produces `dst/.mcagit` and a materialized world (auto-checkout); `reset --hard` in an embedded repo leaves `.mcagit/` intact; existing bare/external flows (`init repo --worktree world`, clone `--bare`) still pass.
- Existing suite: all current tests (which use `Repository::init` = bare) must stay green; only physical-layout-checking tests (e.g. those reading `<repo>/objects`) are unaffected because they keep using the bare `init`.

## Out of scope (YAGNI)

- Migrating existing bare repos to embedded (not needed — both are supported).
- A `--separate-git-dir`-style split (gitdir elsewhere, pointer file in worktree).
- Worktree-relative `.mcagitignore` or per-worktree config beyond what exists.
- Changing `serve` to embedded.

## Affected files

- `crates/repo/src/repository.rs` — `init_embedded`, the shared flat-repo detector, `is_repository`/`open`/`discover` embedded-aware.
- `crates/repo/src/checkout.rs` — `prune_extra` repo-dir exclusion; `checkout` passes `repo.dir()`.
- `crates/repo/src/transfer.rs` + `crates/repo/src/remote.rs` — clone entry points choose embedded vs bare construction and auto-checkout on the embedded path.
- `crates/cli/src/main.rs` — `init` flags (`--bare`; keep `--worktree`), `clone` flags (`--bare`/`--worktree`), wiring to the new constructors + post-clone checkout.
- Docs: `README.md`, `CLAUDE.md` (layout, `init`/`clone` flags, discovery/`-C` accepting embedded or bare).

## Invariant check

- **Never capture/destroy repo metadata.** Snapshot already excludes `repo.dir()`; §4 extends the same exclusion to prune, so the embedded `.mcagit/` is neither committed nor deleted. This is the load-bearing safety property.
- **Reproduction.** commit→checkout still reproduces a playable world; the embedded round-trip test asserts it.
- **No second walk.** Detection/exclusion reuse the existing `repo.dir()`-prefix approach from `snapshot::build`; no new comparison/scan logic is introduced beyond the prune exclusion.
- **Backward compatibility.** On-disk object/ref/manifest formats are byte-for-byte unchanged; only the enclosing directory differs.
