//! The `.mcapatch` document model (serde JSON, version 1).

use crate::Result;
use serde::{Deserialize, Serialize};
use serde_json::Value as J;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Status {
    Added,
    Removed,
    Modified,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum EntryKind {
    Region,
    Nbt,
    Blob,
}

/// One node change: at `path`, value is `base` before and `value` after (either
/// null = absent). Values are `mca_nbt` type-tagged JSON.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PatchOp {
    pub path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub base: Option<J>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub value: Option<J>,
}

/// Per-chunk ops within a region entry.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ChunkPatch {
    pub x: i32,
    pub z: i32,
    pub status: Status,
    #[serde(default)]
    pub ops: Vec<PatchOp>,
}

/// A patched file: region (per-chunk ops), loose NBT (node ops), or blob (bytes).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PatchFileEntry {
    pub path: String,
    pub kind: EntryKind,
    pub status: Status,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ops: Option<Vec<PatchOp>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub chunks: Option<Vec<ChunkPatch>>,
    /// base64 blob bytes before (for reverse / 3-way check).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub base_blob: Option<String>,
    /// base64 blob bytes after (forward apply writes this).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub value_blob: Option<String>,
}

/// A portable, bidirectional world patch (`*.mcapatch`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WorldPatch {
    pub version: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub base: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target: Option<String>,
    #[serde(default)]
    pub files: Vec<PatchFileEntry>,
}

impl WorldPatch {
    pub fn new() -> Self {
        Self {
            version: 1,
            base: None,
            target: None,
            files: Vec::new(),
        }
    }
    pub fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string_pretty(self)?)
    }
    pub fn from_json(s: &str) -> Result<Self> {
        Ok(serde_json::from_str(s)?)
    }
}

impl Default for WorldPatch {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn patch_json_roundtrip() {
        let mut p = WorldPatch::new();
        p.base = Some("A".into());
        p.target = Some("B".into());
        p.files.push(PatchFileEntry {
            path: "region/r.0.0.mca".into(),
            kind: EntryKind::Region,
            status: Status::Modified,
            ops: None,
            chunks: Some(vec![ChunkPatch {
                x: 0,
                z: 0,
                status: Status::Modified,
                ops: vec![PatchOp {
                    path: "Level.hp".into(),
                    base: Some(serde_json::json!({"int": 20})),
                    value: Some(serde_json::json!({"int": 18})),
                }],
            }]),
            base_blob: None,
            value_blob: None,
        });
        let json = p.to_json().unwrap();
        assert!(json.contains("\"version\""));
        assert_eq!(WorldPatch::from_json(&json).unwrap(), p);
    }
}
