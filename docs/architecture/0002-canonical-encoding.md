# 0002 — Canonical NBT encoding

## Status

Accepted.

## Context

The object store dedups by content hash. A chunk's bytes on disk vary with compression scheme
(GZip / ZLib / LZ4 / none), compression level, and fNbt's tag iteration order — none of which change
the chunk's *meaning*. If the hash were taken over the raw bytes, an unchanged chunk re-saved by
Minecraft would hash differently every time and dedup would collapse.

## Decision

`Nbt/NbtCanonical` produces a **deterministic canonical byte form** of a decoded NBT tree:
decompressed, with compound keys sorted, arrays copied (not shared with fNbt internals), re-serialized
the same way every time. The object hash (`ObjectStore`) is SHA-256 over **this canonical form**, so an
unchanged chunk hashes identically regardless of how it was stored on disk.

## Consequences

- An unchanged chunk is stored **once** across any number of snapshots — the property fastback and
  git-LFS can't provide (they hash whole region blobs).
- `commit` → `checkout` reproduces a *semantically faithful* (not byte-identical) world: chunks are
  re-encoded to ZLib with sorted keys. Tests assert it loads and round-trips in Minecraft.
- The canonical form must **never** depend on fNbt internals, iteration order, or a diff-path
  normalization — doing so would silently change every hash. Diff-only normalizations (ADR 0001's
  identity matching, single-entry-palette `data` dropping) live in `Diff/`, applied to decoded trees
  before comparison, never here.
- Recursion is capped at `NbtCanonical.MaxDepth` (512) as one of several untrusted-input guards.
