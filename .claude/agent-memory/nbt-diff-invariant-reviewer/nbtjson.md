---
name: nbtjson-round-trip-notes
description: NbtJson lossiness edge cases, what is and is not covered by tests, and encoding decisions
metadata:
  type: project
---

## Encoding Design

Type-tagged single-key JSON objects:
- byte, short, int: JSON numbers (GetValue<int/short>; byte casts from int)
- long: JSON string (to preserve 64-bit precision past 2^53)
- float: JSON number (LOSSY for NaN/Infinity — BLOCKER B1)
- double: JSON number (LOSSY for NaN/Infinity — BLOCKER B1)
- string: JSON string
- bytes (ByteArray): JSON array of unsigned ints
- ints (IntArray): JSON array of ints
- longs (LongArray): JSON array of strings
- list: {"list": {"type": "<NbtTagType.ToString()>", "items": [...]}}
- compound: {"compound": {"key": <encoded-value>, ...}}

## Round-Trip Test Coverage (NbtCodecTests.cs)

COVERED:
- All scalar types including Byte(200) for signedness, Long beyond 2^53, LongArray with extremes
- Nested compound+list, empty list with explicit type, IntArray, ByteArray
- Text serialization path (ToJson → ToJsonString → Parse → FromJson)

NOT COVERED:
- float NaN, float PositiveInfinity, float NegativeInfinity
- double NaN, double ±Infinity
- Empty compound round-trip (as a standalone, not nested)
- List of lists (nested NbtList)
- A compound with no keys
- ByteArray where values > 127 (signed vs unsigned) — partial coverage with {0, 255, 1, 254}

## Rust Port Encoding (crates/nbt/src/json.rs) — as of 2026-06-08

In the Rust port, floats and doubles are NOW string-encoded via `x.to_string()` (like longs), matching the fix for B1/B4 above. `from_json` prefers the string form; accepts legacy JSON-number form for backward compatibility with patches written before the fix. Rust's `f32`/`f64` Display is exact (shortest round-trippable representation; NaN/Inf serialize as "NaN"/"inf"/"-inf"). Tests added: `double_survives_file_roundtrip`, `nonfinite_double_survives`.

**OK (Rust float/f32 test):** Both `nonfinite_double_survives` and `nonfinite_float_survives` are present and pass as of feat/nbt-valence. NaN and ±Infinity are covered for both f32 and f64.

**WARN (legacy float path):** The `None => val.as_f64()? as f32` legacy path for float can lose precision (f64→f32 narrowing). This only matters when reading old patch files with JSON-number floats — any float that was previously lossy stays lossy; the new path is exact. Acceptable one-way compat.

## Known Lossiness

1. float/double NaN and ±Infinity (C# / .NET port): System.Text.Json by default will throw or produce invalid JSON for these values when serializing a JsonValue.Create(float.NaN). Even if it writes a string token, FromJson does GetValue<float>() which expects a number token. Behavior is undefined / throws. **This is a BLOCKER if any Minecraft chunk uses NaN (e.g., entity velocity=NaN is a known Minecraft corruption case). FIXED in the Rust port.**

2. NbtByte unsigned display: stored as 0-255 unsigned in JSON. fNbt's ByteValue is unsigned. SNBT convention is signed (-128 to 127). Not a round-trip bug but a display confusion.

3. No type preservation for compound key order: ToJson for compound iterates in fNbt's internal order; FromJson rebuilds in JSON object order. System.Text.Json preserves insertion order for JsonObject. Round-trip preserves the order fNbt uses internally, which may differ from the original NBT file's order (NbtCanonical sorts, but NbtJson does not).
