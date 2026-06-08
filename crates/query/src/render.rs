//! Top-down surface map of a materialized world → PNG. Ported from the .NET
//! mcahub `MapRenderer`: for each block column, scan sections top-down for the
//! first non-air block, map it to a color, then apply north-facing height
//! shading so relief reads. Targets the 1.18+ chunk layout. Self-contained PNG
//! writer (zlib via flate2 — no image dependency).

use crate::{QueryError, Result};
use flate2::write::ZlibEncoder;
use flate2::Compression;
use mca_anvil::{codec, section_blocks, section_ys, RegionFile};
use mca_nbt::NbtValue;
use std::collections::HashMap;
use std::io::Write;
use std::path::Path;
use std::sync::OnceLock;

/// Dimensions + chunk count of a rendered map.
#[derive(Debug, Clone, serde::Serialize)]
pub struct MapInfo {
    pub width: u32,
    pub height: u32,
    pub chunks: usize,
    pub truncated: bool,
}

const MAX_SIDE_CHUNKS: i64 = 160; // cap the rendered span at 160×160 chunks (2560px)
const BG: [u8; 3] = [17, 22, 28]; // slate, matches the hub UI background

struct Surface {
    r: [u8; 256],
    g: [u8; 256],
    b: [u8; 256],
    y: [i32; 256],
}

/// Render the overworld (or a dimension) of `world` to PNG bytes + [`MapInfo`].
pub fn render_map(
    world: &Path,
    dim: Option<&str>,
    max_chunks: usize,
) -> Result<(Vec<u8>, MapInfo)> {
    let region_dir = crate::dim_region(world, dim);
    // The Nether has a bedrock roof (~y122-127); cap the scan below it.
    let max_scan_y: i32 = match dim {
        Some("nether" | "the_nether" | "-1") => 120,
        _ => i32::MAX,
    };

    let mut surfaces: HashMap<(i32, i32), Surface> = HashMap::new();
    let (mut min_cx, mut min_cz, mut max_cx, mut max_cz) = (i32::MAX, i32::MAX, i32::MIN, i32::MIN);
    let mut cap_hit = false;

    let mut files: Vec<std::path::PathBuf> = std::fs::read_dir(&region_dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.extension().and_then(|s| s.to_str()) == Some("mca")
                && p.file_name()
                    .and_then(|s| s.to_str())
                    .is_some_and(|n| n.starts_with("r."))
        })
        .collect();
    files.sort();

    for file in files {
        if cap_hit {
            break;
        }
        let Ok(bytes) = std::fs::read(&file) else {
            continue;
        };
        let Ok(rf) = RegionFile::parse(&file, &bytes) else {
            continue;
        };
        let positions: Vec<_> = rf.chunks().map(|c| c.pos).collect();
        for cp in positions {
            if surfaces.len() >= max_chunks {
                cap_hit = true;
                break;
            }
            let Some(raw) = rf.get(cp) else { continue };
            let Ok(chunk) = codec::decode(raw) else {
                continue;
            };
            if let Some(s) = surface_of(&chunk, max_scan_y) {
                min_cx = min_cx.min(cp.x);
                max_cx = max_cx.max(cp.x);
                min_cz = min_cz.min(cp.z);
                max_cz = max_cz.max(cp.z);
                surfaces.insert((cp.x, cp.z), s);
            }
        }
    }

    if surfaces.is_empty() {
        return Ok((
            encode_png(1, 1, &BG)?,
            MapInfo {
                width: 0,
                height: 0,
                chunks: 0,
                truncated: false,
            },
        ));
    }

    // Clamp a runaway span (region filenames are untrusted; compute in i64).
    let mut truncated = cap_hit;
    if max_cx as i64 - min_cx as i64 + 1 > MAX_SIDE_CHUNKS {
        let c = (min_cx as i64 + max_cx as i64) / 2;
        min_cx = (c - MAX_SIDE_CHUNKS / 2) as i32;
        max_cx = min_cx + MAX_SIDE_CHUNKS as i32 - 1;
        truncated = true;
    }
    if max_cz as i64 - min_cz as i64 + 1 > MAX_SIDE_CHUNKS {
        let c = (min_cz as i64 + max_cz as i64) / 2;
        min_cz = (c - MAX_SIDE_CHUNKS / 2) as i32;
        max_cz = min_cz + MAX_SIDE_CHUNKS as i32 - 1;
        truncated = true;
    }

    let w = ((max_cx - min_cx + 1) * 16) as usize;
    let h = ((max_cz - min_cz + 1) * 16) as usize;
    let mut rgb = vec![0u8; w * h * 3];
    let mut height = vec![i32::MIN; w * h];
    for i in 0..w * h {
        rgb[i * 3] = BG[0];
        rgb[i * 3 + 1] = BG[1];
        rgb[i * 3 + 2] = BG[2];
    }

    let mut placed = 0;
    for ((cx, cz), s) in &surfaces {
        let base_col = (cx - min_cx) * 16;
        let base_row = (cz - min_cz) * 16;
        if base_col < 0 || base_row < 0 || base_col as usize + 16 > w || base_row as usize + 16 > h
        {
            continue;
        }
        let (base_col, base_row) = (base_col as usize, base_row as usize);
        for lz in 0..16usize {
            for lx in 0..16usize {
                let cell = lz * 16 + lx;
                if s.y[cell] == i32::MIN {
                    continue;
                }
                let p = (base_row + lz) * w + (base_col + lx);
                rgb[p * 3] = s.r[cell];
                rgb[p * 3 + 1] = s.g[cell];
                rgb[p * 3 + 2] = s.b[cell];
                height[p] = s.y[cell];
            }
        }
        placed += 1;
    }

    north_shade(&mut rgb, &height, w, h);
    let png = encode_png(w as u32, h as u32, &rgb)?;
    Ok((
        png,
        MapInfo {
            width: w as u32,
            height: h as u32,
            chunks: placed,
            truncated,
        },
    ))
}

fn surface_of(chunk: &NbtValue, max_scan_y: i32) -> Option<Surface> {
    let mut ys = section_ys(chunk);
    ys.retain(|&y| (-4..=19).contains(&y) && y * 16 <= max_scan_y);
    let mut decoded: Vec<(i32, Vec<mca_anvil::BlockState>)> = ys
        .into_iter()
        .filter_map(|sy| section_blocks(chunk, sy).map(|b| (sy, b)))
        .collect();
    if decoded.is_empty() {
        return None;
    }
    decoded.sort_by_key(|(sy, _)| std::cmp::Reverse(*sy)); // tallest section first

    let mut s = Surface {
        r: [0; 256],
        g: [0; 256],
        b: [0; 256],
        y: [i32::MIN; 256],
    };
    for lz in 0..16usize {
        for lx in 0..16usize {
            let out_cell = lz * 16 + lx;
            'col: for (sy, blocks) in &decoded {
                for ly in (0..16i32).rev() {
                    if sy * 16 + ly > max_scan_y {
                        continue;
                    }
                    let bs = &blocks[(ly as usize) * 256 + lz * 16 + lx];
                    let name = strip(&bs.name);
                    if is_air(name) {
                        continue;
                    }
                    let (r, g, b) = color_of(name);
                    s.r[out_cell] = r;
                    s.g[out_cell] = g;
                    s.b[out_cell] = b;
                    s.y[out_cell] = sy * 16 + ly;
                    break 'col;
                }
            }
        }
    }
    Some(s)
}

fn north_shade(rgb: &mut [u8], height: &[i32], w: usize, h: usize) {
    let clamp = |v: f64| -> u8 { v.clamp(0.0, 255.0) as u8 };
    for row in (1..h).rev() {
        for col in 0..w {
            let p = row * w + col;
            let n = (row - 1) * w + col;
            if height[p] == i32::MIN || height[n] == i32::MIN {
                continue;
            }
            let d = height[p] - height[n];
            if d == 0 {
                continue;
            }
            let f = if d > 0 { 1.12 } else { 0.86 };
            rgb[p * 3] = clamp(rgb[p * 3] as f64 * f);
            rgb[p * 3 + 1] = clamp(rgb[p * 3 + 1] as f64 * f);
            rgb[p * 3 + 2] = clamp(rgb[p * 3 + 2] as f64 * f);
        }
    }
}

fn strip(name: &str) -> &str {
    name.strip_prefix("minecraft:").unwrap_or(name)
}

fn is_air(n: &str) -> bool {
    matches!(n, "air" | "cave_air" | "void_air")
}

fn color_of(n: &str) -> (u8, u8, u8) {
    let c = |s: &str| n.contains(s);
    if c("water") {
        (63, 118, 228)
    } else if c("lava") {
        (224, 108, 29)
    } else if matches!(
        n,
        "grass_block" | "grass" | "tall_grass" | "fern" | "large_fern"
    ) || c("moss")
    {
        (98, 160, 75)
    } else if c("leaves") {
        (62, 124, 49)
    } else if n.ends_with("log") || c("planks") || c("wood") || c("stem") {
        (120, 90, 55)
    } else if c("sandstone") {
        (199, 182, 130)
    } else if c("sand") {
        (219, 205, 158)
    } else if c("podzol")
        || c("mud")
        || matches!(n, "coarse_dirt" | "rooted_dirt")
        || c("dirt")
        || c("farmland")
        || c("path")
    {
        (134, 96, 67)
    } else if n == "powder_snow" || c("snow") {
        (245, 247, 250)
    } else if c("packed_ice") || c("ice") {
        (150, 190, 240)
    } else if c("clay") {
        (160, 166, 179)
    } else if c("gravel") {
        (130, 127, 124)
    } else if c("deepslate") {
        (70, 70, 75)
    } else if c("blackstone") {
        (42, 40, 46)
    } else if c("basalt") {
        (78, 78, 84)
    } else if c("obsidian") {
        (24, 20, 38)
    } else if c("netherrack") || c("nether_wart") {
        (97, 38, 38)
    } else if c("bedrock") {
        (40, 40, 40)
    } else if c("cobblestone")
        || c("stone")
        || c("andesite")
        || c("granite")
        || c("diorite")
        || n == "tuff"
    {
        (122, 122, 122)
    } else if c("gold") {
        (232, 206, 99)
    } else if c("diamond") {
        (110, 221, 213)
    } else if c("coal") {
        (54, 54, 58)
    } else if c("glass") {
        (200, 225, 235)
    } else if c("brick") {
        (150, 90, 80)
    } else {
        color_from_name(n) // wool/concrete/terracotta/unknown → stable muted color
    }
}

fn color_from_name(n: &str) -> (u8, u8, u8) {
    let mut h: i32 = 0;
    for c in n.chars() {
        h = h.wrapping_mul(31).wrapping_add(c as i32);
    }
    let x = h as u32;
    (
        80 + (x & 0x4F) as u8,
        80 + ((x >> 6) & 0x4F) as u8,
        80 + ((x >> 12) & 0x4F) as u8,
    )
}

// ---- minimal PNG writer (RGB truecolor, filter None) ----

fn encode_png(w: u32, h: u32, rgb: &[u8]) -> Result<Vec<u8>> {
    let mut out = vec![137, 80, 78, 71, 13, 10, 26, 10];
    let mut ihdr = Vec::with_capacity(13);
    ihdr.extend_from_slice(&w.to_be_bytes());
    ihdr.extend_from_slice(&h.to_be_bytes());
    ihdr.extend_from_slice(&[8, 2, 0, 0, 0]); // 8-bit, RGB, deflate, filter-none, no-interlace
    write_chunk(&mut out, b"IHDR", &ihdr);

    let stride = w as usize * 3;
    let mut raw = Vec::with_capacity(h as usize * (1 + stride));
    for y in 0..h as usize {
        raw.push(0); // per-scanline filter type: None
        raw.extend_from_slice(&rgb[y * stride..y * stride + stride]);
    }
    let mut enc = ZlibEncoder::new(Vec::new(), Compression::default());
    enc.write_all(&raw).map_err(QueryError::Io)?;
    let comp = enc.finish().map_err(QueryError::Io)?;
    write_chunk(&mut out, b"IDAT", &comp);
    write_chunk(&mut out, b"IEND", &[]);
    Ok(out)
}

fn write_chunk(out: &mut Vec<u8>, typ: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(typ);
    out.extend_from_slice(data);
    let mut crc = 0xFFFF_FFFFu32;
    let table = crc_table();
    for &x in typ.iter().chain(data) {
        crc = table[((crc ^ x as u32) & 0xFF) as usize] ^ (crc >> 8);
    }
    out.extend_from_slice(&(crc ^ 0xFFFF_FFFF).to_be_bytes());
}

fn crc_table() -> &'static [u32; 256] {
    static TABLE: OnceLock<[u32; 256]> = OnceLock::new();
    TABLE.get_or_init(|| {
        let mut t = [0u32; 256];
        for (i, slot) in t.iter_mut().enumerate() {
            let mut c = i as u32;
            for _ in 0..8 {
                c = if c & 1 != 0 {
                    0xEDB8_8320 ^ (c >> 1)
                } else {
                    c >> 1
                };
            }
            *slot = c;
        }
        t
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn png_header_is_valid() {
        let png = encode_png(2, 2, &[255; 12]).unwrap();
        assert_eq!(&png[..8], &[137, 80, 78, 71, 13, 10, 26, 10]);
        assert_eq!(&png[12..16], b"IHDR");
        // width/height encode big-endian after the 8-byte len+type
        assert_eq!(&png[16..24], &[0, 0, 0, 2, 0, 0, 0, 2]);
    }

    #[test]
    fn color_palette_known_blocks() {
        assert_eq!(color_of(strip("minecraft:water")), (63, 118, 228));
        assert_eq!(color_of(strip("minecraft:snow_block")), (245, 247, 250));
        assert_eq!(color_of("stone"), (122, 122, 122));
        assert!(is_air("air") && is_air("cave_air") && !is_air("stone"));
    }
}
