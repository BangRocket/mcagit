---
name: test-coverage-map
description: What is and is not covered by the existing test suite for the Diff/Nbt/Patch subsystems
metadata:
  type: project
---

## Test Files

### C# (.NET) test files in tests/McaGit.Tests/
- NbtComparerTests.cs — scalar, add/remove, type change, nested, indexed list, identity list (coords, id), arrays (summary, expanded, length change)
- NbtCodecTests.cs — NbtJson round-trip for all scalar types, Long precision, LongArray, nested compound+list, empty list; NbtEquality.DeepEquals
- NbtPathTests.cs — Get by key/index/identity, Set replace/remove/add, TerminalName, Set with missing parent
- ListMatcherTests.cs — coord identity, UUID identity, collision fallback, non-compound fallback, string-id identity, slot identity
- PatchTests.cs — forward apply, reverse apply, whole-chunk mode, conflict detection, force override, dry-run
- WorldDifferTests.cs — modified chunk+NBT changes, identical files, directory diff, added/removed files
- No golden .mcapatch fixture files exist.
- No NbtCanonical determinism test.
- No sink parity test (NbtChangeSink vs PatchOpSink driven from same walk).

### Rust test files (as of feat/nbt-valence)
- crates/nbt/src/canonical.rs — key_order_does_not_affect_canonical_bytes, nested_compounds_are_sorted_too
- crates/nbt/src/identity.rs — modern_uuid_wins, block_coords, slot_then_id, no_identity_returns_none
- crates/nbt/src/json.rs — large_long_survives_roundtrip, nested_roundtrips, rejects_multi_key_object, double_survives_file_roundtrip, nonfinite_double_survives, nonfinite_float_survives
- crates/nbt/src/read.rs — reads_named_compound_with_int, truncated_input_errors
- crates/nbt/src/write.rs — write_then_read_roundtrips, empty_list_roundtrips_as_empty
- crates/diff/src/comparer.rs — comparer tests (identity list, index list, etc.)
- Total Rust suite: 25 mca-nbt tests, 86 total (as of feat/nbt-valence)

## Missing Critical Tests (C# port)

1. NbtJson float/double NaN, ±Infinity round-trip
2. NbtCanonical: two compounds with same keys in different order → identical bytes
3. Sink parity: same Walk → same set of paths in both sinks (modulo flattening design intent)
4. NbtPath: empty bracket "list[]", double-dot "a..b" (malformed paths)
5. NbtPath: negative index "list[-1]"
6. NbtIdentity: compound with BOTH xyz AND UUID (verify priority)
7. NbtIdentity: Slot with value > 127 (verify unsigned formatting)
8. NbtComparer: TypeChanged event — verify walk stops (no recursion into children)
9. PatchApplier: list element ordering after identity-keyed Add (H1 bug)
10. NbtCanonical: Clone() of array types — verify deep copy not shallow

## Missing Tests (Rust port, as of feat/nbt-valence)

1. conv.rs has NO direct unit tests — from_value/to_value round-trip not tested for all types.
   Recommend a property/round-trip test: for each NbtValue variant, from_value(to_value(v, false)) == v.
2. No canonical bytes test for list-of-compound with sort=true (verifies nested sort propagates).
3. No test with NbtValue::Byte(i8) negative values through write_named/read (e.g., Byte(-1)).
4. No test for ByteArray with high bytes (> 127) through binary round-trip (covers i8↔u8 cast).
