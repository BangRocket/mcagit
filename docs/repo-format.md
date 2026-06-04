# Repository on-disk format

An mcadiff repository is a **bare, content-addressed object store** that lives outside the world it tracks (a bound worktree is recorded in `config`). The model is git's, with one difference that matters: the unit of dedup is the **chunk**, hashed by its decoded NBT, so an unchanged chunk is stored once no matter how many snapshots reference it. This document describes the bytes so a backup can be trusted and, if needed, read without mcadiff.

## Directory layout

```text
<repo>/
  HEAD                  ref: refs/heads/<branch>   (or a 64-hex commit when detached)
  config                JSON: bound worktree, remotes, dotted config keys
  objects/
    aa/rest…            loose objects: zlib, named by SHA-256 (first 2 hex = dir)
    pack/
      pack-<id>.pack    packfile (see below)
      pack-<id>.idx     its index
  refs/
    heads/<branch>      a commit hash
    tags/<tag>          a tag (annotated → a tag object; lightweight → a commit)
    remotes/<r>/<b>     remote-tracking refs
  logs/HEAD             reflog of HEAD movements
  shallow               newline list of shallow-boundary commits (depth-limited clone)
  chunkcache.json       payload-hash → object-hash decode cache (an accelerator; safe to delete)
  mcadiff.lock          advisory lock held during commit / push
  MERGE_HEAD MERGE_MSG ORIG_HEAD MERGE_CONFLICTS   in-progress merge
  CHERRY_PICK_HEAD REVERT_HEAD SEQ_MSG             in-progress cherry-pick / revert
  REBASE_STATE                                     resumable rebase state (JSON)
  BISECT_START BISECT_BAD BISECT_GOOD BISECT_SKIP BISECT_LOG   in-progress bisect
```

## Objects

Every object is content-addressed: its name is the lowercase-hex SHA-256 of its **uncompressed content**, and it is stored zlib-compressed at `objects/<first two hex>/<remaining 62>`. An object id must be 64 lowercase hex characters; ids are validated before they become a path. On import from a remote the content is re-hashed, so a mismatched object cannot enter the store.

There are four object kinds, classified by content shape (git's model):

- **blob** — a canonical NBT chunk, a canonical loose-NBT file, or a raw file (datapacks, stats, an undecodable type-127 region). The chunk blob is the **canonical** NBT encoding (`NbtCanonical`): deterministic bytes independent of the on-disk compression, which is what makes an unchanged chunk hash identically across snapshots.
- **tree** — a `Manifest` (see below).
- **commit** — a `CommitObject`.
- **tag** — a `TagObject`.

Commit, tree, and tag are camelCase JSON with disjoint key sets; anything else is a blob.

## Manifest (the "tree")

A manifest maps a world snapshot to object hashes.

```jsonc
{
  "regions": {
    "region/r.0.0.mca": { "0,0": "<chunkHash>", "1,0": "<chunkHash>" }
  },
  "nbt":   { "level.dat": "<objectHash>" },
  "blobs": { "datapacks/x/pack.mcmeta": "<objectHash>" },
  "emptyDirs": [ "playerdata" ]
}
```

- `regions` — region file path → (`"x,z"` chunk position → chunk-object hash). Covers `region/`, `entities/`, `poi/`, and custom dimensions.
- `nbt` — loose NBT file path → canonical-NBT object hash.
- `blobs` — any other file path → raw-blob object hash.
- `emptyDirs` — directories preserved with no content.

## Commit and tag

```jsonc
// CommitObject
{
  "tree": "<manifestHash>",
  "parents": ["<commitHash>", "…"],   // 0 for a root, 1 normally, 2+ for a merge
  "message": "…",
  "author": "Name <email>",
  "time": "2026-06-04T12:00:00.000+00:00",   // ISO-8601 author date
  "committer": "Name <email>",
  "commitTime": "2026-06-04T12:00:00.000+00:00",
  "signature": "…"                            // optional, SSH signature format
}
```

```jsonc
// TagObject (annotated tags only; lightweight tags are a plain ref to a commit)
{
  "object": "<commitHash>",
  "type": "commit",
  "tag": "v1",
  "tagger": "Name <email>",
  "time": "2026-06-04T12:00:00.000+00:00",
  "message": "…",
  "signature": "…"                            // optional, SSH
}
```

A signature covers the object's payload with the signature field cleared, so a signed object is its own content-addressed object (git's model).

## Packfiles

`gc` repacks reachable loose objects into a single packfile with delta compression between similar chunks (a chunk whose only change is a ticking `InhabitedTime` packs to a few bytes), then prunes the loose originals.

- **`pack-<id>.pack`** — header `MCAP` + version byte (`1`), then one record per object: a type byte (`0` whole, `1` delta), varint-encoded lengths (and, for a delta, the base reference), then the zlib-compressed payload. Deltas use git-style copy / insert opcodes (`Delta.cs`): a command byte with the high bit set copies a run from the base; a command byte below `0x80` inserts that many literal bytes.
- **`pack-<id>.idx`** — header `MCAI` + version byte, a big-endian object count, then entries sorted by hash, each 32-byte SHA-256 + 8-byte big-endian offset into the pack.
- **`<id>`** is the first 40 hex of SHA-256 over the concatenated **sorted** object hashes, so a pack of the same object set is identical regardless of insertion order — `gc` is idempotent.

## Remotes

Network transfer is content-addressed: only objects the other side lacks are copied. Cloud buckets additionally bundle missing objects into one pack per push — see [`cloud-backend.md`](cloud-backend.md). Refs advance with a fast-forward check (compare-and-swap on a bucket).
