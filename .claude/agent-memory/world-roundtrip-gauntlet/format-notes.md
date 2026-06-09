---
name: format-notes
description: On-disk repo format details (Rust mcagit), backwards-compatibility notes, and version support line
metadata:
  type: project
---

# On-Disk Repo Format Notes (Rust mcagit)

## Layout
```
<repo>/
  HEAD                     # "ref: refs/heads/<branch>\n" or "<hash>\n" (detached)
  config                   # JSON: {worktree, remotes, settings}
  chunkcache.json          # JSON: {cacheKey → objectHash} (opaque perf cache, resilient to corruption/deletion)
  objects/
    <aa>/<rest>            # zstd-compressed content, blake3-named
    pack/
      pack-<id>.pack       # MCAP v1 packfile
      pack-<id>.idx        # MCAI v1 index
  refs/
    heads/<branch>         # plain hex hash + \n
    tags/<name>            # plain hex hash or annotated-tag-object hash + \n
    remotes/<remote>/<br>  # plain hex hash + \n
  logs/HEAD                # reflog: "<from> <to> <msg>\n" lines
  MERGE_HEAD               # transient: theirs hash during conflicted merge
  MERGE_MSG                # transient: merge commit message
  ORIG_HEAD                # transient: pre-merge HEAD (kept after clear)
  MERGE_CONFLICTS          # transient: JSON list of MergeConflict
  BISECT_{START,BAD,GOOD,SKIP,LOG}  # transient bisect state
```

## Key differences from .NET format
- Object IDs: blake3 (not SHA-256)
- Loose object compression: zstd (not zlib)
- Loose object path: `objects/<aa>/<rest>` (same layout pattern)
- Pack format: MCAP/MCAI v1 (same format name, clean-slate encoding)
- chunkcache.json: content-keyed `{compressionType:blake3(payload) -> objectHash}`

## Packfile Format (MCAP v1)
- Header: "MCAP" (4 bytes) + version (1 byte = 1)
- Entries: type byte (0=whole, 1=delta) + varints + zlib-compressed content
- Index: "MCAI" (4 bytes) + version (1 byte = 1) + count (int32 BE) + entries
- Each index entry: 32-byte hash + 8-byte offset (int64 BE)
- mmap'd for reads; ArcSwap for lock-free concurrent pack list updates

## Chunk Cache (chunkcache.json)
- Key: `"<compressionType>:<hex(blake3(compressed_payload))>"`
- Value: object hash (blake3 hex)
- Written on commit, used to skip decode/canonicalize for unchanged compressed payloads
- Missing file: treated as empty cache (cold start)
- Corrupt file: silently discarded, treated as empty cache
- Not written atomically — concurrent commits could corrupt; safe to ignore

## Annotated Tags
- Stored as tag objects in `objects/`; refs/tags/<name> points to tag object hash
- Tag objects reachable from gc/fsck/transfer (as of 85e7ea6)

## Backwards Compatibility
- No prior Rust repo format deployed. .NET repos are incompatible (different hash algo + encodings).
- CommitObject JSON fields: nullable for optional fields (CommitTime, Signature, etc.)
- Packfile v1: no migration path needed (no prior deployed versions).
