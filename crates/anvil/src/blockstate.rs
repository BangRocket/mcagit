//! Decode a chunk's paletted `block_states` / `biomes` to the block or biome at
//! a coordinate. This is the 1.18+ Anvil section format: each section holds a
//! palette (list of states) and a packed `long[]` of palette indices, with
//! `bits = max(min_bits, ceil_log2(palette_len))` and **no index spanning a
//! 64-bit boundary** (the 1.16+ packing). A single-entry palette omits `data`
//! (the whole section is that one state).

use mca_nbt::NbtValue;

/// A resolved block state: its `Name` plus any block-state `Properties`.
#[derive(Debug, Clone, PartialEq)]
pub struct BlockState {
    pub name: String,
    pub properties: Vec<(String, String)>,
}

/// The block state at world coords `(x, y, z)` within a decoded chunk root, or
/// `None` if the section/palette is absent.
pub fn block_at(chunk: &NbtValue, x: i32, y: i32, z: i32) -> Option<BlockState> {
    let section = section_at(chunk, y)?;
    let bs = get(section, "block_states")?;
    let palette = as_list(get(bs, "palette")?)?;
    if palette.is_empty() {
        return None;
    }
    let (lx, ly, lz) = ((x & 15) as usize, (y & 15) as usize, (z & 15) as usize);
    let idx = ly * 256 + lz * 16 + lx; // YZX order, 16^3
    let pi = palette_index(get(bs, "data"), palette.len(), 4, idx)?;
    block_state(palette.get(pi)?)
}

/// The biome id at world coords `(x, y, z)` (4×4×4 cells per section).
pub fn biome_at(chunk: &NbtValue, x: i32, y: i32, z: i32) -> Option<String> {
    let section = section_at(chunk, y)?;
    let biomes = get(section, "biomes")?;
    let palette = as_list(get(biomes, "palette")?)?;
    if palette.is_empty() {
        return None;
    }
    let (bx, by, bz) = (
        ((x & 15) / 4) as usize,
        ((y & 15) / 4) as usize,
        ((z & 15) / 4) as usize,
    );
    let idx = by * 16 + bz * 4 + bx; // YZX order, 4^3
    let pi = palette_index(get(biomes, "data"), palette.len(), 1, idx)?;
    match palette.get(pi)? {
        NbtValue::String(s) => Some(s.clone()),
        _ => None,
    }
}

/// Resolve a palette index for entry `idx`, given the optional packed `data`.
/// A single-entry palette (or absent `data`) always resolves to index 0.
fn palette_index(
    data: Option<&NbtValue>,
    palette_len: usize,
    min_bits: u32,
    idx: usize,
) -> Option<usize> {
    let bits = bits_for(palette_len, min_bits);
    let data = match data {
        Some(NbtValue::LongArray(d)) if bits > 0 => d,
        _ => return Some(0), // single-entry palette: no data, all index 0
    };
    let per_long = 64 / bits as usize;
    let long_i = idx / per_long;
    let off = (idx % per_long) * bits as usize;
    let raw = *data.get(long_i)? as u64;
    let mask = (1u64 << bits) - 1;
    Some(((raw >> off) & mask) as usize)
}

/// The section `Y` indices present in a chunk.
pub fn section_ys(chunk: &NbtValue) -> Vec<i32> {
    let mut out = Vec::new();
    let secs =
        get(chunk, "sections").or_else(|| get(chunk, "Level").and_then(|l| get(l, "Sections")));
    if let Some(NbtValue::List(secs)) = secs {
        for s in secs {
            if let Some(y) = section_y(s) {
                out.push(y as i32);
            }
        }
    }
    out
}

/// Decode all 4096 block states of the section with index `sy` (YZX order:
/// `idx = ly*256 + lz*16 + lx`). `None` if the section/palette is absent.
pub fn section_blocks(chunk: &NbtValue, sy: i32) -> Option<Vec<BlockState>> {
    let section = section_at(chunk, sy << 4)?;
    let bs = get(section, "block_states")?;
    let palette = as_list(get(bs, "palette")?)?;
    if palette.is_empty() {
        return None;
    }
    let states: Vec<BlockState> = palette.iter().filter_map(block_state).collect();
    if states.len() != palette.len() {
        return None;
    }
    let bits = bits_for(palette.len(), 4);
    let mut out = Vec::with_capacity(4096);
    match get(bs, "data") {
        Some(NbtValue::LongArray(data)) if bits > 0 => {
            let per_long = 64 / bits as usize;
            let mask = (1u64 << bits) - 1;
            for idx in 0..4096usize {
                let li = idx / per_long;
                let off = (idx % per_long) * bits as usize;
                let pi = ((*data.get(li)? as u64) >> off) & mask;
                out.push(states.get(pi as usize)?.clone());
            }
        }
        _ => out.extend(std::iter::repeat_n(states[0].clone(), 4096)),
    }
    Some(out)
}

/// Bits per index: `max(min_bits, ceil(log2(palette_len)))`.
fn bits_for(palette_len: usize, min_bits: u32) -> u32 {
    let needed = if palette_len <= 1 {
        0
    } else {
        usize::BITS - (palette_len - 1).leading_zeros()
    };
    needed.max(min_bits)
}

/// Find the section whose `Y` equals `y >> 4` (arithmetic shift = floor div 16).
fn section_at(chunk: &NbtValue, y: i32) -> Option<&NbtValue> {
    let want = (y >> 4) as i64;
    let sections = match get(chunk, "sections")
        .or_else(|| get(chunk, "Level").and_then(|l| get(l, "Sections")))?
    {
        NbtValue::List(s) => s,
        _ => return None,
    };
    sections.iter().find(|s| section_y(s) == Some(want))
}

fn section_y(s: &NbtValue) -> Option<i64> {
    match get(s, "Y")? {
        NbtValue::Byte(b) => Some(*b as i64),
        NbtValue::Int(i) => Some(*i as i64),
        NbtValue::Short(i) => Some(*i as i64),
        _ => None,
    }
}

fn block_state(entry: &NbtValue) -> Option<BlockState> {
    let name = match get(entry, "Name")? {
        NbtValue::String(s) => s.clone(),
        _ => return None,
    };
    let mut properties = Vec::new();
    if let Some(NbtValue::Compound(props)) = get(entry, "Properties") {
        for (k, v) in props {
            if let NbtValue::String(s) = v {
                properties.push((k.clone(), s.clone()));
            }
        }
        properties.sort();
    }
    Some(BlockState { name, properties })
}

fn get<'a>(v: &'a NbtValue, key: &str) -> Option<&'a NbtValue> {
    match v {
        NbtValue::Compound(m) => m.get(key),
        _ => None,
    }
}
fn as_list(v: &NbtValue) -> Option<&Vec<NbtValue>> {
    match v {
        NbtValue::List(l) => Some(l),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_nbt::{Compound, NbtValue};

    fn named(name: &str) -> NbtValue {
        let mut m = Compound::new();
        m.insert("Name".into(), NbtValue::String(name.into()));
        NbtValue::Compound(m)
    }
    fn compound(pairs: Vec<(&str, NbtValue)>) -> NbtValue {
        let mut m = Compound::new();
        for (k, v) in pairs {
            m.insert(k.into(), v);
        }
        NbtValue::Compound(m)
    }

    #[test]
    fn bits_for_palette() {
        assert_eq!(bits_for(1, 4), 4); // single entry -> min
        assert_eq!(bits_for(16, 4), 4); // 0..15 -> 4 bits
        assert_eq!(bits_for(17, 4), 5);
        assert_eq!(bits_for(3, 1), 2); // biome min 1, needs 2
    }

    #[test]
    fn single_entry_palette_is_all_that_block() {
        // a section of solid stone: palette=[stone], no data
        let section = compound(vec![
            ("Y", NbtValue::Byte(0)),
            (
                "block_states",
                compound(vec![(
                    "palette",
                    NbtValue::List(vec![named("minecraft:stone")]),
                )]),
            ),
        ]);
        let chunk = compound(vec![("sections", NbtValue::List(vec![section]))]);
        assert_eq!(block_at(&chunk, 3, 5, 9).unwrap().name, "minecraft:stone");
        assert_eq!(block_at(&chunk, 0, 15, 0).unwrap().name, "minecraft:stone");
    }

    #[test]
    fn packed_palette_resolves_per_coordinate() {
        // palette [air, stone] -> 4 bits/index (min). Put stone at local (0,0,0)
        // = entry 0 (low 4 bits of long 0), air everywhere else.
        let palette = NbtValue::List(vec![named("minecraft:air"), named("minecraft:stone")]);
        // 4096 entries, 16 per long (4 bits) -> 256 longs. entry 0 = 1 (stone).
        let mut data = vec![0i64; 256];
        data[0] = 1; // index 0 -> palette[1] = stone
        let section = compound(vec![
            ("Y", NbtValue::Byte(0)),
            (
                "block_states",
                compound(vec![
                    ("palette", palette),
                    ("data", NbtValue::LongArray(data)),
                ]),
            ),
        ]);
        let chunk = compound(vec![("sections", NbtValue::List(vec![section]))]);
        assert_eq!(block_at(&chunk, 0, 0, 0).unwrap().name, "minecraft:stone");
        assert_eq!(block_at(&chunk, 1, 0, 0).unwrap().name, "minecraft:air");
    }

    #[test]
    fn biome_single_entry() {
        let section = compound(vec![
            ("Y", NbtValue::Byte(0)),
            (
                "biomes",
                compound(vec![(
                    "palette",
                    NbtValue::List(vec![NbtValue::String("minecraft:plains".into())]),
                )]),
            ),
        ]);
        let chunk = compound(vec![("sections", NbtValue::List(vec![section]))]);
        assert_eq!(
            biome_at(&chunk, 1, 2, 3).as_deref(),
            Some("minecraft:plains")
        );
    }
}
