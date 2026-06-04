---
name: divergences-audit-2026
description: Full inventory of undocumented git divergences found in the 2026-06-03 adversarial review (updated with confirmed git semantics from official docs)
metadata:
  type: project
---

Findings from the 2026-06-03 adversarial fidelity review (filed as GitHub issue).
Research method: read all Repo/*.cs and Cli/RepoCommands.cs, then verified git semantics
against official git-scm.com documentation for each command.

## Confirmed BLOCKER divergences

1. **cherry-pick commits on conflict** — git stops, writes CHERRY_PICK_HEAD, leaves markers in worktree, does NOT commit. mcadiff always commits (even with conflicts) and exits 1. Callers checking exit code 0 will think the commit is clean; it is not.

2. **revert commits on conflict** — same pattern. git stops the sequencer, leaves markers, does NOT commit. mcadiff creates the conflict commit and exits 1.

3. **rebase is all-or-nothing, not stop-on-first-conflict** — git rebase stops at the first conflicting commit, writes REBASE_HEAD, requires --continue/--abort/--skip. mcadiff's rebase plows through all commits (accumulating conflicts) and always completes. No REBASE_HEAD, no --continue/--abort.

## Confirmed HIGH divergences

4. **reset requires being on a branch** — git's reset works on detached HEAD (moves HEAD directly). mcadiff errors if CurrentBranch() is null.

5. **reset has no --mixed mode** — git's default is --mixed (move HEAD, reset index, leave worktree). mcadiff only has --soft (move pointer) and --hard (move + materialize). "Neither" = --soft. Not documented.

6. **reset with no target ref** — git defaults target to HEAD. mcadiff requires a positional argument; no-arg invocation returns usage error.

7. **checkout has no dirty-worktree safety check** — git refuses to switch branches if local changes would be overwritten. mcadiff skips this check; only has --force for non-empty output directory.

8. **checkout doesn't update reflog** — SetHeadToBranch/SetHeadDetached called but RecordHead not called. checkout/switch is one of git's major reflog-triggering operations.

9. **merge fast-forward doesn't update reflog** — Merger.Merge calls WriteBranch for FF but not RecordHead.

10. **init refuses re-init** — git re-initializes safely and prints "Reinitialized". mcadiff errors: "already a repository". Scripts that call init idempotently break.

11. **config --global requires being in a repo** — git config --global works anywhere. mcadiff calls Open(dashC) first.

12. **branch cannot create branch at a specific commit** — no start-point argument; always creates at HEAD.

13. **branch has no -d/-D delete** — unimplemented.

14. **branch creates silently duplicate** — no "already exists" check; silently overwrites the branch ref.

## Confirmed MED divergences

15. **reflog output format wrong** — git: `<abbrev-hash> HEAD@{n} action: message`. mcadiff: `<to-hash-10> <message>` with no HEAD@{n} index notation.

16. **reflog only tracks HEAD** — git has per-branch reflogs (logs/refs/heads/<branch>). mcadiff only has logs/HEAD.

17. **rebase writes only one reflog entry** — git writes one per replayed commit. mcadiff writes one for the whole rebase.

18. **A...B symmetric diff includes endpoint commits** — RangeCommits does full ancestor-set symmetric difference. git's `log A...B` excludes common ancestors but shows commits reachable from A or B that the other doesn't have (still correct intent). However the code includes A and B themselves unconditionally if they are not mutual ancestors, which differs from git when A or B is the merge base of the other.

19. **IgnoreRules: glob only matches filename, not full path** — `*.mca` works; `region/*.mca` does not, because the glob regex is matched only against `segs[^1]` (the filename).

20. **clean doesn't remove untracked directories** — no -d flag; EnumerateFiles only. git clean requires -d to remove directories.

21. **clean exit code always 0** — git clean exits non-zero on error. mcadiff always returns 0.

22. **remote: no remove/rm/rename/get-url/set-url** — only `add` and bare listing.

23. **ls-remote HEAD output format** — mcadiff prints `<hash>    HEAD -> <branch>`. git prints `<hash>    HEAD` on one line (and with --symref adds a symref= header line). The arrow notation is not standard.

24. **rev-parse --abbrev-ref on detached HEAD** — git prints "HEAD". mcadiff's code for --abbrev-ref falls through to full hash resolution when spec is not HEAD and branch lookup fails, so detached rev-parse --abbrev-ref <hash> would print the full hash not "HEAD".

25. **rev-parse --short always produces 10 chars** — git's --short produces the minimum unambiguous abbreviation (min 4 chars, default 7 or core.abbrev). mcadiff hard-codes h[..10].

26. **tag -f (force overwrite) not supported** — always writes, or always fails if name exists? Actually WriteTag just overwrites silently. git refuses without -f. So mcadiff silently allows re-tagging without -f. Inverse of git's behavior.

27. **hash-object without -w requires no repo** — mcadiff's hash-object calls Open() only when -w is set (correct). But it uses SHA-256 unconditionally while git uses SHA-1 by default. Documented divergence.

## Confirmed LOW / NOTE

28. **commit: "nothing to commit" exits 0** — git exits 1 when nothing to commit (per docs). mcadiff exits 0. Scripts checking `git commit && ...` chains break.

29. **commit output goes to stderr** — git's "[branch abc1234] message" line goes to stderr. mcadiff matches this. GOOD.

30. **stash pop on conflict retains stash** — matches git. GOOD.

31. **No -- separator support** — ambiguity between path-like branch names silently unhandled.

32. **config --list source** — git shows which file a key came from (with --show-origin). mcadiff omits source. Minor.

33. **No ORIG_HEAD after merge commit** — mcadiff records ORIG_HEAD in BeginMergeState (for abort) but clears it via ClearMergeState after the merge completes. git keeps ORIG_HEAD after a completed merge for recovery.

34. **Virtual merge base author is "mcadiff" with epoch time** — functional but creates non-determinism if the repo is ever inspected. Minor; well-bounded.

35. **gc doesn't honor gc.pruneExpire grace period** — prunes all unreachable objects immediately. git's default prunes objects older than 2 weeks to avoid concurrent-writer corruption.

## Deliberately correct divergences (chunk/marker-free model)

- No in-file conflict markers (<<<<<<</=======/>>>>>>>) — chunk/NBT-node granularity instead
- Rebase is non-interactive (no pick/squash/fixup)
- cherry-pick/revert commit on conflict is WRONG for git parity but the correct model if mcadiff ever supports continuation (sequencer missing)
- SSH signing instead of GPG
- SHA-256 object hashing instead of SHA-1
- No pack-on-the-wire protocol (objects transferred individually)
- hash-object uses SHA-256
