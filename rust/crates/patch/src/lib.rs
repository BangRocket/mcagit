//! `mca-patch` — the invertible `.mcapatch` engine: extract a diff to a portable
//! JSON patch, apply it 3-way-guarded to a fresh output world (forward/reverse).

use thiserror::Error;

#[derive(Debug, Error)]
pub enum PatchError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("json error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("nbt error: {0}")]
    Nbt(#[from] mca_nbt::NbtError),
    #[error("anvil error: {0}")]
    Anvil(#[from] mca_anvil::AnvilError),
    #[error("diff error: {0}")]
    Diff(#[from] mca_diff::DiffError),
    #[error("base64 error: {0}")]
    B64(#[from] base64::DecodeError),
    #[error("unsupported patch version {0}")]
    Version(u32),
    #[error("{0}")]
    Other(String),
}

pub type Result<T> = std::result::Result<T, PatchError>;

pub mod model;
pub use model::{ChunkPatch, EntryKind, PatchFileEntry, PatchOp, Status, WorldPatch};
pub mod op_sink;
pub use op_sink::PatchOpSink;
