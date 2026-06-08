//! `mca-nbt` — NBT tag model, binary codec, canonical form, identity, path, JSON.

use thiserror::Error;

/// Errors produced while reading or addressing NBT.
#[derive(Debug, Error, PartialEq, Eq)]
#[non_exhaustive]
pub enum NbtError {
    #[error("invalid path: {0}")]
    InvalidPath(String),
    #[error("invalid JSON shape: {0}")]
    InvalidJson(String),
    #[error("binary nbt: {0}")]
    Binary(String),
}

/// Crate result alias.
pub type Result<T> = std::result::Result<T, NbtError>;

mod conv;
pub mod value;
pub use value::{tag_id, Compound, NbtValue};
pub mod read;
pub use read::read;
pub mod write;
pub use write::write_named;
pub mod canonical;
pub use canonical::canonical_bytes;
pub mod identity;
pub use identity::identity_key;
pub mod path;
pub use path::NbtPath;
pub mod json;
pub use json::{from_json, to_json};
