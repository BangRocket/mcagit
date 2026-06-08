//! `mca-query` — read-only world-state inspection: list players and find
//! entities, block-entities, and signs across a world's region files. Operates
//! on a world directory (and an optional dimension); never mutates anything.

use mca_anvil::{
    biome_at, block_at, codec, section_blocks, section_ys, BlockState, ChunkPos, RegionFile,
};
use mca_nbt::{Compound, NbtValue};
use std::path::{Path, PathBuf};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum QueryError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("anvil error: {0}")]
    Anvil(#[from] mca_anvil::AnvilError),
}
pub type Result<T> = std::result::Result<T, QueryError>;

pub mod render;
pub use render::{render_map, MapInfo};

/// A located player (from `level.dat` host or `playerdata/<uuid>.dat`).
#[derive(Debug, Clone, serde::Serialize)]
pub struct PlayerHit {
    pub id: String,
    pub source: String,
    pub pos: Option<[f64; 3]>,
    pub dimension: Option<String>,
    pub health: Option<f32>,
    pub xp_level: Option<i32>,
}

/// A matched entity.
#[derive(Debug, Clone, serde::Serialize)]
pub struct EntityHit {
    pub id: String,
    pub pos: Option<[f64; 3]>,
}

/// A matched block-entity (sign hits carry their text lines).
#[derive(Debug, Clone, serde::Serialize)]
pub struct BlockEntityHit {
    pub id: String,
    pub x: i32,
    pub y: i32,
    pub z: i32,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub text: Vec<String>,
}

/// What's at a coordinate.
#[derive(Debug, Clone, serde::Serialize)]
pub struct InspectResult {
    pub x: i32,
    pub y: i32,
    pub z: i32,
    pub block: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub properties: Vec<(String, String)>,
    pub biome: Option<String>,
    pub block_entity: Option<String>,
}

/// A point of interest (villager bed, job site, …).
#[derive(Debug, Clone, serde::Serialize)]
pub struct PoiHit {
    pub kind: String,
    pub x: i32,
    pub y: i32,
    pub z: i32,
}

/// A chunk's storage info within a region file.
#[derive(Debug, Clone, serde::Serialize)]
pub struct RegionChunkInfo {
    pub x: i32,
    pub z: i32,
    pub compression: u8,
    pub bytes: usize,
    pub external: bool,
    pub timestamp: i32,
}

/// Per-chunk storage info for a single region (`.mca`) file.
pub fn region_info(path: &Path) -> Result<Vec<RegionChunkInfo>> {
    let data = std::fs::read(path)?;
    let rf = RegionFile::parse(path, &data)?;
    let mut out: Vec<RegionChunkInfo> = rf
        .chunks()
        .map(|c| RegionChunkInfo {
            x: c.pos.x,
            z: c.pos.z,
            compression: c.compression.to_byte(),
            bytes: c.payload.len(),
            external: c.external,
            timestamp: c.timestamp,
        })
        .collect();
    out.sort_by_key(|c| (c.x, c.z));
    Ok(out)
}

/// Read-only queries over a world directory.
pub struct WorldQuery {
    root: PathBuf,
}

impl WorldQuery {
    pub fn new(world: impl Into<PathBuf>) -> Self {
        Self { root: world.into() }
    }

    /// Resolve the directory for a dimension (overworld default; `nether`/`-1` →
    /// `DIM-1`, `end`/`1` → `DIM1`).
    fn dim_dir(&self, dim: Option<&str>) -> PathBuf {
        match dim {
            Some("nether" | "the_nether" | "-1") => self.root.join("DIM-1"),
            Some("end" | "the_end" | "1") => self.root.join("DIM1"),
            _ => self.root.clone(),
        }
    }

    /// All players: the `level.dat` host (`Data.Player`) plus every
    /// `playerdata/<uuid>.dat`.
    pub fn players(&self) -> Result<Vec<PlayerHit>> {
        let mut out = Vec::new();
        let level = self.root.join("level.dat");
        if level.is_file() {
            if let Ok(v) = codec::load_nbt_file(&level) {
                if let Some(player) = get(&v, "Data").and_then(|d| get(d, "Player")) {
                    out.push(player_hit("level.dat".into(), player));
                }
            }
        }
        let pd = self.root.join("playerdata");
        if let Ok(entries) = std::fs::read_dir(&pd) {
            let mut files: Vec<PathBuf> = entries
                .flatten()
                .map(|e| e.path())
                .filter(|p| p.extension().and_then(|s| s.to_str()) == Some("dat"))
                .collect();
            files.sort();
            for f in files {
                if let Ok(v) = codec::load_nbt_file(&f) {
                    let uuid = f
                        .file_stem()
                        .and_then(|s| s.to_str())
                        .unwrap_or("?")
                        .to_string();
                    out.push(player_hit(uuid, &v));
                }
            }
        }
        Ok(out)
    }

    /// Inspect the block, biome, and any block-entity at world coords `(x,y,z)`.
    pub fn inspect(&self, dim: Option<&str>, x: i32, y: i32, z: i32) -> Result<InspectResult> {
        let (cx, cz) = (x >> 4, z >> 4);
        let (rx, rz) = (cx >> 5, cz >> 5);
        let path = self
            .dim_dir(dim)
            .join("region")
            .join(format!("r.{rx}.{rz}.mca"));
        let mut r = InspectResult {
            x,
            y,
            z,
            block: None,
            properties: Vec::new(),
            biome: None,
            block_entity: None,
        };
        if !path.is_file() {
            return Ok(r);
        }
        let bytes = std::fs::read(&path)?;
        let rf = RegionFile::parse(&path, &bytes)?;
        let Some(raw) = rf.get(ChunkPos::new(cx, cz)) else {
            return Ok(r);
        };
        let Ok(chunk) = codec::decode(raw) else {
            return Ok(r);
        };
        if let Some(bs) = block_at(&chunk, x, y, z) {
            r.block = Some(bs.name);
            r.properties = bs.properties;
        }
        r.biome = biome_at(&chunk, x, y, z);
        let list =
            chunk_get(&chunk, "block_entities").or_else(|| chunk_get(&chunk, "TileEntities"));
        if let Some(NbtValue::List(items)) = list {
            for be in items {
                if get_int(be, "x") == Some(x)
                    && get_int(be, "y") == Some(y)
                    && get_int(be, "z") == Some(z)
                {
                    r.block_entity = get_str(be, "id").map(str::to_string);
                }
            }
        }
        Ok(r)
    }

    /// Find entities whose `id` matches `id_filter` (all entities if `None`).
    pub fn find_entities(
        &self,
        dim: Option<&str>,
        id_filter: Option<&str>,
    ) -> Result<Vec<EntityHit>> {
        let mut out = Vec::new();
        // 1.17+ stores entities in entities/*.mca; older worlds keep them in the
        // region chunk under "Entities" (or "Level.Entities").
        for sub in ["entities", "region"] {
            for root in self.read_chunks(&self.dim_dir(dim).join(sub))? {
                if let Some(NbtValue::List(items)) = chunk_get(&root, "Entities") {
                    for e in items {
                        if let Some(id) = get_str(e, "id") {
                            if id_matches(id, id_filter) {
                                out.push(EntityHit {
                                    id: id.to_string(),
                                    pos: get_pos(e, "Pos"),
                                });
                            }
                        }
                    }
                }
            }
        }
        Ok(out)
    }

    /// Find block-entities whose `id` matches `id_filter` (all if `None`).
    pub fn find_block_entities(
        &self,
        dim: Option<&str>,
        id_filter: Option<&str>,
    ) -> Result<Vec<BlockEntityHit>> {
        self.scan_block_entities(dim, &|id| id_matches(id, id_filter))
    }

    /// Find sign block-entities, extracting their text lines.
    pub fn find_signs(&self, dim: Option<&str>) -> Result<Vec<BlockEntityHit>> {
        self.scan_block_entities(dim, &|id| id.contains("sign"))
    }

    /// Points of interest (villager beds, job sites, etc.) from `poi/*.mca`.
    pub fn poi(&self, dim: Option<&str>) -> Result<Vec<PoiHit>> {
        let mut out = Vec::new();
        for root in self.read_chunks(&self.dim_dir(dim).join("poi"))? {
            let Some(NbtValue::Compound(sections)) = get(&root, "Sections") else {
                continue;
            };
            for (_y, sec) in sections {
                if let Some(NbtValue::List(records)) = get(sec, "Records") {
                    for rec in records {
                        if let (Some(kind), Some(NbtValue::IntArray(p))) =
                            (get_str(rec, "type"), get(rec, "pos"))
                        {
                            if p.len() >= 3 {
                                out.push(PoiHit {
                                    kind: kind.to_string(),
                                    x: p[0],
                                    y: p[1],
                                    z: p[2],
                                });
                            }
                        }
                    }
                }
            }
        }
        Ok(out)
    }

    fn scan_block_entities(
        &self,
        dim: Option<&str>,
        keep: &dyn Fn(&str) -> bool,
    ) -> Result<Vec<BlockEntityHit>> {
        let mut out = Vec::new();
        for root in self.read_chunks(&self.dim_dir(dim).join("region"))? {
            // 1.18+ "block_entities" at chunk root; older "Level.TileEntities".
            let list =
                chunk_get(&root, "block_entities").or_else(|| chunk_get(&root, "TileEntities"));
            if let Some(NbtValue::List(items)) = list {
                for be in items {
                    if let Some(id) = get_str(be, "id") {
                        if keep(id) {
                            out.push(BlockEntityHit {
                                id: id.to_string(),
                                x: get_int(be, "x").unwrap_or(0),
                                y: get_int(be, "y").unwrap_or(0),
                                z: get_int(be, "z").unwrap_or(0),
                                text: sign_text(be),
                            });
                        }
                    }
                }
            }
        }
        Ok(out)
    }

    /// Decode every chunk's root NBT from each `*.mca` in `dir`.
    fn read_chunks(&self, dir: &Path) -> Result<Vec<NbtValue>> {
        let mut out = Vec::new();
        let Ok(entries) = std::fs::read_dir(dir) else {
            return Ok(out);
        };
        for e in entries.flatten() {
            let p = e.path();
            if p.extension().and_then(|s| s.to_str()) != Some("mca") {
                continue;
            }
            let bytes = std::fs::read(&p)?;
            let rf = RegionFile::parse(&p, &bytes)?;
            for c in rf.chunks() {
                if let Ok(v) = codec::decode(c) {
                    out.push(v);
                }
            }
        }
        Ok(out)
    }
}

/// A block-level change between two worlds (grief detector output).
#[derive(Debug, Clone, serde::Serialize)]
pub struct BlockChange {
    pub x: i32,
    pub y: i32,
    pub z: i32,
    pub old: Option<String>,
    pub new: Option<String>,
}

/// Block-level changes turning `old_world` into `new_world` for one dimension:
/// compares chunks present in both worlds, decoding paletted block states and
/// reporting each differing coordinate (the grief detector). Chunks present in
/// only one world are not reported.
pub fn where_changed(
    old_world: &Path,
    new_world: &Path,
    dim: Option<&str>,
) -> Result<Vec<BlockChange>> {
    let old_dir = dim_region(old_world, dim);
    let new_dir = dim_region(new_world, dim);
    let new_files: std::collections::HashSet<String> = list_mca(&new_dir).into_iter().collect();
    let mut out = Vec::new();
    for name in list_mca(&old_dir) {
        if !new_files.contains(&name) {
            continue;
        }
        let ob = std::fs::read(old_dir.join(&name))?;
        let nb = std::fs::read(new_dir.join(&name))?;
        let orf = RegionFile::parse(&old_dir.join(&name), &ob)?;
        let nrf = RegionFile::parse(&new_dir.join(&name), &nb)?;
        let positions: Vec<ChunkPos> = orf.chunks().map(|c| c.pos).collect();
        for cp in positions {
            let (Some(oc_raw), Some(nc_raw)) = (orf.get(cp), nrf.get(cp)) else {
                continue;
            };
            if oc_raw.payload_equals(nc_raw) {
                continue;
            }
            let (Ok(oc), Ok(nc)) = (codec::decode(oc_raw), codec::decode(nc_raw)) else {
                continue;
            };
            let mut ys = section_ys(&oc);
            ys.extend(section_ys(&nc));
            ys.sort_unstable();
            ys.dedup();
            for sy in ys {
                let ob = section_blocks(&oc, sy);
                let nb = section_blocks(&nc, sy);
                if ob == nb {
                    continue;
                }
                for idx in 0..4096usize {
                    let o = ob.as_ref().and_then(|v| v.get(idx));
                    let n = nb.as_ref().and_then(|v| v.get(idx));
                    if o != n {
                        let (ly, rem) = (idx / 256, idx % 256);
                        out.push(BlockChange {
                            x: cp.x * 16 + (rem % 16) as i32,
                            y: sy * 16 + ly as i32,
                            z: cp.z * 16 + (rem / 16) as i32,
                            old: o.map(fmt_block),
                            new: n.map(fmt_block),
                        });
                    }
                }
            }
        }
    }
    out.sort_by_key(|c| (c.x, c.y, c.z));
    Ok(out)
}

fn dim_region(world: &Path, dim: Option<&str>) -> PathBuf {
    let base = match dim {
        Some("nether" | "the_nether" | "-1") => world.join("DIM-1"),
        Some("end" | "the_end" | "1") => world.join("DIM1"),
        _ => world.to_path_buf(),
    };
    base.join("region")
}

fn list_mca(dir: &Path) -> Vec<String> {
    let mut v = Vec::new();
    if let Ok(entries) = std::fs::read_dir(dir) {
        for e in entries.flatten() {
            let p = e.path();
            if p.extension().and_then(|s| s.to_str()) == Some("mca") {
                if let Some(n) = p.file_name().and_then(|s| s.to_str()) {
                    v.push(n.to_string());
                }
            }
        }
    }
    v
}

fn fmt_block(bs: &BlockState) -> String {
    if bs.properties.is_empty() {
        bs.name.clone()
    } else {
        let p: Vec<String> = bs
            .properties
            .iter()
            .map(|(k, v)| format!("{k}={v}"))
            .collect();
        format!("{}[{}]", bs.name, p.join(","))
    }
}

// ---- field accessors ----

fn as_compound(v: &NbtValue) -> Option<&Compound> {
    match v {
        NbtValue::Compound(m) => Some(m),
        _ => None,
    }
}
fn get<'a>(v: &'a NbtValue, key: &str) -> Option<&'a NbtValue> {
    as_compound(v)?.get(key)
}
/// Field lookup that tolerates the pre-1.18 `Level` wrapper.
fn chunk_get<'a>(root: &'a NbtValue, key: &str) -> Option<&'a NbtValue> {
    get(root, key).or_else(|| get(root, "Level").and_then(|l| get(l, key)))
}
fn get_str<'a>(v: &'a NbtValue, key: &str) -> Option<&'a str> {
    match get(v, key) {
        Some(NbtValue::String(s)) => Some(s),
        _ => None,
    }
}
fn get_int(v: &NbtValue, key: &str) -> Option<i32> {
    match get(v, key)? {
        NbtValue::Int(x) => Some(*x),
        NbtValue::Long(x) => Some(*x as i32),
        NbtValue::Short(x) => Some(*x as i32),
        NbtValue::Byte(x) => Some(*x as i32),
        _ => None,
    }
}
fn get_float(v: &NbtValue, key: &str) -> Option<f32> {
    match get(v, key)? {
        NbtValue::Float(x) => Some(*x),
        NbtValue::Double(x) => Some(*x as f32),
        _ => None,
    }
}
fn get_pos(v: &NbtValue, key: &str) -> Option<[f64; 3]> {
    let NbtValue::List(l) = get(v, key)? else {
        return None;
    };
    if l.len() < 3 {
        return None;
    }
    let f = |i: usize| match &l[i] {
        NbtValue::Double(d) => Some(*d),
        NbtValue::Float(d) => Some(*d as f64),
        _ => None,
    };
    Some([f(0)?, f(1)?, f(2)?])
}

fn player_hit(source: String, v: &NbtValue) -> PlayerHit {
    PlayerHit {
        id: source.clone(),
        source,
        pos: get_pos(v, "Pos"),
        dimension: get_str(v, "Dimension").map(str::to_string),
        health: get_float(v, "Health"),
        xp_level: get_int(v, "XpLevel"),
    }
}

/// `None` filter matches all; otherwise exact, `minecraft:`-namespaced, or
/// suffix match (so `find entity zombie` matches `minecraft:zombie`).
fn id_matches(id: &str, filter: Option<&str>) -> bool {
    match filter {
        None => true,
        Some(f) => id == f || id == format!("minecraft:{f}") || id.ends_with(&format!(":{f}")),
    }
}

/// Extract sign text: 1.20+ `front_text`/`back_text` `messages`, else legacy
/// `Text1..Text4`.
fn sign_text(be: &NbtValue) -> Vec<String> {
    let mut out = Vec::new();
    for side in ["front_text", "back_text"] {
        if let Some(NbtValue::List(msgs)) = get(be, side).and_then(|s| get(s, "messages")) {
            for m in msgs {
                if let NbtValue::String(s) = m {
                    out.push(s.clone());
                }
            }
        }
    }
    if out.is_empty() {
        for k in ["Text1", "Text2", "Text3", "Text4"] {
            if let Some(s) = get_str(be, k) {
                out.push(s.to_string());
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_anvil::{ChunkCompression, ChunkPos, RawChunk, RegionWriter};
    use mca_nbt::Compound;

    fn comp(pairs: Vec<(&str, NbtValue)>) -> NbtValue {
        let mut m = Compound::new();
        for (k, v) in pairs {
            m.insert(k.into(), v);
        }
        NbtValue::Compound(m)
    }
    fn pos(x: f64, y: f64, z: f64) -> NbtValue {
        NbtValue::List(vec![
            NbtValue::Double(x),
            NbtValue::Double(y),
            NbtValue::Double(z),
        ])
    }
    fn write_chunk(path: &Path, root: NbtValue) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let payload = codec::encode(&root, ChunkCompression::ZLib).unwrap();
        let chunk = RawChunk {
            pos: ChunkPos::new(0, 0),
            compression: ChunkCompression::ZLib,
            payload,
            external: false,
            timestamp: 0,
        };
        RegionWriter::write(path, std::slice::from_ref(&chunk)).unwrap();
    }

    #[test]
    fn players_find_entities_and_signs() {
        let d = tempfile::tempdir().unwrap();
        let w = d.path().join("World");

        // a player file
        std::fs::create_dir_all(w.join("playerdata")).unwrap();
        let player = comp(vec![
            ("Pos", pos(1.0, 64.0, -3.0)),
            ("Dimension", NbtValue::String("minecraft:overworld".into())),
            ("Health", NbtValue::Float(18.0)),
            ("XpLevel", NbtValue::Int(7)),
        ]);
        codec::save_nbt_file(
            &w.join("playerdata")
                .join("0bde0058-eef6-4855-b90d-470118f9a8c7.dat"),
            &player,
            ChunkCompression::GZip,
        )
        .unwrap();

        // a region chunk with a block-entity sign
        let sign = comp(vec![
            ("id", NbtValue::String("minecraft:sign".into())),
            ("x", NbtValue::Int(10)),
            ("y", NbtValue::Int(70)),
            ("z", NbtValue::Int(-2)),
            (
                "front_text",
                comp(vec![(
                    "messages",
                    NbtValue::List(vec![
                        NbtValue::String("hello".into()),
                        NbtValue::String("world".into()),
                    ]),
                )]),
            ),
        ]);
        write_chunk(
            &w.join("region").join("r.0.0.mca"),
            comp(vec![("block_entities", NbtValue::List(vec![sign]))]),
        );

        // an entity in entities/*.mca
        let zombie = comp(vec![
            ("id", NbtValue::String("minecraft:zombie".into())),
            ("Pos", pos(5.0, 65.0, 5.0)),
        ]);
        write_chunk(
            &w.join("entities").join("r.0.0.mca"),
            comp(vec![("Entities", NbtValue::List(vec![zombie]))]),
        );

        let q = WorldQuery::new(&w);

        let players = q.players().unwrap();
        assert_eq!(players.len(), 1);
        assert_eq!(players[0].pos, Some([1.0, 64.0, -3.0]));
        assert_eq!(players[0].health, Some(18.0));

        let zombies = q.find_entities(None, Some("zombie")).unwrap();
        assert_eq!(zombies.len(), 1);
        assert_eq!(zombies[0].id, "minecraft:zombie");
        assert_eq!(zombies[0].pos, Some([5.0, 65.0, 5.0]));
        assert!(q.find_entities(None, Some("creeper")).unwrap().is_empty());

        let signs = q.find_signs(None).unwrap();
        assert_eq!(signs.len(), 1);
        assert_eq!(signs[0].x, 10);
        assert_eq!(signs[0].text, vec!["hello", "world"]);

        let all_be = q.find_block_entities(None, None).unwrap();
        assert_eq!(all_be.len(), 1);
    }

    #[test]
    fn inspect_resolves_block_biome_and_block_entity() {
        let d = tempfile::tempdir().unwrap();
        let w = d.path().join("World");
        let mut data = vec![0i64; 256];
        data[0] = 1; // local (0,0,0) -> palette[1] = stone
        let section = comp(vec![
            ("Y", NbtValue::Byte(0)),
            (
                "block_states",
                comp(vec![
                    (
                        "palette",
                        NbtValue::List(vec![
                            comp(vec![("Name", NbtValue::String("minecraft:air".into()))]),
                            comp(vec![("Name", NbtValue::String("minecraft:stone".into()))]),
                        ]),
                    ),
                    ("data", NbtValue::LongArray(data)),
                ]),
            ),
            (
                "biomes",
                comp(vec![(
                    "palette",
                    NbtValue::List(vec![NbtValue::String("minecraft:plains".into())]),
                )]),
            ),
        ]);
        let be = comp(vec![
            ("id", NbtValue::String("minecraft:chest".into())),
            ("x", NbtValue::Int(0)),
            ("y", NbtValue::Int(0)),
            ("z", NbtValue::Int(0)),
        ]);
        write_chunk(
            &w.join("region").join("r.0.0.mca"),
            comp(vec![
                ("sections", NbtValue::List(vec![section])),
                ("block_entities", NbtValue::List(vec![be])),
            ]),
        );
        let r = WorldQuery::new(&w).inspect(None, 0, 0, 0).unwrap();
        assert_eq!(r.block.as_deref(), Some("minecraft:stone"));
        assert_eq!(r.biome.as_deref(), Some("minecraft:plains"));
        assert_eq!(r.block_entity.as_deref(), Some("minecraft:chest"));
        // air at a neighbor, no block-entity there
        let n = WorldQuery::new(&w).inspect(None, 1, 0, 0).unwrap();
        assert_eq!(n.block.as_deref(), Some("minecraft:air"));
        assert_eq!(n.block_entity, None);
    }

    #[test]
    fn where_changed_reports_block_changes() {
        let d = tempfile::tempdir().unwrap();
        let old = d.path().join("old");
        let new = d.path().join("new");
        let section = |bs: NbtValue| {
            comp(vec![(
                "sections",
                NbtValue::List(vec![comp(vec![
                    ("Y", NbtValue::Byte(0)),
                    ("block_states", bs),
                ])]),
            )])
        };
        let air = comp(vec![("Name", NbtValue::String("minecraft:air".into()))]);
        let stone = comp(vec![("Name", NbtValue::String("minecraft:stone".into()))]);
        // old: all air
        write_chunk(
            &old.join("region").join("r.0.0.mca"),
            section(comp(vec![("palette", NbtValue::List(vec![air.clone()]))])),
        );
        // new: stone at local (0,0,0)
        let mut data = vec![0i64; 256];
        data[0] = 1;
        write_chunk(
            &new.join("region").join("r.0.0.mca"),
            section(comp(vec![
                ("palette", NbtValue::List(vec![air, stone])),
                ("data", NbtValue::LongArray(data)),
            ])),
        );

        let changes = where_changed(&old, &new, None).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!((changes[0].x, changes[0].y, changes[0].z), (0, 0, 0));
        assert_eq!(changes[0].old.as_deref(), Some("minecraft:air"));
        assert_eq!(changes[0].new.as_deref(), Some("minecraft:stone"));
    }

    #[test]
    fn poi_and_region_info() {
        let d = tempfile::tempdir().unwrap();
        let w = d.path().join("World");
        let record = comp(vec![
            ("type", NbtValue::String("minecraft:home".into())),
            ("pos", NbtValue::IntArray(vec![10, 64, -5])),
        ]);
        // poi chunk: Sections is a compound keyed by section-Y string.
        let poi_root = comp(vec![(
            "Sections",
            comp(vec![(
                "0",
                comp(vec![("Records", NbtValue::List(vec![record]))]),
            )]),
        )]);
        let poi_path = w.join("poi").join("r.0.0.mca");
        write_chunk(&poi_path, poi_root);

        let pois = WorldQuery::new(&w).poi(None).unwrap();
        assert_eq!(pois.len(), 1);
        assert_eq!(pois[0].kind, "minecraft:home");
        assert_eq!((pois[0].x, pois[0].y, pois[0].z), (10, 64, -5));

        let info = region_info(&poi_path).unwrap();
        assert_eq!(info.len(), 1);
        assert_eq!((info[0].x, info[0].z), (0, 0));
    }
}
