# mcagit (Rust)

A clean-slate Rust rewrite of `mcagit` — the semantic, git-style diff/patch/version-control
tool for Anvil-format Minecraft worlds — focused on **speed** (parallel, streaming, native).
Behavioral parity with the .NET original; its own (faster) on-disk formats.

See the design + plans under `../docs/superpowers/`.

## Workspace

```
crates/
  nbt/     mca-nbt    NBT model, big-endian read/write (modified-UTF8), canonical bytes,
                      list-element identity, the path language, lossless type-tagged JSON
  anvil/   mca-anvil  region (r.X.Z.mca) read/write, chunk codecs (zlib/gzip/none/lz4),
                      external .mcc, standalone .dat load/save
  diff/    mca-diff   one comparer -> DiffSink trait; ChangeSink (display); WorldDiffer
                      (parallel, byte-identical fast paths); text format
  patch/   mca-patch  .mcapatch model; extract; apply (3-way guarded, reversible)
  repo/    mca-repo   content-addressed store (blake3 + zstd), packfiles + pack-at-commit,
                      parallel commit, parallel checkout, status, fsck, gc, merge-base +
                      3-way merge, cherry-pick/revert/rebase, stash, path-transport clone/push/pull
  cli/     mcagit     the binary (27 subcommands)
```

## Build & test

```sh
cd rust
cargo test --all
cargo fmt --all -- --check
cargo clippy --all-targets -- -D warnings
cargo build --release      # binary: target/release/mcagit
```

## Why Rust: the benchmark

On the full `dobbscraft` world (2.4 GB, 312,717 chunks, 8 cores), vs the .NET original:

| operation | .NET | Rust |
|---|---|---|
| commit (cold) | 43 s | 58 s |
| commit (incremental) | ~11 s | 13 s |
| **checkout** | **268 s (serial)** | **100 s (parallel)** |

The Rust checkout is **~2.7× faster**, and the .NET tool's semantic diff reports **"No
differences"** on the Rust checkout output — clean-slate, but byte-faithful to Minecraft.

## Commands

`init · commit · checkout · status · log · diff [--json] · extract · apply [--reverse] ·
branch · merge · fsck · gc · revert · cherry-pick · rebase · stash · rev-parse · cat-file ·
ls-tree · tag · reset [--hard] · restore · clean · clone · push · pull`

```sh
mcagit init repo.mcagit --worktree ./World
mcagit -C repo.mcagit commit -m "before raid"
mcagit -C repo.mcagit checkout HEAD~1 ./restored
mcagit ./WorldA ./WorldB                 # (use: mcagit diff A B)
mcagit -C repo.mcagit gc
```

## Clean-slate format notes

- Object id = `blake3(uncompressed canonical content)`; loose objects are `zstd`-compressed
  at `objects/aa/rest`; packs are `objects/pack/pack-<id>.{pack,idx}` (mmap'd).
- Region files written on checkout are valid Anvil (zlib chunks) but not byte-identical to
  Minecraft's output — semantic diff is the equivalence check, as in the .NET tool.

## Not yet ported (vs .NET)

- **Network/cloud remotes**: http/ssh/stdio transports, `serve`, S3/Azure. The object-copy
  core is done + tested via **path transport** (local clone/push/pull); networked transports
  layer the byte-moving on top.
- reflog (`HEAD@{n}`), `bisect`, annotated-tag/commit **SSH signing**.
- The bare `mcagit A B` diff fallthrough (use `mcagit diff A B`).
- Perf knobs still on the table: `flate2` zlib-ng backend and a faster checkout compression
  level (checkout currently uses default zlib; the 2.7× above is the conservative number).
