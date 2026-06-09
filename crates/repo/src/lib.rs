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

pub mod pack;
pub use pack::{PackWriter, Packfile};
pub mod object_store;
pub use object_store::ObjectStore;
pub mod manifest;
pub use manifest::{CommitObject, Manifest, TagObject};
pub mod bisect;
pub mod chunk_cache;
pub mod hooks;
pub mod repository;
pub mod sign;
pub use repository::Repository;
pub mod checkout;
pub mod snapshot;
pub use checkout::checkout;
pub mod status;
pub use status::{status, Change, ChangeKind};
pub mod verify;
pub use verify::{verify_commit, world_tree};
pub mod fsck;
pub use fsck::{fsck, FsckReport};
pub mod gc;
pub use gc::{gc, GcReport};
pub mod merge;
pub use merge::{merge, merge_base, MergeOutcome};
pub mod replay;
pub use replay::{cherry_pick, rebase, revert, ReplayOutcome};
pub mod stash;
pub mod transfer;
pub use transfer::{clone_local, fetch_local, push_local};
pub mod remote;
pub use remote::{connect, verify_remote, Transport, VerifyReport};
pub mod serve;
pub use serve::{serve, serve_stdio};
pub mod bucket;
pub mod wirepack;
pub use bucket::{Bucket, BucketTransport, InMemoryBucket};
pub mod cloud;
