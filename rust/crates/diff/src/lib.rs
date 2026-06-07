//! `mca-diff` — semantic NBT + world diff over `mca-nbt` / `mca-anvil`.
//!
//! All change semantics flow through the single [`comparer::walk`] tree-walk and
//! the [`comparer::DiffSink`] trait, so the display rendering and the patch-op
//! capture cannot drift.

use thiserror::Error;

#[derive(Debug, Error)]
pub enum DiffError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("nbt error: {0}")]
    Nbt(#[from] mca_nbt::NbtError),
    #[error("anvil error: {0}")]
    Anvil(#[from] mca_anvil::AnvilError),
}

pub type Result<T> = std::result::Result<T, DiffError>;

pub mod comparer;
pub use comparer::{walk, DiffSink};
