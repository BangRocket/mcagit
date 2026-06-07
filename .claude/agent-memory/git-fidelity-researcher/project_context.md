---
name: project-context
description: mcagit architecture, git-likeness tiers, chunk-based model, and key design decisions
metadata:
  type: project
---

mcagit is a git-style VCS for Minecraft Anvil worlds (Java Edition). Key design decisions:

- Unit of dedup is the *chunk*, hashed by decoded NBT (not raw bytes). SHA-256, not SHA-1.
- Repo is **external and bare**; bound worktree is stored in repo config.
- Conflict resolution is **marker-free** — conflicts are resolved at chunk/NBT-node granularity.
- Config stored as JSON (`<repo>/config`), not INI. Global config at `~/.mcaconfig`.
- Reflog stored at `logs/HEAD` in the same `from to message` format as git.
- Stash stored as plain commit hashes in `<repo>/stash` (a line-per-entry stack).
- Packfile delta compression exists (`gc`). No pack-on-the-wire yet (per-object transfer).
- SSH signing (not GPG). Tag/commit signing supported.
- `.mcaignore` (gitignore-lite) lives in the world dir, not the repo dir.

**Git-likeness tier history (from commits):**
- Tier 1: init/add/commit/status/diff/log/show/checkout/reset/restore/revert/branch/tag/merge/cherry-pick
- Tier 2: packfiles + delta compression in gc
- Tier 3: merge conflict workflow + recursive merge base
- Tier 4: bisect + staging index (+ faithful-checkout fix)
- Tier 5: stash, rebase, clean, ranges, reflog syntax, hooks, remote polish

**Key files:**
- `src/McaDiff/Cli/RepoCommands.cs` — all porcelain command implementations
- `src/McaDiff/Repo/Repository.cs` — ref resolution, reflog, HEAD, branches, tags
- `src/McaDiff/Repo/Merger.cs` — 3-way merge, --abort, --continue
- `src/McaDiff/Repo/Stash.cs` — stash push/pop/apply/drop/clear
- `src/McaDiff/Repo/Rebase.cs` — non-interactive rebase
- `src/McaDiff/Repo/RemoteOps.cs` — clone/fetch/push
- `src/McaDiff/Repo/Staging.cs` — index (add/unstage)
- `src/McaDiff/Repo/IgnoreRules.cs` — .mcaignore rules

**Why:** Established from full codebase review on 2026-06-03.
