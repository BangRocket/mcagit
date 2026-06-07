# mcagit Rust Engine — `mca-anvil` Implementation Plan (M2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the `mca-anvil` crate: read/write the Anvil region container (`r.X.Z.mca`), decode/encode chunk payloads (zlib/gzip/none/lz4) over `mca-nbt`, and load/save standalone NBT files (`level.dat` etc.) — the layer M3's object store, commit, and checkout build on.

**Architecture:** A library crate depending only on `mca-nbt`. `compression` owns the four payload codecs (bounded against decompression bombs); `chunk` owns coordinates + the raw chunk record; `region` owns the 8 KiB-header container read/write; `codec` bridges raw payloads ↔ `NbtValue`. Mirrors the proven .NET `src/McaGit/Anvil/` semantics exactly (clean-slate applies to the *repo* format, not the Minecraft container, which must stay spec-faithful).

**Tech Stack:** Rust 2021, `mca-nbt` (path dep), `flate2` (zlib/gzip), `lz4_flex` (LZ4 frame), `thiserror`. Dev: `tempfile`. (Perf knobs — `memmap2` reads and the `flate2` zlib-ng backend — are deliberately deferred to **M3**, where checkout speed is measured; M2 is the correctness milestone and uses `std::fs::read` + the pure-Rust `miniz_oxide` backend for zero system deps.)

**Reference:** `.NET` originals `src/McaGit/Anvil/{RegionFile,RegionWriter,RawChunk,ChunkPos,ChunkCodec}.cs`; design spec `docs/superpowers/specs/2026-06-07-mcagit-rust-port-design.md`.

---

## File Structure

```
rust/
  Cargo.toml                    +member crates/anvil; +workspace deps flate2, lz4_flex, tempfile, mca-nbt(path)
  crates/anvil/
    Cargo.toml                  crate manifest
    src/
      lib.rs                    re-exports + AnvilError/Result
      compression.rs            ChunkCompression enum; decompress/compress (zlib/gzip/none/lz4); inflate_bounded
      chunk.rs                  ChunkPos, RawChunk
      region.rs                 RegionFile (read), RegionWriter (write)
      codec.rs                  decode/encode chunk NBT; load/save standalone NBT files
```

Responsibilities: `compression` is the only place raw (de)compression lives; `chunk` is pure data + coordinate math; `region` is the only place the on-disk container layout lives; `codec` is the only place `mca-nbt` meets a payload.

---

## Task 1: Crate scaffold + workspace wiring

**Files:**
- Modify: `rust/Cargo.toml`
- Create: `rust/crates/anvil/Cargo.toml`
- Create: `rust/crates/anvil/src/lib.rs`

- [ ] **Step 1: Add the crate to the workspace and declare shared deps**

In `rust/Cargo.toml`, change the `members` line to:
```toml
members = ["crates/nbt", "crates/anvil"]
```
and add these entries under `[workspace.dependencies]` (keep the existing ones):
```toml
mca-nbt = { path = "crates/nbt" }
flate2 = "1"
lz4_flex = "0.11"
tempfile = "3"
```

- [ ] **Step 2: Create the anvil crate manifest**

`rust/crates/anvil/Cargo.toml`:
```toml
[package]
name = "mca-anvil"
edition.workspace = true
version.workspace = true
license.workspace = true

[dependencies]
mca-nbt = { workspace = true }
flate2 = { workspace = true }
lz4_flex = { workspace = true }
thiserror = { workspace = true }

[dev-dependencies]
tempfile = { workspace = true }
```

- [ ] **Step 3: Create lib.rs with the crate error type**

`rust/crates/anvil/src/lib.rs`:
```rust
//! `mca-anvil` — Anvil region container read/write + chunk codec over `mca-nbt`.

use thiserror::Error;

/// Errors produced while reading/writing regions or (de)coding chunks.
#[derive(Debug, Error)]
pub enum AnvilError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("nbt error: {0}")]
    Nbt(#[from] mca_nbt::NbtError),
    #[error("unsupported chunk compression: {0}")]
    UnsupportedCompression(u8),
    #[error("decompression exceeded {0} bytes (bomb?)")]
    DecompressionBomb(u64),
    #[error("bad region file name (expected r.X.Z.mca): {0}")]
    BadRegionName(String),
}

/// Crate result alias.
pub type Result<T> = std::result::Result<T, AnvilError>;
```

- [ ] **Step 4: Verify the workspace still builds**

Run: `cd rust && cargo build`
Expected: both `mca-nbt` and `mca-anvil` compile; `Finished`.

- [ ] **Step 5: Commit**

```bash
git add rust/Cargo.toml rust/Cargo.lock rust/crates/anvil/Cargo.toml rust/crates/anvil/src/lib.rs
git commit -m "feat(anvil): scaffold mca-anvil crate"
```

---

## Task 2: ChunkCompression

**Files:**
- Create: `rust/crates/anvil/src/compression.rs`
- Modify: `rust/crates/anvil/src/lib.rs` (add `pub mod compression;` + re-export)

- [ ] **Step 1: Write the enum + byte mapping with tests**

`rust/crates/anvil/src/compression.rs` (the full codec is added in Task 4; start with the enum):
```rust
//! Chunk payload compression schemes and their codecs.

/// On-disk compression scheme of a chunk payload (the byte after the length).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChunkCompression {
    GZip,
    ZLib,
    None,
    Lz4,
    Custom,
}

impl ChunkCompression {
    /// Map a header byte (with the external `0x80` bit already cleared) to a scheme.
    pub fn from_byte(b: u8) -> Option<Self> {
        Some(match b {
            1 => Self::GZip,
            2 => Self::ZLib,
            3 => Self::None,
            4 => Self::Lz4,
            127 => Self::Custom,
            _ => return None,
        })
    }

    /// The header byte for this scheme.
    pub fn to_byte(self) -> u8 {
        match self {
            Self::GZip => 1,
            Self::ZLib => 2,
            Self::None => 3,
            Self::Lz4 => 4,
            Self::Custom => 127,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn byte_roundtrip() {
        for s in [
            ChunkCompression::GZip,
            ChunkCompression::ZLib,
            ChunkCompression::None,
            ChunkCompression::Lz4,
            ChunkCompression::Custom,
        ] {
            assert_eq!(ChunkCompression::from_byte(s.to_byte()), Some(s));
        }
    }

    #[test]
    fn unknown_byte_is_none() {
        assert_eq!(ChunkCompression::from_byte(0), None);
        assert_eq!(ChunkCompression::from_byte(5), None);
    }
}
```

- [ ] **Step 2: Wire the module**

In `rust/crates/anvil/src/lib.rs`, after the `Result` alias:
```rust
pub mod compression;
pub use compression::ChunkCompression;
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-anvil compression`
Expected: 2 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/anvil/src/compression.rs rust/crates/anvil/src/lib.rs
git commit -m "feat(anvil): ChunkCompression scheme + byte mapping"
```

---

## Task 3: ChunkPos + RawChunk

**Files:**
- Create: `rust/crates/anvil/src/chunk.rs`
- Modify: `rust/crates/anvil/src/lib.rs` (add `pub mod chunk;` + re-export)

- [ ] **Step 1: Write coordinate math + raw record with tests**

`rust/crates/anvil/src/chunk.rs`:
```rust
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
```

- [ ] **Step 2: Wire the module**

In `rust/crates/anvil/src/lib.rs`:
```rust
pub mod chunk;
pub use chunk::{ChunkPos, RawChunk};
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-anvil chunk`
Expected: 3 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/anvil/src/chunk.rs rust/crates/anvil/src/lib.rs
git commit -m "feat(anvil): ChunkPos coordinate math + RawChunk"
```

---

## Task 4: Payload (de)compression codecs

**Files:**
- Modify: `rust/crates/anvil/src/compression.rs` (append codec functions + tests)

- [ ] **Step 1: Append the codecs and a bounded inflate**

Add to the top of `rust/crates/anvil/src/compression.rs` (imports), and append the functions after the `impl ChunkCompression` block (before the `#[cfg(test)]` module):

Imports at the very top of the file:
```rust
use crate::{AnvilError, Result};
use flate2::read::{GzDecoder, ZlibDecoder};
use flate2::write::{GzEncoder, ZlibEncoder};
use flate2::Compression;
use lz4_flex::frame::{FrameDecoder, FrameEncoder};
use std::io::{Read, Write};
```

Functions (append before `#[cfg(test)]`):
```rust
/// Generous cap so a crafted payload can't inflate to gigabytes and OOM us.
const MAX_INFLATED: u64 = 128 * 1024 * 1024;

/// Drain a decompressor into a `Vec`, erroring past [`MAX_INFLATED`].
fn inflate_bounded(mut r: impl Read) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    let mut buf = [0u8; 81920];
    let mut total: u64 = 0;
    loop {
        let n = r.read(&mut buf)?;
        if n == 0 {
            break;
        }
        total += n as u64;
        if total > MAX_INFLATED {
            return Err(AnvilError::DecompressionBomb(MAX_INFLATED));
        }
        out.extend_from_slice(&buf[..n]);
    }
    Ok(out)
}

/// Decompress a raw chunk payload into uncompressed NBT bytes.
pub fn decompress(scheme: ChunkCompression, payload: &[u8]) -> Result<Vec<u8>> {
    match scheme {
        ChunkCompression::None => Ok(payload.to_vec()),
        ChunkCompression::ZLib => inflate_bounded(ZlibDecoder::new(payload)),
        ChunkCompression::GZip => inflate_bounded(GzDecoder::new(payload)),
        ChunkCompression::Lz4 => inflate_bounded(FrameDecoder::new(payload)),
        ChunkCompression::Custom => Err(AnvilError::UnsupportedCompression(127)),
    }
}

/// Compress uncompressed NBT bytes into a chunk payload under `scheme`.
pub fn compress(scheme: ChunkCompression, raw: &[u8]) -> Result<Vec<u8>> {
    match scheme {
        ChunkCompression::None => Ok(raw.to_vec()),
        ChunkCompression::ZLib => {
            let mut e = ZlibEncoder::new(Vec::new(), Compression::default());
            e.write_all(raw)?;
            Ok(e.finish()?)
        }
        ChunkCompression::GZip => {
            let mut e = GzEncoder::new(Vec::new(), Compression::default());
            e.write_all(raw)?;
            Ok(e.finish()?)
        }
        ChunkCompression::Lz4 => {
            let mut e = FrameEncoder::new(Vec::new());
            e.write_all(raw)?;
            Ok(e.finish()?)
        }
        ChunkCompression::Custom => Err(AnvilError::UnsupportedCompression(127)),
    }
}
```

- [ ] **Step 2: Add codec round-trip tests**

Inside the existing `#[cfg(test)] mod tests` block in `compression.rs`, add:
```rust
    #[test]
    fn compress_decompress_roundtrip_all_schemes() {
        let data = b"hello nbt payload \x00\x01\x02 repeated repeated repeated".repeat(50);
        for s in [
            ChunkCompression::None,
            ChunkCompression::ZLib,
            ChunkCompression::GZip,
            ChunkCompression::Lz4,
        ] {
            let packed = compress(s, &data).unwrap();
            let back = decompress(s, &packed).unwrap();
            assert_eq!(back, data, "scheme {s:?} did not round-trip");
        }
    }

    #[test]
    fn custom_scheme_cannot_be_coded() {
        assert!(matches!(
            decompress(ChunkCompression::Custom, b"x"),
            Err(AnvilError::UnsupportedCompression(127))
        ));
        assert!(matches!(
            compress(ChunkCompression::Custom, b"x"),
            Err(AnvilError::UnsupportedCompression(127))
        ));
    }
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-anvil compression`
Expected: 4 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/anvil/src/compression.rs
git commit -m "feat(anvil): zlib/gzip/none/lz4 payload codecs (bounded inflate)"
```

---

## Task 5: Chunk + NBT-file codec

**Files:**
- Create: `rust/crates/anvil/src/codec.rs`
- Modify: `rust/crates/anvil/src/lib.rs` (add `pub mod codec;` + re-export)

- [ ] **Step 1: Write decode/encode + standalone NBT file load/save with tests**

`rust/crates/anvil/src/codec.rs`:
```rust
//! Bridge between raw chunk payloads / `.dat` files and `mca-nbt` values.

use crate::chunk::RawChunk;
use crate::compression::{self, ChunkCompression};
use crate::Result;
use mca_nbt::NbtValue;
use std::path::Path;

/// Decompress and parse a chunk into its root NBT value.
pub fn decode(chunk: &RawChunk) -> Result<NbtValue> {
    let raw = compression::decompress(chunk.compression, &chunk.payload)?;
    let (_name, value) = mca_nbt::read(&raw)?;
    Ok(value)
}

/// Serialize an NBT root and compress it into a chunk payload (the body after
/// the length/compression header). Chunk roots are written with an empty name,
/// matching Minecraft.
pub fn encode(root: &NbtValue, scheme: ChunkCompression) -> Result<Vec<u8>> {
    let raw = mca_nbt::write_named("", root, false);
    compression::compress(scheme, &raw)
}

/// Load a standalone NBT file (e.g. `level.dat`), auto-detecting gzip/zlib by
/// magic bytes (these are usually gzip), else treating the bytes as raw NBT.
pub fn load_nbt_file(path: &Path) -> Result<NbtValue> {
    let bytes = std::fs::read(path)?;
    let raw = match bytes.as_slice() {
        [0x1f, 0x8b, ..] => compression::decompress(ChunkCompression::GZip, &bytes)?,
        [0x78, _, ..] => compression::decompress(ChunkCompression::ZLib, &bytes)?,
        _ => bytes,
    };
    let (_name, value) = mca_nbt::read(&raw)?;
    Ok(value)
}

/// Save a standalone NBT file under `scheme` (Minecraft writes `.dat` as gzip).
pub fn save_nbt_file(path: &Path, root: &NbtValue, scheme: ChunkCompression) -> Result<()> {
    let raw = mca_nbt::write_named("", root, false);
    let packed = compression::compress(scheme, &raw)?;
    std::fs::write(path, packed)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use mca_nbt::{Compound, NbtValue};

    fn sample() -> NbtValue {
        let mut m = Compound::new();
        m.insert("Name".into(), NbtValue::String("chunk".into()));
        m.insert("Level".into(), NbtValue::Int(42));
        m.insert(
            "Heights".into(),
            NbtValue::IntArray(vec![1, 2, 3, 4, 5]),
        );
        NbtValue::Compound(m)
    }

    #[test]
    fn decode_after_encode_all_schemes() {
        let v = sample();
        for s in [
            ChunkCompression::None,
            ChunkCompression::ZLib,
            ChunkCompression::GZip,
            ChunkCompression::Lz4,
        ] {
            let payload = encode(&v, s).unwrap();
            let chunk = RawChunk {
                pos: crate::ChunkPos::new(0, 0),
                compression: s,
                payload,
                external: false,
                timestamp: 0,
            };
            assert_eq!(decode(&chunk).unwrap(), v, "scheme {s:?}");
        }
    }

    #[test]
    fn nbt_file_save_load_roundtrip() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("level.dat");
        let v = sample();
        save_nbt_file(&path, &v, ChunkCompression::GZip).unwrap();
        assert_eq!(load_nbt_file(&path).unwrap(), v);
    }
}
```

- [ ] **Step 2: Wire the module**

In `rust/crates/anvil/src/lib.rs`:
```rust
pub mod codec;
```

- [ ] **Step 3: Run tests**

Run: `cd rust && cargo test -p mca-anvil codec`
Expected: 2 tests pass.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/anvil/src/codec.rs rust/crates/anvil/src/lib.rs
git commit -m "feat(anvil): chunk + standalone NBT file codec over mca-nbt"
```

---

## Task 6: RegionWriter

**Files:**
- Create: `rust/crates/anvil/src/region.rs`
- Modify: `rust/crates/anvil/src/lib.rs` (add `pub mod region;` + re-export `RegionWriter`)

- [ ] **Step 1: Write the region writer**

`rust/crates/anvil/src/region.rs`:
```rust
//! Anvil region container (`r.X.Z.mca`): the 8 KiB header + sector-aligned bodies.

use crate::chunk::RawChunk;
use crate::Result;
use std::path::Path;

const SECTOR: usize = 4096;
const MAX_INLINE_SECTORS: usize = 255; // sector count is a single header byte

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
```

- [ ] **Step 2: Wire the module**

In `rust/crates/anvil/src/lib.rs`:
```rust
pub mod region;
pub use region::{RegionFile, RegionWriter};
```

(Note: `RegionFile` is added in Task 7; this re-export line compiles only after Task 7. To keep this task green, temporarily re-export just the writer:)
```rust
pub mod region;
pub use region::RegionWriter;
```

- [ ] **Step 3: Verify it builds (writer has no standalone test yet — exercised in Task 7)**

Run: `cd rust && cargo build -p mca-anvil`
Expected: compiles.

- [ ] **Step 4: Commit**

```bash
git add rust/crates/anvil/src/region.rs rust/crates/anvil/src/lib.rs
git commit -m "feat(anvil): RegionWriter (header + sector-aligned bodies, .mcc spill)"
```

---

## Task 7: RegionFile reader + round-trip

**Files:**
- Modify: `rust/crates/anvil/src/region.rs` (add `RegionFile` + tests)
- Modify: `rust/crates/anvil/src/lib.rs` (re-export `RegionFile`)

- [ ] **Step 1: Add the reader**

At the top of `rust/crates/anvil/src/region.rs`, extend the imports:
```rust
use crate::chunk::{ChunkPos, RawChunk};
use crate::compression::ChunkCompression;
use crate::{AnvilError, Result};
use std::collections::HashMap;
use std::path::Path;
```
(Replace the existing `use crate::chunk::RawChunk;` and `use crate::Result;` lines with the block above.)

Append after the `impl RegionWriter` block:
```rust
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
            return Ok(Self { region_x: rx, region_z: rz, chunks }); // empty/truncated
        }

        for i in 0..1024usize {
            let e = i * 4;
            let offset_sectors =
                ((bytes[e] as usize) << 16) | ((bytes[e + 1] as usize) << 8) | bytes[e + 2] as usize;
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
                let dir = path.parent().unwrap_or_else(|| Path::new("."));
                let mcc = dir.join(format!("c.{}.{}.mcc", pos.x, pos.z));
                match std::fs::read(&mcc) {
                    Ok(b) => b,
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
                RawChunk { pos, compression: scheme, payload, external, timestamp },
            );
        }

        Ok(Self { region_x: rx, region_z: rz, chunks })
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
```

- [ ] **Step 2: Update the re-export**

In `rust/crates/anvil/src/lib.rs`, change the region re-export to include the reader:
```rust
pub mod region;
pub use region::{RegionFile, RegionWriter};
```

- [ ] **Step 3: Add round-trip tests (writer → reader, incl. external spill)**

Append to `rust/crates/anvil/src/region.rs`:
```rust
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
        RegionWriter::write(&path, &[big.clone()]).unwrap();
        assert!(dir.path().join("c.1.2.mcc").exists());

        let rf = RegionFile::open(&path).unwrap();
        let got = rf.get(ChunkPos::new(1, 2)).unwrap();
        assert!(got.external);
        assert_eq!(got.compression, ChunkCompression::None);
        assert_eq!(got.payload, big.payload);
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
}
```

- [ ] **Step 4: Run tests**

Run: `cd rust && cargo test -p mca-anvil region`
Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add rust/crates/anvil/src/region.rs rust/crates/anvil/src/lib.rs
git commit -m "feat(anvil): RegionFile reader + write/read round-trip tests"
```

---

## Task 8: Real-region gate + crate-wide lint/test gate (M2 gate)

**Files:**
- Modify: `rust/crates/anvil/src/region.rs` (add an env-gated real-region test)

- [ ] **Step 1: Add an env-gated round-trip test against a real region**

Append inside the `#[cfg(test)] mod tests` block in `region.rs`:
```rust
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
        assert!(rf.len() > 0, "region had no chunks");
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
```

- [ ] **Step 2: Run the full crate test suite (skips the real-region test)**

Run: `cd rust && cargo test -p mca-anvil`
Expected: all anvil tests pass (≈ 15), the real-region test prints "skipping…" and passes.

- [ ] **Step 3: Run the M2 gate against a real dobbscraft region**

Run (substitute any present region; pick one from the newest snapshot's overworld):
```bash
cd rust && MCAGIT_TEST_REGION="$(ls -1 /Volumes/Storage/Code/minecraft/dobbscraft-snapshots/2026-05-31_05-39-05/New_World/region/r.*.mca | head -1)" \
  cargo test -p mca-anvil real_region_chunks_roundtrip -- --nocapture
```
Expected: prints "round-tripped N chunks…" with N > 0, test passes. **This is the M2 gate.**

- [ ] **Step 4: Workspace-wide green + lint gate**

Run:
```bash
cd rust && cargo test --all && cargo fmt --all -- --check && cargo clippy --all-targets -- -D warnings
```
Expected: all tests pass (nbt + anvil), fmt clean, clippy clean.

- [ ] **Step 5: Commit (incl. any fmt fixes)**

```bash
git add -A rust/
git commit -m "test(anvil): env-gated real-region round-trip + M2 gate green"
```

---

## Done criteria (M2)

- `mca-anvil` exposes: `ChunkCompression`, `ChunkPos`, `RawChunk`, `RegionFile`, `RegionWriter`, `codec::{decode, encode, load_nbt_file, save_nbt_file}`, `compression::{decompress, compress}`, `AnvilError`/`Result`.
- Write→read region round-trip (inline + external `.mcc`) verified; all four chunk schemes encode/decode; standalone NBT file save/load verified.
- **M2 gate:** a real dobbscraft region round-trips every decodable chunk (decode→encode→decode equal).
- `cargo test --all` green; clippy `-D warnings` + fmt clean.

## Deferred to later milestones (tracked, not silently dropped)

- **Perf (M3):** `memmap2` region reads and the `flate2` `zlib-ng` backend (M2 uses `std::fs::read` + `miniz_oxide` for zero system deps); a tunable/fast checkout compression level.
- **Hardening (trust-boundary pass):** an NBT recursion-depth guard in `mca-nbt`'s reader to reject pathological nesting before a stack overflow (the .NET `NbtDepthGuard`). The `inflate_bounded` cap (decompression-bomb guard) is already in this milestone.
- **Next milestone (M3):** object store (blake3 + zstd) + manifest + commit (incremental fast-path) + **parallel checkout** — the first benchmark against the .NET baselines.
