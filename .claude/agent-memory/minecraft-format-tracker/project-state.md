---
name: project-state
description: Current implementation state of mcagit's Anvil/NBT code: what works, what's missing, key gaps found in 2026-06 audit
metadata:
  type: project
---

## What Works
- Region file parsing: sector layout, location/timestamp tables, compression byte, 0x80 external flag (RegionFile.cs)
- RegionWriter: writes valid .mca including .mcc spill for oversized chunks
- ChunkCodec: GZip (type 1), Zlib (type 2), None (type 3) via fNbt
- NbtCanonical: sorts compound keys, handles empty lists, produces deterministic hash
- NbtEquality: correct deep equality including float NaN-safe comparison
- NbtIdentity: x/y/z coordinates, UUID int[4], UUIDMost/UUIDLeast, Slot byte, id string
- WorldDiffer: parallel diff, compressed-payload fast path, chunk-level and NBT-level diffs
- Snapshotter: parallel per-chunk hashing, ChunkCache for re-commit acceleration
- WorldSource: discovers region/entities/poi across root + DIM-1 + DIM1

## Key Gaps (from 2026-06 adversarial audit)

### BLOCKER
- LZ4 (type 4): ChunkCodec.Decode throws UnsupportedChunkException; Snapshotter catches it and falls back to raw-blob storage for the ENTIRE region file (losing per-chunk granularity). Fix: add K4os.Compression.LZ4 NuGet (LZ4 frame format); dispatch in ChunkCodec switch.
- .mcc file format: RegionWriter writes raw payload with NO header into .mcc; RegionFile reads .mcc as raw payload. This is CORRECT per spec (wiki confirms .mcc = raw compressed payload, no extra header). No bug here — confirmed correct.

### HIGH
- block_states.data absent for single-palette-entry sections: NbtComparer treats absent data as a compound-key removal and reports it as a diff. Fix: NbtComparer or a pre-pass should normalize single-entry palette sections by synthesizing an absent data as empty long[].
- block_states long array is opaque: diff reports "long[N] — M of N entries differ" with no coordinate-level attribution. Fix: decode palette indices per the bit-packing spec to enable per-block diffs.
- WorldSource misses advancements/ and stats/ at world root (loose JSON, not NBT .dat — correctly excluded from NBT path, but worth noting).
- WorldSource misses custom dimensions under dimensions/<ns>/<path>/region/ etc.
- RegionFile.ParseRegionCoords falls back to (0,0) silently on malformed filename — chunk coordinates become wrong (all chunks collide).

### MED  
- NbtIdentity has no handler for POI records: Records[] elements have pos [Int Array] not x/y/z ints, so identity falls through to null → index-based matching. A reorder of POI records reports as wholesale changes.
- entities/ chunk NBT: Position field is Int Array [chunkX, chunkZ], not x/y/z ints. NbtIdentity would not key on it (it looks for Int tags named x/y/z). Entities are keyed by UUID correctly.
- pre-1.18 Level compound: WorldDiffer/NbtComparer will diff Level.Sections vs sections etc. correctly structurally but paths in output will be Level.sections[...] not sections[...]. Not wrong, just version-aware path differences.
- Compression type 127 (custom): treated same as LZ4 — throws UnsupportedChunkException. Correct behavior but no friendly error message.
- Snapshotter.TryChunks: on UnsupportedChunkException from LZ4, returns null → whole region becomes a blob. This is the LZ4 fallback and loses all per-chunk dedup.

### LOW
- NbtPath has no escaping for compound keys containing '.' or '['. Per comment in code, "Real Minecraft keys don't" — true for current versions.
- RegionFile.Parse clamps dataLen to EOF rather than throwing — silent truncation of corrupt chunk.
- NbtCanonical.Serialize sorts compound keys — this is intentional for hashing but means round-tripped NBT changes key order (cosmetic, not semantic).
- WorldSource.ResolveFile treats any non-.mca file as UnitKind.Loose with Category="nbt" regardless of extension — .mcc files or session.lock would be misclassified if passed directly.

## Files and Key Locations
- RegionFile.cs: Parse() line 55; external .mcc read line 88-94; compression byte decode line 78-81
- RegionWriter.cs: Write() line 17; .mcc spill line 35-39
- ChunkCodec.cs: Decode() line 21 — LZ4 falls to default throw line 28
- Snapshotter.cs: TryChunks() line 80; LZ4 fallback comment line 99; catch → null line 109
- WorldSource.cs: DimensionRoots line 15 (missing custom dims); LoosePatterns line 20 (missing advancements/, stats/)
- NbtIdentity.cs: KeyOf() — no POI pos handler; no entities/ Position handler
