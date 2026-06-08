//! Chunk coordinates and the raw (still-compressed) chunk record.

use crate::compression::ChunkCompression;

/// Absolute chunk coordinates (in chunks, not blocks).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ChunkPos {
    pub x: i32,
    pub z: i32,
}

impl ChunkPos {
    pub fn new(x: i32, z: i32) -> Self {
        Self { x, z }
    }

    /// Index 0..1024 of this chunk within its region's location table.
    pub fn region_index(self) -> usize {
        ((self.x & 31) + (self.z & 31) * 32) as usize
    }

    /// Region file coordinate that contains this chunk.
    pub fn region(self) -> (i32, i32) {
        (self.x >> 5, self.z >> 5)
    }

    /// The chunk at location-table `index` (0..1024) within region (rx, rz).
    pub fn from_region_index(rx: i32, rz: i32, index: usize) -> Self {
        Self {
            x: rx * 32 + (index % 32) as i32,
            z: rz * 32 + (index / 32) as i32,
        }
    }
}

/// One chunk as it sits in a region file: position, on-disk compression, the
/// last-modified timestamp, and the raw still-compressed payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RawChunk {
    pub pos: ChunkPos,
    pub compression: ChunkCompression,
    pub payload: Vec<u8>,
    pub external: bool,
    pub timestamp: i32,
}

impl RawChunk {
    /// Byte-for-byte payload equality — the "unchanged chunk" fast path.
    pub fn payload_equals(&self, other: &RawChunk) -> bool {
        self.compression == other.compression && self.payload == other.payload
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn region_index_and_back() {
        // chunk (33, -1): region (1, -1), local (1, 31) -> index 1 + 31*32
        let p = ChunkPos::new(33, -1);
        assert_eq!(p.region(), (1, -1));
        assert_eq!(p.region_index(), 1 + 31 * 32);
        assert_eq!(ChunkPos::from_region_index(1, -1, p.region_index()), p);
    }

    #[test]
    fn negative_coords_wrap_correctly() {
        // chunk (-1, -1): region (-1, -1), local (31, 31)
        let p = ChunkPos::new(-1, -1);
        assert_eq!(p.region(), (-1, -1));
        assert_eq!(p.region_index(), 31 + 31 * 32);
        assert_eq!(ChunkPos::from_region_index(-1, -1, p.region_index()), p);
    }

    #[test]
    fn all_indices_roundtrip() {
        for i in 0..1024 {
            let p = ChunkPos::from_region_index(0, 0, i);
            assert_eq!(p.region_index(), i);
        }
    }
}
