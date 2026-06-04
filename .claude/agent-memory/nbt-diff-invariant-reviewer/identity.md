---
name: nbt-identity-patch-path-stability
description: How NbtIdentity.KeyOf computes keys, priority order, backward-compat risks, and NbtPath resolution details
metadata:
  type: project
---

## NbtIdentity.KeyOf Priority Order

1. x+y+z (all NbtTagType.Int) → "@x,y,z"  [block entities, POI records]
2. UUID (NbtTagType.IntArray, length 4) → "uuid:XXXXXXXX..." (32 hex chars)  [entities 1.16+]
3. UUIDMost+UUIDLeast (both NbtTagType.Long) → "uuid:..." (32 hex chars)  [entities pre-1.16]
4. Slot (NbtTagType.Byte) → "slot:N" where N is UNSIGNED byte value
5. id (NbtTagType.String, non-empty) → "id:<value>"  [attributes, modifiers]
6. null → no identity, fall back to index alignment

## Backward-Compat Hazard Points

- Any change to priority order changes which key format is emitted for compounds that match multiple rules. Existing .mcapatch files using old key format will silently fail to find the node (NbtPath.FindIdentity returns -1 → conflict).
- The Slot key is emitted as UNSIGNED byte value (e.g., "slot:255" for Slot=-1). If this is ever changed to signed ("slot:-1"), all existing inventory-targeted patches break.
- UUID key format: 8 hex chars per int component, no separators. If the format changes (e.g., adding dashes), all entity-targeted patches break.
- No version stamp distinguishes old vs new key formats in .mcapatch files.

## NbtPath Resolution

- Parse: dotted segments (compound keys), bracketed segments (list elements)
- Bracket content: if TryIndex (int.TryParse) succeeds → positional; else → identity key via NbtIdentity.KeyOf scan
- FindIdentity: linear scan O(N) over list elements. Large lists (256+ entities in a chunk) may be slow.
- Set for identity-keyed add: appends at tail (H1 bug in bugs.md)
- Set for positional add: inserts at index if within bounds, appends if == count, returns false if beyond count

## ListMatcher Behavior

- Returns null (index fallback) if: list is empty, list is not NbtTagType.Compound, any element has no identity, or any two elements have the same identity key (collision).
- Only checks A's list first; if A has keys, then checks B. If B has collision, falls back to index.
- Switching between identity and index mode between versions changes all patch paths for that list.

## No Versioning / Migration

WorldPatch.Version = 1 hardcoded. No format migration logic. Any breaking change to NbtIdentity or NbtPath format requires a manual version bump and migration note.
