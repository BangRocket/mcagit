# 0003 — Per-NBT-node three-way merge

## Status

Accepted.

## Context

Two people editing copies of the same world is a real workflow (two builders, a creative server). A
line-based merge is meaningless on binary region files, and a whole-chunk "take ours or theirs" merge
throws away half the work whenever both sides touched the same chunk — which they almost always do
(ticking `InhabitedTime`, lighting, nearby edits).

## Decision

`Repo/Merger` merges **per NBT node**, three-way, against the common ancestor found by
`Repo/MergeBase` (recursive, criss-cross-safe — folds multiple bases into a virtual base):

- A node changed on only one side takes that side's value.
- A node changed on both sides to the *same* value is not a conflict.
- A node changed on both sides to *different* values is a genuine conflict — kept *ours* by default
  (*theirs* with `--theirs`) and reported.

Conflicts surface through the same workflow as git: `MERGE_HEAD` + a conflict list, `merge --continue`
/ `--abort`. Resolution is by re-snapshotting the worktree (the files are binary — there are no in-file
conflict markers).

## Consequences

- Two builders' edits to *different* parts of the same chunk both land — far finer than git's
  line-based merge or a whole-blob backup tool.
- Only a genuine same-node clash is a conflict, so merges rarely conflict in practice.
- The merge engine is read-mostly and reusable — the GUI (#29) and collaboration workflow (#31) drive
  the same `Merger`/`MergeBase` with a friendlier presentation, not a second engine.
