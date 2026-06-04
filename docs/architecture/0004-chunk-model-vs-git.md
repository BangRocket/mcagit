# 0004 — The chunk model vs git

## Status

Accepted.

## Context

mcadiff is deliberately git-shaped (commits, branches, tags, merge, remotes, packfiles). But a world is
not a tree of text files, and copying git's model literally would either lose semantics or be wildly
inefficient. The decision is *where* mcadiff matches git and where it diverges.

## Decision

The **unit of dedup and diff is the chunk**, hashed by its decoded NBT (ADR 0002), not the file. A
`Manifest` (git's "tree") maps region path → (chunk position → chunk-object hash), plus loose-NBT and
raw-blob entries. Everything above the chunk — commits, refs, reflog, merge, packfiles with delta
chains, remotes — mirrors git.

Deliberate divergences from git:

- **`commit` exits 0 on "nothing to commit"** (git exits 1) — a scheduled backup of an unchanged world
  is not an error; the `--json` `committed` flag is the signal.
- **No byte-identical restore** — `checkout` re-encodes chunks canonically (ADR 0002); reproduction is
  semantic, not bit-for-bit. Region chunk timestamps are reset.
- **Diff is semantic** — region/chunk and coordinate-level block changes, not "binary files differ".
- **Merge is per-NBT-node** (ADR 0003), not line-based.

## Consequences

- `git log` / `git fsck` cannot operate on an mcadiff repo — the object model (manifests-as-trees,
  canonical-NBT blobs, custom packfile format) is its own; this is intentional and there is no git
  interop goal.
- A change that touches diff/patch/repo semantics must go through the shared comparison walk and
  canonical encoding so the three layers can't drift (this is why there is one `NbtComparer`/`IDiffSink`
  and one `NbtCanonical`).
- New "git command X" work consults real-git semantics first, then maps onto the chunk model — keeping
  the surface familiar without pretending a world is a source tree.
