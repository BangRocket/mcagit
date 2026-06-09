//! Anvil region container (`r.X.Z.mca`): the 8 KiB header + sector-aligned bodies.

use crate::chunk::{ChunkPos, RawChunk};
use crate::compression::ChunkCompression;
use crate::{AnvilError, Result};
use std::collections::HashMap;
use std::path::Path;

const SECTOR: usize = 4096;
const MAX_INLINE_SECTORS: usize = 255; // sector count is a single header byte

/// Cap on an external `.mcc` body read — the *compressed* payload, whose
/// inflate is separately bounded ([`crate::compression`]); mirrors that bound
/// so a hostile `.mcc` can't become an unbounded read into memory.
pub const MAX_EXTERNAL_CHUNK: u64 = 128 * 1024 * 1024;

/// Writes a valid region file from a set of chunks. Chunks are laid out sorted
/// by region index; oversized chunks (> 255 sectors) spill to an external
/// `c.X.Z.mcc` next to the region with the `0x80` bit set.
pub struct RegionWriter;

impl RegionWriter {
    pub fn write(path: &Path, chunks: &[RawChunk]) -> Result<()> {
        if let Some(dir) = path.parent() {
            if !dir.as_os_str().is_empty() {
                std::fs::create_dir_all(dir)?;
            }
        }
        let dir = path.parent().unwrap_or_else(|| Path::new("."));

        let mut ordered: Vec<&RawChunk> = chunks.iter().collect();
        ordered.sort_by_key(|c| c.pos.region_index());

        let mut header = vec![0u8; SECTOR * 2];
        let mut bodies: Vec<u8> = Vec::new();
        let mut offset_sectors: usize = 2; // bodies start right after the 8 KiB header

        for ch in ordered {
            let needed = (5 + ch.payload.len()).div_ceil(SECTOR);
            let comp_byte;
            let inline: &[u8];
            let sectors;
            if needed > MAX_INLINE_SECTORS {
                // Spill to external .mcc; the inline body becomes just the header byte.
                let mcc = dir.join(format!("c.{}.{}.mcc", ch.pos.x, ch.pos.z));
                std::fs::write(&mcc, &ch.payload)?;
                comp_byte = ch.compression.to_byte() | 0x80;
                inline = &[];
                sectors = 1;
            } else {
                comp_byte = ch.compression.to_byte();
                inline = &ch.payload;
                sectors = needed;
            }

            let mut body = vec![0u8; sectors * SECTOR];
            // length includes the 1 compression byte
            body[0..4].copy_from_slice(&((inline.len() as u32) + 1).to_be_bytes());
            body[4] = comp_byte;
            body[5..5 + inline.len()].copy_from_slice(inline);
            bodies.extend_from_slice(&body);

            let e = ch.pos.region_index() * 4;
            header[e] = (offset_sectors >> 16) as u8;
            header[e + 1] = (offset_sectors >> 8) as u8;
            header[e + 2] = offset_sectors as u8;
            header[e + 3] = sectors as u8;
            let t = SECTOR + e;
            header[t..t + 4].copy_from_slice(&ch.timestamp.to_be_bytes());

            offset_sectors += sectors;
        }

        let mut out = header;
        out.extend_from_slice(&bodies);
        std::fs::write(path, out)?;
        Ok(())
    }
}

/// Reader for the Anvil region container. Parses the 8 KiB header and exposes
/// each present chunk's raw compressed bytes (external `.mcc` bodies are loaded
/// from beside the region file).
pub struct RegionFile {
    pub region_x: i32,
    pub region_z: i32,
    chunks: HashMap<ChunkPos, RawChunk>,
}

impl RegionFile {
    /// Parse a region file at `path` (coordinates come from the `r.X.Z.mca` name).
    pub fn open(path: &Path) -> Result<Self> {
        let bytes = std::fs::read(path)?;
        Self::parse(path, &bytes)
    }

    /// Parse already-loaded region `bytes` (lets a caller avoid a re-read).
    pub fn parse(path: &Path, bytes: &[u8]) -> Result<Self> {
        let (rx, rz) = Self::parse_region_coords(path)?;
        let mut chunks = HashMap::with_capacity(1024);

        if bytes.len() < SECTOR * 2 {
            return Ok(Self {
                region_x: rx,
                region_z: rz,
                chunks,
            }); // empty/truncated
        }

        for i in 0..1024usize {
            let e = i * 4;
            let offset_sectors = ((bytes[e] as usize) << 16)
                | ((bytes[e + 1] as usize) << 8)
                | bytes[e + 2] as usize;
            let sector_count = bytes[e + 3];
            if offset_sectors == 0 || sector_count == 0 {
                continue; // chunk not generated
            }

            let start = offset_sectors * SECTOR;
            if start + 5 > bytes.len() {
                continue; // location past EOF — skip defensively
            }

            let length = i32::from_be_bytes([
                bytes[start],
                bytes[start + 1],
                bytes[start + 2],
                bytes[start + 3],
            ]);
            if length <= 0 {
                continue;
            }

            let comp_byte = bytes[start + 4];
            let external = (comp_byte & 0x80) != 0;
            let scheme = match ChunkCompression::from_byte(comp_byte & 0x7F) {
                Some(s) => s,
                None => continue, // unknown scheme — skip defensively
            };

            let pos = ChunkPos::from_region_index(rx, rz, i);
            let ts_at = SECTOR + e;
            let timestamp = i32::from_be_bytes([
                bytes[ts_at],
                bytes[ts_at + 1],
                bytes[ts_at + 2],
                bytes[ts_at + 3],
            ]);

            let payload: Vec<u8> = if external {
                // Oversized chunk: body lives in c.X.Z.mcc beside the region.
                // Bound the read like the inline path — a hostile/corrupt .mcc
                // must not become an unbounded read into memory. Erroring (not
                // skipping) keeps a real oversized chunk from being silently
                // dropped from a snapshot.
                let dir = path.parent().unwrap_or_else(|| Path::new("."));
                let mcc = dir.join(format!("c.{}.{}.mcc", pos.x, pos.z));
                match std::fs::metadata(&mcc) {
                    Ok(meta) if meta.len() > MAX_EXTERNAL_CHUNK => {
                        return Err(AnvilError::ExternalChunkTooLarge(MAX_EXTERNAL_CHUNK));
                    }
                    Ok(_) => match std::fs::read(&mcc) {
                        Ok(b) => b,
                        Err(_) => continue, // raced away — nothing to read
                    },
                    Err(_) => continue, // external body missing — nothing to read
                }
            } else {
                // payload length excludes the 1-byte compression tag; clamp to EOF.
                let data_start = start + 5;
                let mut data_len = (length as usize) - 1;
                if data_start + data_len > bytes.len() {
                    data_len = bytes.len() - data_start;
                }
                if data_len == 0 {
                    continue;
                }
                bytes[data_start..data_start + data_len].to_vec()
            };

            chunks.insert(
                pos,
                RawChunk {
                    pos,
                    compression: scheme,
                    payload,
                    external,
                    timestamp,
                },
            );
        }

        Ok(Self {
            region_x: rx,
            region_z: rz,
            chunks,
        })
    }

    pub fn len(&self) -> usize {
        self.chunks.len()
    }
    pub fn is_empty(&self) -> bool {
        self.chunks.is_empty()
    }
    pub fn get(&self, pos: ChunkPos) -> Option<&RawChunk> {
        self.chunks.get(&pos)
    }
    pub fn chunks(&self) -> impl Iterator<Item = &RawChunk> {
        self.chunks.values()
    }

    /// Extract (X, Z) from an `r.X.Z.mca` file name.
    pub fn parse_region_coords(path: &Path) -> Result<(i32, i32)> {
        let stem = path
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or_default();
        let parts: Vec<&str> = stem.split('.').collect();
        if parts.len() == 3 {
            if let (Ok(x), Ok(z)) = (parts[1].parse::<i32>(), parts[2].parse::<i32>()) {
                if parts[0] == "r" {
                    return Ok((x, z));
                }
            }
        }
        Err(AnvilError::BadRegionName(
            path.file_name()
                .and_then(|s| s.to_str())
                .unwrap_or_default()
                .to_string(),
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::codec;
    use mca_nbt::{Compound, NbtValue};

    fn chunk_at(x: i32, z: i32, n: i32) -> RawChunk {
        let mut m = Compound::new();
        m.insert("n".into(), NbtValue::Int(n));
        let payload = codec::encode(&NbtValue::Compound(m), ChunkCompression::ZLib).unwrap();
        RawChunk {
            pos: ChunkPos::new(x, z),
            compression: ChunkCompression::ZLib,
            payload,
            external: false,
            timestamp: 1000 + n,
        }
    }

    #[test]
    fn write_then_read_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("r.0.0.mca");
        let a = chunk_at(0, 0, 7);
        let b = chunk_at(5, 9, 11);
        RegionWriter::write(&path, &[a.clone(), b.clone()]).unwrap();

        let rf = RegionFile::open(&path).unwrap();
        assert_eq!(rf.len(), 2);
        let ra = rf.get(ChunkPos::new(0, 0)).unwrap();
        assert_eq!(ra.compression, ChunkCompression::ZLib);
        assert_eq!(ra.payload, a.payload);
        assert_eq!(ra.timestamp, 1007);
        assert!(!ra.external);
        // decodes back to the original NBT
        let mut m = Compound::new();
        m.insert("n".into(), NbtValue::Int(7));
        assert_eq!(codec::decode(ra).unwrap(), NbtValue::Compound(m));
        // and the second chunk is present
        assert_eq!(rf.get(ChunkPos::new(5, 9)).unwrap().payload, b.payload);
    }

    #[test]
    fn oversized_chunk_spills_to_mcc() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("r.0.0.mca");
        // None-compressed payload > 255 sectors (255 * 4096 = 1_044_480) forces a spill.
        let big = RawChunk {
            pos: ChunkPos::new(1, 2),
            compression: ChunkCompression::None,
            payload: vec![0xAB; 1_100_000],
            external: false,
            timestamp: 5,
        };
        RegionWriter::write(&path, std::slice::from_ref(&big)).unwrap();
        assert!(dir.path().join("c.1.2.mcc").exists());

        let rf = RegionFile::open(&path).unwrap();
        let got = rf.get(ChunkPos::new(1, 2)).unwrap();
        assert!(got.external);
        assert_eq!(got.compression, ChunkCompression::None);
        assert_eq!(got.payload, big.payload);
    }

    #[test]
    fn oversized_mcc_is_rejected_not_read() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("r.0.0.mca");
        let big = RawChunk {
            pos: ChunkPos::new(1, 2),
            compression: ChunkCompression::None,
            payload: vec![0xAB; 1_100_000],
            external: false,
            timestamp: 5,
        };
        RegionWriter::write(&path, std::slice::from_ref(&big)).unwrap();
        // Grow the external body past the cap (sparse — no real disk use).
        let mcc = std::fs::OpenOptions::new()
            .write(true)
            .open(dir.path().join("c.1.2.mcc"))
            .unwrap();
        mcc.set_len(MAX_EXTERNAL_CHUNK + 1).unwrap();

        assert!(matches!(
            RegionFile::open(&path),
            Err(AnvilError::ExternalChunkTooLarge(_))
        ));
    }

    #[test]
    fn empty_region_has_no_chunks() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("r.0.0.mca");
        RegionWriter::write(&path, &[]).unwrap();
        let rf = RegionFile::open(&path).unwrap();
        assert!(rf.is_empty());
    }

    #[test]
    fn bad_region_name_errors() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("not-a-region.mca");
        RegionWriter::write(&path, &[]).unwrap();
        assert!(matches!(
            RegionFile::open(&path),
            Err(AnvilError::BadRegionName(_))
        ));
    }

    // Round-trips a real region (decode -> encode -> decode == equal NBT for every
    // decodable chunk). Set MCAGIT_TEST_REGION to an r.X.Z.mca path to run it;
    // auto-skips otherwise (mirrors the .NET RegionFileTests gate).
    #[test]
    fn real_region_chunks_roundtrip() {
        let Ok(path) = std::env::var("MCAGIT_TEST_REGION") else {
            eprintln!("skipping: set MCAGIT_TEST_REGION to a real r.X.Z.mca");
            return;
        };
        let rf = RegionFile::open(Path::new(&path)).unwrap();
        assert!(!rf.is_empty(), "region had no chunks");
        let mut checked = 0;
        for raw in rf.chunks() {
            if raw.compression == ChunkCompression::Custom {
                continue; // opaque, by design
            }
            let value = codec::decode(raw).unwrap();
            let repacked = codec::encode(&value, raw.compression).unwrap();
            let again = codec::decode(&RawChunk {
                payload: repacked,
                ..raw.clone()
            })
            .unwrap();
            assert_eq!(again, value, "chunk {:?} did not round-trip", raw.pos);
            checked += 1;
        }
        eprintln!("round-tripped {checked} chunks from {path}");
        assert!(checked > 0);
    }
}
