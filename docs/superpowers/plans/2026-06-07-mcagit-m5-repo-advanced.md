# mcagit Rust — M5: packfiles/gc, fsck, merge, and the git-likeness tail

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline). Steps use `- [ ]`.

**Goal:** Make commit fast (packfiles + pack-at-commit), add integrity (`fsck`) and space reclamation (`gc`), then the git-likeness commands: branch/tag, merge-base + 3-way merge, and the tail (rebase, stash, bisect, reflog, revert, cherry-pick). SSH signing optional/deferred.

**Architecture:** Extend `mca-repo`. A packfile is one file of concatenated zstd object bodies + a JSON index (id → offset/len); `ObjectStore` reads packs first, then loose. Commit batches its new objects into one pack (the fix for the APFS-bound loose-object commit). `gc` consolidates loose+packs into a single pack and prunes unreachable. Merge reuses `mca-diff`/`mca-patch` 3-way machinery per chunk/node.

**Reference:** .NET `Repo/{Packfile,Delta,Gc,Fsck,MergeBase,Merger,Rebase,Stash,Bisect}.cs`.

## Tasks (value order; each green + committed)

### M5-T1: Packfile format + ObjectStore pack reads

- `pack.rs`: write `objects/pack/pack-<id>.{pack,idx}` — `.pack` = concatenated `zstd(obj)` bodies, `.idx` = JSON `{id: [offset,len]}`. `Packfile::write(items)`, `Packfile::open` (mmap or read), `get(id)`.
- `ObjectStore`: load packs once; `read`/`exists` check packs then loose. `write` unchanged (loose).
- Tests: write a pack of N objects, read each back; ObjectStore reads from pack.
- Commit `feat(repo): packfile format + pack-aware object store`.

### M5-T2: pack-at-commit

- `ObjectStore::write_batch(contents) -> Vec<id>` that streams all new objects into one packfile (dedup vs existing). `snapshot` collects (rel/pos → content) then one `write_batch`. Commit becomes one pack write, not 310k loose creates.
- Benchmark note: re-run the focused dobbscraft commit — expect seconds, not minutes.
- Commit `feat(repo): pack-at-commit (batch object write)`.

### M5-T3: fsck

- `fsck(repo)`: every object's stored bytes re-hash to its id; reachability from branches/tags/HEAD/reflog; report corrupt/missing/unreachable.
- Tests: clean repo → 0 issues; corrupt an object → detected.
- Commit `feat(repo): fsck`.

### M5-T4: gc

- `gc(repo, threads)`: gather reachable objects, write one consolidated pack (parallel segments + serial concat, mirroring the .NET branch), delete loose + old packs, prune unreachable.
- Tests: commit, gc, checkout still reproduces; unreachable pruned.
- Commit `feat(repo): gc (consolidate + prune)`.

### M5-T5: branch/tag + merge-base

- `branch` create/list/delete, `tag` (lightweight + annotated TagObject), `merge_base(a,b)` recursive (criss-cross safe via ancestor sets).
- Tests: linear + branching histories; criss-cross.
- Commit `feat(repo): branch/tag + recursive merge-base`.

### M5-T6: 3-way merge

- `merge(repo, ours, theirs)`: base = merge_base; per file/chunk/node 3-way (reuse comparer to derive ours/theirs ops vs base; apply non-conflicting; report conflicts). Fast-forward when base==ours. Writes worktree + records MERGE_HEAD on conflict.
- Tests: ff merge; clean 3-way (disjoint chunk edits); conflicting edit → reported.
- Commit `feat(repo): 3-way merge`.

### M5-T7: the tail (rebase, stash, bisect, reflog, revert, cherry-pick)

- Reflog (HEAD moves), revert (inverse patch commit), cherry-pick (apply one commit's diff via 3-way), stash (shelve/restore worktree), rebase (replay commits onto new base via cherry-pick), bisect (good/bad binary search).
- Tests per command (synthetic histories).
- Commit per command.

### M5-T8: CLI wiring + gate

- CLI: branch/tag/merge/rebase/stash/bisect/revert/cherry-pick/reflog/fsck/gc.
- Gate: `cargo test --all` + fmt + clippy; re-run dobbscraft commit (now packed/fast) + checkout + .NET oracle; push.

## Deferred

- SSH commit/tag signing (M7 or optional), delta compression between pack objects (size optimization; M5 packs are zstd-per-object, no cross-object delta yet).

## Done criteria

- Commit is pack-fast on real worlds; fsck/gc work; merge (ff + 3-way + conflict) works; tail commands present; CLI wired; full gate green.
