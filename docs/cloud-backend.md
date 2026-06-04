# Serverless cloud backend — design notes

mcadiff can push/clone a repository to a dumb object-storage bucket (Azure Blob, or any
S3-compatible store) with **no server-side process**. The whole sync protocol runs
client-side over a minimal `IBucket` interface (get / put / put-if-match / list / delete).
This note records the decisions behind that, so they don't get re-litigated.

## Why a pack-based protocol (not one blob per object)

The naive mapping — one bucket blob per content-addressed object — is correct but
*stupid expensive*: object stores bill per request, and a single world snapshot is
thousands of per-chunk objects. A push that wrote one PUT per new object would cost
thousands of writes and a clone thousands of GETs.

Instead a push bundles **only the objects the bucket is missing** into a single
content-addressed **pack**, and uploads:

```
<prefix>/packs/<id>          the pack
<prefix>/packs/<id>.idx      its index (hash → offset)
<prefix>/packs/manifest      newline list of pack ids   (CAS-guarded)
<prefix>/refs/heads/<branch> the branch tip             (CAS-guarded)
<prefix>/HEAD                default branch for clone
```

So a push is **≈3 writes regardless of how many chunks changed** (pack + idx + manifest),
plus the tiny ref blob. A clone downloads the manifest, the per-pack `.idx` files to build
a hash→pack map, and then the packs. Object reads during a fetch are served from packs
downloaded on demand.

### Decision: reuse the existing custom `Packfile` format

We reuse mcadiff's own `Packfile` (the same format `gc` already writes — git-style opcode
delta chains between similar chunks) rather than adopting git's on-disk pack format.

- **Pro:** zero new format. The reader/writer, delta encoder, and index already exist and
  are tested; the bucket transport is *just transport*. Delta compression between similar
  chunks (e.g. a chunk whose only change is `InhabitedTime`) carries over for free.
- **Pro:** the pack is content-addressed by its own id, so uploads are idempotent and a
  half-finished push leaves no dangling ref (the ref is the last, CAS-guarded write).
- **Con:** not interoperable with git tooling — but nothing else speaks mcadiff's object
  model anyway (manifests-as-trees, canonical NBT blobs), so git-pack compatibility would
  buy nothing.

### Concurrency: compare-and-swap, fast-forward client-side

`packs/manifest` and each `refs/heads/<branch>` are updated with an **ETag
compare-and-swap** (`PutIfMatch`). Two clients pushing at once can't lose a pack
(manifest append retries on CAS failure) or clobber a ref (a stale ref update is
rejected — fetch + retry). The fast-forward check itself stays client-side in
`RemoteOps.PushTo`; the bucket only enforces "the ref is still what you read."

This needs the provider to honor conditional writes: Azure Blob always does; AWS S3
(If-Match, GA 2024) and Cloudflare R2 do. (A `single-flight` push per branch is still the
recommended operational posture — see issue #2.)

## Remote integrity check (`verify-remote`)

`verify-remote` walks every branch on the remote — commit → tree → leaf objects — over the
transport, confirming each commit/tree decodes back to its own hash and every referenced
leaf object is present. `--deep` additionally downloads and re-hashes every leaf (a full
bit-rot scan; downloads everything). It works over *any* transport, not just buckets, so
it doubles as an offsite "is my backup intact?" check. Cheap mode uses one batched
presence query for the leaves; the commit/tree spine is always hash-verified.

## Shallow clone (`clone --depth N`)

`--depth N` fetches only the last N commits (BFS, so each commit is fetched at its minimum
depth), with their worlds fully intact, and records the boundary commits in a `shallow`
file. Every history/reachability walk goes through one chokepoint, `Repository.ParentsOf`,
which **grafts boundary commits to having no parents** — so `log`, `checkout`, `gc`, and
`fsck` terminate cleanly at the boundary instead of faulting on the intentionally-absent
parents. Operations that genuinely need the pruned history (diffing the boundary commit
against its missing parent, merging across it) are unsupported on a shallow clone — the
same constraint git imposes. The target use case is disaster recovery: pull the latest
snapshot without dragging down the entire backup history.

## Encryption (reframed)

mcadiff does **not** encrypt bucket objects at rest. A world's NBT is recoverable by
anyone who can read the bucket. The practical answer today is the provider's **server-side
encryption (SSE)** plus tight bucket ACLs and per-backup credentials.

True client-side / zero-knowledge encryption is **not** simply "encrypt each object before
upload." Encrypting whole content-addressed objects destroys cross-snapshot dedup — two
snapshots that share a chunk would encrypt it to different ciphertext (under any
nonce-using scheme) and re-upload it, which defeats the incremental-push economics that
the pack protocol exists to provide. Done right it has to be **record-level**: a
convergent/deterministic scheme keyed by content so identical plaintext chunks still
dedup, with the key held only by the client. That's a real feature with real key-management
surface, and is intentionally left for later rather than bolted on as object-level
encryption that would silently break dedup.
