# The `.mcapatch` format

A `.mcapatch` is a human-readable JSON document describing the changes from a **base** world to a **target** world as a list of node-level operations. Every op records *both* the old and new value, which makes a patch invertible (`apply --reverse`) and lets apply guard each change against the target (3-way). `apply` never mutates its target — it copies to a fresh output world and rewrites only the patched nodes.

## Top-level shape

```jsonc
{
  "version": 1,
  "base":   "New_World_Older",   // informational labels
  "target": "New_World_Newer",
  "note":   "optional --note text",
  "files": [ /* PatchFileEntry… */ ]
}
```

`version` gates the encoding below. A reader should refuse a version it does not understand.

## File entries

Each entry is one comparable file — a region (`.mca`, with per-chunk ops) or a loose NBT file (with ops directly).

```jsonc
// loose NBT file
{ "path": "level.dat", "kind": "Loose", "status": "Modified",
  "ops": [ /* PatchOp… */ ] }

// region file
{ "path": "region/r.0.0.mca", "kind": "Region", "status": "Modified",
  "chunks": [
    { "x": 0, "z": 0, "status": "Modified", "timestamp": 1717500000,
      "ops": [ /* PatchOp… */ ] }
  ] }
```

`status` is `Added` / `Removed` / `Modified`. A chunk carries its region `timestamp` so a re-applied chunk keeps its header time.

## Operations

```jsonc
{ "path": "Data.Time", "base": { "long": "383" }, "value": { "long": "1574" } }
```

- `path` — an `NbtPath` into the unit's NBT tree (see grammar below). An **empty** path (`""`) means the op replaces the whole unit root, used by `--whole-chunk` / `--whole-file`.
- `base` — the value the op expects to find (type-tagged NBT JSON, or `null` for an add).
- `value` — the value to write (type-tagged NBT JSON, or `null` for a remove).

Apply is **3-way guarded**: a node is changed only if the target's current value equals `base`; otherwise it is reported as a conflict and skipped (unless `--force`). `--reverse` swaps `base` and `value`, restoring the base world.

## Type-tagged NBT JSON

Values are single-key objects tagged by NBT type. Numbers that JSON cannot represent exactly — `long` (beyond 2^53) and `float` / `double` (including `NaN` / `Infinity`) — are **string-encoded** and must round-trip exactly.

```jsonc
{ "byte":   1 }
{ "short":  256 }
{ "int":    65536 }
{ "long":   "9223372036854775807" }          // string-encoded
{ "float":  "3.1400001" }                     // string-encoded (round-trip "R")
{ "double": "2.718281828459045" }             // string-encoded
{ "string": "text" }
{ "bytes":  [1, 2, 3] }                        // byte array
{ "ints":   [256, 512] }                       // int array
{ "longs":  ["9223372036854775807"] }          // long array (string elements)
{ "list":   { "type": "Int", "items": [ {"int": 1}, {"int": 2} ] } }
{ "compound": { "Name": {"string": "minecraft:stone"} } }
```

## NbtPath grammar

A path is a dotted / bracketed expression navigating the decoded NBT tree.

- `Key` — a compound field: `Data.Player.Pos`.
- `[n]` — a list element by integer index: `Pos[0]`.
- `[@x,y,z]` — a list element by block coordinates (block entities): `block_entities[@5,63,8]`.
- `[uuid:…]` — by entity UUID: `Entities[uuid:641b80dd…]`.
- `[slot:n]` — by inventory slot: `Items[slot:3]`.
- `[id:…]` — by string `id`: `attributes[id:minecraft:generic.movement_speed]`.

Identity selectors are what make a reorder not look like a rewrite; when an element has no usable identity, lists fall back to index matching.

### Limitations

- A compound key containing a literal `.` or `[` is not addressable (real Minecraft keys do not use them).
- An identity value containing a literal `]` (e.g. a modded id) is truncated at the first `]`.
- Patches are **DataVersion-sensitive**: a static path can silently conflict across format boundaries that rename or restructure nodes (e.g. 1.21.2 / DV 4080, 1.21.4 / DV 4189). Apply onto a base whose version matches the patch's, or expect guarded conflicts.

## Example

```sh
mcagit extract New_World_Older New_World_Newer -o changes.mcapatch
mcagit apply changes.mcapatch New_World_Older -o Old_Updated      # forward
mcagit apply --reverse changes.mcapatch New_World_Newer -o Restored  # invert
```

Flags: `extract` takes `--only <cats>`, `--whole-chunk`, `--whole-file`, `--note`; `apply` takes `--reverse`, `--force`, `--dry-run`, `--only`. `apply` exits `0` clean, `1` if any conflicts were skipped.
