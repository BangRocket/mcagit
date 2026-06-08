//! The whole-world snapshot (`Manifest` ≈ a git tree) and the `CommitObject`.

use crate::Result;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

/// A whole-world snapshot by content hash. Region files map each present chunk
/// position (`"x,z"`) to its chunk-object id; loose NBT and all other files map
/// their relative path to a single object id.
#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    #[serde(default)]
    pub regions: BTreeMap<String, BTreeMap<String, String>>,
    #[serde(default)]
    pub nbt: BTreeMap<String, String>,
    #[serde(default)]
    pub blobs: BTreeMap<String, String>,
    /// Directories with no files — recorded so checkout reproduces them.
    #[serde(default)]
    pub empty_dirs: Vec<String>,
}

impl Manifest {
    pub fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string_pretty(self)?)
    }
    pub fn from_json(s: &str) -> Result<Self> {
        Ok(serde_json::from_str(s)?)
    }
}

/// A commit: a snapshot (`tree`) plus history and metadata.
#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommitObject {
    pub tree: String,
    #[serde(default)]
    pub parents: Vec<String>,
    #[serde(default)]
    pub message: String,
    #[serde(default)]
    pub author: String,
    /// ISO-8601 author date.
    #[serde(default)]
    pub time: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub committer: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub commit_time: Option<String>,
}

impl CommitObject {
    pub fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string_pretty(self)?)
    }
    pub fn from_json(s: &str) -> Result<Self> {
        Ok(serde_json::from_str(s)?)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn manifest_json_roundtrip() {
        let mut m = Manifest::default();
        let mut region = BTreeMap::new();
        region.insert("0,0".to_string(), "aa11".to_string());
        region.insert("1,0".to_string(), "bb22".to_string());
        m.regions.insert("region/r.0.0.mca".to_string(), region);
        m.nbt.insert("level.dat".to_string(), "cc33".to_string());
        m.blobs.insert("icon.png".to_string(), "dd44".to_string());
        m.empty_dirs.push("playerdata".to_string());

        let json = m.to_json().unwrap();
        assert!(json.contains("emptyDirs"));
        assert_eq!(Manifest::from_json(&json).unwrap(), m);
    }

    #[test]
    fn commit_json_roundtrip() {
        let c = CommitObject {
            tree: "treehash".into(),
            parents: vec!["p1".into()],
            message: "snapshot".into(),
            author: "Joshua <j@example.com>".into(),
            time: "2026-06-07T00:00:00Z".into(),
            committer: Some("Joshua <j@example.com>".into()),
            commit_time: Some("2026-06-07T00:00:01Z".into()),
        };
        let json = c.to_json().unwrap();
        assert_eq!(CommitObject::from_json(&json).unwrap(), c);
    }

    #[test]
    fn commit_without_committer_omits_field() {
        let c = CommitObject {
            tree: "t".into(),
            ..Default::default()
        };
        let json = c.to_json().unwrap();
        assert!(!json.contains("committer"));
        assert_eq!(CommitObject::from_json(&json).unwrap(), c);
    }
}
