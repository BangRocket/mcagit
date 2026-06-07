//! `mca-repo` — clean-slate content-addressed repo: object store, manifest,
//! commit, parallel checkout.

use thiserror::Error;

/// Errors produced by repository operations.
#[derive(Debug, Error)]
pub enum RepoError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("json error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("nbt error: {0}")]
    Nbt(#[from] mca_nbt::NbtError),
    #[error("anvil error: {0}")]
    Anvil(#[from] mca_anvil::AnvilError),
    #[error("not a repository: {0}")]
    NotARepository(String),
    #[error("bad revision: {0}")]
    BadRef(String),
    #[error("{0}")]
    Other(String),
}

/// Crate result alias.
pub type Result<T> = std::result::Result<T, RepoError>;

pub mod object_store;
pub use object_store::ObjectStore;
