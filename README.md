# mcagit

A semantic, git-style **diff / patch / version-control** tool for Anvil-format Minecraft
(Java Edition) worlds — built in Rust for **speed** (parallel, streaming, native).

`mcagit` understands worlds at the NBT level: it matches chunks and list elements by
*identity* (block coords, entity UUID, inventory slot, `id`), so a reorder isn't a rewrite,
and its unit of storage dedup is the **chunk**, hashed by decoded content. The result is a
git work-alike — commit, branch, merge, rebase, stash, gc — that stores Minecraft worlds
compactly and reproduces them byte-faithfully (playable in Minecraft).

> mcagit began as a .NET proof-of-concept; this Rust implementation (validated against that
> original during the port) is now the sole, primary version.

## Build & test

Requires a stable Rust toolchain (see `rust-toolchain.toml`).

```sh
cargo build --release        # binary: target/release/mcagit
cargo test --all
cargo fmt --all -- --check
cargo clippy --all-targets -- -D warnings
```

## Workspace

```text
crates/
  nbt/     mca-nbt    NBT model, big-endian read/write (modified-UTF8), canonical bytes,
                      list-element identity, the path language, lossless type-tagged JSON
  anvil/   mca-anvil  region (r.X.Z.mca) read/write, chunk codecs (zlib/gzip/none/lz4),
                      external .mcc, standalone .dat load/save
  diff/    mca-diff   one comparer -> DiffSink trait; ChangeSink (display); WorldDiffer
                      (parallel, byte-identical fast paths); text + json formats
  patch/   mca-patch  .mcapatch model; extract; apply (3-way guarded, reversible)
  repo/    mca-repo   content-addressed store (blake3 + zstd), packfiles + pack-at-commit,
                      parallel commit, parallel checkout, verify, status, fsck, gc,
                      merge-base + 3-way merge, cherry-pick/revert/rebase, stash,
                      path-transport clone/push/pull
  cli/     mcagit     the binary (28 subcommands)
```

## Commands

`init · commit · checkout · status · log · diff [--json] · extract · apply [--reverse] ·
verify · branch · merge · fsck · gc · revert · cherry-pick · rebase · stash · rev-parse ·
cat-file · ls-tree · tag · reset [--hard] · restore · clean · clone · push · pull`

```sh
mcagit init repo.mcagit --worktree ./World
mcagit -C repo.mcagit commit -m "before raid"
mcagit -C repo.mcagit checkout HEAD~1 ./restored
mcagit -C repo.mcagit verify HEAD ./restored      # fast tree-hash accuracy check
mcagit diff ./WorldA ./WorldB
mcagit -C repo.mcagit gc --threads 8
```

## Performance

On the full `dobbscraft` world (2.4 GB, 312,717 chunks, 8 cores):

| operation | time |
|---|---|
| commit (cold) | ~44 s |
| commit (incremental) | ~12 s |
| **checkout (restore)** | **~57 s** (parallel) |
| `verify` (accuracy gate) | 10–12 s |
| semantic diff | 23–26 s |
| storage: 8 snapshots (~19 GB raw) | **2.2 GB** deduped |

Checkout is parallel across the whole pipeline (rayon over a flat per-chunk job list,
lock-free pack reads); against the original serial .NET checkout (268 s) this is **~4.7×**
faster, and every restored world reproduces byte-faithfully (confirmed by `verify` and
semantic diff).

## Clean-slate format notes

- Object id = `blake3(uncompressed canonical NBT)`; loose objects are `zstd`-compressed at
  `objects/aa/rest`; packs are `objects/pack/pack-<id>.{pack,idx}` (mmap'd).
- The repo is **bare and external** to the world; the bound worktree is recorded in the repo
  `config`. `Repository::discover` walks up from cwd, git-style.
- Region files written on checkout are valid Anvil (zlib chunks) but not byte-identical to
  Minecraft's own output — **semantic diff / `verify` is the equivalence check** (an unchanged
  chunk hashes identically regardless of on-disk compression).

Design rationale (NBT identity, canonical encoding, 3-way merge, chunk-vs-git model) lives in
`docs/architecture/`. The sample worlds in `compare-worlds/` (real Anvil data) are used for
end-to-end round-trip checks.

## Not yet implemented

- **Network/cloud remotes**: http/ssh/stdio transports, `serve`, S3/Azure. The object-copy
  core is done + tested via **path transport** (local clone/push/pull); networked transports
  layer byte-moving on top.
- reflog (`HEAD@{n}`), `bisect`, annotated-tag/commit **SSH signing**.
- The bare `mcagit A B` diff fallthrough (use `mcagit diff A B`).
