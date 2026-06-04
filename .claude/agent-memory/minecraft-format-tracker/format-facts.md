---
name: format-facts
description: Authoritative Minecraft format facts: DataVersions, region file spec, chunk NBT evolution, compression types, block_states packing
metadata:
  type: reference
---

## DataVersion Map (key releases)
- 1.17: 2724 | 1.17.1: 2730
- 1.18: 2860 (21w43a introduced flat chunk structure, no more Level compound)
- 1.18.2: 2975 | 1.19: 3105 | 1.19.4: 3337
- 1.20: 3463 | 1.20.4: 3700 | 1.20.5: 3837 (24w04a = 3806, LZ4 added)
- 1.21: 3953 | 1.21.1: 3955
- Latest stable as of 2026-06: Java Edition 26.1.2 = DataVersion 4790
- Highest seen: 4896 (26.2 Pre-Release 3)

## Region File Format (Anvil .mca)
- Sectors: 4096 bytes each; first 2 sectors = header (location table + timestamp table)
- Location table: 1024 entries × 4 bytes = bytes [0..4095]; entry i: bytes[4i..4i+2] = 3-byte big-endian sector offset, bytes[4i+3] = 1-byte sector count
- Timestamp table: 1024 entries × 4 bytes = bytes [4096..8191]; entry i = 4-byte big-endian Unix timestamp
- Chunk header: 4-byte big-endian length (includes compression byte), 1-byte compression type
- Compression types: 1=GZip (unused in practice), 2=Zlib (standard), 3=Uncompressed (pre-1.15.1), 4=LZ4 (since 24w04a, server-only via region-file-compression=lz4), 127=Custom (since 24w05a, namespaced string prefix with 2-byte unsigned length)
- External/oversized: compression byte | 0x80 → payload in c.X.Z.mcc; .mcc file is raw compressed payload with NO additional header (just the bytes that would normally follow the compression byte inline)
- LZ4 variant: lz4-java LZ4FrameInputStream/LZ4FrameOutputStream (standard LZ4 frame format, NOT the proprietary BlockOutputStream format). Readable by standard LZ4 frame decoders.

## Chunk NBT Structure Evolution
### Pre-1.18 (DataVersion < 2860, snapshot boundary 21w43a)
- Root has Level compound containing everything
- Level.Sections[]: each section has Y (byte), Palette (list), BlockStates (long array), BlockLight, SkyLight
- Level.TileEntities (= block entities), Level.Entities (= entities, until 20w45a)
- Level.Biomes: originally 256 bytes; since 19w36a = int[1024]; moved to paletted in 21w39a
- Level.TileTicks, Level.LiquidTicks

### Post-1.18 (DataVersion >= 2860)
- All fields flattened to root; Level compound gone
- sections[]: each section has Y (byte), block_states {palette, data?}, biomes {palette, data?}, BlockLight, SkyLight
- block_entities (was TileEntities), block_ticks (was TileTicks), fluid_ticks
- xPos, zPos [Int], yPos [Int] (lowest Y section, e.g. -4 for standard 1.18 worlds)
- Status [String]: "minecraft:empty" through "minecraft:full"
- Heightmaps, structures, blending_data, PostProcessing

### Single-entry palette optimization
When block_states.palette has exactly 1 entry, the data long array is ABSENT entirely (the section is filled uniformly with that one block).

### Biomes
- Pre-1.15: 256-byte array (16×16 per chunk column, ignoring Y)
- 19w36a–21w38a: int[1024] (4×4×4 per section × 16 sections)
- 21w39a+: paletted in sections[].biomes {palette, data?} same packing as block_states, min 1 bit (effectively), stored per section

## Block States Palette Bit Packing (post-1.16)
- bits_per_entry = max(ceil(log2(palette.length)), 4)  [4 is the minimum]
- For a palette of size 1: data array absent entirely
- Values packed right-to-left (LSB first) within each long
- NO straddling: if a value doesn't fit in the remaining bits of the current long, it starts in the next long (wasted bits at the top of each long)
- 4096 blocks per section; array length = ceil(4096 / floor(64 / bits_per_entry))
- Example: palette size 16 → bits=4, 16 indices per long, 256 longs
- Example: palette size 17 → bits=5, 12 indices per long (64/5=12), ceil(4096/12)=342 longs

## Entity/POI File Formats
- entities/*.mca: same Anvil container; chunk NBT has DataVersion [Int], Position [Int Array, 2 elements: chunk X and Z], Entities [List of entity compounds]
- poi/*.mca: same Anvil container; chunk NBT has DataVersion [Int], Sections [Compound: string keys = Y coordinate]; each section has Valid [Byte], Records [List of {type [String], pos [Int Array 3], free_tickets [Int]}]
- POI introduced 1.14 (19w11a); entities separated from main chunks in 20w45a (1.17 cycle)
