---
name: new-git-command
description: Scaffold a new git-likeness command for mcadiff end to end - research real-git semantics, wire the CLI, implement in Repo/, add tier tests, and update the README. Use when adding a git work-alike subcommand (e.g. "add mcadiff worktree", "implement notes", "next git-likeness tier").
---

Implement a new git-style subcommand following the pattern established by tiers 1–5. The argument is the command name (e.g. `worktree`, `notes`, `describe`).

## Steps

1. **Research real git first.** Launch `git-fidelity-researcher` with the command name. Required output: exact flag set worth supporting, exit-code conventions, edge cases (empty repo, detached HEAD, mid-merge state), and where mcadiff's chunk-based model forces a deliberate divergence. Do not start coding before this comes back — retrofitting semantics is how subtle git-incompatibilities happen.

2. **Survey the wiring points** (read, don't guess):
   - `src/McaDiff/Program.cs` — the subcommand switch and `TopUsage` help text
   - `src/McaDiff/Cli/RepoCommands.cs` — argument parsing + dispatch style for repo commands
   - `src/McaDiff/Repo/` — one file per feature (`Stash.cs`, `Rebase.cs`, `Bisect.cs` are good templates); note how they use `Repository`, `ObjectStore`, `Snapshotter`, and the reflog
   - Revision syntax lives in rev-parse handling — if the command takes refs, reuse it (`HEAD~n`, `HEAD@{n}`, abbreviated hashes), never re-parse

3. **Implement**: new `Repo/<Feature>.cs` + a `RepoCommands` entry + the `Program.cs` switch arm + help text. Match the surrounding style (expression-bodied members, doc comments explaining the git parallel). Worktree-mutating commands must honor the bound-worktree model and write reflog entries where git would.

4. **Tests**: add to the current `GitLikeTierNTests.cs` (or start the next tier file). Synthetic worlds via `TestAnvil` only — no fixtures. Cover the happy path plus every edge case from step 1's research. Follow `testanvil-test-author` conventions; delegate test writing to that agent if the surface is large.

5. **Docs**: README command list (the `mcadiff <cmd>` block in Version control), the Tests paragraph, and Limitations if the implementation is partial (e.g. "non-interactive only"). The README's git-likeness framing is per-tier — match the existing commit message style: `Tier N git-likeness: <features>`.

6. **Gate it**: run the `pre-pr` skill (tests + `nbt-diff-invariant-reviewer`/`world-roundtrip-gauntlet` as the diff dictates).
