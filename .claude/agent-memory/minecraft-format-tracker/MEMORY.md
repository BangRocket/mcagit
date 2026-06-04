# Minecraft Format Tracker — Memory Index

- [Format facts: DataVersions, region, chunk, compression](format-facts.md) — DataVersion map, LZ4 type-4 (framed, lz4-java), .mcc format, block_states packing rules, chunk structure evolution
- [World layout: directory structure](world-layout.md) — Overworld uses root region/entities/poi; DIM-1/DIM1 for Nether/End; dimensions/ only for custom; advancements/ and stats/ at root
- [Project state: mcadiff Anvil/NBT code gaps](project-state.md) — LZ4 throws UnsupportedChunkException, .mcc file has no header (raw payload), poi NbtIdentity gap, dimensions/ not scanned, block_states opaque
- [User profile](user-profile.md) — adversarial/constructive review style preferred; wants brutally specific severity-ranked findings with file:line and fix direction
