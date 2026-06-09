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
                      external .mcc, standalone .dat, paletted block_states/biomes decode
  diff/    mca-diff   one comparer -> DiffSink trait; ChangeSink (display); WorldDiffer
                      (parallel, byte-identical fast paths); coordinate-level block edits;
                      text + json formats
  patch/   mca-patch  .mcapatch model; extract; apply (3-way guarded, reversible)
  query/   mca-query  read-only world inspection: players, find entities/block-entities/
                      signs, inspect a coord, where-changed (grief), region/poi, PNG map render
  repo/    mca-repo   content-addressed store (blake3 + zstd), packfiles + pack-at-commit,
                      parallel commit/checkout, verify, status, fsck, gc, merge-base +
                      3-way merge, cherry-pick/revert/rebase, stash; transports: local path,
                      http (serve), ssh (serve-stdio)
  cli/     mcagit     the binary
```

## Commands

**Version control** — `init · commit [-S] · checkout · status · log [--author/--grep/--since] ·
show · diff [--json] · extract · apply [--reverse] · verify · branch · merge · revert ·
cherry-pick · rebase · stash [push|pop|list|drop] · reset [--soft/--mixed/--hard] · restore ·
clean · tag [-a/-s/-m/-v/-f/-n] · verify-commit · reflog · bisect (start|bad|good|skip|reset|log) ·
config · rev-parse · cat-file · ls-tree · fsck · gc`

`reflog` shows every HEAD movement and powers `HEAD@{n}` revisions; `bisect` binary-searches
history for the first bad commit, checking each suspect out into the worktree.

**Hooks & signing** — `<repo>/hooks/pre-commit` (non-zero aborts the commit) and
`post-commit` run with `MCAGIT_DIR`/`MCAGIT_WORKTREE` set. Commits (`commit -S` or
`config commit.gpgsign true`) and tags (`tag -s`) are SSH-signed via `ssh-keygen -Y`
using `user.signingkey`; `verify-commit` / `tag -v` exit 0 only when the signer matches
`gpg.ssh.allowedSignersFile`.

**Remotes** — `clone [--depth N] · push · pull · fetch · ls-remote · remote ·
verify-remote [--deep]` over a **local path**, **`http(s)://`** (served by
`mcagit serve <dir>`), or **`ssh://`** (served by `mcagit serve-stdio <dir>`, spawned over
`ssh`). Pushes travel as **wire packs** — the missing objects batched (zstd-per-object)
into one request per ~128 MiB instead of a round-trip per object; the server hash-verifies
every object and bounds every inflate before storing. `clone --depth N` makes a shallow
clone (history grafted at a recorded boundary; tags skipped). `verify-remote` walks the
remote's history confirming every commit/tree hashes correctly and every leaf is present
(`--deep` re-downloads and hash-checks the leaves too).

**World inspection & render** — `players · find <entity|block-entity|sign> [id] ·
inspect <x y z> · where-changed <old> <new>` (the grief detector) `· region <file.mca> ·
poi · render <world> -o map.png`

```sh
mcagit init repo.mcagit --worktree ./World
mcagit -C repo.mcagit commit -m "before raid"
mcagit -C repo.mcagit checkout HEAD~1 ./restored
mcagit -C repo.mcagit verify HEAD ./restored        # fast tree-hash accuracy check
mcagit diff ./WorldA ./WorldB                        # shows @x,y,z block changes per chunk
mcagit -C repo.mcagit gc --threads 8

# inspect + grief-detect
mcagit inspect 2 -48 15 --world ./World              # block + properties + biome + block-entity
mcagit where-changed ./WorldBefore ./WorldAfter      # air -> snow_block, … per coordinate
mcagit render ./World -o map.png                     # top-down surface map

# self-hosted remotes
mcagit serve /srv/worlds --addr 0.0.0.0:5080         # hub: clone/push http://host/r/<name>
mcagit push http://host:5080/r/myworld main
mcagit clone ssh://user@host/srv/worlds/myworld ./myworld
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

Incremental commits keep a persistent chunk cache (`<repo>/chunkcache.json`, compressed
payload hash → object id), so re-commits of a mostly-unchanged world skip decoding and
canonicalizing unchanged chunks entirely; a hit is only trusted if the object exists.

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

- **Cloud remotes (S3/Azure)** — local, `http(s)://`, and `ssh://` transports are done
  (with batched wire-pack pushes); serverless cloud object stores are open.
- A staging index (mcagit is deliberately index-free, like fossil).
- The bare `mcagit A B` diff fallthrough (use `mcagit diff A B`).
