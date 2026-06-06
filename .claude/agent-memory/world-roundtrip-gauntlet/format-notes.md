---
name: format-notes
description: On-disk repo format details, backwards-compatibility notes, and version support line
metadata:
  type: project
---

# On-Disk Repo Format Notes

## Layout
```
<repo>/
  HEAD                     # "ref: refs/heads/<branch>\n" or "<hash>\n"
  config                   # JSON: {worktree, remotes, settings}
  chunkcache.json          # JSON: {cacheKey → objectHash} (opaque perf cache)
  index                    # JSON Manifest (optional, staging index)
  objects/
    <aa>/<rest>            # zlib-compressed content, SHA-256 named
    pack/
      pack-<id>.pack       # MCAP v1 packfile
      pack-<id>.idx        # MCAI v1 index
  refs/
    heads/<branch>         # plain hex hash + \n
    tags/<name>            # plain hex hash or tag-object hash + \n
    remotes/<remote>/<br>  # plain hex hash + \n
  logs/HEAD                # reflog: "<from> <to> <msg>\n" lines
  MERGE_HEAD               # transient: theirs hash during conflicted merge
  MERGE_MSG                # transient: merge commit message
  ORIG_HEAD                # transient: pre-merge HEAD (kept after clear)
  MERGE_CONFLICTS          # transient: JSON list of MergeConflict
  BISECT_{START,BAD,GOOD,SKIP,LOG}  # transient bisect state
```

## Packfile Format (MCAP v1)
- Header: "MCAP" (4 bytes) + version (1 byte = 1)
- Entries: type byte (0=whole, 1=delta) + varints + zlib-compressed content
- Index: "MCAI" (4 bytes) + version (1 byte = 1) + count (int32 BE) + entries
- Each index entry: 32-byte SHA-256 + 8-byte offset (int64 BE)
- Delta: relative offset from current entry back to base (always forward-in-file base)

## Backwards Compatibility
- CommitObject: Committer/CommitTime added later → nullable, old commits without them deserialize correctly
- Manifest.EmptyDirs: added later → missing from old JSON → deserialized as []
- TagObject.Signature: nullable → old unsigned tags work
- Packfile v1: no migration. No prior versions deployed.

## Support Line Recommendation
Keep current format as v1 baseline. For any breaking changes:
- Bump packfile version byte
- Add `mcagit migrate` command for repos
- Keep loose object format (SHA-256 + zlib) unchanged (too fundamental)
