---
name: test-coverage-map
description: What is and is not covered by the existing test suite for the Diff/Nbt/Patch subsystems
metadata:
  type: project
---

## Test Files

- NbtComparerTests.cs — scalar, add/remove, type change, nested, indexed list, identity list (coords, id), arrays (summary, expanded, length change)
- NbtCodecTests.cs — NbtJson round-trip for all scalar types, Long precision, LongArray, nested compound+list, empty list; NbtEquality.DeepEquals
- NbtPathTests.cs — Get by key/index/identity, Set replace/remove/add, TerminalName, Set with missing parent
- ListMatcherTests.cs — coord identity, UUID identity, collision fallback, non-compound fallback, string-id identity, slot identity
- PatchTests.cs — forward apply, reverse apply, whole-chunk mode, conflict detection, force override, dry-run
- WorldDifferTests.cs — modified chunk+NBT changes, identical files, directory diff, added/removed files
- No golden .mcapatch fixture files exist.
- No NbtCanonical determinism test.
- No sink parity test (NbtChangeSink vs PatchOpSink driven from same walk).

## Missing Critical Tests

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
