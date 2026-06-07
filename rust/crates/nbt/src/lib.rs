//! `mca-nbt` — NBT tag model, binary codec, canonical form, identity, path, JSON.

use thiserror::Error;

/// Errors produced while reading or addressing NBT.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum NbtError {
    #[error("unexpected end of input")]
    UnexpectedEof,
    #[error("unknown tag id {0}")]
    UnknownTag(u8),
    #[error("negative length in payload")]
    NegativeLength,
    #[error("invalid modified-UTF8 string")]
    InvalidString,
    #[error("invalid path: {0}")]
    InvalidPath(String),
    #[error("invalid JSON shape: {0}")]
    InvalidJson(String),
}

/// Crate result alias.
pub type Result<T> = std::result::Result<T, NbtError>;

pub mod mutf8;
pub mod value;
pub use value::{tag_id, Compound, NbtValue};
pub mod read;
pub use read::read;
