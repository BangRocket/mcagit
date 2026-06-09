---
name: gauntlet-invocations
description: Exact working command invocations for all gauntlet checks (extract, apply, apply --reverse, diff, commit, checkout, gc, fsck, verify, status) against compare-worlds/ fixtures — Rust mcagit binary
metadata:
  type: project
---

# Gauntlet Invocations (Rust mcagit)

## Environment Setup

### macOS / zsh (heidornj machine, /Volumes/Storage/Code/minecraft/mcagit)
- Build: `~/.cargo/bin/cargo build --release` from repo root; binary at `target/release/mcagit`
- Agent bash cwd resets between calls → use absolute paths everywhere.
- Scratch: `SCRATCH=/tmp/mcagit-gauntlet-$(date +%Y%m%d-%H%M%S)`; write path to `/tmp/mcagit-gauntlet-scratch-path.txt` to re-read across bash calls.
- NEVER mutate compare-worlds/ originals — always `cp -R` both worlds into scratch.
- Clean up with `rm -rf $SCRATCH` and `rm -f /tmp/mcagit-gauntlet-scratch-path.txt`.

## Scratch Setup (never mutate compare-worlds/)
```sh
SCRATCH=/tmp/mcagit-gauntlet-$(date +%Y%m%d-%H%M%S)
echo "$SCRATCH" > /tmp/mcagit-gauntlet-scratch-path.txt
mkdir -p "$SCRATCH"
cp -R /Volumes/Storage/Code/minecraft/mcagit/compare-worlds/New_World_Older "$SCRATCH/New_World_Older"
cp -R /Volumes/Storage/Code/minecraft/mcagit/compare-worlds/New_World_Newer "$SCRATCH/New_World_Newer"
```

## CLI Variable
```sh
CLI=/Volumes/Storage/Code/minecraft/mcagit/target/release/mcagit
```

## Invariant 1: Extract → Apply → Diff (forward)
```sh
# Extract
"$CLI" extract "$SCRATCH/New_World_Older" "$SCRATCH/New_World_Newer" -o "$SCRATCH/forward.mcapatch"
# Apply
"$CLI" apply "$SCRATCH/forward.mcapatch" "$SCRATCH/New_World_Older" -o "$SCRATCH/Old_Updated"
# Diff (expect "No differences", exit 0)
"$CLI" diff "$SCRATCH/Old_Updated" "$SCRATCH/New_World_Newer"
```

## Invariant 2: Apply --reverse
```sh
"$CLI" apply --reverse "$SCRATCH/forward.mcapatch" "$SCRATCH/New_World_Newer" -o "$SCRATCH/Newer_Restored"
# Diff (expect "No differences", exit 0)
"$CLI" diff "$SCRATCH/Newer_Restored" "$SCRATCH/New_World_Older"
```

## Invariant 3: Commit → Checkout → Verify
```sh
# Copy Older as mutable worktree
cp -R "$SCRATCH/New_World_Older" "$SCRATCH/worktree"
"$CLI" init "$SCRATCH/test-repo" --worktree "$SCRATCH/worktree"
"$CLI" -C "$SCRATCH/test-repo" commit -m "older world"
# Second commit same world — exercises cache fast path, should say "nothing to commit"
"$CLI" -C "$SCRATCH/test-repo" commit -m "second commit same world"
# Replace worktree with Newer, commit (exercises partial cache hits)
rm -rf "$SCRATCH/worktree"
cp -R "$SCRATCH/New_World_Newer" "$SCRATCH/worktree"
"$CLI" -C "$SCRATCH/test-repo" commit -m "newer world"
# Checkout HEAD to fresh dir
"$CLI" -C "$SCRATCH/test-repo" checkout HEAD "$SCRATCH/checkout_newer"
# Verify (expect "OK — reproduces ...", exit 0)
"$CLI" -C "$SCRATCH/test-repo" verify HEAD "$SCRATCH/checkout_newer"
# Semantic diff (expect "No differences", exit 0)
"$CLI" diff "$SCRATCH/checkout_newer" "$SCRATCH/New_World_Newer"
```

## Invariant 4: GC → Checkout → Verify
```sh
"$CLI" -C "$SCRATCH/test-repo" gc
# Use HEAD~1 syntax (works when HEAD is attached to main, not detached)
"$CLI" -C "$SCRATCH/test-repo" checkout HEAD~1 "$SCRATCH/gc_checkout_older"
# Verify using HEAD~1 or direct hash
"$CLI" -C "$SCRATCH/test-repo" verify HEAD~1 "$SCRATCH/gc_checkout_older"
# Semantic diff (expect "No differences", exit 0)
"$CLI" diff "$SCRATCH/gc_checkout_older" "$SCRATCH/New_World_Older"
# Bonus: fsck
"$CLI" -C "$SCRATCH/test-repo" fsck
```

## Chunk Cache Integrity (Case 5)
```sh
# 5a: Delete cache, run status (should report correctly, not crash)
rm "$SCRATCH/test-repo/chunkcache.json"
"$CLI" -C "$SCRATCH/test-repo" status   # exits 0 if clean, 1 if modifications
# 5b: Corrupt cache, run status + commit (should not crash; corrupt cache silently discarded)
printf 'NOT_VALID_JSON{{{garbage bytes\x00\xff\xfe' > "$SCRATCH/test-repo/chunkcache.json"
"$CLI" -C "$SCRATCH/test-repo" status
"$CLI" -C "$SCRATCH/test-repo" commit -m "commit with corrupt cache"
# Verify cache was regenerated as valid JSON
python3 -c "import json; d=json.load(open('$SCRATCH/test-repo/chunkcache.json')); print('Valid JSON, entries:', len(d))"
```

## Key Behaviors / Gotchas
- `checkout HEAD~1` on a DETACHED HEAD that is the first commit fails with "no parent" — expected.
  Use `checkout HEAD~1` only when HEAD is attached to a branch. After Case 4 checkout, HEAD
  is detached at the Older commit. Reattach with: `printf "ref: refs/heads/main\n" > repo/HEAD`
- `status` exits 0 = clean, 1 = modifications, 2 = error. After `checkout HEAD~1`, HEAD
  is detached at Older commit; if worktree still has Newer content, status correctly shows diffs.
- `verify <hash> <world>` uses the full or prefix hash. `verify HEAD~1` works when HEAD is a branch ref.
- `gc` output: "gc: kept N objects, pruned 0" — exit 0.
- `fsck` output: "checked N objects — 0 corrupt, 0 missing, 0 unreachable" — exit 0.
- Second commit on unchanged world: "nothing to commit — world matches HEAD" — exit 0 (cache fast path).
- Corrupt/missing chunkcache.json: silently discarded, operations continue, cache regenerated on commit.

## Observed Timings (Rust, macOS, Release build, 2026-06-09)
- Build: ~15s (incremental from clean-ish state)
- Extract Older→Newer: ~instant (18 file entries, 4712 ops, exit 0)
- Apply forward: ~instant (4712 ops, exit 0)
- Diff applied_forward vs Newer: ~instant (No differences, exit 0)
- Apply --reverse: ~instant (4712 ops, exit 0)
- Diff applied_reverse vs Older: ~instant (No differences, exit 0)
- Commit Older (cold): ~instant (32 files, 2601 chunks, exit 0)
- Commit same world (warm cache): ~instant ("nothing to commit", exit 0)
- Commit Newer (warm cache, partial hits): ~instant (36 files, 2652 chunks, exit 0)
- Checkout HEAD: ~instant
- Verify: ~instant
- GC: ~instant (kept 4389 objects, pruned 0)
- Fsck: ~instant (0 corrupt, 0 missing, 0 unreachable)

## Fixture Sizes
- New_World_Older: 32 files, 2601 chunks (4 region/.mca, 4 entities/.mca, various .dat, blobs)
- New_World_Newer: 36 files, 2652 chunks (same + 4 poi/ region files)
- Forward patch: 18 file entries, 4712 ops
- Post-GC object count: 4389 objects
