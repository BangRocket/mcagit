//! Chunk payload compression schemes and their codecs.

use crate::{AnvilError, Result};
use flate2::read::{GzDecoder, ZlibDecoder};
use flate2::write::{GzEncoder, ZlibEncoder};
use flate2::Compression;
use lz4_flex::frame::{FrameDecoder, FrameEncoder};
use std::io::{Read, Write};

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
}
