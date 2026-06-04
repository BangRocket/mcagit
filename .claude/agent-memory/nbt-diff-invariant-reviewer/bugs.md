---
name: known-bugs-and-risks
description: Latent bugs and drift risks found in the 2026-06-03 full subsystem audit. Severity: BLOCKER/HIGH/MED/LOW/NOTE.
metadata:
  type: project
---

## BLOCKER

### B1: NbtJson float/double round-trip loses NaN and ±Infinity
- `ToJson` for float/double uses `JsonValue.Create(float/double)`. System.Text.Json serializes NaN and ±Infinity as the string "NaN"/"+Infinity"/"-Infinity" in some paths or throws in others depending on JsonSerializerOptions. The patch file's top-level JsonSerializerOptions does NOT set `AllowTrailingCommas` or any special number handling. When deserializing, `GetValue<float>()` on a JSON string token will throw.
- NbtEquality uses `.Equals()` so NaN==NaN at comparison time — but if the patch round-trip corrupts the float, apply will fail or produce wrong data.
- Fix: string-encode floats (like longs) or use IEEE 754 hex encoding. Add a test: `NbtFloat(float.NaN)`, `NbtFloat(float.PositiveInfinity)`, `NbtDouble(double.NaN)`.

### B2: NbtJson byte round-trip is unsigned-only — signed bytes silently corrupt
- `ToJson` for Byte: `One("byte", tag.ByteValue)`. `ByteValue` in fNbt returns `byte` (0-255, unsigned).
- `FromJson`: `(byte)val!.GetValue<int>()`. This round-trips 0-255 correctly at the binary level.
- BUT: `NbtChangeSink.AsLongs` casts `(sbyte)bsrc[i]` for array comparison. If the patch stores a ByteArray element as its unsigned value (0-255) in the summary display, but the array compares as signed, an element like 0xFF will display as "255" in the patch but compare as -1. This is a display/data mismatch but not a patch corruption bug per se.
- The real bug: scalar NbtByte ToJson emits the unsigned value (e.g. 200 for 0xC8). FromJson reads it back as `(byte)200 = 200`. NBT byte IS unsigned storage but signed semantics in Minecraft; fNbt exposes `.ByteValue` (unsigned byte) AND `.ShortValue` / manual cast for signed reading. The representation is self-consistent but could confuse users comparing display (-56b in SNBT) vs patch value (200).
- Severity: MED for display confusion, LOW for actual data corruption.

## HIGH

### H1: NbtPath.Set for identity list elements — inserts at tail on add, not at original position
- When applying an Added op for a keyed list element, `NbtPath.Set` calls `list.Add(newTag)`. The element is appended at the end, not inserted at the original position. This changes list order relative to what was in B, which matters when Minecraft reads the list by index (e.g., inventory slots whose Slot key and physical position are expected to agree).
- For identity-keyed lists Minecraft re-sorts on load for some cases (block_entities), but not all. If Minecraft reads position-sensitive data by index after a patch that reordered entries, behavior may differ.
- Fix: PatchExtractor could record original index in a side-channel, or PatchApplier could insert at the position computed by FindIdentity on the surrounding elements. At minimum, document the limitation.

### H2: ListMatcher asymmetric key resolution — only A's key presence decides identity mode for both lists
- `CompareList`: `keysA = TryGetKeys(a)`, then `keysB = keysA is null ? null : TryGetKeys(b)`.
- If A's list elements all have identity (returns keys) but B's list has a collision (returns null), `keysB` is null and the code falls back to index alignment. BUT if A's list has a collision and B's has unique identity, `keysA` is null and index alignment is also used — even though B could be meaningfully matched.
- More importantly: if A has N=1 element with identity and B has N=2 with a collision, `keysA` is not null but `keysB` IS null → index fallback, which is correct. But if B has N=1 and A has N=2 with a collision → index fallback too. These are all correct by design (bail to index if either side can't be keyed).
- The actual risk: a list with a single element always has unique identity (no collision possible). A list going from 1 element to 2 with a coord collision will silently switch from identity-mode to index-mode between versions, changing all patch paths for that list. This is a backward-compat hazard for existing .mcapatch files.

### H3: NbtCanonical.Sorted does not handle ByteArray/IntArray/LongArray — Clone() is used
- For array types, `Sorted` falls into the `default: return (NbtTag)tag.Clone()` branch.
- fNbt's `Clone()` on array types: the fNbt source clones the array by reference in some versions, not by value. If fNbt's Clone is a shallow copy of the array backing store, modifying the original array after `Serialize` would retroactively corrupt the canonical bytes. This is library-version-dependent and should be verified.
- Fix: explicitly construct new NbtByteArray/NbtIntArray/NbtLongArray with a copied array in Sorted().

### H4: PatchApplier root-op path check uses `[{ IsRoot: true } rootOp]` list pattern — ordering matters
- `if (cp.Ops is [{ IsRoot: true } rootOp])` matches only when there is exactly ONE op AND it has an empty path. This is correct. But PatchExtractor always produces exactly one root op for whole-chunk/added/removed chunks. If a future code path accidentally adds a second op before the root op in the list, the pattern would fail silently and fall through to per-node apply, which would then fail because the root op's path "" would be treated as a node op and `NbtPath.Set(root, "", ...)` returns false (segs.Count == 0 check).
- Risk: silent conflict reporting instead of clear error. Low likelihood but confusing if triggered.

## MED

### M1: NbtPath parser accepts '][' as valid — adjacent brackets produce empty-string segment
- Input: `"list[]"` → segment `IsBracket=true, Text=""`. `TryIndex("")` returns false. `FindIdentity` with key `""` calls `NbtIdentity.KeyOf` for each element and compares to `""`. KeyOf never returns `""` (it returns null for no identity). So `FindIdentity` returns -1. `Step` returns null. No crash, but a silently wrong path.
- Input: `"list[0][1]"` → two bracket segments. The first steps into the list at index 0 (which must be a list itself). This is consistent behavior but undocumented.
- More concerning: a path like `"a..b"` produces a `Seg(false, "")` for the empty segment between the two dots. `comp.Get("")` on an NbtCompound — fNbt behavior for empty-string key is unspecified; it may return null or throw. This will silently return null from Get, or fail Set with "parent path missing."

### M2: NbtChangeSink.Flatten uses index paths for list elements, but PatchOpSink does not
- When a whole list is Added or Removed (e.g., an entire block_entities list appears in B only), `NbtChangeSink.Flatten` recurses into it using `$"{path}[{i}]"` (integer index). The PatchOpSink does NOT flatten — it emits a single op for the whole list at `path`. These paths are different, but this is BY DESIGN (patch addresses the whole subtree, display shows each leaf). No drift here, but it means display paths for Added/Removed list elements are index-based even when the list would be identity-keyed if compared to something. A future maintainer may be surprised by this.

### M3: WorldDiffer.DiffLoose silently returns null when bytes differ but NBT comparison finds zero changes
- If two .dat files have different compressed bytes but decode to identical NBT (e.g., different compression levels or trailing garbage), `DiffLoose` returns null (no diff). This is correct by design but means re-compressing a file with a different level and saving it will make the file disappear from the diff. Could be surprising when debugging.

### M4: NbtIdentity priority order may produce wrong key for compounds that have BOTH xyz AND UUID
- A block entity compound that also happens to have a UUID field (e.g., some mobs can be block entities) would be keyed by xyz, not UUID. This is probably the right heuristic but is not documented. If the priority ever needs to change, all existing patch files addressing those nodes by UUID will break.

### M5: NbtJson.FromJson for "byte" uses GetValue<int>() then casts to byte — values > 255 corrupt silently
- If someone hand-edits a patch file and writes `{"byte": 300}`, `(byte)300 = 44`. No validation, no error.
- Fix: add a range check or use GetValue<byte>() if the JSON number fits, throw otherwise.

### M6: CompareCompound iterates b's remaining keys from bByName.Values — dictionary value order is not guaranteed stable
- After removing matched keys, the remaining values (added keys in B) are iterated via `bByName.Values`. Dictionary value enumeration order in .NET is insertion order (for small dictionaries, effectively), but this is not guaranteed by the spec. The final sort in `Compare()` normalizes order for display, but PatchOpSink emits ops in walk order — meaning the order of Added ops for new compound keys is non-deterministic in theory.
- In practice, .NET Dictionary preserves insertion order for small dictionaries, and the sort in Compare() fixes display, but PatchOpSink op ordering is undefined. Patch apply doesn't depend on op ordering, so this is only a cosmetic issue for human-readable patch files.

## LOW

### L1: ValueRepr.Scalar for NbtByte uses `.ByteValue` (unsigned) with "b" suffix — SNBT convention is signed
- SNBT (Stringified NBT) uses signed byte values: `-56b` not `200b`. ValueRepr emits `200b` for a byte with value 0xC8. This won't cause any correctness bug, but it differs from what Minecraft's own /data command and most NBT editors show, which could confuse users comparing output.

### L2: RegionFile.ParseRegionCoords falls back to (0,0) for non-standard file names
- If a region file is named something other than `r.X.Z.mca`, chunk coordinates become region-local (0..31 x 0..31) rather than world-absolute. The diff would still work, but block entity paths like `block_entities[@5,63,8]` could be misread if the absolute coordinates are expected. This would affect patch path addressing for such files. Documents says "non-standard name — coords unknown" but does not warn the user.

### L3: NbtPath.Set for numeric list indices: out-of-bounds negative index returns false
- `TryIndex` parses signed integers. A path `list[-1]` would be parsed as index -1. `Set` checks `i >= 0 && i < list.Count` and returns false for negative indices. Get returns null. No crash, but a user who hand-edits a patch with a negative index gets a silent conflict rather than a clear error.

### L4: PatchApplier.Category is a local reimplementation of category logic, not shared with WorldSource
- `PatchApplier.Category(entry)` uses string contains checks (`/entities/`, `/poi/`) to classify entries for `--only-categories`. If WorldSource's category logic changes, PatchApplier won't follow. This is a maintenance hazard.

## BLOCKER (new findings 2026-06-03 adversarial review)

### B3: Merger.ApplyOp silently discards NbtPath.Set failures — silent data loss in merge
- `Merger.ApplyOp` (Merger.cs:275) calls `NbtPath.Set(root, op.Path, tag)` and **ignores the bool return value**. If Set returns false (parent path missing, type mismatch), the op is silently dropped — no conflict recorded, no error thrown.
- Contrast with `PatchApplier.ApplyNodeOp` (PatchApplier.cs:220) which correctly returns `(false, "parent path missing")` when Set fails and records a conflict.
- Fix: check the return value and add a MergeConflict if false.

### B4: NaN/Infinity crash is confirmed: `JsonValue.Create(float.NaN)` throws `ArgumentException`
- Confirmed via direct test: System.Text.Json raises "NET number values such as positive and negative infinity cannot be written as valid JSON" for NaN and both infinities, for both float and double. This is not speculative.
- Any chunk with a velocity/rotation NaN (a known Minecraft corruption artifact) will cause `PatchExtractor.Extract`, `NbtComparer.Walk` via `PatchOpSink`, and `Merger.MergeNode` to crash with unhandled ArgumentException.
- The NbtChangeSink path (display-only) does NOT call NbtJson.ToJson, so `diff` survives — but `extract`, `apply`, and all repo operations die.

## HIGH (new findings)

### H5: Merger.SameValue uses ToJsonString() comparison — key-ordering sensitivity
- `SameValue(a, b)` (Merger.cs:279): `a.ToJsonString() == b.ToJsonString()`. JsonObject preserves insertion order. If both sides independently encode the same compound with keys in different insertion order (e.g., because they came from different NBT files with different key orderings in the same compound), ToJsonString() produces different strings and SameValue returns false — causing a spurious conflict.
- Confirmed: `{"b":2,"a":1}` != `{"a":1,"b":2}` from JsonObject.ToJsonString(). Two structurally-identical Added compounds that were built with different key orderings will be reported as a merge conflict.
- Fix: use NbtEquality.DeepEquals on the deserialized NbtTag values, or sort compound keys before comparison.

### H6: NbtPath bracket parsing breaks for identity keys containing ']'
- NbtPath.Parse (NbtPath.cs:122) uses `path.IndexOf(']', i+1)` — first `]` wins. If an identity key contains `]`, the segment is truncated at the wrong position.
- Confirmed via test: identity key `id:somemod:weird[special]attr` embedded in a path produces `id:somemod:weird[special` as the extracted key (truncated at the inner `]`), causing FindIdentity to return -1 (silent miss).
- For vanilla Minecraft ID strings (namespaced format, no brackets), this is safe. For modded content with unusual IDs, patches silently become no-ops or conflicts.
- Fix: escape `]` in identity keys, or use a different bracket/escape convention.

### H7: UUID cross-format string collision between pre-1.16 and post-1.16 entity formats
- `NbtIdentity.KeyOf` for UUID int[4]: `$"uuid:{v[0]:x8}{v[1]:x8}{v[2]:x8}{v[3]:x8}"` — 32 hex chars.
- For UUIDMost/Least longs: `$"uuid:{most.LongValue:x16}{least.LongValue:x16}"` — also 32 hex chars.
- Confirmed: int[4] = {1,2,3,4} produces `uuid:00000001000000020000000300000004`; longs most=0x0000000100000002, least=0x0000000300000004 produces the IDENTICAL string.
- In practice, a world list would be homogeneous (either all old or all new UUID format), so collision between the two formats in the SAME list is impossible. However, in theory a mixed list (one entity migrated, one not) would cause `ListMatcher.TryGetKeys` to see two elements with the same key → returns null → index fallback. This is safe but subtly wrong: index fallback would mask the identity-based match, possibly causing false diffs on migration.

## NOTE (new)

### N4: WorldPatch.Version = 1 is written but never read by PatchApplier
- PatchModels.cs:47 hardcodes Version=1. PatchApplier.Apply never checks it. There is no version gate, no migration, no error on unknown version. Future format bumps must add a check here.

### N5: fNbt 1.0.0 released July 3 2025 — project is pinned to that version
- fNbt 1.0.0 targets .NET Standard 2.0. Previous version (0.6.4) was from 2018. The gap is large; the 1.0.0 release is the current version and appears to be the maintained fork by mstefarov. No known bugs specific to NaN/float/signedness in fNbt itself were found; the NaN issue is in System.Text.Json, not fNbt.

## NOTE

### N1: No sink parity test exists
- There is no test that drives both NbtChangeSink and PatchOpSink from the same NbtComparer.Walk call and asserts they agree (e.g., same number of events, same paths). All existing tests drive one sink or the other.

### N2: No NbtCanonical determinism test across key-order permutations
- Tests don't verify that two NbtCompound instances with the same keys in different order produce identical canonical bytes.

### N3: NbtEquality.ListEquals checks element order — but a patch that re-adds a list may change order (H1 above)
- After the H1 bug, a list that was re-appended in wrong order would fail the 3-way DeepEquals guard on subsequent reverse-apply, since ListEquals is order-sensitive.
