# 0001 — NBT identity-based list matching

## Status

Accepted.

## Context

NBT lists are ordered, but many of them behave as *sets*: a chunk's `block_entities`, a region's
`Entities`, an inventory's `Items`, an entity's `attributes`. Minecraft reorders these freely between
saves. A position-based comparison would report a reorder as a wholesale rewrite — thousands of
spurious "removed at index 3 / added at index 5" changes — which makes the diff useless and bloats
patches.

## Decision

Lists are matched by **identity**, not position, when their elements have a usable one. The identity
is chosen per element in this order of preference (`Nbt/NbtIdentity`):

1. Block coordinates `(x, y, z)` — block entities.
2. Entity `UUID`.
3. Inventory `Slot`.
4. A string `id`.
5. Index fallback when none of the above apply.

The path language (`Nbt/NbtPath`) addresses elements by that identity — `Entities[uuid:…]`,
`block_entities[@5,63,8]`, `Items[slot:3]` — so a patch is stable across reorders.

## Consequences

- A reorder produces **no** diff; only genuine field changes are reported.
- Patches address elements by identity, so they apply correctly even if the target list was reordered.
- Identity values containing a literal `]` (e.g. a modded id) are a known limitation — `NbtPath`
  truncates at the first `]`.
- New list types with a natural key should extend `NbtIdentity`, not add a second matching path.
