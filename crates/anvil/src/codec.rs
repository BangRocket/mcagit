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
        m.insert("Heights".into(), NbtValue::IntArray(vec![1, 2, 3, 4, 5]));
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
