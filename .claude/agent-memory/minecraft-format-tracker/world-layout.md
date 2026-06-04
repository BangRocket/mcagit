---
name: world-layout
description: Current Java Edition world save directory layout, confirmed against minecraft.wiki/w/World and /w/Java_Edition_level_format as of 2026-06
metadata:
  type: reference
---

## Confirmed Current Layout (1.20+, still valid in 26.x)

```
<world>/
  level.dat            # GZip NBT, global world state
  level.dat_old        # backup of previous level.dat
  level.dat_new        # transient staging during save
  session.lock         # write lock (snowman char), skip when diffing
  icon.png             # optional thumbnail

  region/              # OVERWORLD terrain chunks (r.X.Z.mca)
  entities/            # OVERWORLD entity chunks (r.X.Z.mca), since 20w45a
  poi/                 # OVERWORLD POI chunks (r.X.Z.mca), since 1.14

  DIM-1/               # NETHER
    region/
    entities/
    poi/
    data/

  DIM1/                # THE END
    region/
    entities/
    poi/
    data/

  data/                # Overworld-scoped world data (scoreboard.dat, etc.)
  playerdata/          # <uuid>.dat per player
  advancements/        # <uuid>.json per player
  stats/               # <uuid>.json per player
  datapacks/
  resourcepacks/
  generated/           # custom structures, etc.

  dimensions/<ns>/<path>/   # CUSTOM dimensions only
    region/
    entities/
    poi/
```

## Key Points
- The OVERWORLD uses root-level region/, entities/, poi/ — NOT dimensions/minecraft/overworld/
- DIM-1 and DIM1 are still the canonical paths for Nether and End in all current versions
- advancements/ and stats/ are at the WORLD ROOT (not inside playerdata/)
- dimensions/ folder exists only for modded/custom dimensions added via data packs
- The wiki page at /w/Java_Edition_level_format states this explicitly; earlier wiki responses about "dimensions/minecraft/overworld" were inaccurate (that restructuring was proposed in a 26.x snapshot but the overworld path remains at root)
