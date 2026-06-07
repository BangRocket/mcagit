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
